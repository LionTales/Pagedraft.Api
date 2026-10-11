using System.Collections.Generic;
using System.Linq;
using Pagedraft.Api.Services.Export;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>he/en parity, no empty string, no em-dash, and Hebrew actually Hebrew.</summary>
public class ProofreadAnnotationStringsTests
{
    private static IEnumerable<string?> All(string language) => new[]
    {
        ProofreadAnnotationStrings.Author(language),
        ProofreadAnnotationStrings.SuggestionLine(language, "X", "Y"),
        ProofreadAnnotationStrings.SuggestionLine(language, "X", ""),
        ProofreadAnnotationStrings.SuggestionLine(language, "", "Y"),
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
    public void Suggestion_line_names_both_sides_and_the_right_one_first()
    {
        Assert.Equal("הצעה: חדש במקום ישן", ProofreadAnnotationStrings.SuggestionLine("he", "ישן", "חדש"));
        Assert.Equal("Suggested: new instead of old", ProofreadAnnotationStrings.SuggestionLine("en", "old", "new"));
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
}
