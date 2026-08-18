namespace Pagedraft.Api.Services.Chat;

/// <summary>
/// PURE extraction of the citation line the grounding prompt asks the model to end with
/// (chatbot phase A, c1; d1 item 2 "the guide id(s) ACTUALLY used").
///
/// <para>WHY PARSE AT ALL, rather than just returning the four guides the selector picked. The
/// contract promises the ids the answer USED, and g1 bucket (a) measures whether the returned id
/// names the guide that actually covers the question. Returning all four selected ids would make
/// that measurement meaningless: it would be right whenever retrieval was right, regardless of what
/// the answer was actually built from.</para>
///
/// <para>FAIL-SAFE, in the direction that cannot mislead. A citation is accepted only when the line
/// parses AND names at least one id that was genuinely in this turn's selection. Anything else (no
/// line, a hallucinated id, a line naming nothing) falls back to the full selected set, so a parsing
/// miss degrades to "here is what this answer was grounded in" rather than to a WRONG citation or a
/// silently truncated answer. A model can never widen its citation to a guide it was not given,
/// because the accepted set is always intersected with the selection.</para>
///
/// <para>THE REFS FALL BACK; THE PROSE IS NOT ALWAYS LEFT ALONE, AND THAT SENTENCE USED TO CLAIM IT WAS.
/// Three narrow strips remove a REFUSED line from the answer, and every one of them fires only on the
/// whole-line shape, where position has already proved the line is a citation: a line naming a ref that
/// was never carried, a citation stranded mid-answer, and (g3b) a line that names nothing at all. Each
/// is described at its own site. The refs returned are the honest full set in all three cases.</para>
///
/// <para>TWO ACCEPTED SHAPES, WITH DELIBERATELY DIFFERENT STRICTNESS (g1 finding F1). The prompt asks
/// for the label on a line of its own, and 3 of g1's 72 measured answers put it at the END OF A PROSE
/// LINE instead. The old parser only looked at the START of the last line, so those answers leaked the
/// raw label to the user AND fell back to the full four-guide selection, producing citation chips that
/// contradicted the sentence above them.</para>
/// <list type="number">
///   <item><b>Whole-line</b> (<c>Guides: export</c> on its own line): POSITION already proves intent,
///   so the tail is parsed leniently and anything that is not a selected id is simply ignored. This is
///   the pre-F1 behaviour, with one addition described below.</item>
///   <item><b>Inline trailing</b> (<c>...prose. Guides: export</c>): position proves nothing, so the
///   SHAPE has to. Three guards, all required: the label must not be glued to a word (the character
///   before it may not be a letter or digit, which is what separates a citation from the English prose
///   "in the guides:" and the Hebrew "במדריכים:"/"המדריכים:"); EVERY token after the label must be an
///   id from THIS turn's selection (or the one tolerated piece of scaffolding described below), so a
///   trailing sentence cannot be mistaken for a citation list; and the tail is length-capped, bounding
///   how much text a mis-parse could ever remove from the answer. Fail ANY of them and the whole match
///   is abandoned, which degrades to exactly the pre-F1 behaviour rather than to a wrong answer.</item>
/// </list>
///
/// <para>THE ONE TOLERATED NON-ID TOKEN (g2 finding G3 item 1). An en/he twin pair shares a single
/// guide id, so a model citing both members has no id-level way to say so and adds a parenthesised
/// language tag instead: <c>Guides: faq, chapter-editing-passes (en), chapter-editing-passes (he)</c>.
/// g2's 102-run measurement recorded that exact ending three times verbatim, and the id-only Guard C
/// refused the whole line for it - leaking the raw label into the prose and falling back to the full
/// selection, which is precisely the defect the inline shape was added to remove. Guard C therefore
/// also accepts a token of the exact shape <c>(xx)</c>, two ASCII letters in brackets, and NOTHING
/// else: it is not an id, it is never added to the cited list, and a line whose only tokens are tags
/// cites nothing and is refused. The tolerance is a shape, not a class - <c>(section 3)</c> and
/// <c>(guide)</c> still refuse the whole match, so the guard's real job, refusing a trailing prose
/// sentence whole, is untouched.</para>
///
/// <para>MID-SENTENCE LABELS ARE STILL DELIBERATELY NOT CHASED. g1 also saw a label in the MIDDLE of a
/// sentence. Stripping there would mean deciding, without positional evidence, that words a user is
/// reading are scaffolding, and the only rule strong enough to find them is also strong enough to eat
/// prose that merely mentions a guide. A leaked label mid-answer is cosmetic; a deleted sentence is not.</para>
///
/// <para>PHASE B'S f2 ADDED TWO NARROW STRIPS, BOTH FOR TEXT THAT REACHED THE READER (g1 F-3, F-6, F-10),
/// and both keep the same asymmetry: never delete something that could be a sentence.</para>
/// <list type="number">
///   <item>A WHOLE-LINE citation naming a ref that was never carried used to be refused AND left in the
///   prose, which published <c>chapter-brief:5</c> for a brief the trim had withheld and a guide anchor
///   that does not exist (and in this codebase a guide heading is a retrieval key). It is now removed,
///   but ONLY when a token carries a shape no sentence in either language produces - an artifact ref or
///   an <c>id#anchor</c>. A label followed by ordinary words is still left exactly where it was.</item>
///   <item>A citation line the model then KEPT WRITING PAST is stranded mid-answer, where this parser
///   never looked (it reads the last line) so the label leaked and the refs fell back to everything. An
///   earlier line is accepted only under the strictest bar in this class: the label, and nothing but refs
///   this turn actually carried. That is a line no prose produces.</item>
/// </list>
///
/// <para>THE INSTRUCTION, NOT THIS PARSER, IS WHY g1's ARTIFACT CITATIONS WERE NEAR-INERT. 80-85% of
/// book-scoped runs returned an EMPTY artifact list, which this class cannot produce: an unparsed line
/// falls back to the FULL carried set. Those were lines that parsed and named only guide ids, because
/// two sentences described the citation line and the later, narrower one asked for guide ids. See
/// <c>ProductChatPrompt</c>; the fix is there, and the two strips above are the belt.</para>
/// </summary>
public static class ProductChatCitations
{
    /// <summary>
    /// Every label this parser will accept, in both languages and both vocabularies.
    ///
    /// <para>PHASE B'S f2 ADDED THE "SOURCES" PAIR AND KEPT THE "GUIDES" PAIR (g1 finding F-3). The
    /// book-aware prompt asks for "Sources", because that is what the line is once it can name a chapter
    /// or a status and not only a guide; asking a model to list <c>chapter-brief:7</c> under a label that
    /// reads "Guides" is a contradiction, and 80-85% of book-scoped runs resolved it by listing guides.
    /// The phase-A pair stays accepted rather than being replaced, so a model falling back to the older
    /// wording out of habit still parses: the label is the one part of this mechanism g1 measured working
    /// (91.7% in phase A) and it is not being bet on.</para>
    /// </summary>
    private static readonly string[] Labels = { "guides:", "מדריכים:", "sources:", "מקורות:" };

    /// <summary>
    /// How much text after an INLINE label may be treated as the citation. A real citation is a short
    /// list of slugs; this bounds the blast radius of any mis-parse to a fragment rather than a
    /// paragraph, which is the "bound the SHAPE, not just the content" lesson from the fail-safe
    /// repairs elsewhere in this codebase. Generous next to the real worst case (four ids plus
    /// separators is well under 100 characters).
    /// </summary>
    internal const int MaxInlineCitationChars = 200;

    /// <summary>
    /// Splits <paramref name="answer"/> into the prose the user sees and the guide ids it cited.
    /// Phase A's shape, preserved for every caller that has only guides.
    /// </summary>
    public static (string Answer, IReadOnlyList<string> GuideIds) Extract(
        string answer, IReadOnlyList<GuideDocument> selected)
        // Distinct because an en/he twin pair shares one id: "export" must not be listed twice.
        => Extract(answer, selected.Select(d => d.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList());

    /// <summary>
    /// Splits <paramref name="answer"/> into the prose the user sees and the references it cited.
    ///
    /// <para>PHASE B WIDENED THE VOCABULARY, NOT THE SAFETY PROPERTY. A reference is now either a guide
    /// id (<c>export</c>) or a book-artifact ref (<c>chapter-brief:7</c>, <c>finding:&lt;guid&gt;</c>,
    /// <c>register</c>, <c>status:review</c>, <c>chapter-text:7</c>). Both families go through the SAME
    /// intersection with <paramref name="acceptableRefs"/>, so the invariant that makes this parser safe
    /// is untouched: a citation can only ever NARROW what the answer was actually given, never widen it
    /// to an artifact the prompt did not carry. <paramref name="acceptableRefs"/> is computed from the
    /// blocks that SURVIVED the budget trim, which is what stops a trimmed-away artifact from leaving its
    /// citation behind.</para>
    ///
    /// <para>Returns the references in SELECTION order (not the model's order) so the client renders a
    /// stable list, and strips the citation from the prose only when the citation was actually
    /// accepted.</para>
    /// </summary>
    public static (string Answer, IReadOnlyList<string> References) Extract(
        string answer, IReadOnlyList<string> acceptableRefs)
    {
        var selectedIds = acceptableRefs
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (string.IsNullOrWhiteSpace(answer)) return (answer, selectedIds);

        var lines = answer.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        var last = lines.Length - 1;
        while (last >= 0 && string.IsNullOrWhiteSpace(lines[last])) last--;
        if (last < 0) return (answer, selectedIds);

        var (head, cited, wholeLine) = SplitCitation(lines[last], selectedIds);

        if (cited != null)
        {
            // Intersect with the selection, preserving SELECTION order, so the citation can only ever
            // narrow what the answer was given, never widen it. This is the ONE line that guarantees the
            // safety property, and both accepted shapes go through it.
            var accepted = selectedIds
                .Where(id => cited.Any(c => string.Equals(c, id, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (accepted.Count > 0)
            {
                var kept = string.Join("\n", lines.Take(last).Append(head)).TrimEnd();
                return (kept.Length == 0 ? answer : kept, accepted);
            }

            // A WHOLE-LINE CITATION NAMING A REF THAT WAS NEVER CARRIED IS A FABRICATION, and leaving it
            // in the prose publishes it (g1 F-3: a visible "מדריכים: chapter-brief:5" for a brief the
            // trim had deliberately withheld; g1 F-6: "guide-id#a-heading-that-does-not-exist", which in
            // this codebase points at a retrieval key). The refs returned are still the honest full set,
            // exactly as on any other miss - only the fabricated LINE stops reaching the reader.
            //
            // Narrow on purpose. The general "a citation naming nothing selected leaves the prose alone"
            // behaviour is unchanged, because deleting a line a user might be reading is the worse
            // failure: this fires only when a token carries a shape that cannot be prose in either
            // language (see LooksFabricated), which is what both observed leaks had and what an ordinary
            // sentence beginning "Guides: none of them cover this" does not.
            if (wholeLine && cited.Any(c => LooksFabricated(c, selectedIds)))
            {
                var stripped = string.Join("\n", lines.Take(last)).TrimEnd();
                return (stripped.Length == 0 ? answer : stripped, selectedIds);
            }

            // AN EMPTY WHOLE-LINE CITATION IS SCAFFOLDING WITH NOTHING IN IT, AND IT REACHED THE READER
            // (g3b, 0 of 102 to 2 of 102). Two answers rendered a literal "Guides: ," under the prose while
            // the response carried three perfectly good guide ids: the model wrote the label and the
            // separator and no id between them. Tokenize returns an EMPTY list there rather than a null one,
            // so `cited != null` was true, the intersection was empty, and no token existed for the
            // fabrication strip above to look at - the line fell through to the untouched-answer return and
            // was published verbatim.
            //
            // Stripping it is safe under this class's own asymmetry (never delete something that could be a
            // sentence), and it needs no new judgement call to see that: POSITION already proved this line is
            // a citation, and an empty token list means the tail holds no word at all. Tokenize Clean()s each
            // token and drops the empties, so "Guides:", "Guides: ," and "Guides: ***" all land here while
            // "Guides: none of them cover this" does not - that tokenizes to real words, keeps its non-empty
            // list, and is still left exactly where it was. This is the never-empty contract the rest of the
            // chat layer already carries, floored on "holds no letter or digit" rather than on whitespace.
            //
            // The refs returned are the honest full set, exactly as on every other miss: what stops reaching
            // the reader is only the empty line.
            if (wholeLine && cited.Count == 0)
            {
                var withoutEmpty = string.Join("\n", lines.Take(last)).TrimEnd();
                return (withoutEmpty.Length == 0 ? answer : withoutEmpty, selectedIds);
            }

            return (answer, selectedIds);
        }

        // A CITATION LINE THE MODEL THEN KEPT WRITING PAST (g1 F-10). The parser reads the LAST line, so
        // a model that emits its citation line and then adds another paragraph strands the line in the
        // middle of the answer, where it is scaffolding the reader has to skip. Position proves nothing
        // there, so the SHAPE has to, and the bar is the strictest one in this class: the line must be
        // the label and NOTHING but refs this turn actually carried.
        var stranded = StrandedCitationLine(lines, last, selectedIds);
        if (stranded < 0) return (answer, selectedIds);

        var strandedTokens = StrictTokens(TailOf(lines[stranded]), selectedIds)!;
        var strandedRefs = selectedIds
            .Where(id => strandedTokens.Any(c => string.Equals(c, id, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var withoutStranded = string.Join(
            "\n", lines.Where((_, i) => i != stranded)).TrimEnd();

        return (withoutStranded.Length == 0 ? answer : withoutStranded,
                strandedRefs.Count == 0 ? selectedIds : strandedRefs);
    }

    /// <summary>
    /// True when <paramref name="token"/> was NOT carried this turn and has a shape no sentence in either
    /// language produces: a book-artifact ref (<c>chapter-brief:5</c>, <c>status:review</c>) or a guide id
    /// carrying a heading anchor (<c>whole-book-review#some-heading</c>). Both are exactly the fabricated
    /// shapes g1 observed reaching the reader, and neither can be a word.
    /// </summary>
    private static bool LooksFabricated(string token, IReadOnlyList<string> selectedIds)
    {
        if (selectedIds.Any(id => string.Equals(id, token, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (BookArtifactRefs.LooksLikeArtifactRef(token)) return true;

        var hash = token.IndexOf('#');
        return hash > 0 && hash < token.Length - 1;
    }

    /// <summary>
    /// The index of a citation line stranded BEFORE <paramref name="last"/>, or -1. The latest qualifying
    /// line wins, for the same reason <see cref="LastLabelIndex"/> takes the last label on a line.
    /// </summary>
    private static int StrandedCitationLine(string[] lines, int last, IReadOnlyList<string> selectedIds)
    {
        for (var i = last - 1; i >= 0; i--)
        {
            var cleaned = Clean(lines[i]);
            var label = LabelAtStart(cleaned);
            if (label == null) continue;

            var tokens = StrictTokens(cleaned[label.Length..], selectedIds);
            if (tokens is { Count: > 0 }) return i;
        }

        return -1;
    }

    /// <summary>The token tail of a line already known to open with a label.</summary>
    private static string TailOf(string line)
    {
        var cleaned = Clean(line);
        return cleaned[LabelAtStart(cleaned)!.Length..];
    }

    /// <summary>
    /// Splits the answer's last line into the prose that stays and the citation tokens that go, or
    /// returns a null token list when the line carries no citation this parser will accept.
    /// </summary>
    /// <returns>
    /// <c>WholeLine</c> says which of the two shapes matched, because the caller treats an EMPTY
    /// intersection differently for each: on the whole-line form position already proved the line is a
    /// citation, so a ref that was never carried is a fabricated citation rather than a sentence.
    /// </returns>
    private static (string Head, List<string>? Cited, bool WholeLine) SplitCitation(
        string line, IReadOnlyList<string> selectedIds)
    {
        // (1) WHOLE-LINE form. Its position is the evidence, so the tail is parsed leniently.
        var cleaned = Clean(line);
        var label = LabelAtStart(cleaned);
        if (label != null)
            return (string.Empty, Tokenize(cleaned[label.Length..]), true);

        // (2) INLINE TRAILING form. No positional evidence, so every guard below must hold.
        var at = LastLabelIndex(line, out var inlineLabel);
        if (at < 0) return (line, null, false);

        // GUARD A - the label must not be glued to a word. "in the guides:" and the Hebrew prefixed
        // forms "המדריכים:" / "במדריכים:" are ordinary prose, and this is what tells them apart from a
        // citation that follows a sentence terminator, a bracket or markdown emphasis.
        var head = line[..at].TrimEnd();
        if (head.Length > 0 && char.IsLetterOrDigit(head[^1])) return (line, null, false);

        var tail = line[(at + inlineLabel.Length)..];

        // GUARD B - bound the shape, so a mis-parse can never swallow a paragraph.
        if (tail.Length > MaxInlineCitationChars) return (line, null, false);

        // GUARD C - EVERY token must be a guide this turn actually selected, or the one tolerated piece
        // of scaffolding: a parenthesised two-letter language tag, which a model adds to tell the en/he
        // members of a twin pair apart because they share one id. A trailing sentence ("Guides: export
        // is the one that covers this") therefore still parses as prose, not as a citation, and a
        // citation mixing a real id with a hallucinated one is still refused whole rather than
        // half-trusted. Refusal costs only the pre-F1 behaviour: label left in, full selection returned.
        //
        // The tag is classified from the RAW token, before Clean() strips its brackets, and is DROPPED
        // rather than collected - "en"/"he" are not guide ids and can never reach the cited list.
        var cited = StrictTokens(tail, selectedIds);

        // Tags alone name no guide, so there is nothing to narrow to and nothing to strip. (Extract's
        // empty-intersection fallback would also catch this; the guard keeps SplitCitation's own
        // contract honest - a non-null token list always names at least one real guide.)
        if (cited is not { Count: > 0 }) return (line, null, false);

        return (TrimTrailingScaffold(head), cited, false);
    }

    /// <summary>
    /// Guard C as a reusable test: the tokens of <paramref name="tail"/> when EVERY one of them is a ref
    /// this turn actually carried or the one tolerated language tag, and null the moment one is neither.
    /// Extracted rather than duplicated because the stranded-line scan needs exactly this bar and a second
    /// copy of it is a second place for the tolerance to drift.
    /// </summary>
    private static List<string>? StrictTokens(string tail, IReadOnlyList<string> selectedIds)
    {
        var cited = new List<string>();

        foreach (var raw in SplitTokens(tail))
        {
            var token = Clean(raw);
            if (token.Length == 0) continue;

            if (selectedIds.Any(id => string.Equals(id, token, StringComparison.OrdinalIgnoreCase)))
            {
                cited.Add(token);
                continue;
            }

            if (IsLanguageTag(raw)) continue;

            return null;
        }

        return cited;
    }

    private static readonly char[] TokenSeparators = { ',', ';', '،', '/', '|', ' ', '\t' };

    private static List<string> SplitTokens(string tail)
        => tail.Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries).ToList();

    private static List<string> Tokenize(string tail)
        => SplitTokens(tail)
            .Select(Clean)
            .Where(t => t.Length > 0)
            .ToList();

    /// <summary>
    /// True only for the exact shape <c>(xx)</c> - two ASCII letters in round brackets - allowing the
    /// markdown emphasis and sentence punctuation a model may wrap it in. Deliberately a SHAPE test and
    /// not a "anything in brackets" test: widening it to <c>(section 3)</c> or <c>(guide)</c> would let
    /// Guard C accept a trailing prose sentence, which is the one thing it exists to refuse.
    /// </summary>
    private static bool IsLanguageTag(string rawToken)
    {
        var t = rawToken.Trim().Trim('*', '_', '`', '"', '\'', '.', ' ', '\t');

        return t.Length == 4
            && t[0] == '(' && t[3] == ')'
            && char.IsAsciiLetter(t[1]) && char.IsAsciiLetter(t[2]);
    }

    private static string? LabelAtStart(string line)
        => Labels.FirstOrDefault(l => line.StartsWith(l, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when <paramref name="line"/> is a citation line BY POSITION - the whole-line shape this parser
    /// owns end to end, including its deliberate choice to leave a refused one in place.
    ///
    /// <para>EXPOSED FOR <see cref="ProductChatInternalLabels"/> (final-r03), which strips internal tokens
    /// out of the answer PROSE and must not reach across that boundary: a refs-only line is what a citation
    /// line looks like, so a strip that ran on it would gut the one line here that is supposed to carry
    /// refs. Sharing the test rather than re-deriving it keeps the two layers from disagreeing about what a
    /// label is, which is the same reason <c>StrictTokens</c> is shared rather than copied. The INLINE
    /// trailing shape is deliberately NOT covered: there the label sits at the end of a sentence the author
    /// reads, which is prose.</para>
    /// </summary>
    internal static bool OpensWithCitationLabel(string line)
        => LabelAtStart(Clean(line)) != null;

    /// <summary>
    /// Index of the LAST label occurrence on the line (a model that names two labels, or repeats one,
    /// meant the final one), or -1. Matching is case-insensitive throughout; Hebrew has no case, so the
    /// looser comparison costs the Hebrew labels nothing.
    /// </summary>
    private static int LastLabelIndex(string line, out string label)
    {
        label = Labels[0];
        var best = -1;

        foreach (var candidate in Labels)
        {
            var at = line.LastIndexOf(candidate, StringComparison.OrdinalIgnoreCase);
            if (at > best) { best = at; label = candidate; }
        }

        return best;
    }

    /// <summary>Strips the markdown emphasis / list punctuation a model routinely wraps a line in,
    /// so <c>**Guides:** faq</c> and <c>- Guides: faq</c> read the same as <c>Guides: faq</c>.</summary>
    private static string Clean(string text)
        => text.Trim().Trim('*', '_', '#', '`', '-', '.', ' ', '\t', '"', '\'', '[', ']', '(', ')').Trim();

    /// <summary>
    /// Removes the punctuation that OPENED the stripped citation ("Some prose. (" -> "Some prose."),
    /// while keeping the sentence's own terminator. Only opening/emphasis characters are trimmed, so a
    /// full stop, a question mark or a Hebrew word ending the prose survives untouched.
    /// </summary>
    private static string TrimTrailingScaffold(string head)
        => head.TrimEnd('*', '_', '`', '(', '[', '{', '"', '\'', '-', ' ', '\t');
}
