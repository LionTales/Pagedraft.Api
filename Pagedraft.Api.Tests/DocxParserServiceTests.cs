using System.Collections.Generic;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Pagedraft.Api.Services;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Exercises the real OpenXML-based chapter splitter against in-memory DOCX payloads.
/// Regression coverage for the Hebrew-marker false positives: פרק ("chapter") also means
/// "joint/segment" and חלק ("part") also means "portion", so the markers must only fire on
/// short, heading-like lines that start with the marker - not on prose that mentions the word.
/// </summary>
public class DocxParserServiceTests
{
    [Fact]
    public void SplitIntoChapters_SplitsOnHeading1_WithCleanTitles()
    {
        using var docx = BuildDocx(
            ("Heading1", "אילון"),
            (null, "גוף הפרק הראשון."),
            ("Heading1", "גיל"),
            (null, "גוף הפרק השני."));

        var result = new DocxParserService().SplitIntoChapters(docx);

        Assert.Equal(2, result.Count);
        Assert.Equal("אילון", result[0].Title);
        Assert.Equal("גיל", result[1].Title);
        Assert.All(result, s => Assert.Null(s.PartName));
    }

    [Fact]
    public void SplitIntoChapters_DoesNotSplitOn_ProseMentioningChapterWord_LongParagraph()
    {
        // "פרק ידי" = "my wrist/joint" - the word פרק appears mid-sentence in a long body
        // paragraph and must NOT be treated as a chapter break.
        using var docx = BuildDocx(
            ("Heading1", "אילון"),
            (null, "הוא שבר את פרק ידי בנפילה הקשה ולא הצליח לזוז יותר מכאן והלאה בכלל בכלל."));

        var result = new DocxParserService().SplitIntoChapters(docx);

        var chapter = Assert.Single(result);
        Assert.Equal("אילון", chapter.Title);
        // heading paragraph + the prose paragraph both belong to the single chapter.
        Assert.Equal(2, chapter.BodyElements.Count);
    }

    [Fact]
    public void SplitIntoChapters_DoesNotSplitOn_ShortLine_WhereMarkerIsNotAtStart()
    {
        // Short enough to pass the heading-like length gate, but the marker is mid-line,
        // so the start-anchor must reject it.
        using var docx = BuildDocx(
            ("Heading1", "אילון"),
            (null, "בחנתי את פרק ידי."));

        var result = new DocxParserService().SplitIntoChapters(docx);

        var chapter = Assert.Single(result);
        Assert.Equal("אילון", chapter.Title);
    }

    [Fact]
    public void SplitIntoChapters_DoesNotSetPartName_FromProseMentioningPartWord()
    {
        // "חלק גדול" = "a large portion" - the word חלק starts a long prose paragraph and
        // must NOT overwrite the part name.
        using var docx = BuildDocx(
            ("Heading1", "אילון"),
            (null, "חלק גדול מהיום הזה עבר עליי בלי שהבחנתי כלל במה שקורה סביבי באמת ובכלל."));

        var result = new DocxParserService().SplitIntoChapters(docx);

        Assert.All(result, s => Assert.Null(s.PartName));
    }

    [Fact]
    public void SplitIntoChapters_SplitsOn_RealHebrewChapterHeadingLine()
    {
        // A genuine short heading line (no Heading1 style) still creates a chapter.
        using var docx = BuildDocx(
            (null, "פרק ראשון"),
            (null, "תוכן הפרק."));

        var result = new DocxParserService().SplitIntoChapters(docx);

        var chapter = Assert.Single(result);
        Assert.Equal("פרק ראשון", chapter.Title);
    }

    [Fact]
    public void SplitIntoChapters_SetsPartName_FromRealHebrewPartHeadingLine()
    {
        using var docx = BuildDocx(
            (null, "חלק ראשון"),
            ("Heading1", "אילון"),
            (null, "תוכן."));

        var result = new DocxParserService().SplitIntoChapters(docx);

        var chapter = Assert.Single(result, s => s.Title == "אילון");
        Assert.Equal("חלק ראשון", chapter.PartName);
    }

    [Fact]
    public void SplitIntoChapters_MatchesHeading1Style_CaseAndSpaceInsensitively()
    {
        using var docx = BuildDocx(
            ("heading 1", "אילון"),
            (null, "גוף."),
            ("HEADING1", "גיל"),
            (null, "גוף."));

        var result = new DocxParserService().SplitIntoChapters(docx);

        Assert.Equal(2, result.Count);
        Assert.Equal("אילון", result[0].Title);
        Assert.Equal("גיל", result[1].Title);
    }

    [Fact]
    public void SplitIntoChapters_DoesNotSplitOn_ShortProseContainingProlog_MidLine()
    {
        // A short prose line (<=60 chars) that CONTAINS פרולוג mid-sentence must NOT flush
        // the current chapter or reset the part name. The start-anchor fix (StartsWithMatch)
        // is what prevents this; the length gate alone is insufficient.
        using var docx = BuildDocx(
            ("Heading1", "אילון"),
            (null, "הם דיברו על הפרולוג של הספר."));

        var result = new DocxParserService().SplitIntoChapters(docx);

        var chapter = Assert.Single(result);
        Assert.Equal("אילון", chapter.Title);
        Assert.Null(chapter.PartName);
    }

    [Fact]
    public void SplitIntoChapters_SplitsOn_StandaloneProlog_HeadingLine()
    {
        // A standalone "פרולוג" line (starts with the word) must still create a prolog chapter.
        using var docx = BuildDocx(
            (null, "פרולוג"),
            (null, "תוכן הפרולוג."));

        var result = new DocxParserService().SplitIntoChapters(docx);

        var chapter = Assert.Single(result);
        Assert.Equal("פרולוג", chapter.Title);
    }

    // ---- Real-manuscript-shape regression tests (be-c01) ----
    // Measured against the cleared Hebrew eval manuscript "זיכרונות של מכשף- לאחר הגהה שנייה.docx"
    // (80 chapters). Observed there: every real chapter heading carries the paragraph style ID
    // exactly "Heading1" (paragraph style; the doc ALSO defines Heading1Char..Heading9Char but those
    // are CHARACTER styles that never appear as a paragraph style ID); no marker-only (פרק/חלק/פרולוג)
    // heading exists; the only lines that START with a marker are long (137 & 85 char) prose lines
    // beginning with חלק ("portion"), which must NOT split. These tests lock that validated behavior in.

    [Fact]
    public void SplitIntoChapters_SplitsOn_RealManuscriptHeading1StyleId_ShortCharacterNameTitle()
    {
        // Mirrors the real manuscript: style ID exactly "Heading1" with a short (2-5 char) title.
        using var docx = BuildDocx(
            ("Heading1", "אילון"),
            (null, "גוף הפרק."),
            ("Heading1", "תומר"),
            (null, "גוף הפרק."));

        var result = new DocxParserService().SplitIntoChapters(docx);

        Assert.Equal(2, result.Count);
        Assert.Equal("אילון", result[0].Title);
        Assert.Equal("תומר", result[1].Title);
    }

    [Fact]
    public void SplitIntoChapters_DoesNotSplitOn_Heading1CharCharacterStyleId()
    {
        // The manuscript defines a "Heading1Char" CHARACTER style. The old substring match
        // (style.Contains("Heading1")) would have treated a paragraph carrying "Heading1Char" as a
        // chapter break; the tightened exact-match must NOT. This guards the exact-match against
        // regressing back to the substring behavior.
        using var docx = BuildDocx(
            ("Heading1", "אילון"),
            ("Heading1Char", "לא כותרת אמיתית"),
            (null, "גוף."));

        var result = new DocxParserService().SplitIntoChapters(docx);

        // Only the genuine Heading1 paragraph starts a chapter; the Heading1Char paragraph is body.
        var chapter = Assert.Single(result);
        Assert.Equal("אילון", chapter.Title);
    }

    [Fact]
    public void SplitIntoChapters_DoesNotSplitOn_LongProseStartingWithPartWord_OverLengthGate()
    {
        // Mirrors the real 137/85-char prose lines that START with חלק ("a portion of ...").
        // They exceed the 60-char heading-like gate and must NOT split or set a part name.
        var longChelekProse =
            "חלק מהתלמידים בשכבה כבר הגיעו למקום וכולם נראו מפוחדים ומבולבלים וגם חלקם התקדמו לכיוון היער האפל.";
        Assert.True(longChelekProse.Length > 60, "fixture must exceed the 60-char heading gate");

        using var docx = BuildDocx(
            ("Heading1", "אילון"),
            (null, longChelekProse));

        var result = new DocxParserService().SplitIntoChapters(docx);

        var chapter = Assert.Single(result);
        Assert.Equal("אילון", chapter.Title);
        Assert.Null(chapter.PartName);
    }

    [Fact]
    public void SplitIntoChapters_ExcludesTrailingSectionProperties_FromLastChapterBody()
    {
        // Real Word manuscripts end the body with sectPr (often carrying a footerReference rId).
        // That chrome must not land in any chapter's BodyElements — otherwise SFDT conversion
        // of the last chapter fails with the generic "could not be parsed" import error.
        using var docx = BuildDocxWithSectionProperties(
            footerRelationshipId: "rId99",
            ("Heading1", "פרק אחרון"),
            (null, "גוף הפרק האחרון עם הערת שוליים במסמך."));

        var result = new DocxParserService().SplitIntoChapters(docx);

        var chapter = Assert.Single(result);
        Assert.Equal("פרק אחרון", chapter.Title);
        Assert.DoesNotContain(chapter.BodyElements, e => e is SectionProperties);
        Assert.Contains(chapter.BodyElements, e => e is Paragraph);
    }

    [Fact]
    public void SplitIntoChapters_ProseOnlyDocument_ExcludesSectionProperties()
    {
        // No Heading1 / Hebrew markers → FlushChapter emits untitled "Chapter"; sectPr must still be dropped.
        using var docx = BuildDocxWithSectionProperties(
            footerRelationshipId: "rId11",
            (null, "פסקה יחידה בלי כותרת פרק."));

        var result = new DocxParserService().SplitIntoChapters(docx);

        var chapter = Assert.Single(result);
        Assert.Equal("Chapter", chapter.Title);
        Assert.DoesNotContain(chapter.BodyElements, e => e is SectionProperties);
        Assert.Contains(chapter.BodyElements, e => e is Paragraph);
    }

    [Fact]
    public void SplitIntoChapters_OnlySectionProperties_FallbackExcludesSectionProperties()
    {
        // Body is only Word's trailing sectPr → after skipping it the splitter hits the
        // empty-document fallback ("Chapter 1") and must not re-introduce sectPr there either.
        using var docx = BuildDocxWithSectionProperties(footerRelationshipId: "rId11");

        var result = new DocxParserService().SplitIntoChapters(docx);

        var chapter = Assert.Single(result);
        Assert.Equal("Chapter 1", chapter.Title);
        Assert.Empty(chapter.BodyElements);
    }

    /// <summary>Builds a minimal valid DOCX in memory from (paragraph-style-id, text) tuples.</summary>
    private static MemoryStream BuildDocx(params (string? StyleId, string Text)[] paragraphs)
    {
        using var build = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(build, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new Body();
            foreach (var (styleId, text) in paragraphs)
            {
                var para = new Paragraph();
                if (!string.IsNullOrEmpty(styleId))
                    para.ParagraphProperties = new ParagraphProperties(new ParagraphStyleId { Val = styleId });
                para.AppendChild(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
                body.AppendChild(para);
            }
            main.Document = new Document(body);
            main.Document.Save();
        }

        // ToArray() is valid even after the package is disposed; return a fresh readable stream.
        return new MemoryStream(build.ToArray());
    }

    /// <summary>
    /// Like <see cref="BuildDocx"/> but appends a trailing body sectPr with a footerReference,
    /// matching real Word packages that break SFDT conversion when the rId is cloned without the part.
    /// </summary>
    private static MemoryStream BuildDocxWithSectionProperties(
        string footerRelationshipId,
        params (string? StyleId, string Text)[] paragraphs)
    {
        using var build = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(build, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new Body();
            foreach (var (styleId, text) in paragraphs)
            {
                var para = new Paragraph();
                if (!string.IsNullOrEmpty(styleId))
                    para.ParagraphProperties = new ParagraphProperties(new ParagraphStyleId { Val = styleId });
                para.AppendChild(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
                body.AppendChild(para);
            }
            body.AppendChild(new SectionProperties(
                new FooterReference { Type = HeaderFooterValues.Default, Id = footerRelationshipId },
                new PageSize { Width = 11906, Height = 16838 },
                new PageMargin { Top = 1440, Right = 1800, Bottom = 907, Left = 1800 }));
            main.Document = new Document(body);
            main.Document.Save();
        }

        return new MemoryStream(build.ToArray());
    }
}
