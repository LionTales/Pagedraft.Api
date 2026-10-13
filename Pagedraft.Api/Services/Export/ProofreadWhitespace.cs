namespace Pagedraft.Api.Services.Export;

/// <summary>
/// The whitespace rules of the proofread annotation (plan whole-book-proofread-and-annotated-docx-2026-10-11, c16),
/// measured on 'זיכרונות של מכשף להגהה' (c15): 4,007 of its 4,676 pending Proofread suggestions changed nothing but
/// whitespace (the chunker trims every paragraph's edge whitespace and the merge never puts it back), and another
/// 37 real edits arrived FUSED with such edge whitespace (<c>" כול" -&gt; "כל"</c>), so their span began on a
/// paragraph mark and could not be written as a tracked change.
///
/// <para>WHITESPACE IS THE CLIENT'S, MIRRORED EXACTLY. pagedraft-client
/// <c>src/app/core/utils/proofread-diff.ts</c> <c>proofreadDiff</c> (~line 140) hides a suggestion when
/// <c>s.original === s.suggested</c> or <c>s.original.trim() === s.suggested.trim()</c>. JavaScript's
/// <c>String.prototype.trim</c> strips the ECMAScript WhiteSpace and LineTerminator code points, which is NOT
/// .NET's <see cref="char.IsWhiteSpace(char)"/>: .NET also counts U+0085 (NEXT LINE) and does not count U+FEFF
/// (ZERO WIDTH NO-BREAK SPACE). So the set is spelled out here rather than borrowed from <see cref="string.Trim()"/>:
/// the 25 code units <c>String.prototype.trim</c> removes, enumerated over the whole BMP on node v22.13.1
/// (2026-10-12) and pinned by <c>ProofreadWhitespaceTests</c>.</para>
///
/// <para>THE CHAPTER VIEW AGREES. The rule lives once on the client as <c>isWhitespaceOnlySuggestion</c>
/// (<c>proofread-diff.ts</c>); the diff path, the analysis panel's stored-row mapping
/// (<c>mapDtoSuggestions</c>) and the history tab all call it, so a row this export skips as whitespace-only is
/// hidden in the app too.</para>
/// </summary>
internal static class ProofreadWhitespace
{
    /// <summary>
    /// True for exactly the code units JavaScript's <c>String.prototype.trim</c> removes: TAB, LF, VT, FF, CR, SPACE,
    /// NO-BREAK SPACE, OGHAM SPACE MARK, U+2000..U+200A, LINE SEPARATOR, PARAGRAPH SEPARATOR, NARROW NO-BREAK SPACE,
    /// MEDIUM MATHEMATICAL SPACE, IDEOGRAPHIC SPACE and ZERO WIDTH NO-BREAK SPACE (BOM).
    /// </summary>
    internal static bool IsWhitespace(char c) => c switch
    {
        '\u0009' or '\u000A' or '\u000B' or '\u000C' or '\u000D' or '\u0020' or '\u00A0' or '\u1680' => true,
        >= '\u2000' and <= '\u200A' => true,
        '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000' or '\uFEFF' => true,
        _ => false,
    };

    /// <summary>JavaScript's <c>s.trim()</c>: <paramref name="s"/> without leading and trailing <see cref="IsWhitespace"/>.</summary>
    internal static string Trim(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var start = 0;
        var end = s.Length;
        while (start < end && IsWhitespace(s[start])) start++;
        while (end > start && IsWhitespace(s[end - 1])) end--;
        return s.Substring(start, end - start);
    }

    /// <summary>
    /// The client's hide rule (<c>proofread-diff.ts</c> ~140): the suggestion changes whitespace only, or nothing
    /// at all, when both sides are equal once trimmed. Null counts as empty. Such a suggestion is never written to
    /// the annotated DOCX; the annotator counts it as <see cref="ProofreadAnnotationResult.WhitespaceSkipped"/>.
    /// </summary>
    internal static bool IsWhitespaceOnlyChange(string? original, string? suggested) =>
        string.Equals(Trim(original), Trim(suggested), StringComparison.Ordinal);


    /// <summary>
    /// The FIRST form a real edit is located in: the edge whitespace both sides share is taken off both (at each
    /// end, as many whitespace code units as the side with fewer has there). This never changes what the edit
    /// does beyond whitespace identity: <c>" כול " -&gt; " כל "</c> becomes <c>"כול" -&gt; "כל"</c> one character
    /// later, while <c>"הלך " -&gt; ""</c> (a word deleted with its space) is left whole, so accepting it does not
    /// leave a double space behind. Whitespace INSIDE either side is never touched.
    /// </summary>
    /// <returns>The trimmed pair and <c>Lead</c>, how many code units came off the front of the original.</returns>
    internal static (string Original, string Suggested, int Lead) TrimCommonEdges(string original, string suggested) =>
        TrimEdges(original, suggested, wholeOriginalEdge: false);

    /// <summary>
    /// The FALLBACK form, used only when the first form cannot be placed: ALL of the original's edge whitespace
    /// comes off, and from each end of the suggestion at most as much as came off the same end of the original. It
    /// exists for the edit c15 found fused with a paragraph boundary: <c>" כול" -&gt; "כל"</c> whose leading space
    /// IS the separator between two paragraphs (the chunker trimmed it, so the diff reports it deleted), which no
    /// tracked change can delete; this form lands on <c>"כול"</c> instead. Edge whitespace the suggestion ADDS
    /// beyond what the original had at that end is kept, as is all inner whitespace.
    /// </summary>
    /// <returns>The trimmed pair and <c>Lead</c>, how many code units came off the front of the original.</returns>
    internal static (string Original, string Suggested, int Lead) TrimBoundaryEdges(string original, string suggested) =>
        TrimEdges(original, suggested, wholeOriginalEdge: true);

    /// <summary>
    /// Both trims. An original holding no non-whitespace character (a pure insertion, or a whitespace span
    /// replaced by text, such as a space becoming a maqaf) is returned unchanged with <c>Lead</c> 0: trimming it
    /// would turn a replacement into an insertion and lose the deletion.
    /// </summary>
    private static (string Original, string Suggested, int Lead) TrimEdges(
        string original, string suggested, bool wholeOriginalEdge)
    {
        var originalLead = 0;
        while (originalLead < original.Length && IsWhitespace(original[originalLead])) originalLead++;
        if (originalLead == original.Length) return (original, suggested, 0);
        var originalTrail = 0;
        while (IsWhitespace(original[original.Length - 1 - originalTrail])) originalTrail++;

        var suggestedLead = 0;
        while (suggestedLead < suggested.Length && IsWhitespace(suggested[suggestedLead])) suggestedLead++;
        var suggestedTrail = 0;
        while (suggestedTrail < suggested.Length - suggestedLead
               && IsWhitespace(suggested[suggested.Length - 1 - suggestedTrail]))
            suggestedTrail++;

        var leadOff = Math.Min(originalLead, suggestedLead);
        var trailOff = Math.Min(originalTrail, suggestedTrail);
        var originalLeadOff = wholeOriginalEdge ? originalLead : leadOff;
        var originalTrailOff = wholeOriginalEdge ? originalTrail : trailOff;

        return (
            original.Substring(originalLeadOff, original.Length - originalLeadOff - originalTrailOff),
            suggested.Substring(leadOff, suggested.Length - leadOff - trailOff),
            originalLeadOff);
    }
}
