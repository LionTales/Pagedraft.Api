using Microsoft.Extensions.Options;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;

namespace Pagedraft.Api.Services.Chat;

/// <summary>
/// Grounded product Q&amp;A over the shipped guides corpus (chatbot phase A, c1).
///
/// <para>ONE CAPABILITY, WITH A HARD BOUNDARY: questions about the PRODUCT, answered from the guides,
/// with citations. Not questions about the user's book (phase B, needs summaries and edit history as
/// context), not conversation persistence, not quota, not personalization. Mixing any of those in
/// here would blur the single property that makes phase A safe to ship, namely that every answer is
/// traceable to a shipped document.</para>
///
/// <para>ROUTING is through <see cref="IAiRouter"/> with <see cref="AiTaskType.ProductChat"/> and
/// nothing else - never a provider directly - so this feature can move from local Ollama to cloud by
/// config, and so its tuning cannot be silently moved by a change made for chapter QA. It does NOT
/// call <c>BookAiTierResolver</c>: phase A chat is app-level, there is no book id to resolve a tier
/// from, and <c>ProductChat</c> is deliberately outside <c>AiTierPolicy.TieredTasks</c>, so it always
/// runs on the Fast (local) rung by construction.</para>
///
/// <para>FAIL-SAFE, AND OBSERVABLE. A missing or unreadable corpus, a corpus that parsed to nothing,
/// a router that throws, and an empty completion all produce an honest "I cannot reach the guides
/// right now" with <c>isGrounded=false</c> and a machine-readable fault reason, never an answer from
/// the model's own priors. Each path also LOGS its cause: an endpoint kept non-throwing by a catch
/// that says nothing ships its failures invisibly, which this codebase has been bitten by before. The
/// user's question TEXT is never logged (it is arbitrary user content); lengths, counts, ids and
/// reasons are.</para>
///
/// <para>ONE THING IS REWRITTEN ON THE WAY OUT: the em-dash, which the workspace forbids in
/// user-facing text and which two live runs measured the model emitting anyway (see
/// <see cref="ProductChatPunctuation"/>). It runs AFTER the citation parse, and it LOGS its count,
/// because a silent rewrite of model output is the other way this layer could ship a failure
/// invisibly.</para>
/// </summary>
public class ProductChatService
{
    /// <summary>
    /// How many prior turns are forwarded to the model, newest kept (d1's finding for c1: it suggests
    /// 6 to 10). The client is NOT trusted to bound its history: d1's context-budget math assumes a
    /// bounded history and does not by itself protect the window on a long conversation, and an
    /// unbounded list would overrun the 16k window silently rather than fail.
    /// </summary>
    public const int MaxHistoryTurns = 8;

    /// <summary>
    /// Per-turn character cap. The turn COUNT alone does not bound the history: one pasted turn can
    /// be larger than the whole guides corpus. Sized so eight capped turns cost roughly 4k Hebrew
    /// tokens at d1's ~2 chars/token floor, which the Hebrew worst-case retrieval budget absorbs.
    ///
    /// <para>These two caps SHAPE the history; they do not BOUND the prompt. Both are proxies, and
    /// neither can see the other half of the prompt (the guides, which grow whenever the corpus is
    /// edited). The real bound is <see cref="ProductChatBudget"/>, measured on what is actually
    /// composed.</para>
    /// </summary>
    public const int MaxHistoryTurnChars = 1000;

    // ─── Fail-safe copy. No em-dash: these strings are rendered to the user. ─────────────────────

    private const string GuidesUnreachableHe =
        "אינני מצליח להגיע כרגע למדריכים של PageDraft, ולכן לא אענה מתוך ניחוש. נסו שוב בעוד רגע.";

    private const string GuidesUnreachableEn =
        "I cannot reach the PageDraft guides right now, so I will not answer from guesswork. " +
        "Please try again in a moment.";

    private const string AnswerUnavailableHe =
        "המדריכים זמינים, אבל לא הצלחתי להפיק תשובה כרגע. נסו שוב בעוד רגע.";

    private const string AnswerUnavailableEn =
        "The guides are available, but I could not produce an answer right now. Please try again in a moment.";

    private readonly GuidesCorpusReader _guides;
    private readonly IAiRouter _router;
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly ILogger<ProductChatService> _logger;

    public ProductChatService(
        GuidesCorpusReader guides,
        IAiRouter router,
        IOptions<AiOptions> aiOptions,
        ILogger<ProductChatService> logger)
    {
        _guides = guides;
        _router = router;
        _aiOptions = aiOptions;
        _logger = logger;
    }

    /// <summary>
    /// Answers one product question. Never throws for a reachable-but-broken dependency; the caller
    /// gets a <see cref="ProductChatResponseDto"/> whose <c>IsGrounded</c> says which kind of answer
    /// it is. Cancellation IS propagated, because a cancelled request has no user left to mislead.
    /// </summary>
    public async Task<ProductChatResponseDto> AnswerAsync(ProductChatRequest request, CancellationToken ct)
    {
        var question = (request.Question ?? string.Empty).Trim();
        var language = ChatLanguage.Detect(question, request.Language);

        var corpus = _guides.Load();
        if (!corpus.CanGround)
        {
            var fault = corpus.Fault ?? ProductChatFaults.GuidesEmpty;
            _logger.LogWarning(
                "Product chat REFUSED to ground an answer ({Fault}): the guides corpus at {GuidesDirectory} " +
                "yielded {DocumentCount} usable guide(s) ({UnparseableCount} rejected). Answering from the " +
                "model's own knowledge is exactly the failure this refusal exists to prevent, so the caller " +
                "gets the honest fail-safe instead.",
                fault, corpus.ResolvedDirectory, corpus.Documents.Count, corpus.UnparseableFileCount);
            return FailSafe(language, fault);
        }

        var selected = GuideSelector.Select(question, corpus.Documents, language);
        if (selected.Count == 0)
        {
            // Unreachable while the corpus is non-empty (the selector never refuses on a weak match),
            // stated rather than assumed so a future selector change cannot silently ground nothing.
            _logger.LogWarning(
                "Product chat REFUSED to ground an answer ({Fault}): the selector returned no guide from a " +
                "corpus of {DocumentCount}. This should be unreachable; the selector is contracted to return " +
                "its top N whenever the corpus is non-empty.",
                ProductChatFaults.GuidesEmpty, corpus.Documents.Count);
            return FailSafe(language, ProductChatFaults.GuidesEmpty);
        }

        var receivedTurns = request.History?.Count ?? 0;
        var history = CapHistory(request.History);

        // THE PROMPT IS BOUNDED HERE, not by the turn cap above (g1 F2). Ollama truncates from the
        // START - the grounding rule and the guides - so an overrun returns a confident ungrounded
        // answer that still claims to be grounded. History is dropped first, guides only as a last
        // resort, and the SURVIVING guides are what the citation is computed against.
        var composed = ProductChatBudget.Compose(language, selected, history, question, InputTokenBudget());
        var instruction = composed.Instruction;
        selected = composed.Guides;
        LogTrim(composed, history.Count);

        AiResponse response;
        try
        {
            response = await _router.CompleteAsync(new AiRequest
            {
                InputText = question,
                Instruction = instruction,
                TaskType = AiTaskType.ProductChat,
                Language = language,
                SourceId = "product-chat"
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Product chat could not reach the routed model for {Task}; returning the honest fail-safe " +
                "({Fault}) rather than an ungrounded answer. Guides selected: [{GuideIds}].",
                AiTaskType.ProductChat, ProductChatFaults.ModelUnavailable, string.Join(", ", Ids(selected)));
            return FailSafe(language, ProductChatFaults.ModelUnavailable);
        }

        if (string.IsNullOrWhiteSpace(response.Content))
        {
            _logger.LogWarning(
                "Product chat got an EMPTY completion from {Provider}/{Model} for {Task}; returning the " +
                "fail-safe ({Fault}). Guides selected: [{GuideIds}], instruction {InstructionChars} chars.",
                response.Provider, response.Model, AiTaskType.ProductChat, ProductChatFaults.EmptyAnswer,
                string.Join(", ", Ids(selected)), instruction.Length);
            return FailSafe(language, ProductChatFaults.EmptyAnswer);
        }

        var (answer, guideIds) = ProductChatCitations.Extract(response.Content.Trim(), selected);

        // PUNCTUATION REPAIR RUNS AFTER THE CITATION PARSE, never before. The parser reads the answer's
        // LAST line, where the label sits on it, and the character immediately before that label (its
        // Guard A refuses a label glued to a letter or digit). Rewriting punctuation first would change
        // what it sees, so the repair only ever touches the prose the parser already handed back.
        var (repaired, emDashes) = ProductChatPunctuation.Repair(answer);
        if (emDashes > 0)
        {
            // A silent rewrite of model output that says nothing is a layer that ships its failures
            // invisibly. The COUNT is logged (never the answer text, same rule as the question): this
            // line is the only thing that would say so if a prompt or corpus change started producing
            // em-dashes materially above the rate the live runs measured. That rate is g3's, and it is
            // the only one measured WITH this layer in place: 5 repairs across 108 answers (4.6%), all
            // English. g1's 1-of-72 and g2's 1-of-102 counted only the em-dashes that reached a reader,
            // not the ones the model produced, so they are floors and not the model's rate.
            _logger.LogWarning(
                "Product chat REPAIRED the answer punctuation before returning it: replaced {EmDashCount} " +
                "em-dash(es) (U+2014) with a comma. Model output is user-facing text, so it is covered by the " +
                "no-em-dash rule that the authored strings already honour, but no authored-string discipline " +
                "reaches it. Answered in {Language} via {Provider}/{Model} from guides [{CitedGuideIds}]. " +
                "A rate materially above the measured ~5 in 108 wants a prompt or corpus look, not a wider rewrite.",
                emDashes, language, response.Provider, response.Model, string.Join(", ", guideIds));
        }

        answer = repaired;

        _logger.LogInformation(
            "Product chat answered in {Language} from guides [{CitedGuideIds}] (selected [{SelectedGuideIds}]) " +
            "via {Provider}/{Model}. Question {QuestionChars} chars, history {ForwardedTurns} of {ReceivedTurns} " +
            "turns forwarded, instruction {InstructionChars} chars, ~{EstimatedTokens} of {BudgetTokens} input tokens.",
            language, string.Join(", ", guideIds), string.Join(", ", Ids(selected)),
            response.Provider, response.Model, question.Length, composed.History.Count, receivedTurns,
            instruction.Length, composed.EstimatedTokens, composed.BudgetTokens);

        return new ProductChatResponseDto(answer, guideIds, language, IsGrounded: true, FaultReason: null);
    }

    /// <summary>
    /// The input-token budget for this task, read off the SAME resolved config the provider itself
    /// uses for <c>num_ctx</c> and <c>num_predict</c> (<c>Ai:ProviderSettings:Ollama_ProductChat</c>
    /// today: 16384 minus 2048 minus the scaffolding reserve). Derived rather than hard-coded, so
    /// re-tuning the window moves the guard with it instead of leaving a stale constant behind.
    ///
    /// <para>ProductChat is outside <c>AiTierPolicy.TieredTasks</c> and has no book id, so it always
    /// resolves on the Fast rung - the same 2-argument resolution the router performs for it.</para>
    /// </summary>
    private int InputTokenBudget()
    {
        var options = _aiOptions.Value;
        return ProductChatBudget.InputTokenBudget(
            BookContextAssembler.ResolveNumCtxForTask(options, AiTaskType.ProductChat),
            BookContextAssembler.ResolveOutputReserveForTask(options, AiTaskType.ProductChat));
    }

    /// <summary>
    /// Says WHAT was given up and WHY, at Warning, because a prompt that quietly shed its context is
    /// indistinguishable from one that never had any once the answer comes back.
    /// </summary>
    private void LogTrim(ProductChatBudget.Composition composed, int cappedTurns)
    {
        if (composed.Trimmed)
        {
            _logger.LogWarning(
                "Product chat TRIMMED the prompt to fit the context budget: ~{EstimatedTokens} estimated input " +
                "tokens against {BudgetTokens}. Dropped {DroppedTurns} of {CappedTurns} history turn(s), oldest " +
                "first, and {DroppedGuideCount} guide(s) [{DroppedGuideIds}]. History is given up BEFORE guides on " +
                "purpose: an answer missing conversation context is recoverable, an answer missing its source is " +
                "the ungrounded answer this feature exists to prevent. Guides kept: [{KeptGuideIds}].",
                composed.EstimatedTokens, composed.BudgetTokens, composed.DroppedTurns, cappedTurns,
                composed.DroppedGuideIds.Count, string.Join(", ", composed.DroppedGuideIds),
                string.Join(", ", Ids(composed.Guides)));
        }

        if (composed.StillOverBudget)
        {
            _logger.LogWarning(
                "Product chat prompt STILL exceeds the context budget after trimming: ~{EstimatedTokens} " +
                "estimated input tokens against {BudgetTokens}, with {GuideCount} guide(s) [{KeptGuideIds}] and " +
                "{HistoryTurns} history turn(s). Nothing further can be given up without giving up the grounding " +
                "itself, so it is sent as is and the runtime may truncate it from the START. Widen " +
                "Ai:ProviderSettings:Ollama_ProductChat NumCtx or shorten the guides.",
                composed.EstimatedTokens, composed.BudgetTokens, composed.Guides.Count,
                string.Join(", ", Ids(composed.Guides)), composed.History.Count);
        }
    }

    /// <summary>
    /// The last <see cref="MaxHistoryTurns"/> non-blank turns, oldest first, each truncated to
    /// <see cref="MaxHistoryTurnChars"/>. Blank turns are dropped BEFORE the cap so a client padding
    /// its transcript with empties cannot push real context out of the window.
    /// </summary>
    internal static IReadOnlyList<ProductChatTurn> CapHistory(IReadOnlyList<ProductChatTurnDto>? history)
    {
        if (history == null || history.Count == 0) return Array.Empty<ProductChatTurn>();

        return history
            .Where(t => !string.IsNullOrWhiteSpace(t.Content))
            .TakeLast(MaxHistoryTurns)
            .Select(t => new ProductChatTurn(
                IsUser: !string.Equals(t.Role, "assistant", StringComparison.OrdinalIgnoreCase),
                Content: Truncate(t.Content!.Trim(), MaxHistoryTurnChars)))
            .ToList();
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max];

    private static IEnumerable<string> Ids(IReadOnlyList<GuideDocument> guides)
        => guides.Select(g => g.Id).Distinct(StringComparer.OrdinalIgnoreCase);

    private static ProductChatResponseDto FailSafe(string language, string fault)
    {
        var isHebrew = ChatLanguage.IsHebrew(language);
        var answer = fault is ProductChatFaults.ModelUnavailable or ProductChatFaults.EmptyAnswer
            ? (isHebrew ? AnswerUnavailableHe : AnswerUnavailableEn)
            : (isHebrew ? GuidesUnreachableHe : GuidesUnreachableEn);

        return new ProductChatResponseDto(answer, Array.Empty<string>(), language, IsGrounded: false, FaultReason: fault);
    }
}
