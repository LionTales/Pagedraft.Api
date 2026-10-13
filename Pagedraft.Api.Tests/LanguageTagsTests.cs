using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Services.Chat;
using Pagedraft.Api.Services.Export;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// The API's one Hebrew-ness rule (be-c09): <see cref="LanguageTags.IsHebrew"/>, and every accessor that
/// classifies a language string through it, held to the SAME verdict over the same inputs. Before it, a dozen
/// copies of <c>StartsWith("he")</c> read <c>help</c> and <c>hen</c> as Hebrew, and the DOCX conversion and the
/// proofread comment strings of one export disagreed on a missing language.
/// </summary>
public class LanguageTagsTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static TheoryData<string?, bool> Tags() => new()
    {
        { "he", true },
        { "he-IL", true },
        { "HE", true },
        { " he-il ", true },
        { "en", false },
        { "en-US", false },
        { null, false },
        { "", false },
        { "   ", false },
        { "help", false },      // a request value that merely begins with the letters
        { "hen", false },
        { "hebrew", false },
        { "hebridean", false },
        { "he_IL", false },     // underscore is not the BCP-47 separator
        { "iw", false },        // the legacy code is not read as Hebrew by any accessor here
    };

    [Theory]
    [MemberData(nameof(Tags))]
    public void IsHebrew_matches_the_he_subtag_only(string? tag, bool hebrew)
        => Assert.Equal(hebrew, LanguageTags.IsHebrew(tag));

    [Theory]
    [MemberData(nameof(Tags))]
    public void Every_accessor_gives_the_same_verdict(string? tag, bool hebrew)
    {
        var blank = string.IsNullOrWhiteSpace(tag);

        Assert.Equal(hebrew, ProofreadAnnotationStrings.Author(tag) == ProofreadAnnotationStrings.Author("he"));
        Assert.Equal(hebrew, ChatLanguage.Normalize(tag) == ChatLanguage.Hebrew);
        Assert.Equal(hebrew, GuidesCatalog.NormalizeLanguage(tag) == "he");
        Assert.Equal(hebrew ? ExpectedScript.Hebrew : ExpectedScript.Latin,
            DynamicTermRepairService.ExpectedScriptForLanguage(tag));

        // The stored-language key: a blank value is the product default (Hebrew); every other value keys as
        // Hebrew exactly when the predicate says so, and classifying the KEY gives the raw value's verdict.
        var key = BaselineLanguageResolver.Normalize(tag);
        Assert.Equal(hebrew || blank, key == "he");
        Assert.True(key.Length <= 10, $"key '{key}' would not fit the Language columns");
        Assert.Equal(key, BaselineLanguageResolver.Normalize(key));
        if (!blank) Assert.Equal(hebrew, LanguageTags.IsHebrew(key));

        // The DOCX conversion: a Hebrew book's export makes right-to-left the Normal style's default.
        Assert.Equal(hebrew, NormalStyleIsRightToLeft(new SfdtConversionService().ConvertSfdtToDocx(MinimalSfdt, tag)));
    }

    [Theory]
    [InlineData("he", "he")]
    [InlineData("he-IL", "he")]
    [InlineData("en-GB", "en")]
    [InlineData("zh-Hant", "zh")]
    [InlineData("fr", "fr")]
    [InlineData("hebridean", "hebri")]
    [InlineData("english", "engli")]
    [InlineData(null, "he")]
    [InlineData("", "he")]
    public void Baseline_key_keeps_a_primary_subtag_and_never_turns_a_word_into_a_language(string? tag, string expected)
        => Assert.Equal(expected, BaselineLanguageResolver.Normalize(tag));

    private static readonly string MinimalSfdt = SfdtConversionService.CreateMinimalSfdtFromText("שורה אחת.");

    private static bool NormalStyleIsRightToLeft(byte[] docx)
    {
        using var zip = new ZipArchive(new MemoryStream(docx));
        using var stream = zip.GetEntry("word/styles.xml")!.Open();
        var normal = XDocument.Load(stream).Descendants(W + "style")
            .Single(s => s.Attribute(W + "styleId")?.Value == "Normal");
        var bidi = normal.Element(W + "pPr")?.Element(W + "bidi");
        return bidi != null && bidi.Attribute(W + "val")?.Value is null or "1" or "true" or "on";
    }
}
