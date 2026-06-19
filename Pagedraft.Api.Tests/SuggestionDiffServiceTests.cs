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
    public void ComputeProofreadSuggestions_StraySpaceBeforePeriod_ProducesWhitespaceCorrection()
    {
        // Regression: removing a stray space before sentence-final punctuation is a real correction.
        // The trim-equality "meaningful" filter used to drop it because the only change was trailing
        // whitespace on the word span.
        const string original = "דרך המראה ראיתי שהיא מסמיקה .";
        const string result = "דרך המראה ראיתי שהיא מסמיקה.";

        var suggestions = _sut.ComputeProofreadSuggestions(original, result);

        var s = Assert.Single(suggestions);
        Assert.EndsWith(" ", s.OriginalText);
        Assert.Equal(s.OriginalText.TrimEnd(), s.SuggestedText);
    }

    [Fact]
    public void ComputeProofreadSuggestions_WordJoin_KeptAsOneSuggestion()
    {
        // Regression: a space deleted between two words ("ל הראות" → "להראות") joins them into one.
        // The oversized-range splitter used to break the merged range into two single words, each of
        // which mapped to an unchanged word, losing the join entirely.
        const string original = "שירה התחילה ל הראות סימני לחץ.";
        const string result = "שירה התחילה להראות סימני לחץ.";

        var suggestions = _sut.ComputeProofreadSuggestions(original, result);

        var s = Assert.Single(suggestions);
        Assert.Equal("ל הראות", s.OriginalText);
        Assert.Equal("להראות", s.SuggestedText);
    }

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

        var span = TextNormalization.NormalizeTextForAnalysis(original)[suggestion.StartOffset!.Value..suggestion.EndOffset!.Value];
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
            Assert.InRange(s.StartOffset!.Value, 0, originalNorm.Length);
            Assert.InRange(s.EndOffset!.Value, s.StartOffset.Value, originalNorm.Length);
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
        var slice = normDoc[suggestion.StartOffset!.Value..suggestion.EndOffset!.Value];
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

    [Fact]
    public void ComputeProofreadSuggestions_SuggestionAtStartOfText_ContextBeforeIsEmpty()
    {
        const string original = "First word here and more text.";
        const string result = "Second word here and more text.";

        var suggestions = _sut.ComputeProofreadSuggestions(original, result);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal("", suggestion.ContextBefore);
        Assert.NotNull(suggestion.ContextAfter);
        Assert.True(suggestion.ContextAfter!.Length > 0);
    }

    [Fact]
    public void ComputeProofreadSuggestions_SuggestionAtEndOfText_ContextAfterIsEmpty()
    {
        // No character after the changed word so ContextAfter is empty
        const string original = "Some text here and final";
        const string result = "Some text here and last";

        var suggestions = _sut.ComputeProofreadSuggestions(original, result);

        var suggestion = Assert.Single(suggestions);
        Assert.NotNull(suggestion.ContextBefore);
        Assert.True(suggestion.ContextBefore!.Length > 0);
        Assert.Equal("", suggestion.ContextAfter);
    }

    [Fact]
    public void ComputeProofreadSuggestions_MidDocumentSuggestion_BothContextsPopulatedMax50Chars()
    {
        // Long enough prefix/suffix so 50 chars exist on both sides of the changed word
        var prefix = new string('x', 60);
        var suffix = new string('y', 60);
        var original = prefix + " wrong " + suffix;
        var result = prefix + " right " + suffix;

        var suggestions = _sut.ComputeProofreadSuggestions(original, result);

        var suggestion = Assert.Single(suggestions);
        Assert.NotNull(suggestion.ContextBefore);
        Assert.NotNull(suggestion.ContextAfter);
        Assert.True(suggestion.ContextBefore!.Length > 0);
        Assert.True(suggestion.ContextAfter!.Length > 0);
        Assert.True(suggestion.ContextBefore.Length <= 50, "ContextBefore should be at most 50 characters");
        Assert.True(suggestion.ContextAfter.Length <= 50, "ContextAfter should be at most 50 characters");
    }

    [Fact]
    public void ComputeProofreadSuggestions_HallucinatedRepetition_RejectsDisproportionateSuggestions()
    {
        const string original = "אחד שניים שלושה ארבע חמש שש שבע שמונה תשע עשר";
        var repeated = string.Concat(Enumerable.Repeat("שלושה ארבע חמש שש שבע שמונה תשע עשר ", 20));
        var result = "אחד שניים " + repeated;

        var suggestions = _sut.ComputeProofreadSuggestions(original, result);

        foreach (var s in suggestions)
        {
            Assert.True(
                s.SuggestedText.Length <= s.OriginalText.Length * 5 + 30,
                $"Suggestion for '{s.OriginalText}' has disproportionate suggestedText length {s.SuggestedText.Length}");
        }
    }

    [Fact]
    public void ComputeLineEditSuggestions_MidDocument_CapturesContextBeforeAndAfter()
    {
        var prefix = new string('a', 55);
        var suffix = new string('b', 55);
        const string middle = "replace me";
        var doc = prefix + " " + middle + " " + suffix;

        var structured = new LineEditResult
        {
            Suggestions = new List<LineEditSuggestion>
            {
                new() { Original = middle, Suggested = "replaced", Reason = "style", Category = "style" }
            }
        };

        var suggestions = _sut.ComputeLineEditSuggestions(structured, doc);

        var suggestion = Assert.Single(suggestions);
        Assert.NotNull(suggestion.ContextBefore);
        Assert.NotNull(suggestion.ContextAfter);
        Assert.True(suggestion.ContextBefore!.Length > 0);
        Assert.True(suggestion.ContextAfter!.Length > 0);
        Assert.True(suggestion.ContextBefore.Length <= 50, "ContextBefore should be at most 50 characters");
        Assert.True(suggestion.ContextAfter.Length <= 50, "ContextAfter should be at most 50 characters");
    }

    // -----------------------------------------------------------------------
    // ComputeConsistencyIssueSuggestions tests
    // -----------------------------------------------------------------------

    [Fact]
    public void ComputeConsistencyIssueSuggestions_VerbatimSpan_CorrectOffsetsInNormalizedSpace()
    {
        // The span is verbatim text that exists in the document.
        // Expected offsets must match where the span sits in the normalized document.
        const string inputText = "He walked slowly down the street. He ran fast.";
        const string spanRaw = "walked slowly";

        var normDoc = TextNormalization.NormalizeTextForAnalysis(inputText);
        var normSpan = TextNormalization.NormalizeTextForAnalysis(spanRaw);
        var expectedStart = normDoc.IndexOf(normSpan, StringComparison.Ordinal);
        var expectedEnd = expectedStart + normSpan.Length;

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "tense", Span = spanRaw, Description = "tense shift" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal(expectedStart, suggestion.StartOffset);
        Assert.Equal(expectedEnd, suggestion.EndOffset);
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_BidiSpan_MatchesAfterNormalization()
    {
        // Span contains an LRM bidi mark (‎) that the document does not have.
        // After normalization both are stripped, so the span is still found.
        const string inputText = "הוא הלך לאט במסדרון. הוא רץ מהר.";
        // Span has a trailing LRM bidi mark that the document text does not contain.
        var spanRaw = "הלך לאט‎";

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "register", Span = spanRaw, Description = "register shift" }
        };

        var normDoc = TextNormalization.NormalizeTextForAnalysis(inputText);
        var normSpan = TextNormalization.NormalizeTextForAnalysis(spanRaw);
        var expectedStart = normDoc.IndexOf(normSpan, StringComparison.Ordinal);

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        var suggestion = Assert.Single(suggestions);
        Assert.NotNull(suggestion.StartOffset);
        Assert.Equal(expectedStart, suggestion.StartOffset);
        Assert.Equal(expectedStart + normSpan.Length, suggestion.EndOffset);
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_RepeatedPhrase_DistinctNonOverlappingOffsets()
    {
        // The same phrase appears twice; the second issue must map to the second occurrence.
        const string inputText = "She smiled. Then she smiled again.";
        const string spanRaw = "smiled";

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "pov", Span = spanRaw, Description = "first occurrence" },
            new() { Type = "pov", Span = spanRaw, Description = "second occurrence" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        Assert.Equal(2, suggestions.Count);
        var first = suggestions[0];
        var second = suggestions[1];
        // Both must be matched (non-null offsets).
        Assert.NotNull(first.StartOffset);
        Assert.NotNull(second.StartOffset);
        // Second occurrence must start after first occurrence ends.
        Assert.True(second.StartOffset > first.StartOffset, "Second match must be past first match");
        Assert.True(second.StartOffset >= first.EndOffset, "Offsets must not overlap");
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_IssuesOutOfDocumentOrder_AllMatchedNoneDropped()
    {
        // Issues arrive in SIGNIFICANCE order, not document order: issues[0]'s span sits LATER in the
        // document than issues[1]'s span. The old monotonic-searchStart code advanced the cursor past
        // issues[0]'s (later) match, so IndexOf for issues[1]'s (earlier) span returned -1 and the
        // issue was silently dropped. The fix locates each issue independently from offset 0, so both
        // are matched and issues[1] maps to its REAL earlier offset.
        const string inputText = "The tense slip is here near the start. Much later the POV shift appears.";
        const string laterSpan = "POV shift appears";   // sits later in the document
        const string earlierSpan = "tense slip is here"; // sits earlier in the document

        var normDoc = TextNormalization.NormalizeTextForAnalysis(inputText);
        // Sanity-check the fixture: the first-emitted span really is later than the second-emitted span.
        var laterIdx = normDoc.IndexOf(laterSpan, StringComparison.Ordinal);
        var earlierIdx = normDoc.IndexOf(earlierSpan, StringComparison.Ordinal);
        Assert.True(laterIdx > earlierIdx, "fixture: issues[0] span must sit later than issues[1] span");

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "pov",   Span = laterSpan,   Description = "emitted first, located later" },
            new() { Type = "tense", Span = earlierSpan, Description = "emitted second, located earlier" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        // Both must survive (this is what fails against the old monotonic-cursor code).
        Assert.Equal(2, suggestions.Count);

        var firstEmitted = suggestions[0];  // the later span
        var secondEmitted = suggestions[1]; // the earlier span

        Assert.NotNull(firstEmitted.StartOffset);
        Assert.NotNull(firstEmitted.EndOffset);
        Assert.NotNull(secondEmitted.StartOffset);
        Assert.NotNull(secondEmitted.EndOffset);

        // Each suggestion must map to its real span position in normalized space.
        Assert.Equal(laterIdx, firstEmitted.StartOffset);
        Assert.Equal(laterIdx + laterSpan.Length, firstEmitted.EndOffset);
        Assert.Equal(earlierIdx, secondEmitted.StartOffset);
        Assert.Equal(earlierIdx + earlierSpan.Length, secondEmitted.EndOffset);

        // The second-emitted issue's REAL position is earlier than the first-emitted issue's.
        Assert.True(
            secondEmitted.StartOffset < firstEmitted.StartOffset,
            "issues[1] must map to its real earlier offset, before issues[0]'s offset");
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_DistinctIssuesShareSingleOccurrenceSpan_BothAnchoredNotDropped()
    {
        // Two DIFFERENT issue types quote the SAME passage, which appears exactly once in the text.
        // The old overlap-dedup dropped the second issue (no other occurrence to claim); but distinct
        // issues legitimately share an anchor, so BOTH must survive and anchor to that one occurrence.
        const string inputText = "She walks to the door and opened it slowly, then she leaves the room.";
        const string sharedSpan = "She walks to the door and opened it slowly";

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "tense", Span = sharedSpan, Description = "tense shift: walks vs opened" },
            new() { Type = "pov",   Span = sharedSpan, Description = "pov phrasing in the same passage" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        // Both issues survive (the bug surfaced only the first in significance order).
        Assert.Equal(2, suggestions.Count);
        Assert.Contains(suggestions, s => s.Category == "consistency-tense");
        Assert.Contains(suggestions, s => s.Category == "consistency-pov");

        var normDoc = TextNormalization.NormalizeTextForAnalysis(inputText);
        var idx = normDoc.IndexOf(sharedSpan, StringComparison.Ordinal);
        Assert.True(idx >= 0, "fixture: shared span must be present once in the text");
        // Span occurs once, so both issues anchor to that single occurrence (overlap is allowed here).
        Assert.All(suggestions, s =>
        {
            Assert.Equal(idx, s.StartOffset);
            Assert.Equal(idx + sharedSpan.Length, s.EndOffset);
        });
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_DistinctIssuesOverlappingWindows_BothAnchored()
    {
        // Tense and POV issues quote OVERLAPPING (not identical) windows over the same sentence. The
        // second window's only occurrence overlaps the first's claimed range; it must still anchor to
        // its real position rather than being dropped.
        const string inputText = "He walks into the kitchen and she opened the window while they watch the rain.";
        const string tenseWindow = "He walks into the kitchen and she opened";       // earlier window
        const string povWindow   = "kitchen and she opened the window while they";   // overlaps tenseWindow

        var normDoc = TextNormalization.NormalizeTextForAnalysis(inputText);
        var tenseIdx = normDoc.IndexOf(tenseWindow, StringComparison.Ordinal);
        var povIdx = normDoc.IndexOf(povWindow, StringComparison.Ordinal);
        Assert.True(tenseIdx >= 0 && povIdx >= 0, "fixture: both windows present");
        // Sanity-check the fixture: the two windows really overlap in text.
        Assert.True(povIdx < tenseIdx + tenseWindow.Length && tenseIdx < povIdx + povWindow.Length,
            "fixture: windows must overlap so the dedup path is exercised");

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "tense", Span = tenseWindow, Description = "tense shift" },
            new() { Type = "pov",   Span = povWindow,   Description = "pov shift" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        Assert.Equal(2, suggestions.Count);
        var tense = Assert.Single(suggestions, s => s.Category == "consistency-tense");
        var pov = Assert.Single(suggestions, s => s.Category == "consistency-pov");
        // Each maps to its own window's real position even though they overlap.
        Assert.Equal(tenseIdx, tense.StartOffset);
        Assert.Equal(povIdx, pov.StartOffset);
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_UnmatchedSpan_IsDropped()
    {
        // Span is not present in the analyzed text at all. It is not navigable in this unit, so it
        // must be dropped entirely (no null-offset fallback item).
        const string inputText = "A completely different sentence.";
        const string spanRaw = "text not found anywhere";

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "register", Span = spanRaw, Description = "unmatched issue" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        // Dropped - nothing surfaced for an all-unmatched input.
        Assert.Empty(suggestions);
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_SpanFromContextNotInTarget_IsDropped()
    {
        // Mimics the cross-chapter bug: the model detected a register/POV shift relative to the
        // surrounding chapters and quoted a sentence from that NEIGHBORING context. The span is a
        // verbatim sentence that exists only in some OTHER text, NOT in the analyzed target chapter.
        // Because it cannot be located in the analyzed unit, it is not navigable and must be dropped.
        const string targetChapter =
            "The morning was quiet. Sarah opened the shutters and let the light spill across the floor.";
        // This sentence exists only in the PRECEDING_CONTEXT (the previous chapter), never in the target.
        const string spanFromOtherChapter =
            "Down in the harbor the old fishermen mended their nets before dawn";

        // Sanity: the span really is absent from the analyzed text.
        Assert.DoesNotContain(spanFromOtherChapter, targetChapter, StringComparison.Ordinal);

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "register", Span = spanFromOtherChapter, Description = "register shift vs previous chapter" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, targetChapter);

        Assert.Empty(suggestions);
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_CategoryMapping_RegisterTensePov()
    {
        // Each issue type must produce the correct "consistency-{type}" category.
        const string inputText = "He runs. She walked. They think.";

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "register", Span = "He runs",   Description = "register" },
            new() { Type = "tense",    Span = "She walked", Description = "tense"    },
            new() { Type = "pov",      Span = "They think", Description = "pov"      }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        Assert.Equal(3, suggestions.Count);
        Assert.Contains(suggestions, s => s.Category == "consistency-register");
        Assert.Contains(suggestions, s => s.Category == "consistency-tense");
        Assert.Contains(suggestions, s => s.Category == "consistency-pov");
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_CategoryMapping_NormalizesTypeCase()
    {
        // Mixed-case and whitespace-padded Type values must be trimmed + lowercased in the category.
        const string inputText = "He runs. She walked. They think.";

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = " Register ", Span = "He runs",   Description = "register padded" },
            new() { Type = "Tense",      Span = "She walked", Description = "tense mixed"    },
            new() { Type = "POV",        Span = "They think", Description = "pov upper"      }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        Assert.Equal(3, suggestions.Count);
        Assert.Contains(suggestions, s => s.Category == "consistency-register");
        Assert.Contains(suggestions, s => s.Category == "consistency-tense");
        Assert.Contains(suggestions, s => s.Category == "consistency-pov");
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_SuggestedTextIsAlwaysEmpty()
    {
        // SuggestedText must be "" for every produced suggestion (navigate-only v1).
        // Both spans are present in the analyzed text so both are surfaced (matched spans only).
        const string inputText = "He walked. She ran.";

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "tense",    Span = "walked", Description = "tense shift" },
            new() { Type = "register", Span = "ran",    Description = "register shift" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        Assert.Equal(2, suggestions.Count);
        foreach (var s in suggestions)
        {
            Assert.Equal(string.Empty, s.SuggestedText);
        }
    }
}

