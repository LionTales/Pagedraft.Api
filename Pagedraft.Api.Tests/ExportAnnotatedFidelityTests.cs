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
using Pagedraft.Api.Data;
using Pagedraft.Api.Hubs;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Export;
using Syncfusion.DocIO;
using Syncfusion.DocIO.DLS;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// The annotated (proofread) exports carry the same Hebrew export fidelity as the plain ones (plan
/// whole-book-proofread-and-annotated-docx-2026-10-11, todo c13). The plain exports were threaded the book's
/// language on the clipboard branch (c2 font names + he-IL, c11 right-to-left defaults), but the annotated
/// path converts through <c>BookExportService.Render</c>, which did not take it, so every annotated export of
/// a Hebrew book was laid out left-to-right and left the complex-script language unset. The fixture has the
/// stored shape that exposed it: runs named "Calibri (Hebrew)", paragraphs with no direction of their own.
/// Every assertion reads the produced package's XML element by element.
/// </summary>
public class ExportAnnotatedFidelityTests
{
    private const string Pseudo = "Calibri (Hebrew)";

    // Invented for this test; never the author's prose.
    private const string FirstSentence = "השמש זרחה מעל הגבעות.";
    private const string SecondSentence = "הרוח נשבה בין העצים.";

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    [Theory]
    [InlineData("book")]
    [InlineData("chapter")]
    public async Task Hebrew_annotated_export_is_rtl_by_default_names_real_faces_and_stamps_he_IL(string path)
    {
        var docx = await ExportAnnotatedAsync("he", path);

        // Non-vacuity: the annotation really ran, so this is the annotated path and not the plain one.
        var document = Part(docx, "word/document.xml");
        Assert.NotEmpty(document.Descendants(W + "del"));
        Assert.NotEmpty(document.Descendants(W + "ins"));

        var normal = DefaultParagraphStyle(docx);
        Assert.Equal("Normal", normal.Element(W + "name")?.Attribute(W + "val")?.Value);
        Assert.True(IsOn(normal.Element(W + "pPr")?.Element(W + "bidi")), $"[{path}] Normal style has no <w:bidi/>: {normal}");

        var sections = document.Descendants(W + "sectPr").ToList();
        Assert.Equal(path == "book" ? 2 : 1, sections.Count);
        Assert.All(sections, s => Assert.True(IsOn(s.Element(W + "bidi")), $"[{path}] section without <w:bidi/>: {s}"));

        AssertNoPseudoFontAnywhere(docx, path);

        // The untouched second sentence carried no complex-script language in the SFDT: the stamp fills it.
        var untagged = document.Descendants(W + "r").Where(r => Text(r).Contains(SecondSentence, StringComparison.Ordinal)).ToList();
        Assert.Equal(path == "book" ? 2 : 1, untagged.Count);
        Assert.All(untagged, r => Assert.Equal("he-IL", EffectiveBidiLanguage(docx, r)));
    }

    [Theory]
    [InlineData("book")]
    [InlineData("chapter")]
    public async Task English_annotated_export_declares_no_direction(string path)
    {
        var docx = await ExportAnnotatedAsync("en", path);

        Assert.NotEmpty(Part(docx, "word/document.xml").Descendants(W + "del"));
        Assert.Null(DefaultParagraphStyle(docx).Element(W + "pPr")?.Element(W + "bidi"));
        Assert.All(Part(docx, "word/document.xml").Descendants(W + "sectPr"), s => Assert.Null(s.Element(W + "bidi")));
        var untagged = Part(docx, "word/document.xml").Descendants(W + "r")
            .Where(r => Text(r).Contains(SecondSentence, StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(untagged);
        Assert.All(untagged, r => Assert.NotEqual("he-IL", EffectiveBidiLanguage(docx, r)));
    }

    /// <summary>
    /// be-c09: one export reads ONE language. The layout (the conversion's Hebrew rule), the revision author (the
    /// comment strings' rule) and the result filter used to decide separately: a blank stored language laid the
    /// file out left-to-right in English while the filter keyed it as Hebrew, and <c>help</c> was Hebrew to all of
    /// them by prefix. Now the export normalizes the book's language once (blank = the default, Hebrew) and every
    /// consumer classifies that value with <c>LanguageTags.IsHebrew</c>.
    /// </summary>
    [Theory]
    [InlineData("book", "", true)]
    [InlineData("chapter", "", true)]
    [InlineData("book", "  ", true)]
    [InlineData("chapter", "HE-il", true)]
    [InlineData("book", "help", false)]
    [InlineData("chapter", "help", false)]
    public async Task One_export_reads_one_language_for_layout_and_revision_author(string path, string language, bool hebrew)
    {
        var docx = await ExportAnnotatedAsync(language, path);

        var document = Part(docx, "word/document.xml");
        var authors = document.Descendants()
            .Where(e => e.Name == W + "ins" || e.Name == W + "del")
            .Select(e => e.Attribute(W + "author")?.Value)
            .Distinct()
            .ToList();
        Assert.Equal(new[] { ProofreadAnnotationStrings.Author(hebrew ? "he" : "en") }, authors);
        Assert.Equal(hebrew, IsOn(DefaultParagraphStyle(docx).Element(W + "pPr")?.Element(W + "bidi")));
    }

    // ---- fixtures and helpers -----------------------------------------------------------------------------

    /// <summary>A two-chapter book, each chapter holding one pending, placeable Active Proofread suggestion.</summary>
    private static async Task<byte[]> ExportAnnotatedAsync(string language, string path)
    {
        var (db, export) = Build();
        var book = new Book { Id = Guid.NewGuid(), Title = "גבעות", Language = language };
        db.Books.Add(book);
        var first = NewChapter(book.Id, 0);
        var second = NewChapter(book.Id, 1);
        db.Chapters.AddRange(first, second);
        SeedResult(db, first, language);
        SeedResult(db, second, language);
        await db.SaveChangesAsync();

        var result = path == "book"
            ? await export.ExportBookProofreadAsync(book.Id, ct: CancellationToken.None)
            : await export.ExportChapterProofreadAsync(book.Id, first.Id, ct: CancellationToken.None);

        Assert.Equal(BookExportOutcome.Ok, result.Outcome);
        Assert.Equal(path == "book" ? 2 : 1, result.Annotation?.Placed);
        return result.Content!;
    }

    /// <summary>Stored in the book's language, as a real run is: the annotated export reads only those (be-c05).</summary>
    private static void SeedResult(AppDbContext db, Chapter chapter, string language)
    {
        var result = new AnalysisResult
        {
            Id = Guid.NewGuid(),
            BookId = chapter.BookId,
            ChapterId = chapter.Id,
            Scope = AnalysisScope.Chapter,
            AnalysisType = AnalysisType.Proofread,
            Type = nameof(AnalysisType.Proofread),
            Status = AnalysisStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            Language = language,
        };
        result.Suggestions.Add(new AnalysisSuggestion
        {
            Id = Guid.NewGuid(),
            AnalysisResultId = result.Id,
            OriginalText = "הגבעות",
            SuggestedText = "ההרים",
            StartOffset = 0,
            EndOffset = "הגבעות".Length,
        });
        db.AnalysisResults.Add(result);
    }

    /// <summary>
    /// The stored shape behind c2 + c11, through EJ2 the way the stored data was made: runs named with Word's
    /// charset-variant font, the first with he-IL and the second with no complex-script language, and NO
    /// paragraph direction anywhere (the import drops it; the editor only applies it on load).
    /// </summary>
    private static string StoredShapeSfdt()
    {
        using var source = new WordDocument();
        source.EnsureMinimal();

        var tagged = source.LastParagraph.AppendText(FirstSentence);
        tagged.CharacterFormat.FontName = Pseudo;
        tagged.CharacterFormat.FontNameBidi = Pseudo;
        tagged.CharacterFormat.Bidi = true;
        tagged.CharacterFormat.LocaleIdBidi = ExportBidiFontNormalizer.HebrewLocaleId;

        var untagged = ((WParagraph)source.LastSection.AddParagraph()).AppendText(SecondSentence);
        untagged.CharacterFormat.FontName = Pseudo;
        untagged.CharacterFormat.FontNameBidi = Pseudo;
        untagged.CharacterFormat.Bidi = true;

        using var stream = new MemoryStream();
        source.Save(stream, FormatType.Docx);
        stream.Position = 0;
        var loaded = Syncfusion.EJ2.DocumentEditor.WordDocument.Load(stream, Syncfusion.EJ2.DocumentEditor.FormatType.Docx);
        string sfdt;
        try
        {
            sfdt = JsonConvert.SerializeObject(loaded);
        }
        finally
        {
            loaded.Dispose();
        }
        // Non-vacuity: the pseudo name reached the SFDT, so a clean export is the export's doing.
        Assert.Contains(Pseudo, Inflate(sfdt), StringComparison.Ordinal);
        return sfdt;
    }

    private static string Inflate(string sfdt)
    {
        var packed = (string)Newtonsoft.Json.Linq.JObject.Parse(sfdt)["sfdt"]!;
        using var zip = new ZipArchive(new MemoryStream(Convert.FromBase64String(packed)));
        using var reader = new StreamReader(zip.Entries[0].Open());
        return reader.ReadToEnd();
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
    /// The complex-script language Word applies to <paramref name="run"/>: its own <c>w:lang/@w:bidi</c>, else
    /// the paragraph style chain's (the default paragraph style when none is named), else the document
    /// defaults. DocIO omits a run value equal to the one it inherits, so reading only the run would call a
    /// correctly inherited he-IL missing (the same rule as <c>ExportBidiFontNormalizerTests</c>).
    /// </summary>
    private static string? EffectiveBidiLanguage(byte[] docx, XElement run)
    {
        static string? LangOf(XElement? rPr) => rPr?.Element(W + "lang")?.Attribute(W + "bidi")?.Value;

        var own = LangOf(run.Element(W + "rPr"));
        if (own != null) return own;

        var styles = Part(docx, "word/styles.xml");
        var byId = styles.Descendants(W + "style")
            .Where(s => s.Attribute(W + "styleId") != null)
            .GroupBy(s => s.Attribute(W + "styleId")!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        var paragraph = run.Ancestors(W + "p").First();
        var styleId = paragraph.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val")?.Value
            ?? DefaultParagraphStyle(docx).Attribute(W + "styleId")?.Value;

        for (var hops = 0; styleId != null && hops < 16 && byId.TryGetValue(styleId, out var style); hops++)
        {
            var fromStyle = LangOf(style.Element(W + "rPr"));
            if (fromStyle != null) return fromStyle;
            styleId = style.Element(W + "basedOn")?.Attribute(W + "val")?.Value;
        }

        return LangOf(styles.Root?.Element(W + "docDefaults")?.Element(W + "rPrDefault")?.Element(W + "rPr"));
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

    private static Chapter NewChapter(Guid bookId, int order) => new()
    {
        Id = Guid.NewGuid(),
        BookId = bookId,
        Order = order,
        Title = $"פרק {order + 1}",
        ContentText = FirstSentence + "\n" + SecondSentence,
        WordCount = 8,
        ContentSfdt = StoredShapeSfdt()
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
