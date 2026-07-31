using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Syncfusion.DocIO;
using Syncfusion.DocIO.DLS;
using Syncfusion.EJ2.DocumentEditor;

namespace Pagedraft.Api.Services;

public class SfdtConversionService
{
    private readonly ILogger<SfdtConversionService>? _logger;

    public SfdtConversionService()
    {
    }

    public SfdtConversionService(ILogger<SfdtConversionService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Converts a chapter's body elements to SFDT JSON and plain text.
    /// </summary>
    public SfdtConversionResult ConvertToSfdt(List<OpenXmlElement> bodyElements)
    {
        if (bodyElements == null || bodyElements.Count == 0)
        {
            var emptySfdt = "{\"sections\":[{\"blocks\":[]}]}";
            return new SfdtConversionResult { SfdtJson = emptySfdt, PlainText = "", WordCount = 0 };
        }

        try
        {
            return BuildSfdtFromBodyElements(bodyElements);
        }
        catch (NullReferenceException ex)
        {
            _logger?.LogWarning(ex,
                "ConvertToSfdt hit NullReferenceException while building SFDT from {Count} body elements.",
                bodyElements.Count);
            throw new InvalidOperationException(
                "This document could not be parsed. It may contain unsupported elements (e.g. equations, content controls, or special formatting). Try saving a copy with simpler formatting or paste the content into a new document.",
                ex);
        }
        catch (Exception ex) when (ex.GetType().FullName?.StartsWith("Syncfusion.", StringComparison.Ordinal) == true)
        {
            _logger?.LogWarning(ex,
                "ConvertToSfdt hit Syncfusion exception while building SFDT from {Count} body elements.",
                bodyElements.Count);
            throw new InvalidOperationException(
                "This document could not be parsed. It may contain unsupported elements (e.g. equations, content controls, or special formatting). Try saving a copy with simpler formatting or paste the content into a new document.",
                ex);
        }
    }

    /// <summary>
    /// Converts SFDT JSON back to DOCX bytes for export.
    /// </summary>
    public byte[] ConvertSfdtToDocx(string sfdtJson)
    {
        using var docIoDocument = Syncfusion.EJ2.DocumentEditor.WordDocument.Save(sfdtJson);
        using var outStream = new MemoryStream();
        docIoDocument.Save(outStream, Syncfusion.DocIO.FormatType.Docx);
        return outStream.ToArray();
    }

    /// <summary>
    /// Core DOCX -> SFDT conversion shared by ConvertToSfdt and CreateMinimalSfdtFromText.
    /// Builds a minimal DOCX from the body elements and round-trips it through the
    /// Syncfusion library so the resulting SFDT is one that WordDocument.Save can parse
    /// back (i.e. GetTextFromSfdt yields the original text).
    /// </summary>
    private static SfdtConversionResult BuildSfdtFromBodyElements(List<OpenXmlElement> bodyElements)
    {
        using var docxStream = BuildMinimalDocx(bodyElements);
        docxStream.Position = 0;

        using var docIoDocument = new Syncfusion.DocIO.DLS.WordDocument(docxStream, Syncfusion.DocIO.FormatType.Docx);
        var plainText = docIoDocument.GetText();
        var wordCount = CountWords(plainText);
        docIoDocument.Dispose();
        docxStream.Position = 0;

        var sfdtDocument = Syncfusion.EJ2.DocumentEditor.WordDocument.Load(docxStream, Syncfusion.EJ2.DocumentEditor.FormatType.Docx);
        var sfdtJson = JsonConvert.SerializeObject(sfdtDocument);
        sfdtDocument.Dispose();

        return new SfdtConversionResult { SfdtJson = sfdtJson, PlainText = plainText.Trim(), WordCount = wordCount };
    }

    private static MemoryStream BuildMinimalDocx(List<OpenXmlElement> bodyElements)
    {
        var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            // Drop SectionProperties: Word's trailing body sectPr often carries header/footer
            // relationship ids. Those parts are not copied into this minimal package, and
            // Syncfusion NullRefs (or throws) when it tries to resolve them. Page layout is
            // irrelevant to chapter SFDT — prose paragraphs/tables are what matter.
            var safeElements = bodyElements
                .Where(e => e is not SectionProperties)
                .Select(e => (OpenXmlElement)e.CloneNode(true))
                .ToArray();
            mainPart.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(
                new Body(safeElements)
            );
            mainPart.Document.Save();
        }
        return stream;
    }

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        return text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <summary>
    /// Extracts plain text and word count from SFDT JSON (e.g. for ContentText on save).
    /// Normalizes text by stripping Unicode bidi control characters so analysis and proofread
    /// diff stay consistent with the client (avoids punctuation / "identical" suggestion issues for RTL/Hebrew).
    /// </summary>
    public (string PlainText, int WordCount) GetTextFromSfdt(string sfdtJson)
    {
        if (string.IsNullOrWhiteSpace(sfdtJson) || sfdtJson == "{}")
            return ("", 0);
        try
        {
            using var docIoDocument = Syncfusion.EJ2.DocumentEditor.WordDocument.Save(sfdtJson);
            var text = docIoDocument.GetText().Trim();
            text = TextNormalization.NormalizeTextForStorage(text);
            return (text, CountWords(text));
        }
        catch (Exception ex)
        {
            // A silent empty result here is what surfaces as the analyzer's
            // "Scene has no content to analyze" error. Log so future malformed
            // SFDT (e.g. a write path emitting a shape WordDocument.Save can't parse)
            // is diagnosable instead of hidden. Return contract is unchanged.
            _logger?.LogWarning(
                ex,
                "GetTextFromSfdt failed to parse SFDT ({Length} chars); returning empty text (analyzer will see this scene as empty). Structural SFDT excerpt (first {N} chars, for diagnosis): {Prefix}",
                sfdtJson.Length,
                Math.Min(120, sfdtJson.Length),
                sfdtJson.Length > 120 ? sfdtJson[..120] : sfdtJson);
            return ("", 0);
        }
    }

    /// <summary>
    /// Builds a full, round-trippable SFDT from plain text. Used when creating scenes from auto-split.
    /// One paragraph per newline-separated line so multi-paragraph (and RTL/Hebrew) content survives.
    /// The SFDT is produced through the same DOCX -> Syncfusion library path as import, so it
    /// round-trips through <see cref="GetTextFromSfdt"/> (WordDocument.Save -> GetText) back to
    /// <c>TextNormalization.NormalizeTextForStorage(plainText)</c>. The previous hand-rolled minimal
    /// JSON ({"sections":[{"blocks":[{"inlines":[{"text":...}]}]}]}) parsed to EMPTY text via
    /// WordDocument.Save, which made the analyzer reject auto-split scenes.
    ///
    /// Robustness: if <paramref name="plainText"/> contains XML-invalid control characters (e.g. raw
    /// STX/ETX/BEL), OpenXml throws an <see cref="System.ArgumentException"/> at DOCX-stream flush
    /// time (inside Dispose). The method therefore sanitizes those characters upfront before building
    /// body elements, then falls back to an empty-blocks SFDT if conversion still fails for any reason,
    /// so the caller never gets an unhandled exception propagating as HTTP 500.
    /// </summary>
    public static string CreateMinimalSfdtFromText(string plainText)
    {
        const string EmptySfdt = "{\"sections\":[{\"blocks\":[]}]}";

        if (string.IsNullOrEmpty(plainText))
            return EmptySfdt;

        // Sanitize upfront: strip XML 1.0 invalid control characters before building the DOCX.
        // OpenXml's XmlWriter throws ArgumentException at flush/Dispose time for chars in
        // [0x00-0x08], [0x0B-0x0C], [0x0E-0x1F] — these can appear in plain text copied from
        // corrupted sources. Legal whitespace (\t=0x09, \n=0x0A, \r=0x0D) is preserved.
        var safeText = SanitizeXmlInvalidChars(plainText);

        // Split on \n (handle \r\n by trimming \r) and emit one paragraph per line.
        // A paragraph with no run yields an empty paragraph, preserving blank-line structure.
        var lines = safeText.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

        var bodyElements = new List<OpenXmlElement>(lines.Length);
        foreach (var line in lines)
        {
            var paragraph = string.IsNullOrEmpty(line)
                ? new Paragraph()
                : new Paragraph(new Run(new Text(line) { Space = SpaceProcessingModeValues.Preserve }));
            bodyElements.Add(paragraph);
        }

        try
        {
            return BuildSfdtFromBodyElements(bodyElements).SfdtJson;
        }
        catch (NullReferenceException) { /* fall through to empty-blocks fallback */ }
        catch (Exception ex) when (ex.GetType().FullName?.StartsWith("Syncfusion.", StringComparison.Ordinal) == true)
        { /* fall through to empty-blocks fallback */ }
        catch (Exception) { /* any other serialization / conversion error – fall through */ }

        return EmptySfdt;
    }

    /// <summary>
    /// Strips characters that are invalid in XML 1.0 but are NOT the legal whitespace set
    /// (\t = 0x09, \n = 0x0A, \r = 0x0D).  Anything in [0x00-0x08], [0x0B-0x0C], [0x0E-0x1F]
    /// will cause OpenXml or Syncfusion to throw when serializing to DOCX XML.
    /// </summary>
    private static string SanitizeXmlInvalidChars(string text)
    {
        // Fast path: no control chars at all.
        var hasInvalid = false;
        foreach (var c in text)
        {
            if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') { hasInvalid = true; break; }
        }
        if (!hasInvalid) return text;

        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') continue; // strip
            sb.Append(c);
        }
        return sb.ToString();
    }
}
