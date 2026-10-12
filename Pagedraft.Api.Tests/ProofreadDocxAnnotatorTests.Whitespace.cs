using System;
using System.IO;
using System.Linq;
using Pagedraft.Api.Models;
using Syncfusion.DocIO;
using Syncfusion.DocIO.DLS;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// The whitespace rules of the annotator (plan whole-book-proofread-and-annotated-docx-2026-10-11, c16), built from
/// what c15 measured on 'זיכרונות של מכשף להגהה': 4,007 of 4,676 pending suggestions changed whitespace only
/// (999 of chapter 1's were a lone no-break space), and 37 real edits were fused with the paragraph-edge
/// whitespace the chunker had trimmed (<c>" כול" -&gt; "כל"</c> starting ON the paragraph separator), so they
/// were counted unanchored. The fixtures below are those shapes: a paragraph ending in a no-break space, and an
/// edit whose span begins or ends on the boundary between two paragraphs.
/// </summary>
public partial class ProofreadDocxAnnotatorTests
{
    [Theory]
    [InlineData("\u00A0", "")]   // the 999: a paragraph-final no-break space deleted
    [InlineData(" ", "")]        // the 242: a space deleted
    [InlineData("  ", " ")]      // a double space collapsed
    [InlineData("\u00A0", " ")]  // a no-break space made a space
    public void A_whitespace_only_suggestion_is_skipped_counted_apart_and_never_written(string original, string suggested)
    {
        var first = "הוא היה חייב לנצח בקרב הזה.\u00A0";
        var docx = Docx(P((first, false)), P(("הסתתרתי מאחורי גזע עץ רחב.", false)));

        var result = Annotate(docx, Sug(original, suggested, first.Length - 1, before: "בקרב הזה.", after: " הסתתרתי"));

        Assert.Equal((0, 0, 1), (result.Placed, result.Unanchored, result.WhitespaceSkipped));
        Assert.Same(docx, result.Docx);
    }

    [Fact]
    public void Whitespace_only_suggestions_beside_real_ones_leave_only_the_real_ones_in_the_file()
    {
        var first = "רוני הלכה לבית הספר היום.\u00A0";
        var docx = Docx(P((first, false)), P(("היא פגשה את דני ליד השער.", false)));

        var result = Annotate(docx,
            Sug("\u00A0", "", first.Length - 1, before: "הספר היום.", after: " היא פגשה"),
            Sug("לבית הספר", "לביה\"ס", 10),
            Sug(" ", "", first.Length, before: "היום.\u00A0", after: "היא פגשה"),
            Sug("דני", "דנה", first.Length + 13),
            Sug("  ", " ", 4, outcome: SuggestionOutcome.Dismissed));

        // Four pending: two placed, two whitespace-only; the dismissed one is in none of the three counts.
        Assert.Equal((2, 0, 2), (result.Placed, result.Unanchored, result.WhitespaceSkipped));
        var document = Document(result.Docx);
        Assert.Equal(new[] { "לבית הספר", "דני" }, document.Descendants(W + "del").Select(d => d.Value));
        Assert.Equal(new[] { "לביה\"ס", "דנה" }, document.Descendants(W + "ins").Select(i => i.Value));

        // Word sees exactly the two edits, and no revision that is whitespace and nothing else.
        using var reloaded = new WordDocument(new MemoryStream(result.Docx), FormatType.Docx);
        Assert.Equal(4, reloaded.Revisions.Count);
        Assert.All(
            document.Descendants().Where(e => e.Name == W + "del" || e.Name == W + "ins"),
            e => Assert.False(string.IsNullOrWhiteSpace(e.Value.Replace('\u00A0', ' ')), $"whitespace-only revision '{e.Value}'"));
    }

    [Fact]
    public void A_real_edit_fused_with_the_paragraph_boundary_is_placed_on_the_word_only()
    {
        // c15's 1c7c461a: " כול" -> "כל" whose leading space IS the paragraph separator on the snapshot axis.
        var first = "לא הצלחתי להפסיק לחשוב.";
        var docx = Docx(P((first, false)), P(("כול הנקודות פתאום התחברו.", false)));

        var result = Annotate(docx, Sug(" כול", "כל", first.Length, before: "להפסיק לחשוב.", after: " הנקודות פתאום"));

        Assert.Equal((1, 0, 0), (result.Placed, result.Unanchored, result.WhitespaceSkipped));
        var kinds = ParagraphContaining(result.Docx, "הנקודות").Elements().Where(e => e.Name != W + "pPr").Select(Describe).ToList();
        Assert.Equal(new[] { "del:כול", "ins:כל", "r: הנקודות פתאום התחברו." }, kinds);
        Assert.Empty(ParagraphContaining(result.Docx, "לחשוב").Descendants(W + "del"));
    }

    [Fact]
    public void A_real_edit_fused_with_the_trailing_paragraph_boundary_is_placed_on_the_words_only()
    {
        // The separator is the original's trailing space; the suggestion has none, so only the fallback places it.
        var first = "הוא התעקש לדבוק בתשובות חד הברתיות.";
        var docx = Docx(P((first, false)), P(("כשנכנסנו למטבח מצאנו פתק.", false)));
        var start = first.IndexOf("חד", StringComparison.Ordinal);

        var result = Annotate(docx, Sug("חד הברתיות. ", "חד-הברתיות.", start, before: "בתשובות ", after: "כשנכנסנו"));

        Assert.Equal((1, 0, 0), (result.Placed, result.Unanchored, result.WhitespaceSkipped));
        var kinds = ParagraphContaining(result.Docx, "התעקש").Elements().Where(e => e.Name != W + "pPr").Select(Describe).ToList();
        Assert.Equal(new[] { "r:הוא התעקש לדבוק בתשובות ", "del:חד הברתיות.", "ins:חד-הברתיות." }, kinds);
        Assert.Empty(ParagraphContaining(result.Docx, "כשנכנסנו").Descendants(W + "del"));
    }

    [Fact]
    public void A_paragraph_final_no_break_space_inside_the_first_form_goes_with_the_edit()
    {
        // c15's e14e196f: "חד הברתיות.<NBSP> " -> "חד-הברתיות. ". One trailing space is shared and comes off; the
        // no-break space is ordinary text inside the paragraph, so the first form places and takes it along
        // (Accept leaves "חד-הברתיות." with no invisible no-break space; Reject restores both).
        var first = "הוא התעקש לדבוק בתשובות חד הברתיות.\u00A0";
        var docx = Docx(P((first, false)), P(("כשנכנסנו למטבח מצאנו פתק.", false)));
        var start = first.IndexOf("חד", StringComparison.Ordinal);

        var result = Annotate(docx, Sug("חד הברתיות.\u00A0 ", "חד-הברתיות. ", start, before: "בתשובות ", after: "כשנכנסנו"));

        Assert.Equal((1, 0, 0), (result.Placed, result.Unanchored, result.WhitespaceSkipped));
        var kinds = ParagraphContaining(result.Docx, "התעקש").Elements().Where(e => e.Name != W + "pPr").Select(Describe).ToList();
        Assert.Equal(new[] { "r:הוא התעקש לדבוק בתשובות ", "del:חד הברתיות.\u00A0", "ins:חד-הברתיות." }, kinds);
    }

    [Fact]
    public void A_word_deleted_with_its_space_inside_a_paragraph_takes_the_space_along()
    {
        // The first form is placeable, so the fallback (which would drop the space) is never used: accepting the
        // deletion leaves one space, not two.
        var docx = Docx(P(("הוא הלך הלך הביתה.", false)));

        var result = Annotate(docx, Sug("הלך ", "", 4, before: "הוא ", after: "הלך הביתה."));

        Assert.Equal((1, 0, 0), (result.Placed, result.Unanchored, result.WhitespaceSkipped));
        using var reloaded = new WordDocument(new MemoryStream(result.Docx), FormatType.Docx);
        foreach (var revision in reloaded.Revisions.Cast<Revision>().ToList()) revision.Accept();
        Assert.Equal("הוא הלך הביתה.", reloaded.Sections[0].Paragraphs.Cast<WParagraph>().Single().Text);
    }

    [Theory]
    [InlineData("אתהבית", "את הבית")]   // adds a space between words
    [InlineData("ל הראות", "להראות")]   // removes a space between words
    public void An_edit_that_adds_or_removes_a_space_between_words_keeps_it(string original, string suggested)
    {
        var text = $"הוא רצה {original} לכולם.";
        var docx = Docx(P((text, false)));

        var result = Annotate(docx, Sug(original, suggested, 8, before: "הוא רצה ", after: " לכולם."));

        Assert.Equal((1, 0, 0), (result.Placed, result.Unanchored, result.WhitespaceSkipped));
        var p = ParagraphContaining(result.Docx, "לכולם");
        Assert.Equal(original, p.Element(W + "del")!.Value);
        Assert.Equal(suggested, p.Element(W + "ins")!.Value);
    }

    [Fact]
    public void Edge_whitespace_the_suggestion_adds_is_kept()
    {
        var docx = Docx(P(("הוא אמר שלוםכול הנקודות.", false)));

        var result = Annotate(docx, Sug("כול", " כל", 12, before: "אמר שלום", after: " הנקודות"));

        Assert.Equal((1, 0, 0), (result.Placed, result.Unanchored, result.WhitespaceSkipped));
        var p = ParagraphContaining(result.Docx, "הנקודות");
        Assert.Equal("כול", p.Element(W + "del")!.Value);
        Assert.Equal(" כל", p.Element(W + "ins")!.Value);
    }

    [Fact]
    public void A_whitespace_span_replaced_by_text_stays_a_replacement()
    {
        var docx = Docx(P(("מילים חד הברתיות בלבד.", false)));

        var result = Annotate(docx, Sug(" ", "-", 8, before: "מילים חד", after: "הברתיות בלבד"));

        Assert.Equal((1, 0, 0), (result.Placed, result.Unanchored, result.WhitespaceSkipped));
        var p = ParagraphContaining(result.Docx, "מילים");
        Assert.Equal(" ", p.Element(W + "del")!.Value);
        Assert.Equal("-", p.Element(W + "ins")!.Value);
    }

    [Fact]
    public void A_real_edit_spanning_two_paragraphs_is_still_unanchored()
    {
        // c15's C3: the edge whitespace comes off, the separator INSIDE the span does not.
        var first = "אבל הם לא ידעו מה שהיה ביננו.\u00A0";
        var docx = Docx(P((first, false)), P(("שיט.\u00A0", false)), P(("הרגשתי כאילו מישהו נתן לי אגרוף.", false)));
        var start = first.IndexOf("ביננו", StringComparison.Ordinal);

        var result = Annotate(docx, Sug("ביננו.\u00A0 שיט.\u00A0 ", "בינינו. שיט. ", start, before: "מה שהיה ", after: "הרגשתי"));

        Assert.Equal((0, 1, 0), (result.Placed, result.Unanchored, result.WhitespaceSkipped));
        Assert.Same(docx, result.Docx);
    }

    [Fact]
    public void Placed_plus_unanchored_plus_whitespace_skipped_is_every_pending_suggestion()
    {
        var first = "אחת שתיים שלוש.\u00A0";
        var docx = Docx(P((first, false)), P(("ארבע חמש שש.", false)));

        var suggestions = new[]
        {
            Sug("אחת", "אחד", 0),
            Sug("\u00A0", "", first.Length - 1, before: "שלוש.", after: " ארבע"),
            Sug(" ארבע", "ארבעה", first.Length, before: "שלוש.\u00A0", after: " חמש"),
            Sug("שבע", "שבעה", 30),
            Sug("", "", 5),
            Sug("שתיים", "שניים", 4, outcome: SuggestionOutcome.Accepted),
        };
        var result = Annotate(docx, suggestions);

        var pending = suggestions.Count(s => s.Outcome == null);
        Assert.Equal(5, pending);
        Assert.Equal((2, 1, 2), (result.Placed, result.Unanchored, result.WhitespaceSkipped));
        Assert.Equal(pending, result.Placed + result.Unanchored + result.WhitespaceSkipped);
    }
}
