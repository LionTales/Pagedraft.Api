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

    /// <summary>
    /// How many guides ride along when a bookId IS present (phase B, d1 section (2)). Phase A's default
    /// of 4 is kept for every book-less request.
    ///
    /// <para>THIS IS ARITHMETIC, NOT TASTE. Phase A's own measured Hebrew worst case sits at
    /// 13,806 of 14,080 tokens, which is ~274 tokens of headroom, and d1 measured ONE formatted chapter
    /// brief at ~700-800 tokens against the real dev DB. Book artifacts cannot be appended on top of an
    /// unmodified phase-A payload; the gap is multiples, not a rounding error. Halving the guide count
    /// frees the room proactively instead of leaving every book-scoped turn to the reactive trimmer. The
    /// floor stays <see cref="ProductChatBudget.MinGuides"/>, so a mixed question never loses ALL product
    /// grounding.</para>
    ///
    /// <para>g1 should watch specifically whether 2 guides starves the PRODUCT half of a mixed question;
    /// no live measurement stands behind this number, only the token arithmetic.</para>
    /// </summary>
    public const int BookAwareGuideCount = 2;

    private const string BookUnreachableHe =
        "אינני מצליח לראות כרגע את הספר שלכם, ולכן לא אענה עליו מתוך ניחוש. נסו שוב בעוד רגע.";

    private const string BookUnreachableEn =
        "I cannot see your book right now, so I will not answer about it from guesswork. " +
        "Please try again in a moment.";

    private readonly GuidesCorpusReader _guides;
    private readonly IAiRouter _router;
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly IBookChatContextReader _bookContext;
    private readonly ILogger<ProductChatService> _logger;

    public ProductChatService(
        GuidesCorpusReader guides,
        IAiRouter router,
        IOptions<AiOptions> aiOptions,
        IBookChatContextReader bookContext,
        ILogger<ProductChatService> logger)
    {
        _guides = guides;
        _router = router;
        _aiOptions = aiOptions;
        _bookContext = bookContext;
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

        var selected = GuideSelector.Select(
            question, corpus.Documents, language,
            request.BookId.HasValue ? BookAwareGuideCount : GuideSelector.DefaultCount);
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

        // ─── The BOOK half (phase B). Absent bookId = no read, no blocks, no prompt change ───────
        //
        // It runs in its OWN try beside the guides half, and a failure here NEVER takes the guides half
        // down: one broken status lookup must not silence an otherwise-fine guide-grounded answer.
        var book = BookChatContext.None;
        if (request.BookId.HasValue)
        {
            // The ambient open chapter travels with the read and NOWHERE else: it is a retrieval input,
            // not a second book scope. With both fields null this is byte-identical to the pre-ambient
            // path, which is what keeps g2's verdict a measurement of this code too.
            var ambient = new AmbientChapterContext(request.AmbientChapterId, request.AmbientChapterOrder);

            book = await ReadBookContextAsync(request.BookId.Value, question, language, ambient, ct)
                .ConfigureAwait(false);

            if (book.IsBlind)
            {
                _logger.LogWarning(
                    "Product chat REFUSED to answer about book {BookId} ({Faults}): not one book artifact " +
                    "could be read, not even the statuses. Answering about a manuscript nothing was read " +
                    "from is exactly the failure this refusal exists to prevent, so the caller gets the " +
                    "honest fail-safe instead.",
                    request.BookId.Value, string.Join(", ", book.Faults));
                return BookFailSafe(language, book.Faults);
            }
        }

        // THE PROMPT IS BOUNDED HERE, not by the turn cap above (g1 F2). Ollama truncates from the
        // START - the grounding rule, the guides and the book artifacts - so an overrun returns a
        // confident ungrounded answer that still claims to be grounded. History is dropped first, then
        // the book artifacts in d1's priority order, guides only near the last resort, and the SURVIVING
        // guides and artifacts are what the citation is computed against.
        var composed = ProductChatBudget.Compose(
            language, selected, history, question, InputTokenBudget(), book.Blocks, book.BookTitle,
            book.Keys.AmbiguousChapterNumbers, book.Keys.NeedsChapterClarification);
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
                SourceId = "product-chat",
                // THE SYSTEM SLOT CARRIES THE SAME STRING THE INSTRUCTION OPENS WITH - literally the same
                // string, taken from the composition rather than recomputed here (g1 F-1). PromptFactory
                // sees only the task type, so left to itself it always returned the BOOK-LESS message, and
                // a book-scoped turn then told the model both "answer from the BOOK section below" and
                // "say that answering about a specific book is not available yet". Two emphatic rules that
                // collide are resolved by the model rather than by the author; exactly one rule reaches it
                // now, because exactly one string exists. With no surviving book block that string is
                // byte-identical to PromptFactory's, so phase A's gate verdict is untouched BY
                // CONSTRUCTION and not by this call site remembering to opt out.
                SystemMessageOverride = composed.SystemMessage
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

        // Citations are computed against what SURVIVED composition, guides and book artifacts alike, so a
        // trim can never leave a citation pointing at grounding that was dropped.
        var (answer, references) = ProductChatCitations.Extract(
            response.Content.Trim(), composed.AcceptableReferences);

        var guideIds = references.Where(r => !BookArtifactRefs.LooksLikeArtifactRef(r)).ToList();
        var artifactRefs = references.Where(BookArtifactRefs.LooksLikeArtifactRef).ToList();

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
            "and book artifacts [{CitedArtifactRefs}] (carried [{CarriedArtifactRefs}]) via {Provider}/{Model}. " +
            "Question {QuestionChars} chars, history {ForwardedTurns} of {ReceivedTurns} " +
            "turns forwarded, instruction {InstructionChars} chars, ~{EstimatedTokens} of {BudgetTokens} input tokens.",
            language, string.Join(", ", guideIds), string.Join(", ", Ids(selected)),
            string.Join(", ", artifactRefs),
            string.Join(", ", composed.BookBlocks.SelectMany(b => b.References)),
            response.Provider, response.Model, question.Length, composed.History.Count, receivedTurns,
            instruction.Length, composed.EstimatedTokens, composed.BudgetTokens);

        return new ProductChatResponseDto(
            answer, guideIds, language, IsGrounded: true, FaultReason: null,
            ArtifactRefs: artifactRefs,
            BookFaultReason: book.Faults.Count > 0 ? book.Faults[0] : null,
            // FROM THE SELECTION, NEVER FROM THE ANSWER. The model is also told to ask in prose when this
            // is true, but nothing it writes can set or clear the flag: that is what makes "Show never
            // asks when the chapter resolved" a property of the code rather than of the model's
            // compliance. BookChatContext.None carries Keys.Empty, so this is false on every book-less
            // turn without a second condition to keep in step.
            NeedsChapterClarification: book.Keys.NeedsChapterClarification);
    }

    /// <summary>
    /// Retrieves the book half. Never throws for a broken SOURCE - the reader records typed faults for
    /// that - so this catch exists for the case it breaks its own contract, and it still logs, because a
    /// catch that keeps the endpoint non-throwing and says nothing ships its failures invisibly.
    ///
    /// <para>THE TWO HALVES FAIL INDEPENDENTLY, AND THE GRANULARITY IS THE POINT. A PARTIAL fault (the
    /// review status threw while the briefs read fine) leaves faults on the context and lets the turn
    /// proceed with whatever survived, because a thinner answer beats a refusal. Only
    /// <see cref="BookChatContext.IsBlind"/> - not one artifact readable, faults recorded - earns the
    /// fail-safe, and the caller handles that; see <see cref="BookFailSafe"/>.</para>
    /// </summary>
    private async Task<BookChatContext> ReadBookContextAsync(
        Guid bookId, string question, string language, AmbientChapterContext ambient, CancellationToken ct)
    {
        try
        {
            return await _bookContext.ReadAsync(bookId, question, language, ambient, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Product chat's book-context reader THREW for book {BookId}, which its own contract says it " +
                "does not do for a broken source ({Fault}). The turn degrades to the honest book fail-safe " +
                "rather than to an answer about a manuscript nothing was read from.",
                bookId, BookChatFaults.BookUnavailable);
            return BookChatContext.None with { Faults = new[] { BookChatFaults.BookUnavailable } };
        }
    }

    /// <summary>
    /// The BOOK fail-safe, mirroring phase A's exactly: an honest sentence, <c>isGrounded=false</c>, and
    /// a machine-readable reason. Reached only when NOTHING about the book could be read - not even the
    /// statuses, which are the cheapest and most robust source.
    ///
    /// <para>It is a DETERMINISTIC string rather than a sentence the model is asked to produce from an
    /// artifact, for the same reason phase A's is: "never from priors" cannot be a property of the model's
    /// behaviour under a prompt, it has to be a property of the code path. It costs the guides half of a
    /// mixed question in a state that means the user's book row is unreadable, which is a state where a
    /// confident answer would be the worse trade.</para>
    /// </summary>
    private static ProductChatResponseDto BookFailSafe(string language, IReadOnlyList<string> faults)
    {
        var fault = faults.Count > 0 ? faults[0] : BookChatFaults.BookUnavailable;
        var answer = ChatLanguage.IsHebrew(language) ? BookUnreachableHe : BookUnreachableEn;

        return new ProductChatResponseDto(
            answer, Array.Empty<string>(), language, IsGrounded: false, FaultReason: fault,
            ArtifactRefs: Array.Empty<string>(), BookFaultReason: fault);
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
                "first, {DroppedGuideCount} guide(s) [{DroppedGuideIds}] and {DroppedBookCount} book-artifact " +
                "ref(s) [{DroppedBookRefs}]. History is given up BEFORE guides and book artifacts on " +
                "purpose: an answer missing conversation context is recoverable, an answer missing its source is " +
                "the ungrounded answer this feature exists to prevent. Guides kept: [{KeptGuideIds}]. " +
                "Book artifacts kept: [{KeptBookRefs}].",
                composed.EstimatedTokens, composed.BudgetTokens, composed.DroppedTurns, cappedTurns,
                composed.DroppedGuideIds.Count, string.Join(", ", composed.DroppedGuideIds),
                composed.DroppedBookRefs.Count, string.Join(", ", composed.DroppedBookRefs),
                string.Join(", ", Ids(composed.Guides)),
                string.Join(", ", composed.BookBlocks.SelectMany(b => b.References)));
        }

        if (composed.StillOverBudget)
        {
            _logger.LogWarning(
                "Product chat prompt STILL exceeds the context budget after trimming: ~{EstimatedTokens} " +
                "estimated input tokens against {BudgetTokens}, with {GuideCount} guide(s) [{KeptGuideIds}], " +
                "{HistoryTurns} history turn(s) and {BookBlockCount} undroppable book artifact(s) " +
                "[{KeptBookRefs}]. Nothing further can be given up without giving up the grounding " +
                "itself, so it is sent as is and the runtime may truncate it from the START. Widen " +
                "Ai:ProviderSettings:Ollama_ProductChat NumCtx or shorten the guides.",
                composed.EstimatedTokens, composed.BudgetTokens, composed.Guides.Count,
                string.Join(", ", Ids(composed.Guides)), composed.History.Count,
                composed.BookBlocks.Count,
                string.Join(", ", composed.BookBlocks.SelectMany(b => b.References)));
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

        // ArtifactRefs is EMPTY and BookFaultReason is NULL on every phase-A fail-safe: the guides half
        // failed, which says nothing about the book half and must not be reported as if it did.
        return new ProductChatResponseDto(
            answer, Array.Empty<string>(), language, IsGrounded: false, FaultReason: fault,
            ArtifactRefs: Array.Empty<string>(), BookFaultReason: null);
    }
}
