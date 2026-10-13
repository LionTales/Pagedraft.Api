using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Syncfusion.DocIO;
using Syncfusion.DocIO.DLS;
using Xunit;
using Ox = DocumentFormat.OpenXml.Wordprocessing;

namespace Pagedraft.Api.Tests;

/// <summary>
/// The placement branches the DocIO-built fixtures never reach (be-c06): a field result, an existing tracked
/// insertion, a hyperlink, a bookmark the deletion swallows, a soft hyphen or tab inside a span, touching spans,
/// an insertion at a deletion's edge, a point with no editable neighbour, XML-illegal model text, a footnote
/// that already holds revision ids and a run carrying a tracked formatting change. The inputs are written with
/// the Open XML SDK so each shape is exactly the markup named, and EVERY input is validated first, so "0 schema
/// errors" on the output is a statement about the annotator. Each output is then reloaded by DocIO and its
/// revisions accepted, the text the author gets when they accept everything.
/// </summary>
public partial class ProofreadDocxAnnotatorTests
{
    [Fact]
    public void A_span_in_a_field_result_is_unanchored_and_the_field_survives_the_edit_beside_it()
    {
        var docx = OxDocx(OxP(
            OxR("before "),
            new Ox.Run(new Ox.FieldChar { FieldCharType = Ox.FieldCharValues.Begin }),
            new Ox.Run(new Ox.FieldCode(" QUOTE 12 ") { Space = SpaceProcessingModeValues.Preserve }),
            new Ox.Run(new Ox.FieldChar { FieldCharType = Ox.FieldCharValues.Separate }),
            OxR("12"),
            new Ox.Run(new Ox.FieldChar { FieldCharType = Ox.FieldCharValues.End }),
            OxR(" after")));

        var result = Annotate(docx, Sug("before", "BEFORE", 0), Sug("12", "13", 7));

        Assert.Equal((1, 1), (result.Placed, result.Unanchored));
        AssertSchemaValid(result.Docx);
        Assert.Single(Document(result.Docx).Descendants(W + "instrText"));
        Assert.Equal("BEFORE 12 after", AcceptAllText(result.Docx));
    }

    [Fact]
    public void A_span_in_an_existing_tracked_insertion_is_unanchored_and_its_id_is_not_reused()
    {
        var docx = OxDocx(OxP(
            OxR("plain words "),
            new Ox.InsertedRun(OxR("tracked word")) { Id = "0", Author = "Author", Date = Stamp }));

        var result = Annotate(docx, Sug("plain", "Plain", 0), Sug("tracked", "T", 12));

        Assert.Equal((1, 1), (result.Placed, result.Unanchored));
        AssertSchemaValid(result.Docx);
        AssertRevisionIdsUnique(result.Docx);
        Assert.Equal("Plain words tracked word", AcceptAllText(result.Docx));
    }

    [Fact]
    public void An_edit_inside_a_hyperlink_stays_inside_the_hyperlink()
    {
        var docx = OxDocx(OxP(
            OxR("see "),
            new Ox.Hyperlink(OxR("the link text")) { Anchor = "target" },
            OxR(" now")));

        var result = Annotate(docx, Sug("link", "LINK", 8, explanation: "Capitals."));

        Assert.Equal((1, 0), (result.Placed, result.Unanchored));
        AssertSchemaValid(result.Docx);
        var link = Document(result.Docx).Descendants(W + "hyperlink").Single();
        Assert.Equal("link", link.Element(W + "del")!.Value);
        Assert.Equal("LINK", link.Element(W + "ins")!.Value);
        Assert.NotNull(link.Element(W + "commentRangeStart"));
        Assert.NotNull(link.Element(W + "commentRangeEnd"));
        Assert.Equal("see the LINK text now", AcceptAllText(result.Docx));
    }

    [Fact]
    public void A_bookmark_start_inside_the_span_is_swallowed_into_the_deletion_and_still_reloads()
    {
        var docx = OxDocx(OxP(
            OxR("alpha be"),
            new Ox.BookmarkStart { Id = "0", Name = "mark" },
            OxR("ta gamma"),
            new Ox.BookmarkEnd { Id = "0" }));

        var result = Annotate(docx, Sug("beta", "BETA", 6));

        Assert.Equal((1, 0), (result.Placed, result.Unanchored));
        AssertSchemaValid(result.Docx);
        var del = Document(result.Docx).Descendants(W + "del").Single();
        Assert.Equal("beta", del.Value);
        Assert.NotNull(del.Element(W + "bookmarkStart"));
        using (var reloaded = new WordDocument(new MemoryStream(result.Docx), FormatType.Docx))
            Assert.Equal(2, reloaded.Revisions.Count);
        Assert.Equal("alpha BETA gamma", AcceptAllText(result.Docx));
    }

    [Fact]
    public void A_span_across_a_soft_hyphen_or_a_tab_is_unanchored()
    {
        var docx = OxDocx(
            OxP(new Ox.Run(
                new Ox.Text("hyph"), new Ox.SoftHyphen(), new Ox.Text("enated"), new Ox.Text(" word"))),
            OxP(new Ox.Run(new Ox.Text("one"), new Ox.TabChar(), new Ox.Text("two")), OxR(" three")));

        var result = Annotate(docx,
            Sug("hyphenated", "X", 0), Sug("word", "WORD", 11), Sug("one two", "1 2", 16), Sug("three", "3", 24));

        Assert.Equal((2, 2), (result.Placed, result.Unanchored));
        AssertSchemaValid(result.Docx);
        Assert.Single(Document(result.Docx).Descendants(W + "softHyphen"));
        Assert.Single(Document(result.Docx).Descendants(W + "tab"));
        // DocIO's GetText reads the soft hyphen as U+001F (Word's own soft-hyphen code) and the tab as a space.
        Assert.Equal("hyph\u001Fenated WORD\none two 3", AcceptAllText(result.Docx));
    }

    [Fact]
    public void Touching_spans_are_both_placed()
    {
        var docx = OxDocx(OxP(OxR("abcdef ghi")));

        var result = Annotate(docx, Sug("abc", "X", 0), Sug("def", "Y", 3));

        Assert.Equal((2, 0), (result.Placed, result.Unanchored));
        AssertSchemaValid(result.Docx);
        AssertRevisionIdsUnique(result.Docx);
        Assert.Equal("XY ghi", AcceptAllText(result.Docx));
    }

    [Fact]
    public void An_insertion_exactly_at_a_deletions_edge_is_dropped_and_counted()
    {
        var docx = OxDocx(OxP(OxR("one two three")));

        var result = Annotate(docx, Sug("two", "2", 4), Sug("", "!", 7, before: "one two", after: " three"));

        Assert.Equal((1, 1), (result.Placed, result.Unanchored));
        AssertSchemaValid(result.Docx);
        Assert.Equal("one 2 three", AcceptAllText(result.Docx));
    }

    [Fact]
    public void An_insertion_with_no_editable_neighbour_is_unanchored()
    {
        // The point lies between two field results: neither neighbour can take a w:ins beside it.
        var docx = OxDocx(OxP(OxR("x "), Field("12"), Field("34"), OxR(" y")));

        var result = Annotate(docx, Sug("", "-", 4, before: "x 12", after: "34 y"), Sug("y", "Y", 7));

        Assert.Equal((1, 1), (result.Placed, result.Unanchored));
        AssertSchemaValid(result.Docx);
        Assert.Equal("x 1234 Y", AcceptAllText(result.Docx));
    }

    [Fact]
    public void An_explanation_with_xml_illegal_characters_is_written_without_them()
    {
        var docx = OxDocx(OxP(OxR("hello world")));

        // Each of these threw "hexadecimal value ..., is an invalid character" at Save before be-c06.
        var result = Annotate(docx, Sug("world", "World", 6, explanation: "bad \uFFFF char \uD800 x\u0001 \uFFFE end 😀"));

        Assert.Equal((1, 0), (result.Placed, result.Unanchored));
        AssertSchemaValid(result.Docx);
        Assert.Equal(new[] { "bad char x end 😀" }, CommentLines(Comments(result.Docx).Single()));
        using (var reloaded = new WordDocument(new MemoryStream(result.Docx), FormatType.Docx))
            Assert.Equal(1, reloaded.Comments.Count);
        Assert.Equal("hello World", AcceptAllText(result.Docx));
    }

    [Fact]
    public void A_suggested_text_with_xml_illegal_characters_is_written_without_them_keeping_valid_pairs()
    {
        var docx = OxDocx(OxP(OxR("hello world")));

        var result = Annotate(docx,
            Sug("world", "Wor\uFFFFld\uDE00 😀\u0001", 6),
            Sug("", "\uFFFF\uD800", 5, before: "hello", after: " World")); // cleans to nothing: unanchored

        Assert.Equal((1, 1), (result.Placed, result.Unanchored));
        AssertSchemaValid(result.Docx);
        Assert.Equal("World 😀", Document(result.Docx).Descendants(W + "ins").Single().Value);
        Assert.Equal("hello World 😀", AcceptAllText(result.Docx));
    }

    [Fact]
    public void A_span_or_point_that_would_cut_a_surrogate_pair_is_unanchored()
    {
        var docx = OxDocx(OxP(OxR("smile 😀x end")));

        // A lone low surrogate in the original matches the second half of the document's pair.
        var result = Annotate(docx,
            Sug("\uDE00x", "y", 7),
            Sug("", "-", 7, before: "smile \uD83D", after: "\uDE00x end"),
            Sug("end", "END", 10));

        Assert.Equal((1, 2), (result.Placed, result.Unanchored));
        AssertSchemaValid(result.Docx);
        Assert.Equal("smile 😀x END", AcceptAllText(result.Docx));
    }

    [Fact]
    public void Revision_ids_are_allocated_past_the_ids_a_footnote_already_holds()
    {
        var docx = OxDocx(main =>
        {
            var footnotes = main.AddNewPart<FootnotesPart>();
            footnotes.Footnotes = new Ox.Footnotes(
                new Ox.Footnote(new Ox.Paragraph(new Ox.Run(new Ox.SeparatorMark())))
                { Id = -1, Type = Ox.FootnoteEndnoteValues.Separator },
                new Ox.Footnote(new Ox.Paragraph(new Ox.Run(new Ox.ContinuationSeparatorMark())))
                { Id = 0, Type = Ox.FootnoteEndnoteValues.ContinuationSeparator },
                new Ox.Footnote(new Ox.Paragraph(
                    new Ox.InsertedRun(OxR("note added")) { Id = "3", Author = "Author", Date = Stamp },
                    new Ox.DeletedRun(new Ox.Run(new Ox.DeletedText("note gone") { Space = SpaceProcessingModeValues.Preserve }))
                    { Id = "4", Author = "Author", Date = Stamp }))
                { Id = 1 });
            footnotes.Footnotes.Save();
        }, OxP(OxR("body text here"), new Ox.Run(new Ox.FootnoteReference { Id = 1 })));

        var result = Annotate(docx, Sug("body", "BODY", 0), Sug("here", "HERE", 10));

        Assert.Equal((2, 0), (result.Placed, result.Unanchored));
        AssertSchemaValid(result.Docx);
        AssertRevisionIdsUnique(result.Docx, "word/document.xml", "word/footnotes.xml");
        // GetText appends the footnote; accepting its own revisions keeps "note added" and drops "note gone".
        Assert.Equal("BODY text HEREnote added", AcceptAllText(result.Docx));
    }

    [Fact]
    public void A_tracked_formatting_change_on_the_run_is_renumbered_in_every_clone()
    {
        Ox.RunProperties Bold() => new(new Ox.Bold(),
            new Ox.RunPropertiesChange(new Ox.PreviousRunProperties()) { Id = "5", Author = "Author", Date = Stamp });
        var docx = OxDocx(OxP(OxR("format change word", Bold())));

        var result = Annotate(docx, Sug("change", "CHANGE", 7));

        Assert.Equal((1, 0), (result.Placed, result.Unanchored));
        AssertSchemaValid(result.Docx);
        // Four runs now carry the author's formatting change (left fragment, deleted, inserted, right fragment),
        // each with its own id, the original keeping 5.
        var changeIds = Document(result.Docx).Descendants(W + "rPrChange").Select(e => (string)e.Attribute(W + "id")!).ToList();
        Assert.Equal(4, changeIds.Count);
        Assert.Equal(4, changeIds.Distinct().Count());
        Assert.Contains("5", changeIds);
        AssertRevisionIdsUnique(result.Docx);
        Assert.Equal("format CHANGE word", AcceptAllText(result.Docx));
    }

    /// <summary>final-r01: the id scan streams every XML part, and "every XML part" must not include a BINARY part
    /// whose OOXML content type merely contains "xml" (an embedded workbook, which every native chart carries, an
    /// OLE object, an obfuscated font). The scan threw on one, so the whole unit shipped unannotated.</summary>
    [Fact]
    public void A_binary_part_whose_content_type_mentions_xml_does_not_stop_the_annotation()
    {
        var docx = OxDocx(main =>
        {
            var workbook = main.AddEmbeddedPackagePart("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
            using var bytes = new MemoryStream(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0xFF, 0x10, 0x20 });
            workbook.FeedData(bytes);
        }, OxP(OxR("body text here")));

        var result = Annotate(docx, Sug("body", "BODY", 0));

        Assert.Equal((1, 0), (result.Placed, result.Unanchored));
        Assert.Equal("BODY text here", AcceptAllText(result.Docx));
    }

    // ------------------------------------------------------------------ Open XML fixtures

    private static Ox.Run OxR(string text, Ox.RunProperties? properties = null)
    {
        var run = new Ox.Run();
        if (properties != null) run.Append(properties);
        run.Append(new Ox.Text(text) { Space = SpaceProcessingModeValues.Preserve });
        return run;
    }

    /// <summary>A complex field (QUOTE) whose visible result is <paramref name="result"/>.</summary>
    private static OpenXmlElement[] Field(string result) => new OpenXmlElement[]
    {
        new Ox.Run(new Ox.FieldChar { FieldCharType = Ox.FieldCharValues.Begin }),
        new Ox.Run(new Ox.FieldCode($" QUOTE {result} ") { Space = SpaceProcessingModeValues.Preserve }),
        new Ox.Run(new Ox.FieldChar { FieldCharType = Ox.FieldCharValues.Separate }),
        OxR(result),
        new Ox.Run(new Ox.FieldChar { FieldCharType = Ox.FieldCharValues.End }),
    };

    /// <summary>A paragraph of elements and element groups (<see cref="Field"/>), flattened in order.</summary>
    private static Ox.Paragraph OxP(params object[] content) =>
        new(content.SelectMany(c => c as OpenXmlElement[] ?? new[] { (OpenXmlElement)c }));

    private static byte[] OxDocx(params Ox.Paragraph[] paragraphs) => OxDocx(null, paragraphs);

    /// <summary>A package written by the Open XML SDK, asserted schema-valid BEFORE the annotator sees it.</summary>
    private static byte[] OxDocx(Action<MainDocumentPart>? extra, params Ox.Paragraph[] paragraphs)
    {
        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new Ox.Document(new Ox.Body(paragraphs));
                extra?.Invoke(main);
                main.Document.Save();
            }
            bytes = ms.ToArray();
        }
        AssertSchemaValid(bytes);
        return bytes;
    }

    private static void AssertSchemaValid(byte[] docx)
    {
        using var doc = WordprocessingDocument.Open(new MemoryStream(docx), false);
        var errors = new OpenXmlValidator(FileFormatVersions.Office2019).Validate(doc)
            .Select(e => $"{e.Part?.Uri} {e.Path?.XPath}: {e.Description}").ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors));
    }

    /// <summary>Every w:ins / w:del / w:rPrChange id across the named parts (default: the body) is distinct.</summary>
    private static void AssertRevisionIdsUnique(byte[] docx, params string[] parts)
    {
        var names = new[] { W + "ins", W + "del", W + "rPrChange" };
        var ids = (parts.Length == 0 ? new[] { "word/document.xml" } : parts)
            .SelectMany(p => Part(docx, p).Descendants().Where(e => names.Contains(e.Name)))
            .Select(e => (string)e.Attribute(W + "id")!)
            .ToList();
        Assert.True(ids.Count == ids.Distinct().Count(), "duplicate revision ids: " + string.Join(",", ids));
    }

    /// <summary>The body text DocIO gives after accepting every revision, paragraphs joined by <c>\n</c>.</summary>
    private static string AcceptAllText(byte[] docx)
    {
        using var reloaded = new WordDocument(new MemoryStream(docx), FormatType.Docx);
        foreach (var revision in reloaded.Revisions.Cast<Revision>().ToList()) revision.Accept();
        var text = SyncfusionWatermarkStripper.StripSyncfusionWatermark(reloaded.GetText());
        return text.Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n');
    }
}
