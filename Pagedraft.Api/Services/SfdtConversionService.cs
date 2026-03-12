using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Newtonsoft.Json;
using Syncfusion.DocIO;
using Syncfusion.DocIO.DLS;
using Syncfusion.EJ2.DocumentEditor;

namespace Pagedraft.Api.Services;

public class SfdtConversionService
{
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

        using var docxStream = BuildMinimalDocx(bodyElements);
        docxStream.Position = 0;

        try
        {
            using (var docIoDocument = new Syncfusion.DocIO.DLS.WordDocument(docxStream, Syncfusion.DocIO.FormatType.Docx))
            {
                var plainText = docIoDocument.GetText();
                var wordCount = CountWords(plainText);
                docIoDocument.Dispose();
                docxStream.Position = 0;

                var sfdtDocument = Syncfusion.EJ2.DocumentEditor.WordDocument.Load(docxStream, Syncfusion.EJ2.DocumentEditor.FormatType.Docx);
                var sfdtJson = JsonConvert.SerializeObject(sfdtDocument);
                sfdtDocument.Dispose();

                return new SfdtConversionResult { SfdtJson = sfdtJson, PlainText = plainText.Trim(), WordCount = wordCount };
            }
        }
        catch (NullReferenceException)
        {
            throw new InvalidOperationException(
                "This document could not be parsed. It may contain unsupported elements (e.g. equations, content controls, or special formatting). Try saving a copy with simpler formatting or paste the content into a new document.");
        }
        catch (Exception ex) when (ex.GetType().FullName?.StartsWith("Syncfusion.", StringComparison.Ordinal) == true)
        {
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

    private static MemoryStream BuildMinimalDocx(List<OpenXmlElement> bodyElements)
    {
        var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(
                new Body(bodyElements.Select(e => (OpenXmlElement)e.CloneNode(true)).ToArray())
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
        catch
        {
            return ("", 0);
        }
    }

    /// <summary>
    /// Builds minimal SFDT JSON from plain text (one paragraph). Used when creating scenes from auto-split.
    /// </summary>
    public static string CreateMinimalSfdtFromText(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return "{\"sections\":[{\"blocks\":[]}]}";
        var escaped = System.Text.Json.JsonSerializer.Serialize(plainText);
        return "{\"sections\":[{\"blocks\":[{\"inlines\":[{\"text\":" + escaped + "}]}]}]}";
    }
}
