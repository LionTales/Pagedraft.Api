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

            // Hebrew markers (highest priority) — but ONLY on short, heading-like lines
            // where the marker starts the line. Without this gate, common prose words
            // (פרק = "joint/segment", חלק = "portion/part") inside body paragraphs trigger
            // bogus chapter splits and overwrite the part name with random prose.
            var trimmed = text?.Trim();
            if (IsHeadingLikeLine(trimmed))
            {
                if (StartsWithMatch(PartRegex, trimmed!))
                {
                    currentPartName = trimmed;
                    currentElements.Add(element.CloneNode(true));
                    continue;
                }

                if (StartsWithMatch(PrologRegex, trimmed!))
                {
                    FlushChapter();
                    currentPartName = null;
                    currentElements = new List<OpenXmlElement> { element.CloneNode(true) };
                    currentTitle = "פרולוג";
                    order++;
                    continue;
                }

                // Known residual edge: ChapterRegex is פרק\s+(\d+|[א-ת]+), which cannot distinguish a real
                // chapter heading ("פרק ראשון") from a body sentence that happens to START with "פרק <Hebrew word>"
                // (e.g. "פרק ידי כאב לי" = "my wrist hurt"). Such a line still triggers a split.
                // Accepted low-probability limitation — real manuscripts rarely open body paragraphs this way,
                // and the real-book validation (be-c01) confirmed the shipped corpus has no such case.
                if (StartsWithMatch(ChapterRegex, trimmed!))
                {
                    FlushChapter();
                    // keep currentPartName for new chapter
                    currentElements = new List<OpenXmlElement> { element.CloneNode(true) };
                    currentTitle = trimmed;
                    order++;
                    continue;
                }
            }

            // Heading 1 style
            if (IsHeading1Style(style))
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

    // Real Hebrew chapter/part headings are short standalone lines. Gating on length
    // keeps common prose words (פרק/חלק) inside long body paragraphs from splitting chapters.
    private const int MaxHeadingLineLength = 60;

    private static bool IsHeadingLikeLine(string? trimmed)
        => !string.IsNullOrEmpty(trimmed) && trimmed.Length <= MaxHeadingLineLength;

    private static bool StartsWithMatch(System.Text.RegularExpressions.Regex regex, string text)
    {
        var m = regex.Match(text);
        return m.Success && m.Index == 0;
    }

    private static bool IsHeading1Style(string? style)
    {
        if (string.IsNullOrEmpty(style)) return false;
        // Normalize: drop whitespace + lowercase so "Heading 1", "heading1", "HEADING1" all match.
        var normalized = style.Replace(" ", "").ToLowerInvariant();
        // Exact-level match avoids false positives on Heading10/Heading11 etc.
        return normalized is "heading1" or "1" or "כותרת1" or "1כותרת";
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
