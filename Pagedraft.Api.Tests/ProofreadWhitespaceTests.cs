using System.Linq;
using Pagedraft.Api.Services.Export;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// <see cref="ProofreadWhitespace"/> mirrors the client's hide rule (pagedraft-client
/// <c>src/app/core/utils/proofread-diff.ts</c> ~140: <c>s.original.trim() === s.suggested.trim()</c>), so its
/// whitespace is JavaScript's, not .NET's. The expected set below was enumerated over the whole BMP with
/// <c>String.fromCharCode(c).trim() === ''</c> on node v22.13.1 (2026-10-12): 25 code units.
/// </summary>
public class ProofreadWhitespaceTests
{
    private static readonly int[] JavaScriptTrimSet =
    {
        0x0009, 0x000A, 0x000B, 0x000C, 0x000D, 0x0020, 0x00A0, 0x1680,
        0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200A,
        0x2028, 0x2029, 0x202F, 0x205F, 0x3000, 0xFEFF,
    };

    [Fact]
    public void IsWhitespace_is_exactly_the_set_JavaScript_trim_removes_over_the_whole_BMP()
    {
        var actual = Enumerable.Range(0, 0x10000).Where(c => ProofreadWhitespace.IsWhitespace((char)c)).ToArray();

        Assert.Equal(JavaScriptTrimSet, actual);
    }

    [Theory]
    [InlineData('\uFEFF', true)]   // JavaScript trims the BOM; char.IsWhiteSpace says no
    [InlineData('\u0085', false)]  // NEXT LINE: char.IsWhiteSpace says yes; JavaScript keeps it
    [InlineData('\u180E', false)]  // MONGOLIAN VOWEL SEPARATOR: whitespace in neither since Unicode 6.3
    [InlineData('\u200B', false)]  // ZERO WIDTH SPACE is a format character, not whitespace
    [InlineData('\u200F', false)]  // RLM: a bidi control, dropped by normalization, never trimmed
    public void The_cases_where_dotnet_and_JavaScript_could_disagree_follow_JavaScript(char c, bool expected)
    {
        Assert.Equal(expected, ProofreadWhitespace.IsWhitespace(c));
    }

    [Theory]
    [InlineData("\u00A0", "", true)]
    [InlineData(" ", "", true)]
    [InlineData("  ", " ", true)]
    [InlineData("\u00A0", " ", true)]
    [InlineData("", "", true)]
    [InlineData(null, null, true)]
    [InlineData("הוא ", " הוא", true)]          // the client's own example: only whitespace changed
    [InlineData("שלום\uFEFF", "שלום", true)]
    [InlineData("שלום\u0085", "שלום", false)]
    [InlineData(" כול", "כל", false)]
    [InlineData("אתהבית", "את הבית", false)]
    [InlineData(" ", "-", false)]
    [InlineData("", "מילה", false)]
    public void IsWhitespaceOnlyChange_is_the_clients_trim_equality(string? original, string? suggested, bool expected)
    {
        Assert.Equal(expected, ProofreadWhitespace.IsWhitespaceOnlyChange(original, suggested));
    }

    [Theory]
    // Shared on both sides: off both, the start moves past the leading one.
    [InlineData(" כול ", " כל ", "כול", "כל", 1)]
    // Whitespace only the original has is NOT shared: a word deleted with its space keeps the space.
    [InlineData("הלך ", "", "הלך ", "", 0)]
    [InlineData(" כול", "כל", " כול", "כל", 0)]
    // The original's trailing NBSP + separator against the suggestion's one space: one comes off each.
    [InlineData("חד הברתיות.\u00A0 ", "חד-הברתיות. ", "חד הברתיות.\u00A0", "חד-הברתיות.", 0)]
    // Edge whitespace the suggestion adds is kept.
    [InlineData("כול", " כל", "כול", " כל", 0)]
    [InlineData(" כול", "  כל", "כול", " כל", 1)]
    // Whitespace inside either side is never touched.
    [InlineData("אתהבית", "את הבית", "אתהבית", "את הבית", 0)]
    // An original with nothing but whitespace (or nothing) is left as it is.
    [InlineData(" ", "-", " ", "-", 0)]
    [InlineData("", " גם", "", " גם", 0)]
    // An all-whitespace suggestion is not counted twice (its one space is the leading run).
    [InlineData("\u00A0מילה\u00A0", " ", "מילה\u00A0", "", 1)]
    public void TrimCommonEdges_takes_off_only_the_edge_whitespace_both_sides_share(
        string original, string suggested, string expectedOriginal, string expectedSuggested, int expectedLead)
    {
        var (o, s, lead) = ProofreadWhitespace.TrimCommonEdges(original, suggested);

        Assert.Equal((expectedOriginal, expectedSuggested, expectedLead), (o, s, lead));
    }

    [Theory]
    // The fused edit c15 found: the original's edge whitespace (the paragraph separator) comes off.
    [InlineData(" כול", "כל", "כול", "כל", 1)]
    [InlineData("הלך ", "", "הלך", "", 0)]
    [InlineData(" כול ", " כל ", "כול", "כל", 1)]
    // NBSP + separator against one space: both off the original, the one space off the suggestion.
    [InlineData("חד הברתיות.\u00A0 ", "חד-הברתיות. ", "חד הברתיות.", "חד-הברתיות.", 0)]
    // Edge whitespace the suggestion adds beyond the original's is still kept.
    [InlineData("כול", " כל", "כול", " כל", 0)]
    [InlineData(" כול", "  כל", "כול", " כל", 1)]
    // Whitespace inside either side is never touched.
    [InlineData(" ל הראות ", "להראות", "ל הראות", "להראות", 1)]
    [InlineData(" ", "-", " ", "-", 0)]
    [InlineData("", " גם", "", " גם", 0)]
    [InlineData("\u00A0מילה\u00A0", " ", "מילה", "", 1)]
    public void TrimBoundaryEdges_takes_off_all_the_originals_edge_whitespace_and_only_as_much_of_the_suggestions(
        string original, string suggested, string expectedOriginal, string expectedSuggested, int expectedLead)
    {
        var (o, s, lead) = ProofreadWhitespace.TrimBoundaryEdges(original, suggested);

        Assert.Equal((expectedOriginal, expectedSuggested, expectedLead), (o, s, lead));
    }
}
