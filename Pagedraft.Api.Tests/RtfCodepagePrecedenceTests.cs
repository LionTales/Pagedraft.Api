using System;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Services;
using Xunit;
using static Pagedraft.Api.Tests.RtfClipboardTestKit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// One test per step of <see cref="RtfCodepageNormalizer"/>'s code page precedence, each built so that ONLY that
/// step can produce the expected letters (the steps above it are absent, the steps below it would pick a different
/// code page), plus the text-bearing ignorable destinations and the endpoint's fail-safe. Every assertion reads the
/// code points back out of the SFDT the <c>SystemClipboard</c> endpoint returns. All text is invented.
/// </summary>
public class RtfCodepagePrecedenceTests
{
    private const string Fonts =
        @"{\f0\fnil\fcharset0 Arial;}{\f1\fnil\fcharset177 David;}{\f2\fnil\fcharset204 Arial Cyr;}";

    private const string Alef = "U+05D0 U+05D1 U+05D2"; // אבג
    private const string Mir = "U+043C U+0438 U+0440"; // мир
    private const string Cafe = "U+0063 U+0061 U+0066 U+00E9"; // café

    // ---- step 2: the font's charset -------------------------------------------------------------------------

    [Fact]
    public void Font_charset_outranks_a_conflicting_right_to_left_run_language()
    {
        // The review's probe: \lang1025 (Arabic, the complex-script default of Word's en-US Normal.dotm) on a
        // \fcharset177 run. DocIO alone reads Hebrew here; the old precedence (language first) gave cp1256.
        var rtf = Document(@"\ansi\ansicpg1252\deff0",
            @"{\ltrch\fcs1 \af0\alang1033 \rtlch\fcs0 \f1\lang1025 " + Escapes(Cp1255, "אבג") + "}");

        Assert.Equal(Alef, CodePoints(PasteThroughEndpoint(rtf)));
    }

    // ---- step 3: a non-Western run language -----------------------------------------------------------------

    [Fact]
    public void Right_to_left_run_language_outranks_an_ansi_font_and_the_header()
    {
        // fcharset0 font, \ansicpg1252 header: only the run's \rtlch\lang1037 says Hebrew.
        var rtf = Document(@"\ansi\ansicpg1252\deff0", @"{\rtlch\f0\lang1037 " + Escapes(Cp1255, "אבג") + "}");

        Assert.Equal(Alef, CodePoints(PasteThroughEndpoint(rtf)));
    }

    [Theory]
    [InlineData("")] // no direction word at all
    [InlineData(@"\rtlch\fcs1 \af0\alang1037 \ltrch\fcs0 ")] // Word's left-to-right run shape
    public void Hebrew_language_on_a_run_that_is_not_right_to_left_does_not_decide(string direction)
    {
        // The review's probe: \lang1037 on a Latin run turned caf\'e9 into "cafי". The tag is ignored, so the
        // header's \ansicpg1252 decides.
        var rtf = Document(@"\ansi\ansicpg1252\deff0", "{" + direction + @"\f0\lang1037 caf" + Escapes(Cp1252, "é") + "}");

        Assert.Equal(Cafe, CodePoints(PasteThroughEndpoint(rtf)));
    }

    [Fact]
    public void Left_to_right_script_language_decides_without_a_direction_word()
    {
        // Cyrillic has no direction signal to gate on, so its tag decides on any run.
        var rtf = Document(@"\ansi\ansicpg1252\deff0", @"{\f0\lang1049 " + Escapes(Cp1251, "мир") + "}");

        Assert.Equal(Mir, CodePoints(PasteThroughEndpoint(rtf)));
    }

    [Fact]
    public void RealWordHeader_hebrew_run_decodes_by_its_own_tag_without_the_header_default()
    {
        // The committed real Word header says \adeflang1037, which alone would also give Hebrew; neutralizing it
        // proves Word's actual run shape (\ltrch ... \rtlch\fcs0 \f45 ...\lang1037) passes the direction gate.
        var rtf = RealHeaderDocument(LangDrivenHebrewRun("אבג"));
        var neutralized = rtf.Replace(@"\adeflang1037", @"\adeflang1033", StringComparison.Ordinal);
        Assert.NotEqual(rtf, neutralized);

        Assert.Equal(Alef, CodePoints(PasteThroughEndpoint(neutralized)));
    }

    // ---- step 4: a Western run language ---------------------------------------------------------------------

    [Fact]
    public void Western_run_language_outranks_the_ansi_code_page()
    {
        // \ansicpg1255 would read \'e9 as yod; the run's French tag says Western.
        var rtf = Document(@"\ansi\ansicpg1255\deff0", @"{\f0\lang1036 caf" + Escapes(Cp1252, "é") + "}");

        Assert.Equal(Cafe, CodePoints(PasteThroughEndpoint(rtf)));
    }

    // ---- steps 5 to 7: the header ---------------------------------------------------------------------------

    [Fact]
    public void Ansi_code_page_outranks_the_associated_default_language()
    {
        var rtf = Document(@"\ansi\ansicpg1251\adeflang1037\deff0", @"{\f0 " + Escapes(Cp1251, "мир") + "}");

        Assert.Equal(Mir, CodePoints(PasteThroughEndpoint(rtf)));
    }

    [Fact]
    public void Associated_default_language_outranks_the_default_language()
    {
        var rtf = Document(@"\ansi\ansicpg0\adeflang1037\deflang1049\deff0", @"{\f0 " + Escapes(Cp1255, "אבג") + "}");

        Assert.Equal(Alef, CodePoints(PasteThroughEndpoint(rtf)));
    }

    [Fact]
    public void Default_language_decides_when_nothing_above_it_does()
    {
        var rtf = Document(@"\ansi\ansicpg0\deflang1049\deff0", @"{\f0 " + Escapes(Cp1251, "мир") + "}");

        Assert.Equal(Mir, CodePoints(PasteThroughEndpoint(rtf)));
    }

    [Fact]
    public void Nothing_declared_decodes_as_western()
    {
        var rtf = Document(@"\ansi\deff0", @"{\f0 caf" + Escapes(Cp1252, "é") + "}");

        Assert.Equal(Cafe, CodePoints(PasteThroughEndpoint(rtf)));
    }

    // ---- text-bearing ignorable destinations -----------------------------------------------------------------

    [Fact]
    public void Text_box_text_inside_a_shape_instruction_is_rewritten_and_its_property_values_are_not()
    {
        // DocIO shows \shptxt text, decoded by font charset like body text (raw: "àáâ"). The \sv value and the
        // \*\shpinst's other content are not run text and must stay byte-identical.
        const string propertyValue = @"{\sp{\sn wzDescription}{\sv \'e4\'e4}}";
        var shape = @"{\shp{\*\shpinst\shpleft0\shptop0\shpright2000\shpbottom1000{\sp{\sn shapeType}{\sv 202}}"
                    + propertyValue
                    + @"{\shptxt \pard\plain \rtlch\f0\lang1037 " + Escapes(Cp1255, "אבג") + @"\par}}}";
        var rtf = Document(@"\ansi\ansicpg1252\uc1\deff0", shape);

        var normalized = RtfCodepageNormalizer.Normalize(rtf, out var rewritten);

        Assert.Equal(3, rewritten);
        Assert.Contains(propertyValue, normalized);
        Assert.Equal(Alef, CodePoints(PasteThroughEndpoint(rtf)));
    }

    [Fact]
    public void Shape_text_stays_skipped_when_the_shape_sits_in_skipped_content()
    {
        // A text box inside an ignorable destination nobody reads keeps its bytes.
        var rtf = Document(@"\ansi\ansicpg1252\deff0",
            @"{\*\unknowndest{\shp{\*\shpinst{\shptxt \rtlch\f0\lang1037 \'e0}}}}{\rtlch\f0\lang1037 \'e1}");

        var normalized = RtfCodepageNormalizer.Normalize(rtf, out var rewritten);

        Assert.Equal(1, rewritten);
        Assert.Contains(@"{\shptxt \rtlch\f0\lang1037 \'e0}", normalized);
    }

    [Fact]
    public void Comment_body_is_rewritten_and_its_author_and_reference_groups_are_not()
    {
        // DocIO drops \'xx characters in a comment body outright (raw: the Hebrew vanishes, "café" reads "caf").
        const string author = @"{\*\atnauthor \'e0\'e1}";
        const string reference = @"{\*\atnref \'e2}";
        var comment = @"{\f0{\*\atnid AB}" + author + @"\chatn {\*\annotation" + reference + @"\pard "
                      + @"{\rtlch\f0\lang1037 " + Escapes(Cp1255, "אבג") + "}"
                      + @"{\ltrch\f0\lang1033  caf" + Escapes(Cp1252, "é") + "}}}";
        // \sectd: DocIO's RTF reader throws a NullReferenceException on a comment without one.
        var rtf = @"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl" + Fonts + @"}\sectd\pard {\f0 x}" + comment + @"{\f0 y}\par}";

        var normalized = RtfCodepageNormalizer.Normalize(rtf, out var rewritten);
        var text = PasteThroughEndpoint(rtf);

        Assert.Equal(4, rewritten);
        Assert.Contains(author, normalized);
        Assert.Contains(reference, normalized);
        Assert.Contains(Alef + " U+0020 " + Cafe, CodePoints(text));
    }

    // ---- the endpoint never loses a paste to the pre-pass ---------------------------------------------------

    [Fact]
    public void A_throwing_prepass_converts_the_original_content_and_logs_no_text()
    {
        var logger = new CapturingLogger<DocumentEditorController>();
        var controller = new DocumentEditorController(logger, _ => throw new InvalidOperationException("boom"));
        // Charset177 font: DocIO decodes this correctly on its own, so the original content still pastes Hebrew.
        var rtf = Document(@"\ansi\ansicpg1252\deff0", @"{\f1 " + Escapes(Cp1255, "אבג") + "}");

        var text = PasteThroughEndpoint(controller, rtf);

        Assert.Equal(Alef, CodePoints(text));
        var warning = Assert.Single(logger.At(LogLevel.Warning));
        Assert.Contains(nameof(InvalidOperationException), warning);
        Assert.Contains(rtf.Length.ToString(), warning);
        Assert.DoesNotContain(@"\'", warning);
        Assert.DoesNotContain("boom", warning);
    }

    [Fact]
    public void The_default_prepass_runs_when_no_seam_is_given()
    {
        // Guards the seam: the public constructor must still normalize (fcharset0 + \rtlch\lang1037 needs it).
        var rtf = Document(@"\ansi\ansicpg1252\deff0", @"{\rtlch\f0\lang1037 " + Escapes(Cp1255, "אבג") + "}");
        var controller = new DocumentEditorController(new CapturingLogger<DocumentEditorController>());

        Assert.Equal(Alef, CodePoints(PasteThroughEndpoint(controller, rtf)));
    }

    private static string Document(string header, string body) => SmallDocument(header, Fonts, body + @"\par");
}
