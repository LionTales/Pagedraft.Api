using System;
using System.Collections.Generic;
using System.Linq;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

public class SuggestionDiffServiceTests
{
    private readonly SuggestionDiffService _sut = new();

    [Fact]
    public void ComputeProofreadSuggestions_NoChanges_ReturnsEmpty()
    {
        var text = "שלום עולם, זהו טקסט לבדיקה.";

        var result = _sut.ComputeProofreadSuggestions(text, text);

        Assert.Empty(result);
    }

    [Fact]
    public void ComputeProofreadSuggestions_SingleReplacement_ProducesOneSuggestionWithCorrectSpan()
    {
        const string original = "Hello world, this is a test.";
        const string result = "Hello friend, this is a test.";

        var suggestions = _sut.ComputeProofreadSuggestions(original, result);

        var suggestion = Assert.Single(suggestions);

        Assert.True(suggestion.StartOffset >= 0);
        Assert.True(suggestion.EndOffset > suggestion.StartOffset);

        var span = TextNormalization.NormalizeTextForAnalysis(original)[suggestion.StartOffset..suggestion.EndOffset];
        Assert.Equal(suggestion.OriginalText, span);

        Assert.Equal("world", suggestion.OriginalText.TrimEnd(',', ' '));
        Assert.Equal("friend", suggestion.SuggestedText.TrimEnd(',', ' '));
    }

    [Fact]
    public void ComputeProofreadSuggestions_PunctuationInclusiveSpan_IncludesPunctuationInOffsets()
    {
        const string original = "הוא אמר, ואז הפסיק.";
        const string result = "הוא אמר. ואז הפסיק.";

        var suggestions = _sut.ComputeProofreadSuggestions(original, result);

        var suggestion = Assert.Single(suggestions);

        Assert.EndsWith(",", suggestion.OriginalText);
        Assert.EndsWith(".", suggestion.SuggestedText);
    }

    [Fact]
    public void ComputeProofreadSuggestions_DistantChanges_ProduceSeparateSuggestions()
    {
        const string original = "one two three four five six seven eight nine ten";
        const string result = "one TOO three four five six SEVEN eight nine ten";

        var suggestions = _sut.ComputeProofreadSuggestions(original, result);

        Assert.True(suggestions.Count >= 2);

        Assert.Contains(suggestions, s => s.OriginalText.Contains("two", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(suggestions, s => s.OriginalText.Contains("seven", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ComputeProofreadSuggestions_DeletedText_ProducesSuggestionWithEmptySuggestedText()
    {
        const string original = "First sentence. Second sentence should be removed. Third sentence.";
        const string result = "First sentence. Third sentence.";

        var suggestions = _sut.ComputeProofreadSuggestions(original, result);

        Assert.Contains(suggestions, s => string.IsNullOrEmpty(s.SuggestedText?.Trim()));
    }

    [Fact]
    public void ComputeProofreadSuggestions_BidiAndCrLfAreIgnoredForOffsets()
    {
        var originalRaw = "שלום\u200F עולם\r\nשורה שנייה";
        var resultRaw = "שלום\u200F טוב\r\nשורה שנייה.";

        var originalNorm = TextNormalization.NormalizeTextForAnalysis(originalRaw);
        TextNormalization.NormalizeTextForAnalysis(resultRaw);

        var suggestions = _sut.ComputeProofreadSuggestions(originalRaw, resultRaw);

        Assert.NotEmpty(suggestions);

        foreach (var s in suggestions)
        {
            Assert.InRange(s.StartOffset, 0, originalNorm.Length);
            Assert.InRange(s.EndOffset, s.StartOffset, originalNorm.Length);
        }
    }

    [Fact]
    public void ComputeLineEditSuggestions_UniqueMatch_MapsToCorrectOffsets()
    {
        const string doc = "זהו משפט אחד. זהו משפט שני.";

        var structured = new LineEditResult
        {
            Suggestions = new List<LineEditSuggestion>
            {
                new() { Original = "משפט שני", Suggested = "משפט שני משופר", Reason = "clarity", Category = "clarity" }
            }
        };

        var suggestions = _sut.ComputeLineEditSuggestions(structured, doc);

        var suggestion = Assert.Single(suggestions);

        Assert.Equal("משפט שני", suggestion.OriginalText);
        Assert.Contains("משפט שני משופר", suggestion.SuggestedText);

        var normDoc = TextNormalization.NormalizeTextForAnalysis(doc);
        var slice = normDoc[suggestion.StartOffset..suggestion.EndOffset];
        Assert.Equal("משפט שני", slice);
    }

    [Fact]
    public void ComputeLineEditSuggestions_NotFound_SkipsSuggestion()
    {
        const string doc = "זהו משפט אחד בלבד.";

        var structured = new LineEditResult
        {
            Suggestions = new List<LineEditSuggestion>
            {
                new() { Original = "טקסט שלא קיים", Suggested = "טקסט חדש", Reason = "style", Category = "style" }
            }
        };

        var suggestions = _sut.ComputeLineEditSuggestions(structured, doc);

        Assert.Empty(suggestions);
    }

    [Fact]
    public void ComputeLineEditSuggestions_MultipleOccurrences_UsesSearchStartOffsetToAdvance()
    {
        const string doc = "משפט חוזר. משפט חוזר. משפט חוזר.";

        var structured = new LineEditResult
        {
            Suggestions = new List<LineEditSuggestion>
            {
                new() { Original = "משפט חוזר", Suggested = "ראשון", Reason = "style", Category = "style" },
                new() { Original = "משפט חוזר", Suggested = "שני", Reason = "style", Category = "style" },
                new() { Original = "משפט חוזר", Suggested = "שלישי", Reason = "style", Category = "style" }
            }
        };

        var suggestions = _sut.ComputeLineEditSuggestions(structured, doc);

        Assert.Equal(3, suggestions.Count);

        var starts = suggestions.Select(s => s.StartOffset).ToArray();
        Assert.True(starts[0] < starts[1] && starts[1] < starts[2]);
    }

    [Fact]
    public void ComputeLineEditSuggestions_PreservesCategory_ForConsistencyAndContinuity()
    {
        const string doc = "First sentence. Second sentence. Third sentence.";

        var structured = new LineEditResult
        {
            Suggestions = new List<LineEditSuggestion>
            {
                new()
                {
                    Original = "Second sentence.",
                    Suggested = "Second sentence, improved.",
                    Reason = "consistency",
                    Category = "consistency"
                },
                new()
                {
                    Original = "Third sentence.",
                    Suggested = "Third sentence, adjusted.",
                    Reason = "continuity",
                    Category = "continuity"
                }
            }
        };

        var suggestions = _sut.ComputeLineEditSuggestions(structured, doc);

        Assert.Equal(2, suggestions.Count);
        Assert.Contains(suggestions, s => s.Category == "consistency");
        Assert.Contains(suggestions, s => s.Category == "continuity");
    }
}

