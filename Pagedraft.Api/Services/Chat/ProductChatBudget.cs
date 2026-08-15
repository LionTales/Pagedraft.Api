using Pagedraft.Api.Services.Analysis;

namespace Pagedraft.Api.Services.Chat;

/// <summary>
/// PURE context-budget guard for the product chat prompt (chatbot phase A, g1 finding F2).
///
/// <para>THE FAILURE THIS EXISTS TO PREVENT. Ollama truncates an over-long prompt from the START, and
/// the start is exactly where the grounding rule and the guide text sit. An overrun therefore does not
/// fail: it returns a confident answer built from the model's own priors while the response still says
/// <c>isGrounded=true</c> and still carries guide-id citations. That is the precise failure the whole
/// phase exists to prevent, and it is silent. g1 measured the Hebrew worst case (four whole Hebrew
/// guides plus a FULL eight-turn history at the per-turn cap) at 13,033 tokens against a 14,336 input
/// budget - 90.9%, no live request ever exercised it, and nothing in the code noticed how little slack
/// was left.</para>
///
/// <para>SO THE BOUND IS ON THE COMPOSED PROMPT, NOT ON THE TURN COUNT. A turn cap is a proxy:
/// <c>MaxHistoryTurns</c> x <c>MaxHistoryTurnChars</c> bounds the history but says nothing about the
/// guides beside it, and the guides are the half that grows when the corpus is edited. This measures
/// what will actually be sent (system message + composed instruction + question, because the local
/// provider concatenates all three) and trims until it fits.</para>
///
/// <para>DROP ORDER IS THE WHOLE POINT: HISTORY FIRST, OLDEST FIRST, AND GUIDES ONLY AFTER THE HISTORY
/// IS GONE. An answer that lost its conversation context is recoverable - the user asks again with the
/// missing detail. An answer that lost its source is the ungrounded answer. Guides are never trimmed
/// below <see cref="MinGuides"/>, and a guide is dropped from the TAIL of the selection, which is the
/// LOWEST-ranked one (<see cref="GuideSelector.Select"/> returns best first). A dropped guide is also
/// removed from the list handed to <see cref="ProductChatCitations"/>, so the response can never cite
/// a guide the model was not actually given.</para>
///
/// <para>ESTIMATION, AND WHY IT IS A CHARACTER MODEL. There is no tokenizer on this side of the wire;
/// asking the model to count would cost a round trip per request. The constants below are CALIBRATED
/// against g1's measurement on the routed model's own tokenizer (<c>prompt_eval_count</c> on
/// <c>qwen3.5:9b</c>), which measured WHOLE FILES: 2.07-2.20 chars/token for the Hebrew guides,
/// 3.53-4.59 for the English ones.</para>
///
/// <para>Hebrew and Latin are counted SEPARATELY in one pass rather than by the question's language,
/// because a cross-language selection routinely puts Hebrew guide text in an English turn's prompt
/// (g1 F3 measured that in 25% of English selections) and a single blended rate would under-count
/// exactly that case. That is also why <see cref="HebrewCharsPerToken"/> is BELOW the measured
/// whole-file range instead of inside it: only 70-73% of a Hebrew guide's characters are Hebrew
/// letters (the rest is markdown, digits and English product terms), so the per-script rate has to be
/// lower than the blended one to reproduce the same total. Checked against g1's largest measured
/// case - 17,919 chars of Hebrew guides, 71% Hebrew letters, 8,358 real tokens - these rates estimate
/// 8,553, i.e. 2% ABOVE the truth. The bias is deliberately in that direction throughout:
/// over-estimating trims slightly early, under-estimating lets the runtime silently drop the
/// guides.</para>
/// </summary>
public static class ProductChatBudget
{
    /// <summary>
    /// Tokens held back from the window for what this estimate cannot see: the model's chat template,
    /// role markers, and the rounding error in a character-based estimate. Small, because the estimate
    /// is already pessimistic; non-zero, because a budget with no margin is a budget that is sometimes
    /// exceeded.
    /// </summary>
    public const int PromptOverheadTokens = 256;

    /// <summary>Chars per token for HEBREW LETTERS ALONE. Below g1's 2.07-2.20 whole-FILE range on
    /// purpose: see the class doc, a Hebrew guide is only ~71% Hebrew letters.</summary>
    public const double HebrewCharsPerToken = 1.8;

    /// <summary>Pessimistic floor of g1's measured 3.53-4.59 chars/token for Latin-script text.</summary>
    public const double LatinCharsPerToken = 3.5;

    /// <summary>An answer with no guide at all is not an answer this feature is willing to give.</summary>
    public const int MinGuides = 1;

    /// <summary>
    /// The input tokens a composed prompt may occupy: the model's context window minus what the
    /// provider will let the model GENERATE minus <see cref="PromptOverheadTokens"/>. Both inputs come
    /// from the same resolved config the provider itself reads
    /// (<see cref="BookContextAssembler.ResolveNumCtxForTask"/> /
    /// <see cref="BookContextAssembler.ResolveOutputReserveForTask"/>), so this cannot drift from the
    /// window that is actually configured for this task.
    /// </summary>
    public static int InputTokenBudget(int numCtx, int outputReserve)
        => numCtx - outputReserve - PromptOverheadTokens;

    /// <summary>
    /// Script-aware token estimate. One pass, counting Hebrew letters against the Hebrew rate and
    /// everything else against the Latin rate, so a mixed-language prompt is estimated per part.
    /// </summary>
    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var hebrew = 0;
        foreach (var c in text)
        {
            if (ScriptTokenPredicates.IsHebrewLetter(c)) hebrew++;
        }

        var other = text.Length - hebrew;
        return (int)Math.Ceiling(hebrew / HebrewCharsPerToken + other / LatinCharsPerToken);
    }

    /// <summary>What was composed, what it cost, and what had to be given up to make it fit.</summary>
    /// <param name="SystemMessage">
    /// THE SYSTEM SLOT FOR THIS TURN, and the reason it lives here rather than being recomputed by the
    /// caller. Whether the turn is book-aware is a predicate over the blocks that SURVIVED the trim, and
    /// three places need the answer: the token estimate below, the head of
    /// <see cref="ProductChatPrompt.ComposeInstruction"/>, and the request's
    /// <c>SystemMessageOverride</c>. Deriving it three times is three chances to disagree, and the ONE
    /// disagreement that ever mattered here shipped the book refusal and the book-grounding rule in the
    /// same prompt (g1 F-1). It is derived ONCE, on the same iteration that produced
    /// <paramref name="Instruction"/>, so "exactly one rule reaches the model" is a property of there
    /// being exactly one string, not of two call sites happening to agree.
    ///
    /// <para>With no surviving book block this is byte-identical to what
    /// <c>PromptFactory.GetPrompt(AiTaskType.ProductChat, ...)</c> returns, which is what makes phase A's
    /// byte-identity fence hold BY CONSTRUCTION rather than by the caller remembering to skip the
    /// override.</para>
    /// </param>
    /// <param name="Instruction">The composed instruction to send. It OPENS with
    /// <paramref name="SystemMessage"/>, restated at the head of the user message exactly as phase A
    /// restates it, because Ollama truncates from the START and a rule that lived only in the system slot
    /// is the first thing lost.</param>
    /// <param name="Guides">The guides that SURVIVED, in selection order. Use this - not the original
    /// selection - for citations, or the answer can cite a guide it never saw.</param>
    /// <param name="History">The history turns that survived, oldest first.</param>
    /// <param name="DroppedTurns">History turns dropped to fit, oldest first.</param>
    /// <param name="DroppedGuideIds">Guide ids dropped to fit, lowest-ranked first. Empty in every
    /// case the history alone could absorb.</param>
    /// <param name="EstimatedTokens">Estimated input tokens of what is actually being sent.</param>
    /// <param name="BudgetTokens">The budget it was fitted against.</param>
    /// <param name="BookBlocks">The book artifacts that SURVIVED, in prompt order. Use this - not what
    /// the reader retrieved - for citations, exactly as <paramref name="Guides"/> is used, or the answer
    /// can cite an artifact it never saw.</param>
    /// <param name="DroppedBookRefs">Book-artifact references dropped to fit, in drop order.</param>
    public sealed record Composition(
        string SystemMessage,
        string Instruction,
        IReadOnlyList<GuideDocument> Guides,
        IReadOnlyList<ProductChatTurn> History,
        int DroppedTurns,
        IReadOnlyList<string> DroppedGuideIds,
        int EstimatedTokens,
        int BudgetTokens,
        IReadOnlyList<BookArtifactBlock> BookBlocks,
        IReadOnlyList<string> DroppedBookRefs)
    {
        public bool Trimmed => DroppedTurns > 0 || DroppedGuideIds.Count > 0 || DroppedBookRefs.Count > 0;

        /// <summary>True when even <see cref="MinGuides"/> guide(s), no history and the undroppable book
        /// artifacts do not fit. Nothing further can be given up without giving up the grounding itself,
        /// so the caller sends it and says so loudly.</summary>
        public bool StillOverBudget => EstimatedTokens > BudgetTokens;

        /// <summary>
        /// Every citation reference this turn licenses: the surviving guide ids AND the surviving
        /// book-artifact refs. THE ONE PLACE that answer is computed, because the whole point of deriving
        /// it from the SURVIVORS is that a trim can never leave a citation behind for grounding that was
        /// dropped - the exact failure phase A's latent-budget finding warned about, since Ollama
        /// truncates from the START where the context sits.
        /// </summary>
        public IReadOnlyList<string> AcceptableReferences => Guides
            .Select(g => g.Id)
            .Concat(BookBlocks.SelectMany(b => b.References))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Composes the instruction and trims it until it fits <paramref name="budgetTokens"/>.
    ///
    /// <para>DROP ORDER (phase A's, extended by d1 section (2) for the book artifacts), drop-first to
    /// drop-last: history turns oldest first; then book artifacts by
    /// <see cref="BookArtifactKind"/> ascending and lowest RANK first within a kind; then guides beyond
    /// <see cref="MinGuides"/>; then the remaining book artifacts, up to and including the book-level
    /// brief. The statuses are never dropped while a book is in scope, and the escalated raw chapter text
    /// is protected by sitting BELOW the guides in that order rather than by a special case, which is
    /// what makes "escalation never evicts the artifact that triggered it" a property of the ordering
    /// instead of a rule someone has to remember.</para>
    /// </summary>
    /// <param name="question">Sent separately as the request's input text, so it is measured here even
    /// though <see cref="ProductChatPrompt.ComposeInstruction"/> does not contain it.</param>
    /// <param name="bookBlocks">Retrieved book artifacts, or null/empty when the request carried no
    /// bookId. Empty makes every line below a no-op and the composition byte-identical to phase A's.</param>
    /// <param name="questionKeys">
    /// What the question resolved to (<c>BookArtifactSelector.Select</c>), or null when the request
    /// carried no bookId. It is the ONE input the BOOK section's note is derived from - the ambiguity the
    /// selector could not decide, a chapter number the book does not have, the clarify flag (d2 section
    /// (5)) - and it is passed HERE rather than turned into a note by the caller because the note is only
    /// true of the blocks that SURVIVE the trim, and that set is not known until this loop settles; it is
    /// derived per iteration for the same reason <see cref="Composition.SystemMessage"/> is.
    ///
    /// <para>PASSED WHOLE RATHER THAN FIELD BY FIELD (w9). The note is a statement ABOUT the selection, so
    /// assembling it from arguments a call site picked off that selection is what lets one caller emit a
    /// note the selection does not support; and a third note now needs no new parameter at any layer
    /// between the selector and the renderer. The RESPONSE flag the client branches on comes from these
    /// same keys and never from what the model wrote, so the prose and the flag cannot disagree.</para>
    /// </param>
    public static Composition Compose(
        string language,
        IReadOnlyList<GuideDocument> guides,
        IReadOnlyList<ProductChatTurn> history,
        string question,
        int budgetTokens,
        IReadOnlyList<BookArtifactBlock>? bookBlocks = null,
        string? bookTitle = null,
        BookArtifactSelector.BookQuestionKeys? questionKeys = null)
    {
        var keptGuides = guides.ToList();
        var keptHistory = history.ToList();
        var keptBlocks = OrderForPrompt(bookBlocks);
        var droppedTurns = 0;
        var droppedGuideIds = new List<string>();
        var droppedBookRefs = new List<string>();

        while (true)
        {
            // DERIVED ONCE PER ITERATION AND THEN CARRIED, never re-derived downstream. The system message
            // is the BOOK-AWARE one exactly when book artifacts survive, so this same string is what the
            // estimate measures, what ComposeInstruction restates at the head of the user message, and
            // what the caller puts in the request's system slot. See Composition.SystemMessage.
            var systemMessage = ProductChatPrompt.SystemMessage(language, keptBlocks.Count > 0);
            var instruction = ProductChatPrompt.ComposeInstruction(
                language, keptGuides, keptHistory, keptBlocks, bookTitle,
                BookArtifactBlocks.BookSectionNote(language, questionKeys, keptBlocks));

            // What the provider actually sends: the system slot, the composed instruction (which
            // restates the rule) and the question appended after it.
            var estimated = EstimateTokens(systemMessage)
                          + EstimateTokens(instruction)
                          + EstimateTokens(question);

            var fits = estimated <= budgetTokens;
            var nothingLeftToGiveUp =
                keptHistory.Count == 0
                && keptGuides.Count <= MinGuides
                && !keptBlocks.Any(IsDroppable);

            if (fits || nothingLeftToGiveUp)
            {
                return new Composition(
                    systemMessage, instruction, keptGuides, keptHistory, droppedTurns, droppedGuideIds,
                    estimated, budgetTokens, keptBlocks, droppedBookRefs);
            }

            if (keptHistory.Count > 0)
            {
                keptHistory.RemoveAt(0);       // oldest turn: the least useful context to lose
                droppedTurns++;
                continue;
            }

            // Book artifacts ABOVE the guides in the drop order: findings, analysis history, chapter
            // briefs, the register.
            if (TryDropBookBlock(keptBlocks, BookArtifactKind.Register, droppedBookRefs)) continue;

            if (keptGuides.Count > MinGuides)
            {
                droppedGuideIds.Add(keptGuides[^1].Id);
                keptGuides.RemoveAt(keptGuides.Count - 1);   // lowest-ranked guide
                continue;
            }

            // Book artifacts BELOW the guides: the escalated raw chapter text, then the book brief. The
            // statuses are excluded by IsDroppable and so are never reached here.
            if (TryDropBookBlock(keptBlocks, BookArtifactKind.BookBrief, droppedBookRefs)) continue;

            // Unreachable: nothingLeftToGiveUp above covers exactly this state. Stated rather than
            // assumed so a future tier added to BookArtifactKind cannot spin this loop forever.
            var finalSystem = ProductChatPrompt.SystemMessage(language, keptBlocks.Count > 0);
            var final = ProductChatPrompt.ComposeInstruction(
                language, keptGuides, keptHistory, keptBlocks, bookTitle,
                BookArtifactBlocks.BookSectionNote(language, questionKeys, keptBlocks));
            return new Composition(
                finalSystem, final, keptGuides, keptHistory, droppedTurns, droppedGuideIds,
                EstimateTokens(finalSystem) + EstimateTokens(final) + EstimateTokens(question),
                budgetTokens, keptBlocks, droppedBookRefs);
        }
    }

    /// <summary>
    /// Prompt order for the book artifacts: statuses first (they are the tutoring backbone and the one
    /// thing that must survive a runtime truncation from the START), then the book brief, then the rest
    /// by descending survival priority. This is the REVERSE of the drop order, so the block most likely
    /// to be dropped sits last in the prompt too - the two orders agree instead of being maintained
    /// separately.
    /// </summary>
    internal static List<BookArtifactBlock> OrderForPrompt(IReadOnlyList<BookArtifactBlock>? blocks)
        => (blocks ?? Array.Empty<BookArtifactBlock>())
            .OrderByDescending(b => (int)b.Kind)
            .ThenByDescending(b => b.Rank)
            .ThenBy(b => b.References.Count == 0 ? string.Empty : b.References[0], StringComparer.Ordinal)
            .ToList();

    /// <summary>The statuses are the tutoring floor: "the answer is the status plus the next action" is
    /// only possible while they are in the prompt, so they are not droppable at any pressure.</summary>
    private static bool IsDroppable(BookArtifactBlock block) => block.Kind != BookArtifactKind.Status;

    /// <summary>
    /// Drops ONE block: the lowest <see cref="BookArtifactKind"/> present at or below
    /// <paramref name="maxKind"/>, and within that kind the lowest <see cref="BookArtifactBlock.Rank"/>.
    /// Returns false when nothing in range remains.
    /// </summary>
    private static bool TryDropBookBlock(
        List<BookArtifactBlock> blocks, BookArtifactKind maxKind, List<string> droppedRefs)
    {
        BookArtifactBlock? victim = null;

        foreach (var block in blocks)
        {
            if (!IsDroppable(block) || block.Kind > maxKind) continue;

            if (victim == null
                || block.Kind < victim.Kind
                || (block.Kind == victim.Kind && block.Rank < victim.Rank))
            {
                victim = block;
            }
        }

        if (victim == null) return false;

        blocks.Remove(victim);
        droppedRefs.AddRange(victim.References);
        return true;
    }
}
