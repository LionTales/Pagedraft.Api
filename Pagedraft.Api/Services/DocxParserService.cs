using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Pagedraft.Api.Services;

public class DocxParserService
{
    private static readonly System.Text.RegularExpressions.Regex PartRegex = new(@"חלק\s+(\d+|[א-ת]+)", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex PrologRegex = new(@"פרולוג", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex ChapterRegex = new(@"פרק\s+(\d+|[א-ת]+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Parses a DOCX stream and returns a list of chapter segments.
    /// </summary>
    public List<RawChapterSegment> SplitIntoChapters(Stream docxStream)
    {
        using var doc = WordprocessingDocument.Open(docxStream, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body == null)
            return new List<RawChapterSegment>();

        string? currentPartName = null;
        var segments = new List<RawChapterSegment>();
        var currentElements = new List<OpenXmlElement>();
        string? currentTitle = null;
        var order = 0;

        foreach (var element in body.ChildElements)
        {
            var para = element as Paragraph;
            var text = para != null ? GetParagraphText(para) : null;
            var style = para != null ? GetParagraphStyle(para) : null;

            // Hebrew markers (highest priority)
            if (text != null)
            {
                var partMatch = PartRegex.Match(text);
                if (partMatch.Success)
                {
                    currentPartName = text.Trim();
                    currentElements.Add(element.CloneNode(true));
                    continue;
                }

                if (PrologRegex.IsMatch(text))
                {
                    FlushChapter();
                    currentPartName = null;
                    currentElements = new List<OpenXmlElement> { element.CloneNode(true) };
                    currentTitle = "פרולוג";
                    order++;
                    continue;
                }

                if (ChapterRegex.IsMatch(text))
                {
                    FlushChapter();
                    // keep currentPartName for new chapter
                    currentElements = new List<OpenXmlElement> { element.CloneNode(true) };
                    currentTitle = text.Trim();
                    order++;
                    continue;
                }
            }

            // Heading 1 style
            if (style != null && (style.Contains("Heading1") || style.Contains("1כותרת") || style == "1"))
            {
                FlushChapter();
                currentElements = new List<OpenXmlElement> { element.CloneNode(true) };
                currentTitle = text?.Trim() ?? "Chapter";
                order++;
                continue;
            }

            currentElements.Add(element.CloneNode(true));
        }

        FlushChapter();

        void FlushChapter()
        {
            if (currentElements.Count == 0 && currentTitle == null) return;
            var title = currentTitle ?? "Chapter";
            if (currentElements.Count == 0)
                currentElements = new List<OpenXmlElement>();
            segments.Add(new RawChapterSegment
            {
                Title = title,
                PartName = currentPartName,
                Order = segments.Count,
                BodyElements = currentElements
            });
            currentTitle = null;
            currentElements = new List<OpenXmlElement>();
        }

        if (segments.Count == 0)
        {
            // Fallback: entire document as one chapter
            segments.Add(new RawChapterSegment
            {
                Title = "Chapter 1",
                PartName = null,
                Order = 0,
                BodyElements = body.ChildElements.Select(e => e.CloneNode(true)).ToList()
            });
        }

        return segments;
    }

    private static string? GetParagraphText(Paragraph p)
    {
        return string.Join("", p.Descendants<Text>().Select(t => t.Text));
    }

    private static string? GetParagraphStyle(Paragraph p)
    {
        var pPr = p.ParagraphProperties;
        var pStyle = pPr?.ParagraphStyleId?.Val?.Value;
        if (!string.IsNullOrEmpty(pStyle)) return pStyle;
        // Also check run properties for style reference if needed
        return null;
    }
}
