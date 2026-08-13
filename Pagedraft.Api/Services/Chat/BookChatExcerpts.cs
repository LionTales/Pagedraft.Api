using System.Text;

namespace Pagedraft.Api.Services.Chat;

/// <summary>
/// PURE lexical excerpting of a chapter's plain text for the raw-text escalation (chatbot phase B, c1;
/// d1 section (1)'s excerpt rule).
///
/// <para>WHY THIS IS THE COMMON PATH AND NOT AN EDGE CASE. d1 measured the real dev DB: 229 chapters
/// with text, averaging 7,011 characters (~3,895 tokens at this codebase's Hebrew rate) and reaching
/// 25,211 characters (~14,006 tokens) at the top. A single worst-case chapter can consume the ENTIRE
/// 14,080-token input budget on its own. So the excerpt path is what a long chapter routinely takes,
/// and the whole-chapter path is the one that needs the room to be there.</para>
///
/// <para>THE LABEL IS THE SAFETY PROPERTY, NOT THE EXCERPTING. An excerpt that is merely good is still
/// a fabrication engine if the model believes it read the whole chapter: a confidently-cited "chapter 7
/// does not mention X", built on three sentences, is exactly the class phase B's gate exists to catch.
/// Every block this class produces therefore carries an explicit label saying which of the two it is,
/// and <c>ProductChatPrompt</c>'s grounding rule branches on that label.</para>
///
/// <para>ONE HEBREW-INFLECTION IMPLEMENTATION, NOT TWO. Sentence scoring reuses
/// <see cref="GuideSelector.Tokenize"/> and <see cref="GuideSelector.InflectionKeys"/> rather than
/// re-implementing tolerance, so the be-c02 fix cannot go stale in one of two places.</para>
/// </summary>
public static class BookChatExcerpts
{
    /// <summary>
    /// The escalation budget slice, in estimated tokens (d1 section (2)). Sized just BELOW the measured
    /// average chapter (~3,895 tokens) on purpose: an average chapter usually escalates whole, and a long
    /// one predictably degrades to excerpts. A tighter cap would make escalation excerpt-only in practice;
    /// a looser one would let one escalated chapter eat the whole remaining budget.
    ///
    /// <para>This is a slice for the WHOLE escalation, shared when a question names more than one chapter,
    /// not a per-chapter allowance.</para>
    /// </summary>
    public const int EscalationBudgetTokens = 3_500;

    /// <summary>
    /// Sentences taken either side of a matched sentence, so an excerpt reads as prose rather than as a
    /// grep result. Small: the window is context for the match, not a second retrieval mechanism.
    /// </summary>
    public const int WindowRadius = 1;

    /// <summary>Sentence terminators in both scripts. Hebrew uses the Latin set plus the sof-pasuq and
    /// the maqaf-free full stop; the geresh/gershayim are deliberately NOT terminators (they are quote
    /// marks inside a sentence).</summary>
    private static readonly char[] Terminators = { '.', '!', '?', '׃', '…', '\n' };

    /// <summary>
    /// What one chapter's escalation produced.
    /// </summary>
    /// <param name="Text">The chapter text to send: the whole thing, or the assembled excerpt windows
    /// separated by an elision marker. EMPTY when the chapter had no text at all, or when not one
    /// sentence matched and there was no room for a fallback opening.</param>
    /// <param name="IsWholeChapter">True when the WHOLE chapter fit. This is what licenses the prompt's
    /// scoped assertion ("chapter 7 does not mention X"); false keeps the partial-coverage shape
    /// mandatory even for that one chapter.</param>
    /// <param name="EstimatedTokens">What <see cref="Text"/> costs under the ONE shared estimator.</param>
    public sealed record Excerpt(string Text, bool IsWholeChapter, int EstimatedTokens)
    {
        public static Excerpt Empty { get; } = new(string.Empty, false, 0);

        public bool HasText => Text.Length > 0;
    }

    /// <summary>
    /// The elision marker between two non-adjacent excerpt windows. Explicit, because a reader (human or
    /// model) who cannot see that text was skipped will read two distant paragraphs as consecutive.
    /// </summary>
    internal const string Elision = "[...]";

    /// <summary>
    /// Decides whole-chapter vs excerpt against <paramref name="budgetTokens"/> and returns the text to
    /// send.
    /// </summary>
    /// <param name="chapterText">The chapter's plain text (<c>Chapter.ContentText</c>). This is the
    /// EXISTING plain-text surface the analysis paths already read; no second SFDT-to-text extraction is
    /// introduced anywhere in phase B.</param>
    /// <param name="question">The user's question, used only to score sentences.</param>
    /// <param name="budgetTokens">The remaining escalation slice for this chapter.</param>
    public static Excerpt Build(string? chapterText, string question, int budgetTokens)
    {
        if (string.IsNullOrWhiteSpace(chapterText) || budgetTokens <= 0) return Excerpt.Empty;

        var text = chapterText.Trim();

        var wholeTokens = ProductChatBudget.EstimateTokens(text);
        if (wholeTokens <= budgetTokens) return new Excerpt(text, IsWholeChapter: true, wholeTokens);

        var sentences = SplitSentences(text);
        if (sentences.Count == 0) return Excerpt.Empty;

        var questionTokens = GuideSelector.Tokenize(question);
        var questionKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in questionTokens)
        {
            foreach (var key in GuideSelector.InflectionKeys(token)) questionKeys.Add(key);
        }

        // Score every sentence by how many DISTINCT question tokens it carries. Distinct, not total: a
        // sentence repeating one word is not more relevant than one carrying two different ones.
        var scored = new List<(int Index, int Score)>(sentences.Count);
        for (var i = 0; i < sentences.Count; i++)
        {
            var score = ScoreSentence(sentences[i], questionTokens, questionKeys);
            if (score > 0) scored.Add((i, score));
        }

        // Highest score first; ties by chapter position, so the choice is deterministic rather than
        // dependent on the sort's stability.
        scored.Sort((a, b) => b.Score != a.Score ? b.Score.CompareTo(a.Score) : a.Index.CompareTo(b.Index));

        // Grow the kept-sentence set window by window, in SCORE order, stopping the moment the next
        // window would break the budget. The set is then rendered in CHAPTER-POSITION order (not score
        // order) so the excerpt reads coherently.
        var kept = new SortedSet<int>();
        foreach (var (index, _) in scored)
        {
            var candidate = new SortedSet<int>(kept);
            for (var i = Math.Max(0, index - WindowRadius);
                 i <= Math.Min(sentences.Count - 1, index + WindowRadius);
                 i++)
            {
                candidate.Add(i);
            }

            if (candidate.Count == kept.Count) continue;   // window already wholly kept

            var rendered = Render(sentences, candidate);
            if (ProductChatBudget.EstimateTokens(rendered) > budgetTokens) break;

            kept = candidate;
        }

        if (kept.Count == 0)
        {
            // NOTHING matched lexically. Sending the chapter's OPENING is a real, honest excerpt (it is
            // still labeled as one), and it is strictly better than sending nothing: an answer that says
            // "the parts of chapter 7 I could read do not mention X" is only truthful if some part was
            // read. Bounded by the same slice, so this cannot become a budget hole.
            for (var take = Math.Min(sentences.Count, 2 * WindowRadius + 1); take >= 1; take--)
            {
                var opening = new SortedSet<int>(Enumerable.Range(0, take));
                var rendered = Render(sentences, opening);
                if (ProductChatBudget.EstimateTokens(rendered) <= budgetTokens)
                {
                    return new Excerpt(rendered, IsWholeChapter: false, ProductChatBudget.EstimateTokens(rendered));
                }
            }

            return Excerpt.Empty;
        }

        var excerpt = Render(sentences, kept);
        return new Excerpt(excerpt, IsWholeChapter: false, ProductChatBudget.EstimateTokens(excerpt));
    }

    /// <summary>
    /// How many DISTINCT question tokens a sentence carries, counting a Hebrew inflection of one as a
    /// hit (the same tolerance the guide selector applies to headings).
    /// </summary>
    internal static int ScoreSentence(
        string sentence, IReadOnlySet<string> questionTokens, IReadOnlySet<string> questionKeys)
    {
        if (questionTokens.Count == 0) return 0;

        var sentenceTokens = GuideSelector.Tokenize(sentence);
        if (sentenceTokens.Count == 0) return 0;

        var sentenceKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in sentenceTokens)
        {
            foreach (var key in GuideSelector.InflectionKeys(token)) sentenceKeys.Add(key);
        }

        var score = 0;
        foreach (var token in questionTokens)
        {
            if (sentenceTokens.Contains(token) || GuideSelector.MatchesByInflection(token, sentenceKeys))
                score++;
        }

        return score;
    }

    /// <summary>
    /// Splits on sentence terminators, keeping the terminator with its sentence. Deliberately simple
    /// (d1 says "simple terminator-based split"): a real sentence segmenter would need an abbreviation
    /// lexicon per language, and an over-split sentence costs a slightly narrower window, not a wrong
    /// answer, because neighbours ride along anyway.
    /// </summary>
    internal static IReadOnlyList<string> SplitSentences(string text)
    {
        var sentences = new List<string>();
        var current = new StringBuilder();

        foreach (var c in text)
        {
            current.Append(c);

            if (Array.IndexOf(Terminators, c) < 0) continue;

            var sentence = current.ToString().Trim();
            if (sentence.Length > 0) sentences.Add(sentence);
            current.Clear();
        }

        var tail = current.ToString().Trim();
        if (tail.Length > 0) sentences.Add(tail);

        return sentences;
    }

    /// <summary>Renders the kept sentence indices in chapter-position order, marking every gap.</summary>
    private static string Render(IReadOnlyList<string> sentences, SortedSet<int> kept)
    {
        var sb = new StringBuilder();
        var previous = -2;

        foreach (var index in kept)
        {
            if (previous >= 0 && index != previous + 1) sb.Append('\n').Append(Elision).Append('\n');
            else if (previous >= 0) sb.Append(' ');
            else if (index > 0) sb.Append(Elision).Append('\n');

            sb.Append(sentences[index]);
            previous = index;
        }

        if (previous >= 0 && previous < sentences.Count - 1) sb.Append('\n').Append(Elision);

        return sb.ToString();
    }
}
