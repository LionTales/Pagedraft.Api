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

    // ─── p4-lineedit-dedupe: post-parse dedupe + no-op drop + cap ─────

    [Fact]
    public void NormalizeLineEdit_DuplicateLadenTruncatedRealFixture_CollapsesToUniqueNonNoOp()
    {
        // Shape captured from the real broken LineEdit run: Dicta fell into a repetition loop,
        // emitting ~10 IDENTICAL (original,suggested) suggestions plus a "לא,"->"לא" surrounding-
        // punctuation no-op, then the JSON TRUNCATED mid-object. overallFeedback precedes the array
        // so the salvage reconstruction (head + closed objects + "]}") preserves it.
        const string dup =
            "{\"original\":\"הוא הלך לבית\",\"suggested\":\"הוא הלך הביתה\",\"reason\":\"בהירות\",\"category\":\"clarity\"}";
        const string noop =
            "{\"original\":\"לא,\",\"suggested\":\"לא\",\"reason\":\"פיסוק\",\"category\":\"style\"}";

        var sb = new System.Text.StringBuilder();
        sb.Append("{\"overallFeedback\":\"הטקסט תקין אך חוזר על עצמו\",\"suggestions\":[");
        for (var i = 0; i < 10; i++) { sb.Append(dup); sb.Append(','); }
        sb.Append(noop);
        sb.Append(",{\"original\":\"משפט שנחתך\",\"suggested\":\"משפט מש"); // truncated mid-object
        var broken = sb.ToString();

        // Mirror the LineEdit branch of TryParseStructured: salvage the truncated JSON, then normalize.
        var salvaged = UnifiedAnalysisService.SalvageTruncatedLineEditJson(broken);
        Assert.NotNull(salvaged);
        var normalized = UnifiedAnalysisService.NormalizeLineEditResultJson(salvaged);
        Assert.NotNull(normalized);

        var parsed = JsonSerializer.Deserialize<LineEditResult>(normalized!, JsonOpts);
        Assert.NotNull(parsed);

        // 10 identical + 1 no-op + 1 truncated(dropped by salvage) -> exactly 1 unique real suggestion.
        Assert.Single(parsed!.Suggestions);
        Assert.Equal("הוא הלך לבית", parsed.Suggestions[0].Original);
        Assert.Equal("הוא הלך הביתה", parsed.Suggestions[0].Suggested);
        Assert.Equal("clarity", parsed.Suggestions[0].Category);
        // The "לא,"->"לא" surrounding-punctuation no-op is gone.
        Assert.DoesNotContain(parsed.Suggestions, s => s.Original == "לא,");
        // Count is within the pathological-run cap.
        Assert.True(parsed.Suggestions.Count <= 50);
        // OverallFeedback survives normalization untouched.
        Assert.Equal("הטקסט תקין אך חוזר על עצמו", parsed.OverallFeedback);
    }

    [Fact]
    public void NormalizeLineEdit_NoDuplicates_PreservesOrderAndFields()
    {
        var result = new LineEditResult
        {
            OverallFeedback = "משוב כללי",
            Suggestions = new List<LineEditSuggestion>
            {
                new() { Original = "אחד", Suggested = "ראשון", Reason = "בהירות", Category = "clarity" },
                new() { Original = "שתיים", Suggested = "שני", Reason = "זרימה", Category = "flow" },
                new() { Original = "שלוש", Suggested = "שלישי", Reason = "סגנון", Category = "style" }
            }
        };

        var normalized = UnifiedAnalysisService.NormalizeLineEditSuggestions(result);

        Assert.Equal(3, normalized.Suggestions.Count);
        Assert.Equal("אחד", normalized.Suggestions[0].Original);
        Assert.Equal("ראשון", normalized.Suggestions[0].Suggested);
        Assert.Equal("clarity", normalized.Suggestions[0].Category);
        Assert.Equal("בהירות", normalized.Suggestions[0].Reason);
        Assert.Equal("שתיים", normalized.Suggestions[1].Original);
        Assert.Equal("שלוש", normalized.Suggestions[2].Original);
        Assert.Equal("שלישי", normalized.Suggestions[2].Suggested);
        Assert.Equal("style", normalized.Suggestions[2].Category);
        Assert.Equal("משוב כללי", normalized.OverallFeedback);
    }

    [Fact]
    public void NormalizeLineEdit_SurroundingPunctuationNoOp_DroppedButInternalEditKept()
    {
        var result = new LineEditResult
        {
            OverallFeedback = "fb",
            Suggestions = new List<LineEditSuggestion>
            {
                // surrounding-punctuation-only diff -> dropped as noise
                new() { Original = "לא,", Suggested = "לא", Reason = "פיסוק", Category = "style" },
                // INTERNAL punctuation change -> a real edit, kept
                new() { Original = "טוב, מאוד", Suggested = "טוב מאוד", Reason = "פיסוק", Category = "style" }
            }
        };

        var normalized = UnifiedAnalysisService.NormalizeLineEditSuggestions(result);

        Assert.Single(normalized.Suggestions);
        Assert.Equal("טוב, מאוד", normalized.Suggestions[0].Original);
    }

    [Fact]
    public void NormalizeLineEdit_ExceedsCap_TruncatedToFifty()
    {
        var result = new LineEditResult { OverallFeedback = "fb", Suggestions = new List<LineEditSuggestion>() };
        for (var i = 0; i < 75; i++)
        {
            result.Suggestions.Add(new LineEditSuggestion
            {
                Original = $"orig-{i}",
                Suggested = $"sugg-{i}",
                Reason = "r",
                Category = "style"
            });
        }

        var normalized = UnifiedAnalysisService.NormalizeLineEditSuggestions(result);

        Assert.Equal(50, normalized.Suggestions.Count);
        Assert.Equal("orig-0", normalized.Suggestions[0].Original);
        Assert.Equal("orig-49", normalized.Suggestions[49].Original);
        Assert.Equal("fb", normalized.OverallFeedback);
    }

    // ─── p4-key-tolerance: near-miss known-key repair (keys only, bounded, fail-safe) ───

    // The exact real failure: LiteraryAnalysis emitted the misspelled key "narriceVoiceDescription"
    // (schema is "narrativeVoiceDescription"), so the FE read blank -> silent data loss. It is
    // Levenshtein distance 3 / length diff 2 from the schema key (the plan text says "2", but the
    // measured distance is 3 -> the repair bound is 3 so this documented fixture actually binds).
    private const string IntendedNarrativeVoiceDescription =
        "המספר מדבר בגוף ראשון ומשקף את מחשבותיה הפנימיות של הדמות הראשית.";

    [Fact]
    public void KeyTolerance_NarriceVoiceDescriptionTypo_NowBinds()
    {
        var json = $$"""
            {
                "themes": [{ "name": "אומץ", "description": "העלילה עוסקת באומץ אישי", "significance": "major" }],
                "tone": "רציני",
                "toneDescription": "טון רציני ומהורהר",
                "narrativeVoice": "גוף ראשון",
                "narriceVoiceDescription": "{{IntendedNarrativeVoiceDescription}}",
                "rhetoricalDevices": [],
                "moodProgression": "מתח הולך וגובר",
                "summary": "סיכום ספרותי קצר"
            }
            """;

        // Mirror the non-LineEdit branch of TryParseStructured (the switch calls this exact method).
        var reserialized = UnifiedAnalysisService.TryExtractAndReserialize<LiteraryAnalysisResult>(json);
        Assert.NotNull(reserialized);

        var parsed = JsonSerializer.Deserialize<LiteraryAnalysisResult>(reserialized!, JsonOpts);
        Assert.NotNull(parsed);

        // The field now BINDS (was silently dropped before the near-miss key repair).
        Assert.False(string.IsNullOrEmpty(parsed!.NarrativeVoiceDescription));
        Assert.Equal(IntendedNarrativeVoiceDescription, parsed.NarrativeVoiceDescription);
        // The correctly-spelled sibling key is unaffected.
        Assert.Equal("גוף ראשון", parsed.NarrativeVoice);
        Assert.Equal("רציני", parsed.Tone);
    }

    [Fact]
    public void KeyTolerance_RepairNearMissKeys_RenamesOnlyTheTypoKey()
    {
        // Direct helper-level assertion: the corrected key appears, the typo key is gone, nothing else moves.
        const string json =
            "{\"narrativeVoice\":\"גוף ראשון\",\"narriceVoiceDescription\":\"תיאור\",\"summary\":\"סיכום\"}";

        var repaired = UnifiedAnalysisService.RepairNearMissKeys<LiteraryAnalysisResult>(json);

        // Key name is ASCII (unaffected by JSON unicode-escaping of the Hebrew values).
        Assert.Contains("narrativeVoiceDescription", repaired);
        Assert.DoesNotContain("narriceVoiceDescription", repaired);
        // Value carried over unchanged (checked via deserialize; the intermediate JSON string escapes
        // non-ASCII the same way the pipeline's JsonOpts serialize does).
        var parsed = JsonSerializer.Deserialize<LiteraryAnalysisResult>(repaired, JsonOpts);
        Assert.Equal("תיאור", parsed!.NarrativeVoiceDescription);
    }

    [Fact]
    public void KeyTolerance_UnrelatedFarKey_LeftAlone_AbsentFieldStaysEmpty()
    {
        // narrativeVoiceDescription is ABSENT and the only extra key ("editorMetadataBlob") is far from
        // every schema key (distance > 3) -> no rename, no exception, the absent field stays empty.
        const string json = """
            {
                "tone": "רציני",
                "narrativeVoice": "גוף שלישי",
                "editorMetadataBlob": "לא רלוונטי",
                "summary": "סיכום"
            }
            """;

        var reserialized = UnifiedAnalysisService.TryExtractAndReserialize<LiteraryAnalysisResult>(json);
        Assert.NotNull(reserialized);

        var parsed = JsonSerializer.Deserialize<LiteraryAnalysisResult>(reserialized!, JsonOpts);
        Assert.NotNull(parsed);
        Assert.Equal(string.Empty, parsed!.NarrativeVoiceDescription); // not falsely filled from the far key
        Assert.Equal("גוף שלישי", parsed.NarrativeVoice);
        Assert.Equal("סיכום", parsed.Summary);
    }

    [Fact]
    public void KeyTolerance_CorrectKeyJson_NoSpuriousRename()
    {
        // All keys correct: narrativeVoice and narrativeVoiceDescription are both present and distinct;
        // the shorter must NOT be renamed into the longer (no clobber, no spurious near-match rename).
        const string json = """
            {
                "tone": "אירוני",
                "narrativeVoice": "גוף ראשון",
                "narrativeVoiceDescription": "המספר הוא הדמות הראשית",
                "summary": "סיכום"
            }
            """;

        var repaired = UnifiedAnalysisService.RepairNearMissKeys<LiteraryAnalysisResult>(json);
        // Clean JSON with no near-miss passes through byte-identical.
        Assert.Equal(json, repaired);

        var parsed = JsonSerializer.Deserialize<LiteraryAnalysisResult>(
            UnifiedAnalysisService.TryExtractAndReserialize<LiteraryAnalysisResult>(json)!, JsonOpts);
        Assert.Equal("גוף ראשון", parsed!.NarrativeVoice);
        Assert.Equal("המספר הוא הדמות הראשית", parsed.NarrativeVoiceDescription);
    }

    [Fact]
    public void KeyTolerance_BothCorrectAndNearMissPresent_CorrectWins_NoClobber()
    {
        // The correctly-spelled key is present alongside the typo -> the correct value is kept and the
        // typo key is never allowed to overwrite it.
        const string json = """
            {
                "narrativeVoice": "גוף ראשון",
                "narrativeVoiceDescription": "הערך הנכון",
                "narriceVoiceDescription": "הערך השגוי",
                "summary": "סיכום"
            }
            """;

        var reserialized = UnifiedAnalysisService.TryExtractAndReserialize<LiteraryAnalysisResult>(json);
        var parsed = JsonSerializer.Deserialize<LiteraryAnalysisResult>(reserialized!, JsonOpts);
        Assert.Equal("הערך הנכון", parsed!.NarrativeVoiceDescription);
    }

    [Fact]
    public void KeyTolerance_EnumValueTypo_NotCorrected_OnlyKeysTouched()
    {
        // Scope proof: a KEY typo is repaired, but a VALUE typo in an enum-ish field (significance
        // "majr") is left exactly as-is -- the repair never fuzzy-matches values.
        const string json = """
            {
                "themes": [{ "name": "גורל", "description": "תיאור", "significance": "majr" }],
                "narriceVoiceDescription": "תיאור הקול המספר",
                "summary": "סיכום"
            }
            """;

        var reserialized = UnifiedAnalysisService.TryExtractAndReserialize<LiteraryAnalysisResult>(json);
        var parsed = JsonSerializer.Deserialize<LiteraryAnalysisResult>(reserialized!, JsonOpts);

        // Key typo fixed...
        Assert.Equal("תיאור הקול המספר", parsed!.NarrativeVoiceDescription);
        // ...but the enum VALUE typo is untouched (NOT "corrected" to "major").
        Assert.Single(parsed.Themes);
        Assert.Equal("majr", parsed.Themes[0].Significance);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[{\"tone\":\"x\"}]")] // top-level array -> out of scope
    [InlineData("")]
    public void KeyTolerance_BadOrNonObjectInput_ReturnedUnchanged(string input)
    {
        // Fail-safe: unparseable / non-object JSON is returned unchanged and never throws.
        var result = UnifiedAnalysisService.RepairNearMissKeys<LiteraryAnalysisResult>(input);
        Assert.Equal(input, result);
    }

    [Fact]
    public void KeyTolerance_TypoNearTwoAbsentKnownKeys_NoRename_SymmetricGuard()
    {
        // Symmetric ambiguity guard (be-c03 / P3-4). Real-schema pair: CharacterRelationship has the
        // known keys "character1" and "character2" (differ by ONE char). The single present typo key
        // "charactor1" is a near-miss to BOTH absent known keys — Levenshtein 1 from "character1"
        // (e->o) and 2 from "character2" (e->o, 1->2), both within the <=3 distance / <=2 length window.
        // Before the guard it would silently bind to whichever key is first in reflection order
        // ("character1"), landing the value under the WRONG field. With the guard, NO rename fires and
        // the typo key is left untouched (both known keys stay absent).
        const string json =
            "{\"charactor1\":\"אליס\",\"relationship\":\"חברים\"}";

        var repaired = UnifiedAnalysisService.RepairNearMissKeys<CharacterRelationship>(json);

        // No rename fired: the typo key survives and neither ambiguous target key was introduced.
        Assert.Contains("charactor1", repaired);
        Assert.DoesNotContain("character1", repaired);
        Assert.DoesNotContain("character2", repaired);
        // No rename => byte-identical pass-through.
        Assert.Equal(json, repaired);

        // The present correctly-spelled key is unaffected; the ambiguous value did not bind to any field.
        var parsed = JsonSerializer.Deserialize<CharacterRelationship>(repaired, JsonOpts);
        Assert.NotNull(parsed);
        Assert.Equal("חברים", parsed!.Relationship);
        Assert.Equal(string.Empty, parsed.Character1);
        Assert.Equal(string.Empty, parsed.Character2);
    }
}
