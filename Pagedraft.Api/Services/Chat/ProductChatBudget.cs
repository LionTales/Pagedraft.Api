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
    /// <param name="Instruction">The composed instruction to send.</param>
    /// <param name="Guides">The guides that SURVIVED, in selection order. Use this - not the original
    /// selection - for citations, or the answer can cite a guide it never saw.</param>
    /// <param name="History">The history turns that survived, oldest first.</param>
    /// <param name="DroppedTurns">History turns dropped to fit, oldest first.</param>
    /// <param name="DroppedGuideIds">Guide ids dropped to fit, lowest-ranked first. Empty in every
    /// case the history alone could absorb.</param>
    /// <param name="EstimatedTokens">Estimated input tokens of what is actually being sent.</param>
    /// <param name="BudgetTokens">The budget it was fitted against.</param>
    public sealed record Composition(
        string Instruction,
        IReadOnlyList<GuideDocument> Guides,
        IReadOnlyList<ProductChatTurn> History,
        int DroppedTurns,
        IReadOnlyList<string> DroppedGuideIds,
        int EstimatedTokens,
        int BudgetTokens)
    {
        public bool Trimmed => DroppedTurns > 0 || DroppedGuideIds.Count > 0;

        /// <summary>True when even <see cref="MinGuides"/> guide(s) and no history do not fit. Nothing
        /// further can be given up without giving up the grounding itself, so the caller sends it and
        /// says so loudly.</summary>
        public bool StillOverBudget => EstimatedTokens > BudgetTokens;
    }

    /// <summary>
    /// Composes the instruction and trims it until it fits <paramref name="budgetTokens"/>, dropping
    /// the OLDEST history turn first and only then the LOWEST-ranked guide.
    /// </summary>
    /// <param name="question">Sent separately as the request's input text, so it is measured here even
    /// though <see cref="ProductChatPrompt.ComposeInstruction"/> does not contain it.</param>
    public static Composition Compose(
        string language,
        IReadOnlyList<GuideDocument> guides,
        IReadOnlyList<ProductChatTurn> history,
        string question,
        int budgetTokens)
    {
        var keptGuides = guides.ToList();
        var keptHistory = history.ToList();
        var droppedTurns = 0;
        var droppedGuideIds = new List<string>();

        while (true)
        {
            var instruction = ProductChatPrompt.ComposeInstruction(language, keptGuides, keptHistory);

            // What the provider actually sends: the system slot, the composed instruction (which
            // restates the rule) and the question appended after it.
            var estimated = EstimateTokens(ProductChatPrompt.SystemMessage(language))
                          + EstimateTokens(instruction)
                          + EstimateTokens(question);

            var fits = estimated <= budgetTokens;
            var nothingLeftToGiveUp = keptHistory.Count == 0 && keptGuides.Count <= MinGuides;

            if (fits || nothingLeftToGiveUp)
            {
                return new Composition(
                    instruction, keptGuides, keptHistory, droppedTurns, droppedGuideIds,
                    estimated, budgetTokens);
            }

            if (keptHistory.Count > 0)
            {
                keptHistory.RemoveAt(0);       // oldest turn: the least useful context to lose
                droppedTurns++;
            }
            else
            {
                droppedGuideIds.Add(keptGuides[^1].Id);
                keptGuides.RemoveAt(keptGuides.Count - 1);   // lowest-ranked guide, last resort
            }
        }
    }
}
