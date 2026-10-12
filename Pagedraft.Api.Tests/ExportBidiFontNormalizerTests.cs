using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pagedraft.Api.Data;
using Pagedraft.Api.Hubs;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Syncfusion.DocIO;
using Syncfusion.DocIO.DLS;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// The export side of the export-then-paste gibberish (plan whole-book-proofread-and-annotated-docx-2026-10-11,
/// todo c2). A paste from desktop Word stored Word's charset-variant font name "Calibri (Hebrew)" in the chapter
/// SFDT; the export wrote it as <c>w:cs="Calibri (Hebrew)"</c>, a font no machine has, so copying the file out of
/// Word made Word declare it <c>\fcharset0</c> and the paste back decoded as Latin-1. Every assertion here reads
/// the produced package's XML parts element by element, never a substring of the whole file.
/// </summary>
public class ExportBidiFontNormalizerTests
{
    private const string Pseudo = "Calibri (Hebrew)";

    // Invented for this test; never the author's prose.
    private const string FirstSentence = "השמש זרחה מעל הגבעות.";
    private const string SecondSentence = "הרוח נשבה בין העצים.";

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    // ---- the name rule ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Calibri (Hebrew)", "Calibri")]
    [InlineData("Times New Roman (Hebrew)", "Times New Roman")]
    [InlineData("Arial (Arabic)", "Arial")]
    [InlineData("Calibri", "Calibri")]
    [InlineData("David", "David")]
    [InlineData("Times New Roman CE", "Times New Roman CE")]
    [InlineData("Calibri (Vietnamese)", "Calibri (Vietnamese)")]
    [InlineData("Calibri (hebrew)", "Calibri (hebrew)")]
    [InlineData(" (Hebrew)", " (Hebrew)")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void Strip_removes_only_the_rtl_charset_variant_suffix(string? input, string? expected)
    {
        Assert.Equal(expected, PseudoBidiFontNames.Strip(input));
        Assert.Equal(!Equals(input, expected), PseudoBidiFontNames.IsPseudo(input));
    }

    // ---- SFDT -> DOCX, the shape the editor stores --------------------------------------------------------

    [Fact]
    public void Premise_without_the_walk_the_export_writes_the_pseudo_name_as_w_cs()
    {
        // Pins the defect this file guards: if Syncfusion ever drops the name itself, re-measure before trusting.
        using var document = Syncfusion.EJ2.DocumentEditor.WordDocument.Save(OptimizedSfdt(paragraphMarkFont: Pseudo));
        using var stream = new MemoryStream();
        document.Save(stream, FormatType.Docx);

        var runs = HebrewRuns(Part(stream.ToArray(), "word/document.xml"));
        Assert.NotEmpty(runs);
        Assert.All(runs, r => Assert.Equal(Pseudo, r.Element(W + "rPr")?.Element(W + "rFonts")?.Attribute(W + "cs")?.Value));
    }

    [Theory]
    [MemberData(nameof(PseudoFontSfdtShapes))]
    public void Hebrew_book_export_names_the_real_face_and_stamps_he_IL(string shape, string sfdt)
    {
        var docx = new SfdtConversionService().ConvertSfdtToDocx(sfdt, "he");

        var runs = HebrewRuns(Part(docx, "word/document.xml"));
        Assert.True(runs.Count >= 2, $"[{shape}] expected both invented sentences as Hebrew runs, found {runs.Count}");
        foreach (var run in runs)
        {
            var rPr = run.Element(W + "rPr");
            Assert.True(rPr != null, $"[{shape}] Hebrew run without rPr: {run}");
            Assert.Equal("Calibri", rPr!.Element(W + "rFonts")?.Attribute(W + "cs")?.Value);
            Assert.Equal("he-IL", EffectiveBidiLanguage(docx, run));
        }

        AssertNoPseudoFontAnywhere(docx, shape);
    }

    [Fact]
    public void Paragraph_mark_format_is_cleaned_too()
    {
        var docx = new SfdtConversionService().ConvertSfdtToDocx(OptimizedSfdt(paragraphMarkFont: Pseudo), "he");

        var marks = Part(docx, "word/document.xml").Descendants(W + "p")
            .Where(p => HebrewRunsOf(p).Any())
            .Select(p => p.Element(W + "pPr")?.Element(W + "rPr"))
            .ToList();

        Assert.NotEmpty(marks);
        var firstMark = marks[0];
        Assert.True(firstMark != null, "the paragraph mark lost its run properties; nothing left to assert");
        Assert.Equal("Calibri", firstMark!.Element(W + "rFonts")?.Attribute(W + "cs")?.Value);
        Assert.Equal("he-IL", EffectiveBidiLanguage(docx, firstMark));
    }

    [Theory]
    [InlineData("en")]
    [InlineData(null)]
    public void Non_hebrew_book_strips_the_suffix_but_stamps_no_language(string? language)
    {
        var docx = new SfdtConversionService().ConvertSfdtToDocx(OptimizedSfdt(paragraphMarkFont: Pseudo), language);

        var runs = HebrewRuns(Part(docx, "word/document.xml"));
        var unstamped = runs.Single(r => Text(r).Contains(SecondSentence, StringComparison.Ordinal));
        Assert.Equal("Calibri", unstamped.Element(W + "rPr")?.Element(W + "rFonts")?.Attribute(W + "cs")?.Value);
        Assert.NotEqual("he-IL", EffectiveBidiLanguage(docx, unstamped));

        // The run the SFDT itself tagged he-IL keeps it: the language rule only fills what is unset.
        var tagged = runs.Single(r => Text(r).Contains(FirstSentence, StringComparison.Ordinal));
        Assert.Equal("he-IL", EffectiveBidiLanguage(docx, tagged));

        AssertNoPseudoFontAnywhere(docx, language ?? "null");
    }

    [Fact]
    public void A_document_without_pseudo_fonts_keeps_its_fonts()
    {
        var sfdt = OptimizedSfdt(runFont: "David", paragraphMarkFont: "David");

        var docx = new SfdtConversionService().ConvertSfdtToDocx(sfdt, "he");

        foreach (var run in HebrewRuns(Part(docx, "word/document.xml")))
            Assert.Equal("David", run.Element(W + "rPr")?.Element(W + "rFonts")?.Attribute(W + "cs")?.Value);
    }

    // ---- the DocIO walk reaches every part ----------------------------------------------------------------

    [Fact]
    public void Walk_cleans_styles_tables_headers_and_footnotes()
    {
        using var document = new WordDocument();
        document.EnsureMinimal();
        var section = document.LastSection;

        var normal = (WParagraphStyle)document.Styles.FindByName("Normal")!;
        normal.CharacterFormat.FontNameBidi = Pseudo;

        var body = document.LastParagraph;
        body.AppendText(FirstSentence).CharacterFormat.FontNameBidi = Pseudo;
        body.BreakCharacterFormat.FontName = Pseudo;

        var table = section.AddTable();
        table.ResetCells(1, 1);
        table.Rows[0].Cells[0].AddParagraph().AppendText(SecondSentence).CharacterFormat.FontNameBidi = "Times New Roman (Arabic)";

        section.HeadersFooters.OddHeader.AddParagraph().AppendText("כותרת").CharacterFormat.FontNameBidi = Pseudo;

        var footnote = (WFootnote)body.AppendFootnote(FootnoteType.Footnote);
        footnote.TextBody.AddParagraph().AppendText("הערה").CharacterFormat.FontNameBidi = Pseudo;

        var changed = ExportBidiFontNormalizer.Apply(document, stampHebrewLocale: true);

        Assert.True(changed >= 5, $"expected at least the five seeded formats to change, got {changed}");
        using var stream = new MemoryStream();
        document.Save(stream, FormatType.Docx);
        var docx = stream.ToArray();

        AssertNoPseudoFontAnywhere(docx, "docio-built");
        var styles = Part(docx, "word/styles.xml");
        var normalRPr = styles.Descendants(W + "style")
            .Single(s => s.Attribute(W + "styleId")?.Value == "Normal")
            .Element(W + "rPr");
        Assert.Equal("Calibri", normalRPr?.Element(W + "rFonts")?.Attribute(W + "cs")?.Value);
        Assert.Equal("he-IL", normalRPr?.Element(W + "lang")?.Attribute(W + "bidi")?.Value);

        var cellRun = HebrewRuns(Part(docx, "word/document.xml")).Single(r => Text(r) == SecondSentence);
        Assert.Equal("Times New Roman", cellRun.Element(W + "rPr")?.Element(W + "rFonts")?.Attribute(W + "cs")?.Value);

        // Non-vacuity: the header and footnote parts exist and hold the cleaned Hebrew runs.
        Assert.Contains(PartNames(docx), n => n.StartsWith("word/header", StringComparison.Ordinal));
        Assert.Contains(PartNames(docx), n => n == "word/footnotes.xml");
        Assert.Contains(HebrewRuns(Part(docx, "word/footnotes.xml")),
            r => r.Element(W + "rPr")?.Element(W + "rFonts")?.Attribute(W + "cs")?.Value == "Calibri");
    }

    // ---- the export service threads the book's language ---------------------------------------------------

    [Theory]
    [InlineData("he", true)]
    [InlineData("en", false)]
    public async Task Chapter_and_book_exports_use_the_books_language(string language, bool expectStamp)
    {
        var (db, export) = Build();
        var book = new Book { Id = Guid.NewGuid(), Title = "גבעות", Language = language };
        db.Books.Add(book);
        var chapter = NewChapter(book.Id, 0, OptimizedSfdt(paragraphMarkFont: Pseudo));
        db.Chapters.Add(chapter);
        db.Chapters.Add(NewChapter(book.Id, 1, OptimizedSfdt(paragraphMarkFont: Pseudo)));
        await db.SaveChangesAsync();

        var chapterResult = await export.ExportChapterAsync(book.Id, chapter.Id, CancellationToken.None);
        var bookResult = await export.ExportBookAsync(book.Id, CancellationToken.None);

        foreach (var (label, bytes) in new[] { ("chapter", chapterResult.Content!), ("book", bookResult.Content!) })
        {
            AssertNoPseudoFontAnywhere(bytes, label);
            var unset = HebrewRuns(Part(bytes, "word/document.xml"))
                .Where(r => Text(r).Contains(SecondSentence, StringComparison.Ordinal))
                .ToList();
            Assert.Equal(label == "book" ? 2 : 1, unset.Count);
            foreach (var run in unset)
            {
                var bidi = EffectiveBidiLanguage(bytes, run);
                if (expectStamp) Assert.Equal("he-IL", bidi);
                else Assert.NotEqual("he-IL", bidi);
            }
        }
    }

    // ---- fixtures and helpers -----------------------------------------------------------------------------

    public static IEnumerable<object[]> PseudoFontSfdtShapes()
    {
        yield return new object[] { "optimized", OptimizedSfdt(paragraphMarkFont: Pseudo) };
        yield return new object[] { "wrapped", WrappedSfdt(paragraphMarkFont: Pseudo) };
    }

    /// <summary>
    /// The compact SFDT the editor saves (<c>"optimizeSfdt":true</c>; every affected dev-DB chapter is this shape),
    /// i.e. the inflated body of <see cref="WrappedSfdt"/>.
    /// </summary>
    private static string OptimizedSfdt(string runFont = Pseudo, string paragraphMarkFont = "Calibri")
    {
        var wrapper = JObject.Parse(WrappedSfdt(runFont, paragraphMarkFont));
        using var zip = new ZipArchive(new MemoryStream(Convert.FromBase64String((string)wrapper["sfdt"]!)));
        using var reader = new StreamReader(zip.Entries[0].Open());
        var sfdt = reader.ReadToEnd();
        Assert.Contains("\"optimizeSfdt\":true", sfdt);
        // Non-vacuity: the font under test survived into the SFDT, so a clean export is the walk's doing.
        Assert.Contains("\"" + runFont + "\"", sfdt);
        return sfdt;
    }

    /// <summary>
    /// What the server's own converters emit (the paste endpoint and the import path both serialize an EJ2
    /// document to <c>{"sfdt": base64 zip}</c>), built the way the stored data was: a Word document loaded by
    /// EJ2. The first run carries the pseudo name with he-IL, exactly as the pasted runs do; the second carries
    /// the pseudo name with no complex-script language, which is what the locale stamp fills.
    /// </summary>
    private static string WrappedSfdt(string runFont = Pseudo, string paragraphMarkFont = "Calibri")
    {
        using var source = new WordDocument();
        source.EnsureMinimal();

        var first = source.LastParagraph;
        first.ParagraphFormat.Bidi = true;
        first.BreakCharacterFormat.FontName = paragraphMarkFont;
        first.BreakCharacterFormat.FontNameBidi = paragraphMarkFont;
        var tagged = first.AppendText(FirstSentence);
        tagged.CharacterFormat.FontName = runFont;
        tagged.CharacterFormat.FontNameBidi = runFont;
        tagged.CharacterFormat.Bidi = true;
        tagged.CharacterFormat.LocaleIdBidi = ExportBidiFontNormalizer.HebrewLocaleId;

        var second = (WParagraph)source.LastSection.AddParagraph();
        second.ParagraphFormat.Bidi = true;
        var untagged = second.AppendText(SecondSentence);
        untagged.CharacterFormat.FontName = runFont;
        untagged.CharacterFormat.FontNameBidi = runFont;
        untagged.CharacterFormat.Bidi = true;

        using var stream = new MemoryStream();
        source.Save(stream, FormatType.Docx);
        stream.Position = 0;
        var loaded = Syncfusion.EJ2.DocumentEditor.WordDocument.Load(stream, Syncfusion.EJ2.DocumentEditor.FormatType.Docx);
        try
        {
            return JsonConvert.SerializeObject(loaded);
        }
        finally
        {
            loaded.Dispose();
        }
    }

    private static void AssertNoPseudoFontAnywhere(byte[] docx, string label)
    {
        var seen = 0;
        foreach (var name in PartNames(docx).Where(n => n.EndsWith(".xml", StringComparison.Ordinal)))
        {
            foreach (var attribute in Part(docx, name).Descendants().Attributes())
            {
                seen++;
                Assert.False(PseudoBidiFontNames.IsPseudo(attribute.Value),
                    $"[{label}] {name} still names a charset-variant font: {attribute.Parent!.Name.LocalName} {attribute.Name.LocalName}=\"{attribute.Value}\"");
            }
        }

        Assert.True(seen > 0, $"[{label}] read no attributes at all; the package was not parsed");
    }

    /// <summary>
    /// The complex-script language Word applies to <paramref name="node"/> (a <c>w:r</c>, or the paragraph mark's
    /// <c>w:rPr</c>): its own <c>w:lang/@w:bidi</c>, else the paragraph style chain's (the default paragraph
    /// style when none is named), else the document defaults. DocIO omits a run value equal to the one it
    /// inherits, so reading only the run would call a correctly inherited he-IL missing.
    /// </summary>
    private static string? EffectiveBidiLanguage(byte[] docx, XElement node)
    {
        static string? LangOf(XElement? rPr) => rPr?.Element(W + "lang")?.Attribute(W + "bidi")?.Value;

        var own = LangOf(node.Name == W + "r" ? node.Element(W + "rPr") : node);
        if (own != null) return own;

        var styles = Part(docx, "word/styles.xml");
        var byId = styles.Descendants(W + "style")
            .Where(s => s.Attribute(W + "styleId") != null)
            .GroupBy(s => s.Attribute(W + "styleId")!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        var paragraph = node.AncestorsAndSelf(W + "p").First();
        var styleId = paragraph.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val")?.Value
            ?? styles.Descendants(W + "style").FirstOrDefault(s =>
                s.Attribute(W + "type")?.Value == "paragraph" && s.Attribute(W + "default")?.Value is "1" or "true")
                ?.Attribute(W + "styleId")?.Value;

        for (var hops = 0; styleId != null && hops < 16 && byId.TryGetValue(styleId, out var style); hops++)
        {
            var fromStyle = LangOf(style.Element(W + "rPr"));
            if (fromStyle != null) return fromStyle;
            styleId = style.Element(W + "basedOn")?.Attribute(W + "val")?.Value;
        }

        return LangOf(styles.Root?.Element(W + "docDefaults")?.Element(W + "rPrDefault")?.Element(W + "rPr"));
    }

    private static List<XElement> HebrewRuns(XDocument part) =>
        part.Descendants(W + "r").Where(r => Text(r).Any(c => c is >= 'א' and <= 'ת')).ToList();

    private static IEnumerable<XElement> HebrewRunsOf(XElement paragraph) =>
        paragraph.Elements(W + "r").Where(r => Text(r).Any(c => c is >= 'א' and <= 'ת'));

    private static string Text(XElement run) => string.Concat(run.Elements(W + "t").Select(t => t.Value));

    private static IReadOnlyList<string> PartNames(byte[] docx)
    {
        using var zip = new ZipArchive(new MemoryStream(docx));
        return zip.Entries.Select(e => e.FullName).ToList();
    }

    private static XDocument Part(byte[] docx, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(docx));
        var entry = zip.GetEntry(name);
        Assert.True(entry != null, $"package has no {name}");
        using var stream = entry!.Open();
        return XDocument.Load(stream);
    }

    private static Chapter NewChapter(Guid bookId, int order, string sfdt) => new()
    {
        Id = Guid.NewGuid(),
        BookId = bookId,
        Order = order,
        Title = $"פרק {order + 1}",
        ContentText = FirstSentence + " " + SecondSentence,
        WordCount = 8,
        ContentSfdt = sfdt
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
