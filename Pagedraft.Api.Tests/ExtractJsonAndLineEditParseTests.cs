using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

public class ExtractJsonAndLineEditParseTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly SuggestionDiffService _sut = new();

    // ─── ExtractJson: bare JSON ─────────────────────────────────────

    [Fact]
    public void ExtractJson_BareObject_ReturnsFull()
    {
        const string input = """{"suggestions":[],"overallFeedback":"Good"}""";
        var result = UnifiedAnalysisService.ExtractJson(input);
        Assert.NotNull(result);
        Assert.StartsWith("{", result);
        Assert.EndsWith("}", result);

        var parsed = JsonSerializer.Deserialize<LineEditResult>(result!, JsonOpts);
        Assert.NotNull(parsed);
        Assert.Equal("Good", parsed!.OverallFeedback);
    }

    [Fact]
    public void ExtractJson_BareArray_ReturnsFull()
    {
        const string input = """[{"key":"value"}]""";
        var result = UnifiedAnalysisService.ExtractJson(input);
        Assert.NotNull(result);
        Assert.Equal(input, result);
    }

    // ─── ExtractJson: markdown-fenced JSON ──────────────────────────

    [Fact]
    public void ExtractJson_MarkdownFencedJson_ExtractsInner()
    {
        const string input = """
            Here is the result:
            ```json
            {"suggestions":[],"overallFeedback":"Nice"}
            ```
            """;
        var result = UnifiedAnalysisService.ExtractJson(input);
        Assert.NotNull(result);
        var parsed = JsonSerializer.Deserialize<LineEditResult>(result!, JsonOpts);
        Assert.NotNull(parsed);
        Assert.Equal("Nice", parsed!.OverallFeedback);
    }

    [Fact]
    public void ExtractJson_MarkdownFenceUpperCase_ExtractsInner()
    {
        const string input = "```JSON\n{\"suggestions\":[],\"overallFeedback\":\"OK\"}\n```";
        var result = UnifiedAnalysisService.ExtractJson(input);
        Assert.NotNull(result);
        var parsed = JsonSerializer.Deserialize<LineEditResult>(result!, JsonOpts);
        Assert.NotNull(parsed);
        Assert.Equal("OK", parsed!.OverallFeedback);
    }

    [Fact]
    public void ExtractJson_MarkdownFenceNoLanguageTag_ExtractsInner()
    {
        const string input = "```\n{\"suggestions\":[],\"overallFeedback\":\"bare\"}\n```";
        var result = UnifiedAnalysisService.ExtractJson(input);
        Assert.NotNull(result);
        var parsed = JsonSerializer.Deserialize<LineEditResult>(result!, JsonOpts);
        Assert.Equal("bare", parsed!.OverallFeedback);
    }

    // ─── ExtractJson: Hebrew preamble ───────────────────────────────

    [Fact]
    public void ExtractJson_HebrewPreamble_ExtractsJson()
    {
        const string input = "הנה התוצאה שלך:\n{\"suggestions\":[],\"overallFeedback\":\"טוב מאוד\"}";
        var result = UnifiedAnalysisService.ExtractJson(input);
        Assert.NotNull(result);
        var parsed = JsonSerializer.Deserialize<LineEditResult>(result!, JsonOpts);
        Assert.NotNull(parsed);
        Assert.Equal("טוב מאוד", parsed!.OverallFeedback);
    }

    // ─── ExtractJson: BOM and bidi controls ─────────────────────────

    [Fact]
    public void ExtractJson_LeadingBom_StripsAndExtracts()
    {
        var input = "\uFEFF{\"suggestions\":[],\"overallFeedback\":\"BOM\"}";
        var result = UnifiedAnalysisService.ExtractJson(input);
        Assert.NotNull(result);
        var parsed = JsonSerializer.Deserialize<LineEditResult>(result!, JsonOpts);
        Assert.Equal("BOM", parsed!.OverallFeedback);
    }

    [Fact]
    public void ExtractJson_BidiControlsAroundJson_StripsAndExtracts()
    {
        // RLM + RLE before JSON, PDF + RLM after
        var input = "\u200F\u202B{\"suggestions\":[],\"overallFeedback\":\"bidi\"}\u202C\u200F";
        var result = UnifiedAnalysisService.ExtractJson(input);
        Assert.NotNull(result);
        var parsed = JsonSerializer.Deserialize<LineEditResult>(result!, JsonOpts);
        Assert.Equal("bidi", parsed!.OverallFeedback);
    }

    [Fact]
    public void ExtractJson_BidiInsideJsonStrings_PreservedCorrectly()
    {
        // Bidi controls inside JSON string values should be preserved
        var input = "{\"suggestions\":[],\"overallFeedback\":\"שלום \u200Fעולם\"}";
        var result = UnifiedAnalysisService.ExtractJson(input);
        Assert.NotNull(result);
        Assert.Contains("\u200F", result);
    }

    // ─── ExtractJson: edge-case markdown ────────────────────────────

    [Fact]
    public void ExtractJson_BoldWrappedPreamble_FallsBackToSecondPass()
    {
        // Bold markdown formatting before JSON that might confuse first pass
        const string input = "**Results:**\n{\"suggestions\":[],\"overallFeedback\":\"bold\"}";
        var result = UnifiedAnalysisService.ExtractJson(input);
        Assert.NotNull(result);
        var parsed = JsonSerializer.Deserialize<LineEditResult>(result!, JsonOpts);
        Assert.Equal("bold", parsed!.OverallFeedback);
    }

    // ─── ExtractJson: null/empty/whitespace ─────────────────────────

    [Fact]
    public void ExtractJson_Null_ReturnsNull()
    {
        Assert.Null(UnifiedAnalysisService.ExtractJson(null!));
    }

    [Fact]
    public void ExtractJson_Empty_ReturnsNull()
    {
        Assert.Null(UnifiedAnalysisService.ExtractJson(""));
    }

    [Fact]
    public void ExtractJson_Whitespace_ReturnsNull()
    {
        Assert.Null(UnifiedAnalysisService.ExtractJson("   \n\t  "));
    }

    [Fact]
    public void ExtractJson_NoJsonContent_ReturnsNull()
    {
        Assert.Null(UnifiedAnalysisService.ExtractJson("Just some plain text without any JSON"));
    }

    // ─── ExtractJson: malformed / truncated JSON ────────────────────

    [Fact]
    public void ExtractJson_TruncatedJson_ReturnsNull()
    {
        const string input = "{\"suggestions\":[{\"original\":\"test\"";
        var result = UnifiedAnalysisService.ExtractJson(input);
        Assert.Null(result);
    }

    // ─── TryExtractAndReserialize via pipeline: valid LineEditResult ─

    [Fact]
    public void ExtractJson_ValidLineEditResult_DeserializesCorrectly()
    {
        const string input = """
            {
                "suggestions": [
                    {
                        "original": "משפט ישן",
                        "suggested": "משפט חדש",
                        "reason": "clarity",
                        "category": "clarity"
                    }
                ],
                "overallFeedback": "הטקסט טוב אך ניתן לשפר"
            }
            """;

        var json = UnifiedAnalysisService.ExtractJson(input);
        Assert.NotNull(json);

        var parsed = JsonSerializer.Deserialize<LineEditResult>(json!, JsonOpts);
        Assert.NotNull(parsed);
        Assert.Single(parsed!.Suggestions);
        Assert.Equal("משפט ישן", parsed.Suggestions[0].Original);
        Assert.Equal("משפט חדש", parsed.Suggestions[0].Suggested);
        Assert.Equal("clarity", parsed.Suggestions[0].Category);
        Assert.Equal("הטקסט טוב אך ניתן לשפר", parsed.OverallFeedback);
    }

    [Fact]
    public void ExtractJson_EmptySuggestions_DeserializesWithEmptyList()
    {
        const string input = """{"suggestions":[],"overallFeedback":"Excellent writing."}""";
        var json = UnifiedAnalysisService.ExtractJson(input);
        Assert.NotNull(json);

        var parsed = JsonSerializer.Deserialize<LineEditResult>(json!, JsonOpts);
        Assert.NotNull(parsed);
        Assert.Empty(parsed!.Suggestions);
        Assert.Equal("Excellent writing.", parsed.OverallFeedback);
    }

    [Fact]
    public void ExtractJson_MissingOverallFeedback_DefaultsToEmpty()
    {
        const string input = """{"suggestions":[]}""";
        var json = UnifiedAnalysisService.ExtractJson(input);
        Assert.NotNull(json);

        var parsed = JsonSerializer.Deserialize<LineEditResult>(json!, JsonOpts);
        Assert.NotNull(parsed);
        Assert.Equal(string.Empty, parsed!.OverallFeedback);
    }

    // ─── ComputeLineEditSuggestions: Hebrew with bidi controls ──────

    [Fact]
    public void ComputeLineEditSuggestions_HebrewWithBidiControls_MapsCorrectly()
    {
        // Original text with RTL mark that normalization strips
        var doc = "זהו\u200F משפט\u200F אחד. זהו\u200F משפט\u200F שני.";
        var structured = new LineEditResult
        {
            Suggestions = new List<LineEditSuggestion>
            {
                new() { Original = "משפט שני", Suggested = "משפט שני משופר", Reason = "clarity", Category = "clarity" }
            }
        };

        var suggestions = _sut.ComputeLineEditSuggestions(structured, doc);
        Assert.Single(suggestions);
        Assert.Equal("משפט שני", suggestions[0].OriginalText);
    }

    // ─── ComputeLineEditSuggestions: partial mapping (some miss) ────

    [Fact]
    public void ComputeLineEditSuggestions_PartialMapping_SkipsMissingOriginals()
    {
        const string doc = "First sentence. Second sentence. Third sentence.";

        var structured = new LineEditResult
        {
            Suggestions = new List<LineEditSuggestion>
            {
                new() { Original = "First sentence.", Suggested = "Better first.", Reason = "style", Category = "style" },
                new() { Original = "This text does not exist.", Suggested = "Replacement.", Reason = "style", Category = "style" },
                new() { Original = "Third sentence.", Suggested = "Better third.", Reason = "style", Category = "style" }
            }
        };

        var suggestions = _sut.ComputeLineEditSuggestions(structured, doc);

        Assert.Equal(2, suggestions.Count);
        Assert.Equal("First sentence.", suggestions[0].OriginalText);
        Assert.Equal("Third sentence.", suggestions[1].OriginalText);
    }

    [Fact]
    public void ComputeLineEditSuggestions_AllMissing_ReturnsEmpty()
    {
        const string doc = "Some completely different text.";

        var structured = new LineEditResult
        {
            Suggestions = new List<LineEditSuggestion>
            {
                new() { Original = "Not in document", Suggested = "Replacement", Reason = "style", Category = "style" }
            }
        };

        var suggestions = _sut.ComputeLineEditSuggestions(structured, doc);
        Assert.Empty(suggestions);
    }

    [Fact]
    public void ComputeLineEditSuggestions_NullSuggestionsList_ReturnsEmpty()
    {
        var structured = new LineEditResult { Suggestions = null! };
        var suggestions = _sut.ComputeLineEditSuggestions(structured, "Some text");
        Assert.Empty(suggestions);
    }

    [Fact]
    public void ComputeLineEditSuggestions_EmptySuggestionsList_ReturnsEmpty()
    {
        var structured = new LineEditResult { Suggestions = new List<LineEditSuggestion>() };
        var suggestions = _sut.ComputeLineEditSuggestions(structured, "Some text");
        Assert.Empty(suggestions);
    }

    [Fact]
    public void ComputeLineEditSuggestions_BlankOriginalAndSuggested_SkipsSuggestion()
    {
        const string doc = "Some text here.";
        var structured = new LineEditResult
        {
            Suggestions = new List<LineEditSuggestion>
            {
                new() { Original = "  ", Suggested = "  ", Reason = "style", Category = "style" }
            }
        };

        var suggestions = _sut.ComputeLineEditSuggestions(structured, doc);
        Assert.Empty(suggestions);
    }

    // ─── SalvageTruncatedLineEditJson ────────────────────────────────

    [Fact]
    public void Salvage_TruncatedAfterTwoSuggestions_KeepsBothComplete()
    {
        const string input = """
            {"suggestions":[
                {"original":"first","suggested":"better first","reason":"clarity","category":"clarity"},
                {"original":"second","suggested":"better second","reason":"flow","category":"flow"},
                {"original":"third","suggested":"better thi
            """;

        var result = UnifiedAnalysisService.SalvageTruncatedLineEditJson(input);
        Assert.NotNull(result);

        var parsed = JsonSerializer.Deserialize<LineEditResult>(result!, JsonOpts);
        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Suggestions.Count);
        Assert.Equal("first", parsed.Suggestions[0].Original);
        Assert.Equal("better first", parsed.Suggestions[0].Suggested);
        Assert.Equal("second", parsed.Suggestions[1].Original);
        Assert.Equal("better second", parsed.Suggestions[1].Suggested);
    }

    [Fact]
    public void Salvage_TruncatedMidFirstSuggestion_ReturnsNull()
    {
        const string input = """{"suggestions":[{"original":"first","suggested":"bet""";

        var result = UnifiedAnalysisService.SalvageTruncatedLineEditJson(input);
        Assert.Null(result);
    }

    [Fact]
    public void Salvage_ValidCompleteJson_StillWorks()
    {
        const string input = """
            {"suggestions":[{"original":"old","suggested":"new","reason":"style","category":"style"}],"overallFeedback":"Good"}
            """;

        var result = UnifiedAnalysisService.SalvageTruncatedLineEditJson(input);
        Assert.NotNull(result);

        var parsed = JsonSerializer.Deserialize<LineEditResult>(result!, JsonOpts);
        Assert.NotNull(parsed);
        Assert.Single(parsed!.Suggestions);
        Assert.Equal("old", parsed.Suggestions[0].Original);
    }

    [Fact]
    public void Salvage_TruncatedOverallFeedback_KeepsSuggestions()
    {
        const string input = """{"suggestions":[{"original":"a","suggested":"b","reason":"r","category":"c"}],"overallFeedback":"This text is trun""";

        var result = UnifiedAnalysisService.SalvageTruncatedLineEditJson(input);
        Assert.NotNull(result);

        var parsed = JsonSerializer.Deserialize<LineEditResult>(result!, JsonOpts);
        Assert.NotNull(parsed);
        Assert.Single(parsed!.Suggestions);
        Assert.Equal("a", parsed.Suggestions[0].Original);
    }

    [Fact]
    public void Salvage_NoSuggestionsKey_ReturnsNull()
    {
        const string input = """{"data":[{"foo":"bar"}""";
        var result = UnifiedAnalysisService.SalvageTruncatedLineEditJson(input);
        Assert.Null(result);
    }

    [Fact]
    public void Salvage_EmptyContent_ReturnsNull()
    {
        Assert.Null(UnifiedAnalysisService.SalvageTruncatedLineEditJson(""));
        Assert.Null(UnifiedAnalysisService.SalvageTruncatedLineEditJson(null!));
        Assert.Null(UnifiedAnalysisService.SalvageTruncatedLineEditJson("   "));
    }

    [Fact]
    public void Salvage_HebrewContent_TruncatedThirdSuggestion()
    {
        const string input = """
            {"suggestions":[
                {"original":"משפט ישן","suggested":"משפט חדש","reason":"בהירות","category":"clarity"},
                {"original":"ביטוי מיושן","suggested":"ביטוי עדכני","reason":"סגנון","category":"style"},
                {"original":"טקסט ארוך שנחתך","suggested":"טקסט מש
            """;

        var result = UnifiedAnalysisService.SalvageTruncatedLineEditJson(input);
        Assert.NotNull(result);

        var parsed = JsonSerializer.Deserialize<LineEditResult>(result!, JsonOpts);
        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Suggestions.Count);
        Assert.Equal("משפט ישן", parsed.Suggestions[0].Original);
        Assert.Equal("ביטוי מיושן", parsed.Suggestions[1].Original);
    }

    [Fact]
    public void Salvage_WrappedInMarkdownFence_StillSalvages()
    {
        const string input = """
            ```json
            {"suggestions":[
                {"original":"test","suggested":"better","reason":"r","category":"c"},
                {"original":"incomplete","suggested":"inc
            ```
            """;

        var result = UnifiedAnalysisService.SalvageTruncatedLineEditJson(input);
        Assert.NotNull(result);

        var parsed = JsonSerializer.Deserialize<LineEditResult>(result!, JsonOpts);
        Assert.NotNull(parsed);
        Assert.Single(parsed!.Suggestions);
        Assert.Equal("test", parsed.Suggestions[0].Original);
    }

    [Fact]
    public void Salvage_EscapedQuotesInValues_HandlesCorrectly()
    {
        var input = """{"suggestions":[{"original":"she said \"hello\"","suggested":"she said \"hi\"","reason":"conciseness","category":"style"},{"original":"trunc""";

        var result = UnifiedAnalysisService.SalvageTruncatedLineEditJson(input);
        Assert.NotNull(result);

        var parsed = JsonSerializer.Deserialize<LineEditResult>(result!, JsonOpts);
        Assert.NotNull(parsed);
        Assert.Single(parsed!.Suggestions);
        Assert.Contains("hello", parsed.Suggestions[0].Original);
    }

    [Fact]
    public void Salvage_EmptySuggestionsArray_ReturnsNull()
    {
        const string input = """{"suggestions":[],"overallFeedback":"trunc""";
        var result = UnifiedAnalysisService.SalvageTruncatedLineEditJson(input);
        Assert.Null(result);
    }

    // ─── LineEdit XML-like fallback ───────────────────────────────────

    [Fact]
    public void TryLineEditXmlFallback_XmlEditWrapper_ProducesOverallFeedback()
    {
        const string xml =
            "<edit><instruction>הטקסט טוב, אבל אפשר לחזק מעט את הפתיחה.</instruction></edit>";

        var json = UnifiedAnalysisService.TryLineEditXmlFallback(xml);
        Assert.NotNull(json);

        var parsed = JsonSerializer.Deserialize<LineEditResult>(json!, JsonOpts);
        Assert.NotNull(parsed);
        Assert.Empty(parsed!.Suggestions);
        Assert.Equal("הטקסט טוב, אבל אפשר לחזק מעט את הפתיחה.", parsed.OverallFeedback);
    }

    [Fact]
    public void TryLineEditXmlFallback_PlainHebrewNarrative_ReturnsNull()
    {
        const string narrative =
            "הוא התעורר באמצע הלילה ולא הצליח לזכור אם נעל את הדלת. " +
            "החדר היה חשוך והשקט הפך כל רחש קטן לרעידת אדמה.";

        var json = UnifiedAnalysisService.TryLineEditXmlFallback(narrative);
        Assert.Null(json);
    }
}
