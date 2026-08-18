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
///
/// <para>SIZE WAIVER (g2): this file is ~708 lines, a little over the workspace's ~700-line soft
/// ceiling, and is deliberately not split here. The +96 g2 added is the routed read/guide/citation
/// decisions and their reasons, all of which belong on the one method that owns a turn's flow; the
/// natural split - lifting the fail-safes into their own type - would move code that g3 is about to
/// measure, days before the gate that measures it. Split it after g3, not before.</para>
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

    /// <summary>
    /// How many guides ride along on <see cref="ChatRoute.Book"/> (g2). The route means the answer comes
    /// out of the BOOK section, so the guides are there only for the one sentence that still points at
    /// them ("what you say about PageDraft itself comes only from the guides below"); one is enough to
    /// keep that sentence honest, and it is <see cref="ProductChatBudget.MinGuides"/>, so the composition
    /// never has to trim below what it was handed. The tokens go to book artifacts, which is the half of
    /// the answer this route is actually built from.
    ///
    /// <para>APPLIED BY TRIMMING THE SCORED SELECTION, not by selecting again: the selection has to happen
    /// BEFORE the route, because the router thresholds the top score the ranking already computed. The
    /// scored list is best-first, so taking its head is exactly what selecting one would have
    /// returned.</para>
    /// </summary>
    public const int BookRouteGuideCount = 1;

    /// <summary>
    /// How many guides ride along on <see cref="ChatRoute.General"/> (g3). NONE, and the zero is the fix.
    ///
    /// <para>g2 gave this route a prompt that said to mention PageDraft "only where the guides below say
    /// it" while still sending phase A's four guides, so a craft turn carried four documents of product
    /// prose the answer was told not to use. g3 measured 8 Hebrew craft turns and 3 invented a PageDraft
    /// behaviour outright - Chapter recap detecting repeated dialogue, the Linguistic pass warning about
    /// emotional depth, PageDraft warning you when you change narrative person, which no guide mentions at
    /// all. A model improvises around what is in front of it; the answer was to stop putting it there.
    /// The route is REACHED only when the router saw a craft signal, no product signal, no book signal and
    /// no open book, so there is no question shape here a guide was going to answer.</para>
    ///
    /// <para>IT IS BELOW <see cref="ProductChatBudget.MinGuides"/> ON PURPOSE, AND THAT IS SAFE ONLY
    /// BECAUSE OF WHERE IT IS APPLIED. MinGuides is the budget TRIMMER's floor: "an answer that lost its
    /// last guide to a token overrun is not an answer this feature is willing to give". This is not a trim
    /// - it is the route deciding what grounding the turn has at all, one step before composition, exactly
    /// as <see cref="BookRouteGuideCount"/> is. The trimmer never sees a selection it can cut below its own
    /// floor.</para>
    /// </summary>
    public const int GeneralRouteGuideCount = 0;

    // ─── The deterministic answer for a book question with no book open (g2, plan item 8d) ──────
    //
    // IT IS CODE, NOT A PROMPT SENTENCE, and that is the whole of the decision. The sentence it replaces
    // told the model to say that answering about a specific book "is not available yet and is coming",
    // which stopped being true in phase B and which the model was measured reading back verbatim (6 of 6
    // runs of that question shape in g2, including the imperative). A fixed string cannot go stale
    // against the model's compliance, costs no round trip, and can be read by the author in the language
    // they asked in.
    //
    // SINCE g3 THIS IS ALSO THE SENTENCE THE PROMPT ASKS FOR ON THE ONE ROUTE THAT STILL CARRIES A BOOK
    // REFUSAL (ProductChatPromptBlocks.BookRefusalHe quotes OpenTheBookHe word for word). The two paths
    // must not tell the author two different stories about the same product, so a change to either of
    // these strings is a change to that block as well.
    //
    // NO EM-DASH: these strings are rendered to the user.

    private const string OpenTheBookHe =
        "אני יכול לראות ספר רק כשהוא פתוח. פתחו את הספר שעליו אתם שואלים ושאלו אותי שוב, ואסתכל בו.";

    private const string OpenTheBookEn =
        "I can only see a book while it is open. Open the book you are asking about and ask me again, " +
        "and I will look at it.";

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
    private readonly ProductChatGroundingCapture? _capture;
    private readonly IOptions<ProductChatOptions>? _productChatOptions;

    /// <param name="productChatOptions">
    /// The routing flag (g1), OPTIONAL and defaulted to null for the same reason
    /// <paramref name="capture"/> is: this service is constructed directly by the composed-prompt pin
    /// tests, which must keep passing with zero edits, and a required parameter would have forced an edit
    /// to the very files that prove the prompt did not move. NULL MEANS ROUTING OFF, which is also the
    /// class default of <see cref="ProductChatOptions.RoutingEnabled"/>, so the two ways of not
    /// configuring it agree.
    /// </param>
    /// <param name="capture">
    /// Show C1's per-request grounding scratchpad, OPTIONAL and defaulted to null. Optional because this
    /// service is constructed directly by the composed-prompt pin tests, which must keep passing with zero
    /// edits: a required parameter would have forced an edit to the very files that prove the prompt did
    /// not move. Null means "nobody is recording this turn", which is the correct behaviour for a test and
    /// for any future caller outside a request scope. It NEVER affects what is composed or answered.
    /// </param>
    public ProductChatService(
        GuidesCorpusReader guides,
        IAiRouter router,
        IOptions<AiOptions> aiOptions,
        IBookChatContextReader bookContext,
        ILogger<ProductChatService> logger,
        ProductChatGroundingCapture? capture = null,
        IOptions<ProductChatOptions>? productChatOptions = null)
    {
        _guides = guides;
        _router = router;
        _aiOptions = aiOptions;
        _bookContext = bookContext;
        _logger = logger;
        _capture = capture;
        _productChatOptions = productChatOptions;
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

        // SCORED, so the router can threshold the top score the ranking ALREADY computed rather than
        // ranking the corpus a second time with a copy of the weights (g1). Select() itself now delegates
        // to SelectScored, so there is one ordering and the two cannot disagree.
        var scored = GuideSelector.SelectScored(
            question, corpus.Documents, language,
            request.BookId.HasValue ? BookAwareGuideCount : GuideSelector.DefaultCount);
        IReadOnlyList<GuideDocument> selected = scored.Select(s => s.Document).ToList();
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

        // ─── THE ROUTE (g1). RESOLVED ALWAYS, APPLIED ONLY BEHIND THE FLAG ───────────────────────
        //
        // Resolving is a pure string scan, so it is done on every turn even while the flag is off: a
        // route nobody can see is a route nobody can calibrate, and g3 needs the resolved route beside
        // the answer it is scoring. What the flag gates is USE. With it off the composed prompt is forced
        // to Union, so an unconfigured deployment takes no routing decision at all.
        //
        // g3 NOTE: Union is no longer byte-identical to the pre-routing message. Exactly one sentence of
        // its book-refusal arm moved, because that sentence had been false since phase B and g3 measured
        // it reaching real users (ProductChatPromptBlocks.BookRefusalEn). Turning the flag off is still the
        // rollback for every ROUTING decision; it is not a rollback to a prompt that says the book feature
        // is coming, and nothing should re-introduce one.
        var routingEnabled = _productChatOptions?.Value.RoutingEnabled ?? false;
        var guideTopScore = GuideSelector.TopScore(scored);
        var resolvedRoute = ProductChatRouter.Resolve(
            question, request.BookId.HasValue, language, guideTopScore);
        var route = routingEnabled ? resolvedRoute : ChatRoute.Union;

        // ─── A BOOK QUESTION WITH NO BOOK OPEN IS ANSWERED HERE, IN CODE (g2) ────────────────────
        //
        // No model call, no prompt, no route: there is nothing to compose an answer FROM, so the honest
        // answer is a fixed one and it is returned before the model is ever reached. This is the same
        // shape as the fail-safes below - "never from priors" and "never claim the feature is coming"
        // both have to be properties of the code path rather than of the model's compliance - and it is
        // what let g2 delete the false coming-soon refusal from every composed route.
        if (routingEnabled
            && ProductChatRouter.AsksAboutABookThatIsNotOpen(question, request.BookId.HasValue, guideTopScore))
        {
            _logger.LogInformation(
                "Product chat answered DETERMINISTICALLY in {Language}: the question names a place inside a " +
                "manuscript and the request carried no bookId, so there is nothing to ground an answer in " +
                "and no model was called. Question {QuestionChars} chars, guide top score {GuideTopScore}. " +
                "The route the router would have resolved was {ResolvedRoute}.",
                language, question.Length, guideTopScore, resolvedRoute);

            // IsGrounded is TRUE and FaultReason is NULL because this is an ANSWER, not a failure. The
            // client renders a fail-safe with its own per-reason copy and deliberately discards the
            // server's prose (product-chat.component.ts, acceptResponse), so shipping this sentence as a
            // fault would replace it with "I cannot reach the guides right now" - the opposite of honest.
            return new ProductChatResponseDto(
                ChatLanguage.IsHebrew(language) ? OpenTheBookHe : OpenTheBookEn,
                Array.Empty<string>(), language, IsGrounded: true, FaultReason: null,
                ArtifactRefs: Array.Empty<string>(), BookFaultReason: null);
        }

        // ─── g3d/gate 4: AN ENGLISH PRODUCT TURN UNDER THE FLOOR IS HANDED NO DOCUMENTS ──────────
        //
        // THE ONE STRUCTURAL LEVER OF THIS ROUND, AND IT CHANGES NO PROMPT STRING. Four gate runs and four
        // re-wordings left the English product-uncovered cell at 8/8, 8/8, 8/8, 7/8 source-narrating, which
        // is a draw; the residual is not the exemplar. The only configuration measured at 0 of 16 narration
        // in all four runs is the one where the model is handed nothing to talk about, so an English product
        // turn whose corpus scored below the floor gets it. Hebrew is excluded IN THE PREDICATE and must
        // stay excluded - its covered cell answers well at exactly the scores this cut would take away. Read
        // ProductChatRouter.EnglishProductDocumentsFloor for the score distribution and the two covered
        // records that move with it, and for the honest statement that the floor is an unmeasured number.
        var documentsFloor = _productChatOptions?.Value.EnglishProductDocumentsFloor
                             ?? ProductChatRouter.EnglishProductDocumentsFloor;
        var withholdDocuments = ProductChatRouter.WithholdsProductDocuments(
            route, language, guideTopScore, documentsFloor);

        // THE ROUTE DECIDES HOW MANY GUIDES THE TURN HAS. Trimmed from the SCORED selection rather than
        // re-selected: the selection had to run first so the router could threshold its top score.
        // The Book route pays the guides for the artifacts; the General route pays them for nothing at
        // all, because a craft answer that was handed product prose is a craft answer that invents product
        // behaviour (g3: 3 of 8 Hebrew turns). See BookRouteGuideCount and GeneralRouteGuideCount.
        var routeGuideCount = route switch
        {
            ChatRoute.Book => BookRouteGuideCount,
            ChatRoute.General => GeneralRouteGuideCount,
            // LITERALLY THE GENERAL ROUTE'S COUNT, reused rather than re-declared as a third number. Gate 4
            // named General's treatment as the thing to copy, and a second zero with its own name is a
            // second thing to keep in step with the first.
            ChatRoute.Product when withholdDocuments => GeneralRouteGuideCount,
            _ => selected.Count,
        };

        if (selected.Count > routeGuideCount)
        {
            selected = selected.Take(routeGuideCount).ToList();
        }

        if (withholdDocuments)
        {
            // LOGGED ON ITS OWN LINE so the next gate can attribute per record without re-deriving the
            // decision from the score, and so the ROUTED line below keeps the exact shape the existing gate
            // harness parses. No question text, the same rule as everywhere else in this service.
            _logger.LogInformation(
                "Product chat WITHHELD the documents on this turn: {Language} {Route}, guide top score " +
                "{GuideTopScore} below the English product documents floor of {DocumentsFloor}. The model " +
                "gets the product grounding rule and no guide text and no citation sentence, so the only " +
                "answer available to it is the refusal that rule ends with. Guides dropped: {DroppedGuides}.",
                language, route, guideTopScore, documentsFloor, scored.Count);
        }

        // ─── The BOOK half (phase B). Absent bookId = no read, no blocks, no prompt change ───────
        //
        // It runs in its OWN try beside the guides half, and a failure here NEVER takes the guides half
        // down: one broken status lookup must not silence an otherwise-fine guide-grounded answer.
        //
        // THE ROUTE CAN ALSO SUPPRESS THE READ (g2). Product and General compose a book-LESS message by
        // definition, so retrieving artifacts for them would render a BOOK section into the prompt with no
        // rule above it governing what may be said from it - grounding with no contract, which is the
        // shape of every collision this prompt has recorded. Skipping the read is the same decision taken
        // one step earlier, and it costs a database round trip less.
        var book = BookChatContext.None;
        if (request.BookId.HasValue && RouteReadsTheBook(route))
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
            book.Keys, route);
        var instruction = composed.Instruction;
        selected = composed.Guides;
        LogTrim(composed, history.Count);

        // The route is logged whether or not it was applied, and BOTH values are logged, because "what it
        // would have done" is the only calibration data that exists while the flag is off. No question
        // text: the same rule as everywhere else in this service.
        //
        // THE PREFIX OF THIS MESSAGE, UP TO AND INCLUDING THE SCORE, IS PARSED BY THE LIVE GATE HARNESS to
        // attach a route and a score to every recorded turn. New facts go on the END of it (or on their own
        // line, as the withholding above does); re-ordering the head silently empties four runs' worth of
        // route columns.
        _logger.LogInformation(
            "Product chat ROUTED this turn to {ResolvedRoute} (applied: {AppliedRoute}; routing enabled: " +
            "{RoutingEnabled}). Language {Language}, bookId present: {HasBookId}, guide top score " +
            "{GuideTopScore} against a strong-match threshold of {StrongGuideTopScore}. Documents withheld: " +
            "{DocumentsWithheld} (English product documents floor {DocumentsFloor}). A route resolved " +
            "but not applied changed nothing about this answer: the turn composed Union either way.",
            resolvedRoute, route, routingEnabled, language, request.BookId.HasValue,
            guideTopScore, ProductChatRouter.StrongGuideTopScore, withholdDocuments, documentsFloor);

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
                // "say that you can only see a book while it is open". Two emphatic rules that
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
        //
        // THE GENERAL ROUTE LICENSES NOTHING, AND THE REASON IS THE PARSER'S FALLBACK (g2). Its prompt
        // asks for no citation line, because an answer out of Show's own knowledge has no guide to name -
        // and ProductChatCitations, by an explicit fail-safe decision, returns the FULL acceptable set
        // when it finds no line. Handing it the surviving guides here would therefore decorate every
        // general answer with chips for guides the answer never used, which is the false sourcing this
        // route exists to remove, arriving through the belt instead of through the prompt. An empty set
        // also keeps the parser's one safety property intact in the strongest form: a citation can only
        // ever NARROW what the turn carried, and here the turn licenses none.
        //
        // A WITHHELD PRODUCT TURN TAKES THE SAME EMPTY SET, WHICH IS THE SAME DECISION AND NOT A THIRD
        // POLICY (g3d/gate 4). It was handed no documents, so it licenses no citation, exactly as General
        // does and for exactly General's reason. Written as an explicit arm rather than left to fall out of
        // an empty AcceptableReferences: today the two agree only because a withheld turn also carries no
        // book artifacts (Product never reads the book), and a policy that holds by coincidence is a policy
        // that stops holding without anyone editing it.
        var acceptable = route == ChatRoute.General || withholdDocuments
            ? Array.Empty<string>()
            : composed.AcceptableReferences;

        // WHAT A PRODUCT REFUSAL CITES, DECIDED HERE RATHER THAN LEFT TO THE PARSER'S FALLBACK (g3c). The
        // General route above is the same decision one step earlier: it licenses nothing because it uses
        // nothing. The PRODUCT route cannot be decided in advance that way - most of its turns really are
        // answered out of the guides - but it is the one route whose own grounding block ends by telling the
        // model to say it does not have the answer and stop, so a reply that names no guide is an ORDINARY
        // outcome here and not a parse failure. g3c measured the fallback publishing four guide chips under
        // exactly those refusals: narrowed citations 32/36 to 20/36, the 4-id full selection 4 to 16.
        //
        // Book and Union keep the fallback deliberately. On Book the chips are how an author checks an answer
        // against their own manuscript, and Union is the status quo every misroute lands on.
        var onMiss = route == ChatRoute.Product
            ? ProductChatCitations.MissPolicy.CiteNothingWhenNothingIsNamed
            : ProductChatCitations.MissPolicy.FallBackToTheCarriedSet;

        var (answer, references) = ProductChatCitations.Extract(
            response.Content.Trim(), acceptable, onMiss);

        var guideIds = references.Where(r => !BookArtifactRefs.LooksLikeArtifactRef(r)).ToList();
        var artifactRefs = references.Where(BookArtifactRefs.LooksLikeArtifactRef).ToList();

        // THE INTERNAL-TOKEN STRIP RUNS AFTER THE CITATION PARSE FOR THE SAME REASON THE PUNCTUATION REPAIR
        // DOES, and before it because it only ever DELETES: it removes bracketed labels, wire refs and the
        // whole/EXCERPT gloss from the PROSE (final-r03), leaving the citation line - the one place a ref
        // belongs - to the parser above, which has already taken it out of the answer when it accepted it.
        // A token WRAPPED in backticks is removed WITH its wrapper, on both the token path and the
        // whole-group path, so neither leaves the code-span parity the repair below depends on worse than
        // it found it. That is load-bearing rather than tidy, and it was measured: when the group path
        // still left the wrapper behind, an answer reading "Text `(EXCERPT)` and a dash [em-dash] here"
        // stripped to a stray backtick pair and the repair below then found ZERO em-dashes in an answer
        // that had two, because that layer's code-span state is never reset at a newline.
        // ProductChatInternalLabelStripTests pins both paths. It is not an absolute: a whole-group removal
        // deletes the group's CONTENT too, which can hold an odd number of backticks the model wrote
        // itself. That direction only ever RESTORES parity the model had already broken, so it is left.
        var (stripped, internalLabels) = ProductChatInternalLabels.Strip(answer, out var keptInsteadOfEmptying);
        var strippedChars = answer.Length - stripped.Length;

        if (keptInsteadOfEmptying > 0)
        {
            // THE STRIP REFUSED (ProductChatInternalLabels DECISION 6): every word of this answer was
            // internal, so removing them would have left the reader an empty card. The tokens ship. This is
            // logged at WARNING and not folded into the count below because it is a different event with a
            // different owner: the count says the RENDERING guard fired, this says the MODEL returned an
            // answer that was nothing but its own scaffolding, which is a prompt/grounding question. It is
            // also the branch that keeps the emission rate honest - these tokens were emitted, and a gate
            // reading only removals would not see them.
            _logger.LogWarning(
                "Product chat KEPT {KeptInternalLabelCount} internal token(s) in the answer prose that it " +
                "would otherwise have removed: the strip would have emptied the answer, and an empty card " +
                "that claims to be grounded is worse than a leaked token. The answer was {AnswerChars} " +
                "chars and nothing else survived the strip. Answered in {Language} via {Provider}/{Model}. " +
                "This means the MODEL returned an answer made only of its own scaffolding, which is a " +
                "grounding question and not a rendering one.",
                keptInsteadOfEmptying, answer.Length, language, response.Provider, response.Model);
        }
        else if (internalLabels > 0)
        {
            // Same rule as the em-dash count below: a silent rewrite of model output that says nothing is
            // a layer that ships its failures invisibly. THIS COUNT AND THE KEPT COUNT ABOVE ARE THE ONLY
            // PLACE THE UNDERLYING RATE STAYS VISIBLE once the strip is on, and a gate scoring the returned
            // answer is scoring the POST-strip text: what the author sees changed here, what the model
            // emits did not. The measured rates this is calibrated against are g4's 3 of 38 bracketed
            // labels and 5 of 38 refs, and final-r03's attribution run at 4 of 16 (pre-be-c02 clause)
            // against 1 of 16 (HEAD).
            //
            // THE CHARACTER COUNT IS LOGGED BESIDE THE TOKEN COUNT because a token is not a quantity: one
            // token can carry a whole parenthetical away with it (bounded at
            // ProductChatInternalLabels.MaxGroupChars), so "REMOVED 1" alone cannot tell a gloss from a
            // clause. It is a LENGTH, never the text - the same rule as the question.
            _logger.LogWarning(
                "Product chat REMOVED {InternalLabelCount} internal token(s), {RemovedChars} of " +
                "{AnswerChars} chars, from the answer prose before returning it: bracketed chapter labels, " +
                "artifact refs or whole/EXCERPT glosses, none of which the author can act on and all of " +
                "which malform inside RTL prose. Answered in {Language} via {Provider}/{Model}. This is a " +
                "RENDERING guard, not a fix to what the model emits: a rate materially above the measured " +
                "~1 in 16 book-scoped answers wants a look at the grounding clause, but NOT a prohibition " +
                "naming the token, which this program has recorded failing three times.",
                internalLabels, strippedChars, answer.Length, language, response.Provider, response.Model);
        }
        else
        {
            // RAN AND FOUND NOTHING IS NOT THE SAME EVENT AS DID NOT RUN, and with only the warning above
            // the two are one silence. A gate computing a leak rate needs the DENOMINATOR - answers this
            // layer inspected - and a denominator taken from "answers that logged a removal" is the rate
            // itself. Debug, because it fires on every clean answer, which is nearly all of them.
            _logger.LogDebug(
                "Product chat found NO internal tokens in the answer prose ({AnswerChars} chars); the strip " +
                "ran and changed nothing. Answered in {Language} via {Provider}/{Model}. This line is the " +
                "denominator: without it a clean answer and an answer this layer never saw look identical.",
                answer.Length, language, response.Provider, response.Model);
        }

        answer = stripped;

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

        // THE BRACES. The emptiness check at the top of this method runs on response.Content, which is
        // UPSTREAM OF EVERY REWRITE: it says the MODEL answered, and says nothing about what these three
        // layers left of it. This says what the AUTHOR gets. Until this line the DTO below could ship
        // Answer: "" with IsGrounded: true and FaultReason: null - an empty card claiming to be grounded -
        // and it did: an answer of "(EXCERPT)" is entirely a gloss, and the strip removed all of it.
        //
        // The belt is ProductChatInternalLabels' own never-empty guard (its DECISION 6), which returns the
        // ORIGINAL text rather than nothing and is where the STRIP's half of the defect is fixed. This
        // branch is unreachable THROUGH THE STRIP while that guard holds - and it is reachable through the
        // other two layers, which have no such guard, so it is live coverage and not only a fence. The
        // measured route: an answer of a single em-dash passes the IsNullOrWhiteSpace check above (it is
        // not whitespace), the strip finds no token and returns it unchanged, and ProductChatPunctuation
        // DROPS a dash that both opens and ends its line, leaving "". A test reaches this without touching
        // the belt. Keeping the check here rather than in each layer is deliberate: it covers ALL THREE
        // rewrites, so a future change to any of them degrades to an honest refusal, not a blank answer.
        if (string.IsNullOrWhiteSpace(answer))
        {
            _logger.LogError(
                "Product chat REWROTE THE ANSWER AWAY: the model returned {ModelChars} chars and the " +
                "citation, internal-token and punctuation layers between them left {FinalChars}. Returning " +
                "the fail-safe ({Fault}) rather than an empty answer flagged as grounded. This is a defect " +
                "in a rewrite layer, not a model failure. Read the layer counts on the lines above before " +
                "blaming the strip: only ProductChatInternalLabels carries a never-empty guard, so an " +
                "answer emptied with its removal count at zero was emptied by one of the other two. " +
                "Answered in " +
                "{Language} via {Provider}/{Model}.",
                response.Content.Length, answer.Length, ProductChatFaults.EmptyAnswer,
                language, response.Provider, response.Model);
            return FailSafe(language, ProductChatFaults.EmptyAnswer);
        }

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

        // SHOW C1'S GROUNDING SNAPSHOT IS TAKEN HERE, FROM THE SAME VALUES THE LINE ABOVE FORMATS, so the
        // stored snapshot and the log can never independently drift from one event. It is a capture, not a
        // second derivation: nothing below reads it, and a null capture (every direct-construction test)
        // makes this a no-op.
        _capture?.CaptureCitation(
            language, guideIds, Ids(selected), artifactRefs,
            composed.BookBlocks.SelectMany(b => b.References),
            response.Provider, response.Model,
            composed.History.Count, receivedTurns, instruction.Length,
            composed.EstimatedTokens, composed.BudgetTokens);

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
    /// WHETHER THIS ROUTE HAS A RULE TO GOVERN BOOK ARTIFACTS WITH (g2). Only
    /// <see cref="ChatRoute.Book"/> and <see cref="ChatRoute.Union"/> compose a message that says what may
    /// be asserted from the BOOK section; the other two compose a book-less one, so retrieving artifacts
    /// for them would put grounding in the prompt with no contract above it. Written as a predicate rather
    /// than inlined so the read and the composition read the same rule off one line.
    /// </summary>
    private static bool RouteReadsTheBook(ChatRoute route)
        => route is ChatRoute.Book or ChatRoute.Union;

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
