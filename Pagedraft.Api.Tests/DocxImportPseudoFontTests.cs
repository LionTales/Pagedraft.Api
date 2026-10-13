using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Newtonsoft.Json.Linq;
using Pagedraft.Api.Services;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// The import side of the charset-variant font name (be-c09). A Word manuscript whose runs carry
/// <c>w:rFonts w:cs="Calibri (Hebrew)"</c> (Word writes it when a paste from RTF lands in the file, and it
/// survives a save) went through <see cref="DocxParserService"/> and <see cref="SfdtConversionService.ConvertToSfdt"/>
/// into the chapter's stored SFDT verbatim, so the import re-stored the exact name the paste pre-pass and the
/// export cleanup remove. These tests drive the import controller's own two calls and read the SFDT the
/// preview hands the client (and the confirm step stores).
/// </summary>
public class DocxImportPseudoFontTests
{
    private const string Pseudo = "Calibri (Hebrew)";

    // Invented for this test; never the author's prose.
    private const string Heading = "פרק ראשון";
    private const string BodySentence = "הציפורים שרו על גדר האבן.";

    [Fact]
    public void Import_stores_the_real_face_where_the_docx_runs_name_a_charset_variant_font()
    {
        var sfdt = ImportSingleChapter(BuildDocx(Pseudo, "Times New Roman (Hebrew)", "Arial (Arabic)"));

        var fontValues = FontValues(sfdt);
        // Non-vacuity: the run fonts reached the SFDT at all, under their real names.
        Assert.Contains("Calibri", fontValues);
        Assert.Contains("Times New Roman", fontValues);
        Assert.Contains("Arial", fontValues);
        AssertNoPseudoNameAnywhere(sfdt);
        Assert.Contains(BodySentence, AllStrings(sfdt));
    }

    [Fact]
    public void Import_keeps_real_font_names_untouched()
    {
        var sfdt = ImportSingleChapter(BuildDocx("David", "Times New Roman CE", "Calibri (Vietnamese)"));

        var fontValues = FontValues(sfdt);
        Assert.Contains("David", fontValues);
        Assert.Contains("Times New Roman CE", fontValues);
        Assert.Contains("Calibri (Vietnamese)", fontValues);
    }

    [Fact]
    public void StripFromRunFonts_renames_every_slot_and_counts_the_attributes()
    {
        var paragraph = new Paragraph(
            new ParagraphProperties(new ParagraphMarkRunProperties(
                new RunFonts { ComplexScript = Pseudo, Ascii = "Calibri" })),
            new Run(
                new RunProperties(new RunFonts
                {
                    Ascii = Pseudo, HighAnsi = Pseudo, EastAsia = "Arial (Arabic)", ComplexScript = Pseudo
                }),
                new Text(BodySentence)));

        var renamed = PseudoBidiFontNames.StripFromRunFonts(paragraph);

        Assert.Equal(5, renamed);
        var fonts = paragraph.Descendants<RunFonts>().ToList();
        Assert.Equal(2, fonts.Count);
        Assert.Equal("Calibri", fonts[0].ComplexScript?.Value);
        Assert.Equal("Calibri", fonts[1].Ascii?.Value);
        Assert.Equal("Calibri", fonts[1].HighAnsi?.Value);
        Assert.Equal("Arial", fonts[1].EastAsia?.Value);
        Assert.Equal("Calibri", fonts[1].ComplexScript?.Value);
        Assert.Equal(0, PseudoBidiFontNames.StripFromRunFonts(paragraph));
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    /// <summary>What <c>DocumentController.Import</c> does per segment: split, then ConvertToSfdt.</summary>
    private static string ImportSingleChapter(MemoryStream docx)
    {
        var segments = new DocxParserService().SplitIntoChapters(docx);
        var segment = Assert.Single(segments);
        return new SfdtConversionService().ConvertToSfdt(segment.BodyElements).SfdtJson;
    }

    /// <summary>
    /// A heading paragraph and a body paragraph whose run (and paragraph mark) name <paramref name="csFont"/>
    /// in the complex-script slot, <paramref name="asciiFont"/> in ASCII/high-ANSI and <paramref name="eastAsiaFont"/>
    /// in East Asian, the way Word writes a Hebrew run.
    /// </summary>
    private static MemoryStream BuildDocx(string csFont, string asciiFont, string eastAsiaFont)
    {
        RunFonts Fonts() => new() { Ascii = asciiFont, HighAnsi = asciiFont, EastAsia = eastAsiaFont, ComplexScript = csFont };

        var stream = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body(
                new Paragraph(
                    new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }, new BiDi()),
                    new Run(new RunProperties(Fonts(), new RightToLeftText()), new Text(Heading))),
                new Paragraph(
                    new ParagraphProperties(new BiDi(), new ParagraphMarkRunProperties(Fonts())),
                    new Run(
                        new RunProperties(Fonts(), new RightToLeftText(), new Languages { Bidi = "he-IL" }),
                        new Text(BodySentence) { Space = SpaceProcessingModeValues.Preserve }))));
            main.Document.Save();
        }

        stream.Position = 0;
        return stream;
    }

    /// <summary>The SFDT's JSON: the import serializes <c>{"sfdt": base64 zip}</c>; a bare SFDT passes through.</summary>
    private static JContainer Inflate(string sfdt)
    {
        var root = JObject.Parse(sfdt);
        if (root["sfdt"] is not JValue { Type: JTokenType.String } packed) return root;
        using var zip = new ZipArchive(new MemoryStream(Convert.FromBase64String((string)packed!)));
        using var reader = new StreamReader(zip.Entries[0].Open());
        return (JContainer)JToken.Parse(reader.ReadToEnd());
    }

    /// <summary>Every string value and property name in the inflated SFDT.</summary>
    private static List<string> AllStrings(string sfdt)
    {
        var all = new List<string>();
        foreach (var token in Inflate(sfdt).DescendantsAndSelf())
        {
            if (token is JProperty property) all.Add(property.Name);
            else if (token is JValue { Type: JTokenType.String } value) all.Add((string)value!);
        }

        return all;
    }

    /// <summary>
    /// The values of the SFDT font-name keys (optimized <c>ff</c>/<c>ffbi</c>/<c>ffa</c>/<c>fffe</c>/<c>ffnfe</c>
    /// and their verbose <c>fontFamily*</c> forms).
    /// </summary>
    private static HashSet<string> FontValues(string sfdt) =>
        Inflate(sfdt).Descendants().OfType<JProperty>()
            .Where(p => p.Name is "ff" or "ffbi" or "ffa" or "fffe" or "ffnfe" || p.Name.StartsWith("fontFamily", StringComparison.Ordinal))
            .Where(p => p.Value.Type == JTokenType.String)
            .Select(p => (string)p.Value!)
            .ToHashSet(StringComparer.Ordinal);

    private static void AssertNoPseudoNameAnywhere(string sfdt)
    {
        var strings = AllStrings(sfdt);
        Assert.True(strings.Count > 0, "the SFDT parsed to nothing");
        var pseudo = strings.Where(PseudoBidiFontNames.IsPseudo).Distinct().ToList();
        Assert.True(pseudo.Count == 0, "the imported SFDT still names a charset-variant font: " + string.Join(", ", pseudo));
    }
}
