using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Export;
using Syncfusion.DocIO;
using Syncfusion.DocIO.DLS;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// The proofread DOCX annotator, asserted in three layers: STRUCTURE of the produced
/// <c>word/document.xml</c> / <c>word/comments.xml</c> first, then a DocIO reload (revision and comment
/// counts), then accept-all through DocIO yielding the corrected text, which pins the part of Word
/// compatibility the structural assertions cannot. Inputs are built with DocIO, the writer the export uses.
/// </summary>
public class ProofreadDocxAnnotatorTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly DateTime Stamp = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    private readonly ProofreadDocxAnnotator _annotator = new(NullLogger<ProofreadDocxAnnotator>.Instance);

    // ------------------------------------------------------------------ structure

    [Fact]
    public void Single_run_span_becomes_del_then_ins_inside_one_comment_range()
    {
        var docx = Docx(P(("רוני הלכה לבית הספר היום.", false)), P(("היא פגשה את דני ליד השער.", false)));

        var result = Annotate(docx, Sug("לבית הספר", "לביה\"ס", 10));

        Assert.Equal((1, 0), (result.Placed, result.Unanchored));
        var p = ParagraphContaining(result.Docx, "רוני");
        var kinds = p.Elements().Where(e => e.Name != W + "pPr").Select(Describe).ToList();
        Assert.Equal(
            new[] { "r:רוני הלכה ", "commentRangeStart", "del:לבית הספר", "ins:לביה\"ס", "commentRangeEnd", "r:[ref]", "r: היום." },
            kinds);

        var del = p.Element(W + "del")!;
        var ins = p.Element(W + "ins")!;
        Assert.Equal("PageDraft הגהה", (string?)del.Attribute(W + "author"));
        Assert.Equal("PageDraft הגהה", (string?)ins.Attribute(W + "author"));
        Assert.NotNull(del.Attribute(W + "date"));
        Assert.NotEqual((string?)del.Attribute(W + "id"), (string?)ins.Attribute(W + "id"));
        Assert.Equal("preserve", (string?)del.Descendants(W + "delText").Single().Attribute(XNamespace.Xml + "space"));

        var commentId = (string?)p.Element(W + "commentRangeStart")!.Attribute(W + "id");
        Assert.Equal(commentId, (string?)p.Element(W + "commentRangeEnd")!.Attribute(W + "id"));
        Assert.Equal(commentId, (string?)p.Descendants(W + "commentReference").Single().Attribute(W + "id"));

        var comment = Comments(result.Docx).Single();
        Assert.Equal(commentId, (string?)comment.Attribute(W + "id"));
        Assert.Equal("PD", (string?)comment.Attribute(W + "initials"));
        Assert.Equal(new[] { "הצעה: לביה\"ס במקום לבית הספר" }, CommentLines(comment));

        // The other paragraph is untouched.
        Assert.Empty(ParagraphContaining(result.Docx, "השער").Descendants(W + "del"));
    }

    [Fact]
    public void Span_crossing_two_runs_keeps_each_runs_formatting_and_the_insertion_takes_the_first()
    {
        var docx = Docx(P(("רוני הלכה ", false), ("לבית", true), (" הספר היום.", false)));

        var result = Annotate(docx, Sug("הלכה לבית", "צעדה אל בית", 5));

        Assert.Equal(1, result.Placed);
        var p = ParagraphContaining(result.Docx, "רוני");
        var deletedRuns = p.Element(W + "del")!.Elements(W + "r").ToList();
        Assert.Equal(new[] { "הלכה ", "לבית" }, deletedRuns.Select(r => r.Element(W + "delText")!.Value));
        Assert.False(IsBold(deletedRuns[0]));
        Assert.True(IsBold(deletedRuns[1]));

        var insertedRun = p.Element(W + "ins")!.Element(W + "r")!;
        Assert.Equal("צעדה אל בית", insertedRun.Element(W + "t")!.Value);
        Assert.True(XNode.DeepEquals(deletedRuns[0].Element(W + "rPr"), insertedRun.Element(W + "rPr")));
        Assert.False(IsBold(insertedRun));
    }

    [Fact]
    public void Two_suggestions_in_one_paragraph_are_placed_in_order_with_ascending_comment_ids()
    {
        var docx = Docx(P(("רוני הלכה לבית הספר והיא חזרה הבייתה מאוחר.", false)));

        var result = Annotate(docx,
            Sug("הבייתה", "הביתה", 30),
            Sug("לבית הספר", "לבית־הספר", 10));

        Assert.Equal((2, 0), (result.Placed, result.Unanchored));
        var p = ParagraphContaining(result.Docx, "רוני");
        Assert.Equal(new[] { "לבית הספר", "הבייתה" }, p.Elements(W + "del").Select(d => d.Value));
        Assert.Equal(new[] { "לבית־הספר", "הביתה" }, p.Elements(W + "ins").Select(i => i.Value));

        var commentIds = p.Elements(W + "commentRangeStart").Select(c => int.Parse((string)c.Attribute(W + "id")!)).ToList();
        Assert.True(commentIds[0] < commentIds[1]);
        var revisionIds = p.Elements().Where(e => e.Name == W + "del" || e.Name == W + "ins")
            .Select(e => (string)e.Attribute(W + "id")!).ToList();
        Assert.Equal(revisionIds.Count, revisionIds.Distinct().Count());
    }

    [Fact]
    public void Text_in_two_paragraphs_is_resolved_by_context_across_the_paragraph_boundary()
    {
        // The snapshot joins paragraphs with one space, so ContextBefore legitimately spans the boundary.
        var docx = Docx(P(("דני אכל תפוח בבוקר.", false)), P(("רונית אכל תפוח בערב.", false)));
        var s = Sug("אכל", "אכלה", 25, before: "דני אכל תפוח בבוקר. רונית ", after: " תפוח בערב.");

        var result = Annotate(docx, s);

        Assert.Equal(1, result.Placed);
        Assert.Empty(ParagraphContaining(result.Docx, "דני").Descendants(W + "del"));
        Assert.Equal("אכל", ParagraphContaining(result.Docx, "רונית").Element(W + "del")!.Value);
    }

    [Fact]
    public void Ambiguous_text_without_settling_context_is_unanchored_not_guessed()
    {
        var docx = Docx(P(("דני אכל תפוח.", false)), P(("רונית אכל תפוח.", false)));

        var noContext = Annotate(docx, Sug("אכל", "אכלה", 4));
        var wrongContext = Annotate(docx, Sug("אכל", "אכלה", 4, before: "משה ", after: " בננה"));

        Assert.Equal((0, 1), (noContext.Placed, noContext.Unanchored));
        Assert.Equal((0, 1), (wrongContext.Placed, wrongContext.Unanchored));
    }

    [Fact]
    public void Unanchorable_suggestion_is_counted_and_the_bytes_are_returned_untouched()
    {
        var docx = Docx(P(("רוני הלכה לבית הספר.", false)));

        var result = Annotate(docx, Sug("מילה שאינה בטקסט", "אחרת", 0));

        Assert.Equal((0, 1), (result.Placed, result.Unanchored));
        Assert.Same(docx, result.Docx);
    }

    [Fact]
    public void Only_pending_suggestions_are_written()
    {
        var docx = Docx(P(("אחת שתיים שלוש ארבע חמש.", false)));

        var result = Annotate(docx,
            Sug("אחת", "1", 0, outcome: SuggestionOutcome.Accepted),
            Sug("שתיים", "2", 4, outcome: SuggestionOutcome.Dismissed),
            Sug("שלוש", "3", 10, outcome: SuggestionOutcome.Reverted),
            Sug("ארבע", "4", 15, outcome: SuggestionOutcome.Superseded),
            Sug("חמש", "5", 20));

        Assert.Equal((1, 0), (result.Placed, result.Unanchored));
        Assert.Equal(new[] { "חמש" }, Document(result.Docx).Descendants(W + "del").Select(d => d.Value));

        var none = Annotate(docx, Sug("אחת", "1", 0, outcome: SuggestionOutcome.Dismissed));
        Assert.Equal((0, 0), (none.Placed, none.Unanchored));
        Assert.Same(docx, none.Docx);
    }

    [Fact]
    public void Pure_deletion_writes_only_the_del()
    {
        var docx = Docx(P(("הוא הלך הלך הביתה.", false)));

        var result = Annotate(docx, Sug("הלך ", "", 4, before: "הוא ", after: "הלך הביתה."));

        Assert.Equal(1, result.Placed);
        var p = ParagraphContaining(result.Docx, "הוא");
        Assert.Equal("הלך ", p.Element(W + "del")!.Value);
        Assert.Empty(p.Descendants(W + "ins"));
        Assert.Equal(new[] { "הצעה: למחוק הלך " }, CommentLines(Comments(result.Docx).Single()));
    }

    [Fact]
    public void Pure_insertion_writes_only_the_ins_between_its_contexts()
    {
        var docx = Docx(P(("רוני הלכה הספר היום.", false)));

        var result = Annotate(docx, Sug("", "לבית ", 10, before: "רוני הלכה ", after: "הספר היום."));

        Assert.Equal(1, result.Placed);
        var p = ParagraphContaining(result.Docx, "רוני");
        Assert.Empty(p.Descendants(W + "del"));
        var kinds = p.Elements().Where(e => e.Name != W + "pPr").Select(Describe).ToList();
        Assert.Equal(
            new[] { "r:רוני הלכה ", "commentRangeStart", "ins:לבית ", "commentRangeEnd", "r:[ref]", "r:הספר היום." },
            kinds);
    }

    [Fact]
    public void A_paragraph_holding_an_RLM_the_suggestion_does_not_still_anchors()
    {
        var docx = Docx(P(("הוא אמר של‏ום לכולם.", false)));

        var result = Annotate(docx, Sug("שלום", "שלומות", 8, before: "הוא אמר ", after: " לכולם."));

        Assert.Equal(1, result.Placed);
        var p = ParagraphContaining(result.Docx, "הוא");
        Assert.Equal("של‏ום", p.Element(W + "del")!.Value);
        Assert.Equal("שלומות", p.Element(W + "ins")!.Value);
    }

    [Fact]
    public void A_span_crossing_a_paragraph_boundary_is_unanchored()
    {
        // On the snapshot axis the boundary is a space, so the text IS found; it cannot be one w:del.
        var docx = Docx(P(("רוני הלכה לבית הספר.", false)), P(("היא חזרה מאוחר.", false)));

        var result = Annotate(docx, Sug("הספר. היא", "הספר, והיא", 15));

        Assert.Equal((0, 1), (result.Placed, result.Unanchored));
        Assert.Same(docx, result.Docx);
    }

    [Fact]
    public void An_overlapping_later_suggestion_is_dropped_and_counted()
    {
        var docx = Docx(P(("רוני הלכה לבית הספר היום.", false)));

        var result = Annotate(docx,
            Sug("הלכה לבית", "צעדה לבית", 5),
            Sug("לבית הספר", "לביה\"ס", 10));

        Assert.Equal((1, 1), (result.Placed, result.Unanchored));
        Assert.Equal("הלכה לבית", Document(result.Docx).Descendants(W + "del").Single().Value);
    }

    [Fact]
    public void Counts_cover_every_pending_suggestion()
    {
        var docx = Docx(P(("אחת שתיים שלוש.", false)), P(("ארבע חמש שש.", false)));

        var result = Annotate(docx,
            Sug("אחת", "אחד", 0),
            Sug("שלוש", "שלש", 10),
            Sug("חמש", "חמישה", 21),
            Sug("שבע", "שבעה", 30),
            Sug("שתיים", "שניים", 4, outcome: SuggestionOutcome.Dismissed));

        Assert.Equal((3, 1), (result.Placed, result.Unanchored));
        Assert.Equal(3, Comments(result.Docx).Count);
        Assert.Equal(3, Document(result.Docx).Descendants(W + "commentReference").Count());
    }

    [Fact]
    public void Comment_body_carries_the_category_and_explanation_lines_in_the_books_language()
    {
        var docx = Docx(P(("הוא שמר על המסורת.", false)));
        var s = Sug("שמר", "שומר", 4, category: "ktiv-male", explanation: "  כתיב מלא בבינוני.  ");

        var he = Annotate(docx, s);
        var en = _annotator.Annotate(docx, new[] { s }, "en", Stamp);

        Assert.Equal(new[] { "הצעה: שומר במקום שמר", "כתיב מלא לפי האקדמיה", "כתיב מלא בבינוני." },
            CommentLines(Comments(he.Docx).Single()));
        Assert.Equal(new[] { "Suggested: שומר instead of שמר", "Academy full spelling", "כתיב מלא בבינוני." },
            CommentLines(Comments(en.Docx).Single()));
        Assert.Equal("PageDraft Proofreader", (string?)Comments(en.Docx).Single().Attribute(W + "author"));
        Assert.Equal("PageDraft Proofreader",
            (string?)Document(en.Docx).Descendants(W + "del").Single().Attribute(W + "author"));
    }

    [Fact]
    public void Existing_comments_keep_their_ids_and_new_ones_are_allocated_past_them()
    {
        var doc = new WordDocument();
        var section = doc.AddSection();
        var paragraph = section.AddParagraph();
        paragraph.ParagraphFormat.Bidi = true;
        paragraph.AppendText("הערה של המחבר כאן.");
        var authorComment = paragraph.AppendComment("הערה קיימת");
        authorComment.Format.User = "Author";
        var target = section.AddParagraph();
        target.ParagraphFormat.Bidi = true;
        target.AppendText("רוני הלכה לבית הספר.");
        var docx = Save(doc);

        var result = Annotate(docx, Sug("לבית הספר", "לביה\"ס", 25));

        Assert.Equal(1, result.Placed);
        var ids = Comments(result.Docx).Select(c => (string)c.Attribute(W + "id")!).ToList();
        Assert.Equal(2, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        using var reloaded = new WordDocument(new MemoryStream(result.Docx), FormatType.Docx);
        Assert.Equal(2, reloaded.Comments.Count);
    }

    // ------------------------------------------------------------------ DocIO reload

    [Fact]
    public void DocIO_reloads_every_revision_and_comment()
    {
        var result = Annotate(MixedFixture(), MixedSuggestions());

        Assert.Equal((4, 0), (result.Placed, result.Unanchored));
        using var reloaded = new WordDocument(new MemoryStream(result.Docx), FormatType.Docx);
        // replacement (del + ins) x2, deletion (del), insertion (ins) = 6 revisions, 4 comments.
        Assert.Equal(6, reloaded.Revisions.Count);
        Assert.Equal(4, reloaded.Comments.Count);
        Assert.All(reloaded.Revisions.Cast<Revision>(), r => Assert.Equal("PageDraft הגהה", r.Author));
        Assert.Equal(3, reloaded.Revisions.Cast<Revision>().Count(r => r.RevisionType == RevisionType.Deletions));
        Assert.Equal(3, reloaded.Revisions.Cast<Revision>().Count(r => r.RevisionType == RevisionType.Insertions));
    }

    // ------------------------------------------------------------------ accept-all

    [Fact]
    public void Accepting_every_revision_yields_the_corrected_text()
    {
        var result = Annotate(MixedFixture(), MixedSuggestions());

        using var reloaded = new WordDocument(new MemoryStream(result.Docx), FormatType.Docx);
        foreach (var revision in reloaded.Revisions.Cast<Revision>().ToList()) revision.Accept();

        var texts = reloaded.Sections[0].Paragraphs.Cast<WParagraph>().Select(p => p.Text).ToList();
        Assert.Contains("רוני צעדה אל בית הספר היום.", texts);
        Assert.Contains("הוא הלך הביתה ואמר שלומות לכולם.", texts);
        Assert.Contains("היא פגשה את דני ליד השער.", texts);
    }

    [Fact]
    public void Rejecting_every_revision_restores_the_original_text()
    {
        var result = Annotate(MixedFixture(), MixedSuggestions());

        using var reloaded = new WordDocument(new MemoryStream(result.Docx), FormatType.Docx);
        foreach (var revision in reloaded.Revisions.Cast<Revision>().ToList()) revision.Reject();

        var texts = reloaded.Sections[0].Paragraphs.Cast<WParagraph>().Select(p => p.Text).ToList();
        Assert.Contains("רוני הלכה לבית הספר היום.", texts);
        Assert.Contains("הוא הלך הלך הביתה ואמר של‏ום לכולם.", texts);
        Assert.Contains("היא פגשה את דני השער.", texts);
    }

    /// <summary>Three Hebrew paragraphs: a bold run in the middle of one, an RLM inside a word in another.</summary>
    private static byte[] MixedFixture() => Docx(
        P(("רוני הלכה ", false), ("לבית", true), (" הספר היום.", false)),
        P(("הוא הלך הלך הביתה ואמר של‏ום לכולם.", false)),
        P(("היא פגשה את דני השער.", false)));

    private static AnalysisSuggestion[] MixedSuggestions() => new[]
    {
        Sug("הלכה לבית", "צעדה אל בית", 5),
        Sug("הלך ", "", 30, before: "היום. הוא ", after: "הלך הביתה ואמר"),
        Sug("שלום", "שלומות", 50),
        Sug("", "ליד ", 70, before: "היא פגשה את דני ", after: "השער."),
    };

    // ------------------------------------------------------------------ helpers

    private ProofreadAnnotationResult Annotate(byte[] docx, params AnalysisSuggestion[] suggestions) =>
        _annotator.Annotate(docx, suggestions, "he", Stamp);

    private static AnalysisSuggestion Sug(
        string original, string suggested, int start, string? before = null, string? after = null,
        SuggestionOutcome? outcome = null, string? category = null, string? explanation = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            OriginalText = original,
            SuggestedText = suggested,
            StartOffset = start,
            EndOffset = start + original.Length,
            ContextBefore = before,
            ContextAfter = after,
            Outcome = outcome,
            Category = category,
            Explanation = explanation,
        };

    private static (string Text, bool Bold)[] P(params (string Text, bool Bold)[] runs) => runs;

    private static byte[] Docx(params (string Text, bool Bold)[][] paragraphs)
    {
        var doc = new WordDocument();
        var section = doc.AddSection();
        foreach (var runs in paragraphs)
        {
            var paragraph = section.AddParagraph();
            paragraph.ParagraphFormat.Bidi = true;
            foreach (var (text, bold) in runs)
            {
                var range = paragraph.AppendText(text);
                range.CharacterFormat.Bidi = true;
                range.CharacterFormat.Bold = bold;
                range.CharacterFormat.BoldBidi = bold;
            }
        }
        return Save(doc);
    }

    private static byte[] Save(WordDocument doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms, FormatType.Docx);
        doc.Dispose();
        return ms.ToArray();
    }

    private static XDocument Part(byte[] docx, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(docx), ZipArchiveMode.Read);
        var entry = zip.GetEntry(name) ?? throw new InvalidOperationException($"{name} missing");
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    private static XDocument Document(byte[] docx) => Part(docx, "word/document.xml");

    private static List<XElement> Comments(byte[] docx) => Part(docx, "word/comments.xml").Root!.Elements(W + "comment").ToList();

    private static string[] CommentLines(XElement comment) =>
        comment.Elements(W + "p").Select(p => string.Concat(p.Descendants(W + "t").Select(t => t.Value))).ToArray();

    /// <summary>The body paragraph whose visible or deleted text contains <paramref name="fragment"/>.</summary>
    private static XElement ParagraphContaining(byte[] docx, string fragment) =>
        Document(docx).Root!.Element(W + "body")!.Elements(W + "p")
            .Single(p => string.Concat(p.Descendants().Where(e => e.Name == W + "t" || e.Name == W + "delText")
                .Select(e => e.Value)).Contains(fragment, StringComparison.Ordinal));

    private static string Describe(XElement e)
    {
        if (e.Name == W + "r")
            return e.Element(W + "commentReference") != null ? "r:[ref]" : "r:" + string.Concat(e.Elements(W + "t").Select(t => t.Value));
        if (e.Name == W + "del") return "del:" + string.Concat(e.Descendants(W + "delText").Select(t => t.Value));
        if (e.Name == W + "ins") return "ins:" + string.Concat(e.Descendants(W + "t").Select(t => t.Value));
        return e.Name.LocalName;
    }

    private static bool IsBold(XElement run)
    {
        var b = run.Element(W + "rPr")?.Element(W + "b");
        if (b == null) return false;
        var val = (string?)b.Attribute(W + "val");
        return val is null or "1" or "true" or "on";
    }
}
