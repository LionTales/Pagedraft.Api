using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Services;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// The export-then-paste gibberish (plan whole-book-proofread-and-annotated-docx-2026-10-11, todo c1).
/// Desktop Word puts Hebrew on the clipboard as cp1255 <c>\'xx</c> escapes; when the run's font is one Word
/// cannot resolve (the pseudo name "Calibri (Hebrew)" our own export carries) it declares that font
/// <c>\fcharset0</c>, and Syncfusion's RTF reader, which decodes <c>\'xx</c> by the font charset alone, turned
/// the paste into Latin-1. The fixtures here are built on the REAL header and font table Word wrote for that
/// paste (TestData/word-clipboard-header.rtf, no prose in it) with an invented body sentence, and every
/// assertion reads the text back out of the SFDT the endpoint returns, the way the editor receives it.
/// </summary>
public class DocumentEditorClipboardTests
{
    // Invented for this test; never the author's prose.
    private const string HebrewSentence = "השמש זרחה מעל הגבעות.";

    private static readonly Encoding Cp1255 = CodePagesEncodingProvider.Instance.GetEncoding(1255)!;
    private static readonly Encoding Cp1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;

    // ---- the real Word shape, through the endpoint ------------------------------------------------------

    [Fact]
    public void RealWordHeader_fixture_reproduces_the_defect_without_the_prepass()
    {
        // Pins the premise: if this ever reads Hebrew, Syncfusion changed its RTF reader and the pre-pass
        // should be re-measured rather than trusted blindly.
        var rtf = RealHeaderDocument(LangDrivenHebrewRun(HebrewSentence));

        var text = TextOf(Syncfusion.EJ2.DocumentEditor.WordDocument.LoadString(rtf, Syncfusion.EJ2.DocumentEditor.FormatType.Rtf));

        Assert.False(HasHebrew(text), $"expected the unfixed reader to mangle the Hebrew, got [{text}]");
        Assert.True(HasLatin1Accented(text), $"expected Latin-1 mojibake, got [{text}]");
    }

    [Fact]
    public void RealWordHeader_lang1037_run_in_fcharset0_font_pastes_as_hebrew()
    {
        var rtf = RealHeaderDocument(LangDrivenHebrewRun(HebrewSentence));

        var text = PasteThroughEndpoint(rtf);

        AssertCleanHebrew(text);
        Assert.Contains(HebrewSentence, text);
    }

    [Fact]
    public void RealWordHeader_french_lang1036_decodes_as_western_not_hebrew()
    {
        // The header says \adeflang1037; the run's own \lang1036 must win over it.
        var body = @"\pard\plain \ltrpar\ql \ltrch\fcs0 \f37\fs22\lang1036 {\f37\lang1036 caf" + Escapes(Cp1252, "é") + @" noir}\par";
        var rtf = RealHeaderDocument(body);

        var normalized = RtfCodepageNormalizer.Normalize(rtf);
        var text = PasteThroughEndpoint(rtf);

        Assert.Contains(@"caf\u233?", normalized);
        Assert.Contains("café noir", text);
        Assert.False(HasHebrew(text), $"French must not decode as Hebrew, got [{text}]");
    }

    // ---- code page precedence -----------------------------------------------------------------------------

    [Fact]
    public void Charset177_font_without_lang_decodes_as_hebrew()
    {
        var rtf = SmallDocument(
            header: @"\ansi\ansicpg1252\deff0",
            fonts: @"{\f0\fnil\fcharset0 Arial;}{\f1\fnil\fcharset177 David;}",
            body: @"{\f1 " + Escapes(Cp1255, HebrewSentence) + @"}\par");

        var text = PasteThroughEndpoint(rtf);

        AssertCleanHebrew(text);
        Assert.Contains(HebrewSentence, text);
    }

    [Fact]
    public void Ansicpg1255_header_decodes_fcharset0_run_without_lang_as_hebrew()
    {
        var rtf = SmallDocument(
            header: @"\ansi\ansicpg1255\deff0",
            fonts: @"{\f0\fnil\fcharset0 Arial;}",
            body: @"{\f0 " + Escapes(Cp1255, HebrewSentence) + @"}\par");

        var normalized = RtfCodepageNormalizer.Normalize(rtf);
        var text = PasteThroughEndpoint(rtf);

        Assert.Contains(@"\u1513?", normalized); // ש
        AssertCleanHebrew(text);
        Assert.Contains(HebrewSentence, text);
    }

    [Fact]
    public void Western_lang_on_a_charset177_font_keeps_decoding_as_hebrew()
    {
        // DocIO already decodes this shape correctly (by the font charset); the pre-pass must not regress it
        // by letting a Western \lang outrank a font that declares a Hebrew charset.
        var rtf = SmallDocument(
            header: @"\ansi\ansicpg1252\deff0",
            fonts: @"{\f0\fnil\fcharset0 Arial;}{\f1\fnil\fcharset177 David;}",
            body: @"{\f1\lang1033 " + Escapes(Cp1255, HebrewSentence) + @"}\par");

        var text = PasteThroughEndpoint(rtf);

        AssertCleanHebrew(text);
        Assert.Contains(HebrewSentence, text);
    }

    [Fact]
    public void Plain_resets_the_run_language_to_the_header_default()
    {
        // \lang1037 then \plain inside the same group: the bytes after \plain fall back to \ansicpg1252,
        // because \plain drops the run's language.
        var rtf = SmallDocument(
            header: @"\ansi\ansicpg1252\deff0",
            fonts: @"{\f0\fnil\fcharset0 Arial;}",
            body: @"{\lang1037 " + Escapes(Cp1255, "א") + @"\plain " + Escapes(Cp1252, "é") + @"}\par");

        var normalized = RtfCodepageNormalizer.Normalize(rtf);

        Assert.Contains(@"\u1488?", normalized);
        Assert.Contains(@"\u233?", normalized);
    }

    [Fact]
    public void Symbol_font_escapes_are_left_untouched()
    {
        var rtf = SmallDocument(
            header: @"\ansi\ansicpg1255\deff0",
            fonts: @"{\f0\fnil\fcharset0 Arial;}{\f2\fnil\fcharset2 Wingdings;}",
            body: @"{\f2\lang1037 \'e8}\par");

        Assert.Same(rtf, RtfCodepageNormalizer.Normalize(rtf));
    }

    // ---- left byte-identical ----------------------------------------------------------------------------

    [Fact]
    public void Existing_unicode_escapes_and_their_fallbacks_are_untouched_byte_for_byte()
    {
        // Word's own Unicode shape: \uN followed by its \'xx fallback. Rewriting the fallback into another
        // \uN would make the reader skip THAT as the fallback and print the '?'.
        var rtf = SmallDocument(
            header: @"\ansi\ansicpg1252\uc1\deff0",
            fonts: @"{\f0\fnil\fcharset0 Arial;}",
            body: @"{\lang1037 \u1513\'f9\u1502\'ee\u1513\'f9}\par");

        var normalized = RtfCodepageNormalizer.Normalize(rtf, out var rewritten);
        var text = PasteThroughEndpoint(rtf);

        Assert.Equal(0, rewritten);
        Assert.Same(rtf, normalized);
        Assert.Contains("שמש", text);
        AssertCleanHebrew(text);
    }

    [Fact]
    public void A_unicode_fallback_is_kept_while_the_following_byte_escape_is_rewritten()
    {
        var rtf = SmallDocument(
            header: @"\ansi\ansicpg0\uc1\deff0",
            fonts: @"{\f0\fnil\fcharset0 Arial;}",
            body: @"{\lang1037 \u1513\'f9\'ee\'f9}\par");

        var normalized = RtfCodepageNormalizer.Normalize(rtf, out var rewritten);
        var text = PasteThroughEndpoint(rtf);

        Assert.Equal(2, rewritten);
        Assert.Contains(@"\u1513\'f9\u1502?\u1513?", normalized);
        Assert.Contains("שמש", text);
    }

    [Fact]
    public void Emitted_fallback_count_follows_the_current_uc()
    {
        var uc2 = RtfCodepageNormalizer.Normalize(@"{\rtf1\ansi\uc2{\lang1037 \'e0}}");
        var uc0 = RtfCodepageNormalizer.Normalize(@"{\rtf1\ansi\uc0{\lang1037 \'e0x}}");

        Assert.Equal(@"{\rtf1\ansi\uc2{\lang1037 \u1488??}}", uc2);
        Assert.Equal(@"{\rtf1\ansi\uc0{\lang1037 \u1488 x}}", uc0);
    }

    [Fact]
    public void Font_table_names_written_as_byte_escapes_are_untouched()
    {
        const string hebrewFontName = @"{\f2\fbidi\fnil\fcharset0 \'e3\'e5\'e3;}";
        var rtf = SmallDocument(
            header: @"\ansi\ansicpg0\deff0",
            fonts: @"{\f0\fnil\fcharset0 Arial;}" + hebrewFontName,
            body: @"{\f2\lang1037 " + Escapes(Cp1255, HebrewSentence) + @"}\par");

        var normalized = RtfCodepageNormalizer.Normalize(rtf);
        var text = PasteThroughEndpoint(rtf);

        Assert.Contains(hebrewFontName, normalized);
        Assert.DoesNotContain(@"\'", normalized.Substring(normalized.IndexOf(@"\pard", StringComparison.Ordinal)));
        AssertCleanHebrew(text);
    }

    [Fact]
    public void Picture_hex_data_field_instructions_and_bin_payloads_are_untouched()
    {
        const string pict = @"{\*\shppict{\pict\pngblip\picw10\pich10\picwgoal150\pichgoal150 89504e470d0a1a0a0000000d49484452}}";
        const string fieldInstruction = @"{\*\fldinst HYPERLINK ""x\'e4y""}";
        // A \bin payload outside any skipped destination: only the \binN length protects these 8 raw chars.
        const string bin = @"{\lang1037 \bin8 \'e4\'e4}";
        var rtf = SmallDocument(
            header: @"\ansi\ansicpg0\deff0",
            fonts: @"{\f0\fnil\fcharset0 Arial;}",
            body: pict + @"{\field" + fieldInstruction + @"{\fldrslt{\lang1037 \'e0}}}" + bin + @"{\lang1037 \'e1}\par");

        var normalized = RtfCodepageNormalizer.Normalize(rtf, out var rewritten);

        Assert.Equal(2, rewritten); // the field result and the last run, nothing else
        Assert.Contains(pict, normalized);
        Assert.Contains(fieldInstruction, normalized);
        Assert.Contains(bin, normalized);
        Assert.Contains(@"{\fldrslt{\lang1037 \u1488?}}", normalized);
        Assert.Contains(@"{\lang1037 \u1489?}", normalized);
    }

    [Fact]
    public void English_only_rtf_is_returned_byte_identical()
    {
        var rtf = SmallDocument(
            header: @"\ansi\ansicpg1252\deff0",
            fonts: @"{\f0\fswiss\fcharset0 Arial;}",
            body: @"{\f0\lang1033 The quick brown fox \{braces\} and a backslash \\ here.}\par");

        Assert.Same(rtf, RtfCodepageNormalizer.Normalize(rtf));
        Assert.Contains("The quick brown fox {braces} and a backslash \\ here.", PasteThroughEndpoint(rtf));
    }

    [Fact]
    public void Low_ascii_byte_escapes_are_left_as_written()
    {
        var rtf = @"{\rtf1\ansi{\lang1037 a\'41b}}";

        Assert.Same(rtf, RtfCodepageNormalizer.Normalize(rtf));
    }

    // ---- endpoint contract --------------------------------------------------------------------------------

    [Fact]
    public void Html_content_is_not_passed_through_the_rtf_prepass()
    {
        // An HTML clipboard can legitimately contain the characters \'e4; only RTF is rewritten.
        var controller = new DocumentEditorController(NullLogger<DocumentEditorController>.Instance);

        var result = controller.SystemClipboard(new SystemClipboardRequest { Content = @"<p>x\'e4y</p>", Type = ".html" });

        var text = TextOf(Assert.IsType<ContentResult>(result).Content!);
        Assert.Contains(@"x\'e4y", text);
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private static string LangDrivenHebrewRun(string sentence) =>
        // The exact paragraph + run control words Word wrote for a Hebrew run in the captured clipboard.
        @"\pard\plain \rtlpar\qj \li0\ri0\sl276\slmult1\widctlpar\wrapdefault\aspalpha\aspnum\faauto\adjustright\rin0\lin0\itap0 "
        + @"\ltrch\fcs1 \af37\afs22\alang4096 \rtlch\fcs0 \f37\fs22\lang1037\cgrid\langnp4096\langfenp4096 "
        + @"{\ltrch\fcs1 \afs24\alang1033 \rtlch\fcs0 \f45\fs24\lang1037\langnp1033\langfenp1033 "
        + Escapes(Cp1255, sentence)
        + @"}\par";

    private static string RealHeaderDocument(string body)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "word-clipboard-header.rtf");
        var header = File.ReadAllText(path, Encoding.ASCII);
        // Non-vacuity: a missing or swapped fixture must fail here, not read as a pass.
        Assert.StartsWith(@"{\rtf1\adeflang1037\ansi\ansicpg0", header);
        Assert.Contains(@"{\f45\fbidi \froman\fcharset0", header);
        Assert.Contains("Calibri (Hebrew)", header);
        Assert.EndsWith("}}", header);
        return header + body + "}";
    }

    private static string SmallDocument(string header, string fonts, string body) =>
        @"{\rtf1" + header + @"{\fonttbl" + fonts + @"}\pard\plain " + body + "}";

    private static string Escapes(Encoding encoding, string text)
    {
        var sb = new StringBuilder();
        foreach (var b in encoding.GetBytes(text))
        {
            if (b < 0x80) sb.Append((char)b);
            else sb.Append(@"\'").Append(b.ToString("x2"));
        }

        return sb.ToString();
    }

    private static string PasteThroughEndpoint(string rtf)
    {
        var controller = new DocumentEditorController(NullLogger<DocumentEditorController>.Instance);
        var result = controller.SystemClipboard(new SystemClipboardRequest { Content = rtf, Type = ".rtf" });
        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("application/json", content.ContentType);
        return TextOf(content.Content!);
    }

    private static string TextOf(Syncfusion.EJ2.DocumentEditor.WordDocument document)
    {
        try
        {
            return TextOf(Newtonsoft.Json.JsonConvert.SerializeObject(document));
        }
        finally
        {
            document.Dispose();
        }
    }

    /// <summary>Inflates the base64 zip the endpoint returns and joins every text run of the SFDT.</summary>
    private static string TextOf(string endpointJson)
    {
        var wrapper = JObject.Parse(endpointJson);
        using var zip = new ZipArchive(new MemoryStream(Convert.FromBase64String((string)wrapper["sfdt"]!)));
        using var reader = new StreamReader(zip.Entries[0].Open());
        var sfdt = JToken.Parse(reader.ReadToEnd());

        var runs = new List<string>();
        void Walk(JToken token)
        {
            if (token is JObject obj)
            {
                foreach (var property in obj.Properties())
                {
                    if ((property.Name == "text" || property.Name == "tlp") && property.Value.Type == JTokenType.String)
                    {
                        var value = (string)property.Value!;
                        if (!value.Contains("Syncfusion", StringComparison.Ordinal) && !value.Contains("valid key", StringComparison.Ordinal))
                            runs.Add(value);
                    }

                    Walk(property.Value);
                }
            }
            else if (token is JArray array)
            {
                foreach (var item in array) Walk(item);
            }
        }

        Walk(sfdt);
        return string.Concat(runs);
    }

    private static bool HasHebrew(string text) => text.Any(c => c is >= '\u05D0' and <= '\u05EA');

    private static bool HasLatin1Accented(string text) => text.Any(c => c is >= '\u00E0' and <= '\u00FA');

    private static void AssertCleanHebrew(string text)
    {
        Assert.True(HasHebrew(text), $"expected Hebrew letters, got [{text}]");
        Assert.False(HasLatin1Accented(text), $"expected no Latin-1 mojibake, got [{text}]");
    }
}
