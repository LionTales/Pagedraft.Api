using System.Collections.Generic;
using System.Linq;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Services.Export;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// he/en parity, no empty string, no em-dash, and Hebrew actually Hebrew; then the comment rule (a comment
/// only when it adds information) and the explanation floor.
/// </summary>
public class ProofreadAnnotationStringsTests
{
    private static IEnumerable<string?> All(string language) => new[]
    {
        ProofreadAnnotationStrings.Author(language),
        ProofreadAnnotationStrings.CategoryLine(language, ProofreadAnnotationStrings.KtivMaleCategory),
        ProofreadAnnotationStrings.FileNameSuffix(language),
    };

    [Theory]
    [InlineData("he")]
    [InlineData("he-IL")]
    [InlineData("en")]
    public void Every_string_is_present_and_has_no_em_dash(string language)
    {
        Assert.All(All(language), s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s));
            Assert.DoesNotContain('—', s!);
        });
    }

    [Fact]
    public void Hebrew_strings_are_hebrew_and_english_strings_are_not()
    {
        static bool HasHebrew(string? s) => s!.Any(c => c >= 'א' && c <= 'ת');
        Assert.All(All("he"), s => Assert.True(HasHebrew(s), s));
        Assert.All(All("en"), s => Assert.False(HasHebrew(s), s));
    }

    [Fact]
    public void Only_ktiv_male_has_a_category_line()
    {
        Assert.Null(ProofreadAnnotationStrings.CategoryLine("he", null));
        Assert.Null(ProofreadAnnotationStrings.CategoryLine("he", "spelling"));
        Assert.Equal("Academy full spelling", ProofreadAnnotationStrings.CategoryLine("en", "ktiv-male"));
    }

    [Fact]
    public void Unknown_or_missing_language_falls_back_to_english()
    {
        Assert.Equal("PageDraft Proofreader", ProofreadAnnotationStrings.Author(null));
        Assert.Equal("PageDraft Proofreader", ProofreadAnnotationStrings.Author("fr"));
    }

    // ------------------------------------------------------------------ comment rule

    [Theory]
    [InlineData(null, null)]
    [InlineData("spelling", "")]
    [InlineData(null, "   ")]
    [InlineData(null, " .,;!? \n\t ")]
    [InlineData(null, UnifiedAnalysisService.NoExplanationFallback)]
    public void A_suggestion_with_nothing_to_add_gets_no_comment_lines(string? category, string? explanation)
    {
        Assert.Empty(ProofreadAnnotationStrings.CommentLines("he", category, explanation));
        Assert.Empty(ProofreadAnnotationStrings.CommentLines("en", category, explanation));
    }

    [Fact]
    public void Comment_lines_are_the_label_then_the_explanation_and_never_restate_the_change()
    {
        Assert.Equal(new[] { "כתיב מלא לפי האקדמיה" },
            ProofreadAnnotationStrings.CommentLines("he", "ktiv-male", null));
        Assert.Equal(new[] { "כתיב מלא לפי האקדמיה", "צורת הבינוני." },
            ProofreadAnnotationStrings.CommentLines("he", "ktiv-male", " צורת   הבינוני. "));
        Assert.Equal(new[] { "Academy full spelling", "Participle form." },
            ProofreadAnnotationStrings.CommentLines("en", "ktiv-male", "Participle form."));
        Assert.Equal(new[] { "הנושא בלשון נקבה." },
            ProofreadAnnotationStrings.CommentLines("he", null, "הנושא בלשון נקבה."));
    }

    // ------------------------------------------------------------------ explanation floor

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n\t ")]
    [InlineData("...")]
    [InlineData(" - ; ! ")]
    [InlineData("‏‏")]
    [InlineData("\u0001\u0002")]
    [InlineData(UnifiedAnalysisService.NoExplanationFallback)]
    [InlineData("  No explanation could be generated\n for this suggestion.  ")]
    public void An_explanation_holding_no_letter_or_digit_or_the_stored_placeholder_is_absent(string? explanation)
    {
        Assert.Null(ProofreadAnnotationStrings.SanitizeExplanation(explanation));
    }

    [Theory]
    [InlineData("  שורה ראשונה.\r\n\r\nשורה   שנייה.\t", "שורה ראשונה. שורה שנייה.")]
    [InlineData("a\u0000b\u0007 c", "ab c")]
    [InlineData("3", "3")]
    [InlineData("‏מילה‏", "‏מילה‏")]
    public void An_explanation_is_trimmed_with_whitespace_collapsed_and_control_characters_dropped(
        string explanation, string expected)
    {
        Assert.Equal(expected, ProofreadAnnotationStrings.SanitizeExplanation(explanation));
    }

    [Fact]
    public void A_long_explanation_is_capped_with_an_ellipsis()
    {
        var max = ProofreadAnnotationStrings.MaxExplanationLength;
        var exact = new string('א', max);
        Assert.Equal(exact, ProofreadAnnotationStrings.SanitizeExplanation(exact));

        var capped = ProofreadAnnotationStrings.SanitizeExplanation(new string('א', max + 50))!;
        Assert.Equal(new string('א', max) + "…", capped);
    }

    [Fact]
    public void The_cap_never_splits_a_surrogate_pair()
    {
        var max = ProofreadAnnotationStrings.MaxExplanationLength;
        // A pair whose high half sits at the last kept index: the whole pair goes, not half of it.
        var input = new string('a', max - 1) + "\U0001F600" + "tail";
        var capped = ProofreadAnnotationStrings.SanitizeExplanation(input)!;
        Assert.Equal(new string('a', max - 1) + "…", capped);
        Assert.DoesNotContain(capped, char.IsSurrogate);
    }
}
