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
/// line, a hallucinated id, a line naming nothing) falls back to the full selected set and leaves the
/// answer text untouched, so a parsing miss degrades to "here is what this answer was grounded in"
/// rather than to a WRONG citation or a silently truncated answer. A model can never widen its
/// citation to a guide it was not given, because the accepted set is always intersected with the
/// selection.</para>
///
/// <para>TWO ACCEPTED SHAPES, WITH DELIBERATELY DIFFERENT STRICTNESS (g1 finding F1). The prompt asks
/// for the label on a line of its own, and 3 of g1's 72 measured answers put it at the END OF A PROSE
/// LINE instead. The old parser only looked at the START of the last line, so those answers leaked the
/// raw label to the user AND fell back to the full four-guide selection, producing citation chips that
/// contradicted the sentence above them.</para>
/// <list type="number">
///   <item><b>Whole-line</b> (<c>Guides: export</c> on its own line): POSITION already proves intent,
///   so the tail is parsed leniently and anything that is not a selected id is simply ignored. This is
///   the pre-F1 behaviour, unchanged.</item>
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
/// <para>MID-PROSE LABELS ARE DELIBERATELY NOT CHASED. g1 also saw a label in the MIDDLE of an answer.
/// Stripping there would mean deciding, without positional evidence, that a sentence a user is reading
/// is scaffolding, and the only rule strong enough to find it is also strong enough to eat prose that
/// merely mentions a guide. A leaked label mid-answer is cosmetic; a silently deleted sentence is not.</para>
/// </summary>
public static class ProductChatCitations
{
    private const string EnglishLabel = "guides:";
    private const string HebrewLabel = "מדריכים:";

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
    ///
    /// <para>Returns the ids in SELECTION order (not the model's order) so the client renders a
    /// stable list, and strips the citation from the prose only when the citation was actually
    /// accepted.</para>
    /// </summary>
    public static (string Answer, IReadOnlyList<string> GuideIds) Extract(
        string answer, IReadOnlyList<GuideDocument> selected)
    {
        // Distinct because an en/he twin pair shares one id: "export" must not be listed twice.
        var selectedIds = selected
            .Select(d => d.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (string.IsNullOrWhiteSpace(answer)) return (answer, selectedIds);

        var lines = answer.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        var last = lines.Length - 1;
        while (last >= 0 && string.IsNullOrWhiteSpace(lines[last])) last--;
        if (last < 0) return (answer, selectedIds);

        var (head, cited) = SplitCitation(lines[last], selectedIds);
        if (cited == null) return (answer, selectedIds);

        // Intersect with the selection, preserving SELECTION order, so the citation can only ever
        // narrow what the answer was given, never widen it. This is the ONE line that guarantees the
        // safety property, and both accepted shapes go through it.
        var accepted = selectedIds
            .Where(id => cited.Any(c => string.Equals(c, id, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (accepted.Count == 0) return (answer, selectedIds);

        var prose = string.Join("\n", lines.Take(last).Append(head)).TrimEnd();
        return (prose.Length == 0 ? answer : prose, accepted);
    }

    /// <summary>
    /// Splits the answer's last line into the prose that stays and the citation tokens that go, or
    /// returns a null token list when the line carries no citation this parser will accept.
    /// </summary>
    private static (string Head, List<string>? Cited) SplitCitation(string line, IReadOnlyList<string> selectedIds)
    {
        // (1) WHOLE-LINE form. Its position is the evidence, so the tail is parsed leniently.
        var cleaned = Clean(line);
        var label = LabelAtStart(cleaned);
        if (label != null)
            return (string.Empty, Tokenize(cleaned[label.Length..]));

        // (2) INLINE TRAILING form. No positional evidence, so every guard below must hold.
        var at = LastLabelIndex(line, out var inlineLabel);
        if (at < 0) return (line, null);

        // GUARD A - the label must not be glued to a word. "in the guides:" and the Hebrew prefixed
        // forms "המדריכים:" / "במדריכים:" are ordinary prose, and this is what tells them apart from a
        // citation that follows a sentence terminator, a bracket or markdown emphasis.
        var head = line[..at].TrimEnd();
        if (head.Length > 0 && char.IsLetterOrDigit(head[^1])) return (line, null);

        var tail = line[(at + inlineLabel.Length)..];

        // GUARD B - bound the shape, so a mis-parse can never swallow a paragraph.
        if (tail.Length > MaxInlineCitationChars) return (line, null);

        // GUARD C - EVERY token must be a guide this turn actually selected, or the one tolerated piece
        // of scaffolding: a parenthesised two-letter language tag, which a model adds to tell the en/he
        // members of a twin pair apart because they share one id. A trailing sentence ("Guides: export
        // is the one that covers this") therefore still parses as prose, not as a citation, and a
        // citation mixing a real id with a hallucinated one is still refused whole rather than
        // half-trusted. Refusal costs only the pre-F1 behaviour: label left in, full selection returned.
        //
        // The tag is classified from the RAW token, before Clean() strips its brackets, and is DROPPED
        // rather than collected - "en"/"he" are not guide ids and can never reach the cited list.
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

            return (line, null);
        }

        // Tags alone name no guide, so there is nothing to narrow to and nothing to strip. (Extract's
        // empty-intersection fallback would also catch this; the guard keeps SplitCitation's own
        // contract honest - a non-null token list always names at least one real guide.)
        if (cited.Count == 0) return (line, null);

        return (TrimTrailingScaffold(head), cited);
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
    {
        if (line.StartsWith(EnglishLabel, StringComparison.OrdinalIgnoreCase)) return EnglishLabel;
        if (line.StartsWith(HebrewLabel, StringComparison.Ordinal)) return HebrewLabel;
        return null;
    }

    /// <summary>
    /// Index of the LAST label occurrence on the line (a model that names both labels, or repeats one,
    /// meant the final one), or -1. English matches case-insensitively; Hebrew has no case.
    /// </summary>
    private static int LastLabelIndex(string line, out string label)
    {
        var en = line.LastIndexOf(EnglishLabel, StringComparison.OrdinalIgnoreCase);
        var he = line.LastIndexOf(HebrewLabel, StringComparison.Ordinal);

        if (he > en) { label = HebrewLabel; return he; }
        label = EnglishLabel;
        return en;
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
