using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Pagedraft.Api.Data;
using Pagedraft.Api.Hubs;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Xunit;
using OxmlW = DocumentFormat.OpenXml.Wordprocessing;

namespace Pagedraft.Api.Tests;

/// <summary>
/// The export's paragraph direction (plan whole-book-proofread-and-annotated-docx-2026-10-11, todo c11). The
/// owner's Word manuscript declares right-to-left ONLY on its Normal style and its section, the import drops
/// both, and the export wrote no <c>w:bidi</c> anywhere, so Word laid a Hebrew book out left-to-right. The
/// fixtures go through the REAL import (<see cref="SfdtConversionService.ConvertToSfdt"/>, the minimal-package
/// path) so the SFDT has exactly the stored shape: paragraphs with no direction. Every assertion reads the
/// produced package's XML element by element.
/// </summary>
public class ExportParagraphDirectionTests
{
    // Invented for this test; never the author's prose.
    private const string Body = "השמש זרחה מעל הגבעות.";
    private const string Explicit = "Plain English line.";
    private const string RightAligned = "הקדשה קצרה.";

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    [Fact]
    public void Premise_the_imported_SFDT_carries_no_paragraph_direction()
    {
        // If the import ever starts keeping direction, this todo's reason is gone: re-measure before trusting.
        var docx = Export(ImportedSfdt(), language: null);

        var body = ParagraphByText(docx, Body);
        Assert.Null(body.Element(W + "pPr")?.Element(W + "bidi"));
        Assert.Null(DefaultParagraphStyle(docx).Element(W + "pPr")?.Element(W + "bidi"));
        Assert.All(Sections(docx), s => Assert.Null(s.Element(W + "bidi")));
    }

    [Fact]
    public void Hebrew_book_export_declares_rtl_on_the_Normal_style_and_every_section()
    {
        var docx = Export(ImportedSfdt(), "he");

        var normal = DefaultParagraphStyle(docx);
        Assert.Equal("Normal", normal.Element(W + "name")?.Attribute(W + "val")?.Value);
        Assert.True(IsOn(normal.Element(W + "pPr")?.Element(W + "bidi")), $"Normal style has no <w:bidi/>: {normal}");

        var sections = Sections(docx);
        Assert.NotEmpty(sections);
        Assert.All(sections, s => Assert.True(IsOn(s.Element(W + "bidi")), $"section without <w:bidi/>: {s}"));
    }

    [Fact]
    public void Hebrew_book_body_paragraph_inherits_rtl_and_keeps_its_indent_and_justification()
    {
        var docx = Export(ImportedSfdt(), "he");

        var body = ParagraphByText(docx, Body);
        // Inherits: the paragraph itself states no direction, so the style decides.
        Assert.Null(body.Element(W + "pPr")?.Element(W + "bidi"));
        Assert.True(EffectiveRtl(docx, body));
        Assert.Equal("both", body.Element(W + "pPr")?.Element(W + "jc")?.Attribute(W + "val")?.Value);
        Assert.Equal("283", body.Element(W + "pPr")?.Element(W + "ind")?.Attribute(W + "firstLine")?.Value);
    }

    [Fact]
    public void Explicitly_ltr_paragraph_stays_ltr()
    {
        var sfdt = ImportedSfdt();
        // Non-vacuity: the SFDT itself says LTR for that paragraph (the owner's 7 explicit w:bidi="0" lines).
        Assert.Contains("\"bi\":0", Inflate(sfdt));

        var docx = Export(sfdt, "he");

        var ltr = ParagraphByText(docx, Explicit);
        var own = ltr.Element(W + "pPr")?.Element(W + "bidi");
        Assert.True(own != null, $"the explicit-LTR paragraph lost its own w:bidi: {ltr}");
        Assert.False(IsOn(own));
        Assert.False(EffectiveRtl(docx, ltr));
    }

    [Fact]
    public void Alignment_values_are_written_exactly_as_for_a_non_hebrew_export()
    {
        // The trap (CLAUDE.md §4): Word's jc reads differently under bidi, so the fix must not ALSO rewrite jc.
        // The source manuscript's own XML is the reference: the same jc under the same inherited bidi.
        var sfdt = ImportedSfdt();
        var he = Paragraphs(Export(sfdt, "he"));
        var en = Paragraphs(Export(sfdt, "en"));

        Assert.Equal(en.Count, he.Count);
        Assert.Contains(he, p => Jc(p) == "right"); // non-vacuity: a non-justified alignment is in the set
        Assert.Equal(en.Select(Jc), he.Select(Jc));
    }

    [Theory]
    [InlineData("en")]
    [InlineData(null)]
    public void Non_hebrew_book_export_declares_no_direction(string? language)
    {
        var docx = Export(ImportedSfdt(), language);

        Assert.Null(DefaultParagraphStyle(docx).Element(W + "pPr")?.Element(W + "bidi"));
        Assert.All(Sections(docx), s => Assert.Null(s.Element(W + "bidi")));
        Assert.False(EffectiveRtl(docx, ParagraphByText(docx, Body)));
    }

    [Fact]
    public async Task Whole_book_assembly_keeps_rtl_on_every_chapter_section()
    {
        var (db, export) = Build();
        var book = new Book { Id = Guid.NewGuid(), Title = "גבעות", Language = "he" };
        db.Books.Add(book);
        var first = NewChapter(book.Id, 0);
        db.Chapters.Add(first);
        db.Chapters.Add(NewChapter(book.Id, 1));
        db.Chapters.Add(NewChapter(book.Id, 2));
        await db.SaveChangesAsync();

        var bookDocx = (await export.ExportBookAsync(book.Id, CancellationToken.None)).Content!;
        var chapterDocx = (await export.ExportChapterAsync(book.Id, first.Id, CancellationToken.None)).Content!;

        foreach (var (label, docx, chapters) in new[] { ("book", bookDocx, 3), ("chapter", chapterDocx, 1) })
        {
            var sections = Sections(docx);
            Assert.True(sections.Count >= chapters, $"[{label}] expected one section per chapter, got {sections.Count}");
            Assert.All(sections, s => Assert.True(IsOn(s.Element(W + "bidi")), $"[{label}] section without <w:bidi/>"));
            Assert.True(IsOn(DefaultParagraphStyle(docx).Element(W + "pPr")?.Element(W + "bidi")), $"[{label}] Normal not RTL");

            var bodies = Paragraphs(docx).Where(p => Text(p) == Body).ToList();
            Assert.Equal(chapters, bodies.Count);
            Assert.All(bodies, p => Assert.True(EffectiveRtl(docx, p), $"[{label}] body paragraph is not RTL"));
            Assert.All(Paragraphs(docx).Where(p => Text(p) == Explicit), p => Assert.False(EffectiveRtl(docx, p)));
        }
    }

    // ---- fixtures and helpers -----------------------------------------------------------------------------

    /// <summary>
    /// The owner's manuscript shape through the real import: justified body text with a 283-twip first-line
    /// indent and no direction of its own, one explicitly LTR paragraph, one right-aligned dedication line.
    /// </summary>
    private static string ImportedSfdt()
    {
        static OxmlW.Paragraph Para(string text, OxmlW.JustificationValues jc, bool explicitLtr = false, bool indent = true)
        {
            var pPr = new OxmlW.ParagraphProperties();
            if (explicitLtr) pPr.Append(new OxmlW.BiDi { Val = OnOffValue.FromBoolean(false) });
            if (indent) pPr.Append(new OxmlW.Indentation { FirstLine = "283" });
            pPr.Append(new OxmlW.Justification { Val = jc });
            return new OxmlW.Paragraph(pPr,
                new OxmlW.Run(new OxmlW.RunProperties(new OxmlW.RightToLeftText()), new OxmlW.Text(text)));
        }

        var body = new List<OpenXmlElement>
        {
            Para(RightAligned, OxmlW.JustificationValues.Right, indent: false),
            Para(Body, OxmlW.JustificationValues.Both),
            Para(Explicit, OxmlW.JustificationValues.Both, explicitLtr: true),
        };
        return new SfdtConversionService().ConvertToSfdt(body).SfdtJson;
    }

    private static byte[] Export(string sfdt, string? language) => new SfdtConversionService().ConvertSfdtToDocx(sfdt, language);

    /// <summary>The SFDT text, inflated when it is the server's <c>{"sfdt": base64 zip}</c> wrapper.</summary>
    private static string Inflate(string sfdt)
    {
        var wrapper = Newtonsoft.Json.Linq.JObject.Parse(sfdt);
        if (wrapper["sfdt"] is not { } packed) return sfdt;
        using var zip = new ZipArchive(new MemoryStream(Convert.FromBase64String((string)packed!)));
        using var reader = new StreamReader(zip.Entries[0].Open());
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Whether Word lays <paramref name="paragraph"/> out right-to-left: its own <c>w:bidi</c>, else the
    /// paragraph style chain's (the default paragraph style when none is named), else the document defaults.
    /// </summary>
    private static bool EffectiveRtl(byte[] docx, XElement paragraph)
    {
        var own = paragraph.Element(W + "pPr")?.Element(W + "bidi");
        if (own != null) return IsOn(own);

        var styles = Part(docx, "word/styles.xml");
        var byId = styles.Descendants(W + "style")
            .Where(s => s.Attribute(W + "styleId") != null)
            .GroupBy(s => s.Attribute(W + "styleId")!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        var styleId = paragraph.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val")?.Value
            ?? DefaultParagraphStyle(docx).Attribute(W + "styleId")?.Value;

        for (var hops = 0; styleId != null && hops < 16 && byId.TryGetValue(styleId, out var style); hops++)
        {
            var fromStyle = style.Element(W + "pPr")?.Element(W + "bidi");
            if (fromStyle != null) return IsOn(fromStyle);
            styleId = style.Element(W + "basedOn")?.Attribute(W + "val")?.Value;
        }

        return IsOn(styles.Root?.Element(W + "docDefaults")?.Element(W + "pPrDefault")?.Element(W + "pPr")?.Element(W + "bidi"));
    }

    /// <summary>An OOXML on/off element: present with no <c>w:val</c>, or <c>true</c>/<c>1</c>/<c>on</c>.</summary>
    private static bool IsOn(XElement? element)
    {
        if (element == null) return false;
        var value = element.Attribute(W + "val")?.Value;
        return value is null or "1" or "true" or "on";
    }

    private static XElement DefaultParagraphStyle(byte[] docx) =>
        Part(docx, "word/styles.xml").Descendants(W + "style").Single(s =>
            s.Attribute(W + "type")?.Value == "paragraph" && s.Attribute(W + "default")?.Value is "1" or "true");

    private static List<XElement> Sections(byte[] docx) => Part(docx, "word/document.xml").Descendants(W + "sectPr").ToList();

    private static List<XElement> Paragraphs(byte[] docx) => Part(docx, "word/document.xml").Descendants(W + "p").ToList();

    private static XElement ParagraphByText(byte[] docx, string text) => Paragraphs(docx).Single(p => Text(p) == text);

    private static string? Jc(XElement paragraph) => paragraph.Element(W + "pPr")?.Element(W + "jc")?.Attribute(W + "val")?.Value;

    private static string Text(XElement paragraph) => string.Concat(paragraph.Descendants(W + "t").Select(t => t.Value));

    private static XDocument Part(byte[] docx, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(docx));
        var entry = zip.GetEntry(name);
        Assert.True(entry != null, $"package has no {name}");
        using var stream = entry!.Open();
        return XDocument.Load(stream);
    }

    private static Chapter NewChapter(Guid bookId, int order) => new()
    {
        Id = Guid.NewGuid(),
        BookId = bookId,
        Order = order,
        Title = $"פרק {order + 1}",
        ContentText = RightAligned + " " + Body + " " + Explicit,
        WordCount = 9,
        ContentSfdt = ImportedSfdt()
    };

    private static (AppDbContext Db, BookExportService Export) Build()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AppDbContext(options);
        var sfdt = new SfdtConversionService();
        var chapters = new ChapterService(db, new Mock<IHubContext<BookSyncHub>>().Object, sfdt);
        return (db, new BookExportService(db, chapters, sfdt, new BookAssemblyService()));
    }
}
