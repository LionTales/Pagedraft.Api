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

    // -----------------------------------------------------------------------
    // Near-match anchoring fallback tests
    // -----------------------------------------------------------------------

    [Fact]
    public void ComputeConsistencyIssueSuggestions_ExactSpan_AnchorsAtRightOffset_OriginalTextUnchanged()
    {
        // Regression: an exactly-quoted span still anchors at its real offset and keeps the model's
        // verbatim text - the near-match fallback must not interfere with the exact-match path.
        const string inputText = "Now the highway stretches ahead of us and the fields slide past the window.";
        const string spanRaw = "the highway stretches ahead of us";

        var normDoc = TextNormalization.NormalizeTextForAnalysis(inputText);
        var expectedStart = normDoc.IndexOf(spanRaw, StringComparison.Ordinal);
        Assert.True(expectedStart >= 0, "fixture: span must be an exact substring");

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "tense", Span = spanRaw, Description = "tense shift" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal(expectedStart, suggestion.StartOffset);
        Assert.Equal(expectedStart + spanRaw.Length, suggestion.EndOffset);
        // OriginalText is the model's verbatim span (unchanged) on the exact path.
        Assert.Equal(spanRaw, suggestion.OriginalText);
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_HebrewMorphologicalNearMiss_AnchorsToRealDocumentText()
    {
        // The measured leak: the document has the present-tense "ועוצר" but the model mis-quoted it as
        // the past-tense "ועצר" (one character shorter). The exact-substring anchor failed and the tense
        // card was dropped. The near-match fallback must anchor to the REAL document text "ועוצר".
        const string inputText = "הוא רץ אל הדלת ועוצר ליד הסף, מביט החוצה אל הרחוב השקט.";
        const string misquotedSpan = "הוא רץ אל הדלת ועצר ליד הסף"; // "ועצר" (past) vs document "ועוצר" (present)

        var normDoc = TextNormalization.NormalizeTextForAnalysis(inputText);
        var correctSpan = "הוא רץ אל הדלת ועוצר ליד הסף";
        var expectedStart = normDoc.IndexOf(correctSpan, StringComparison.Ordinal);
        Assert.True(expectedStart >= 0, "fixture: real document text must be present");
        Assert.True(normDoc.IndexOf(misquotedSpan, StringComparison.Ordinal) < 0,
            "fixture: the mis-quoted span must NOT be an exact substring");

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "tense", Span = misquotedSpan, Description = "present-tense slip" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal(expectedStart, suggestion.StartOffset);
        Assert.Equal(expectedStart + correctSpan.Length, suggestion.EndOffset);
        // OriginalText is the REAL document text, not the model's mis-quote.
        Assert.Equal(correctSpan, suggestion.OriginalText);
        // Offsets are in normalized space and select exactly the anchored text.
        Assert.Equal(correctSpan, normDoc[suggestion.StartOffset!.Value..suggestion.EndOffset!.Value]);
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_EnglishOneCharNearMiss_AnchorsToRealDocumentText()
    {
        // English analog of the Hebrew morphological slip: the document has "stretches" but the model
        // quoted "streches" (one letter dropped). The near-match fallback anchors to the real text.
        const string inputText = "Now the highway stretches ahead of us and the fields slide past the window.";
        const string misquotedSpan = "the highway streches ahead of us"; // "streches" vs document "stretches"

        var normDoc = TextNormalization.NormalizeTextForAnalysis(inputText);
        const string correctSpan = "the highway stretches ahead of us";
        var expectedStart = normDoc.IndexOf(correctSpan, StringComparison.Ordinal);
        Assert.True(expectedStart >= 0, "fixture: real document text must be present");
        Assert.True(normDoc.IndexOf(misquotedSpan, StringComparison.Ordinal) < 0,
            "fixture: the mis-quoted span must NOT be an exact substring");

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "tense", Span = misquotedSpan, Description = "tense slip" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal(expectedStart, suggestion.StartOffset);
        Assert.Equal(expectedStart + correctSpan.Length, suggestion.EndOffset);
        Assert.Equal(correctSpan, suggestion.OriginalText);
        Assert.Equal(correctSpan, normDoc[suggestion.StartOffset!.Value..suggestion.EndOffset!.Value]);
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_AmbiguousNearMatch_IsDropped()
    {
        // Two equally-good near-match windows exist (the span is one edit away from BOTH), so the match
        // is ambiguous. The uniqueness guard must DROP the issue rather than guess which window to anchor.
        // Document contains both "the red gate stood open wide" and "the bed gate stood open wide";
        // the span "the led gate stood open wide" is exactly one substitution from each.
        const string inputText =
            "Near the orchard the red gate stood open wide. Far across the yard the bed gate stood open wide.";
        const string ambiguousSpan = "the led gate stood open wide";

        var normDoc = TextNormalization.NormalizeTextForAnalysis(inputText);
        Assert.True(normDoc.IndexOf(ambiguousSpan, StringComparison.Ordinal) < 0,
            "fixture: span must not be an exact substring");

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "register", Span = ambiguousSpan, Description = "ambiguous near-match" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        // Ambiguous -> dropped, no false anchor.
        Assert.Empty(suggestions);
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_GenuinelyAbsentSpan_IsStillDropped()
    {
        // A span quoted from OUTSIDE the analyzed unit (e.g. from preceding/following context) is not a
        // near-miss of anything in the body. The tight threshold + uniqueness guard must keep dropping it.
        const string targetChapter =
            "The morning was quiet. Sarah opened the shutters and let the light spill across the floor.";
        const string spanFromElsewhere =
            "Down in the harbor the old fishermen mended their nets before dawn";

        Assert.DoesNotContain(spanFromElsewhere, targetChapter, StringComparison.Ordinal);

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "register", Span = spanFromElsewhere, Description = "absent span" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, targetChapter);

        Assert.Empty(suggestions);
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_DeletionNearMiss_SnapsToWholeWordsNotMidWord()
    {
        // The exact live leak: the model dropped a letter in its span ("ועצר" for the document's
        // "ועוצר", a DELETION). Because the near-match fallback uses a FIXED-LENGTH window and the
        // mis-quote is one char shorter than the real text, the window anchored one char late and shaved
        // the leading "ה" - the card highlighted "וא מגיע ... פעמיים" instead of "הוא מגיע ... פעמיים".
        // The word-boundary snap must restore the full leading word "הוא" and end on a whole word.
        const string inputText =
            "הוא מגיע אל קצה החומה ועוצר. המגדלור מהבהב פעם, פעמיים, והמים שוטפים את הסלעים.";
        // Span has "ועצר" (dropped vav) - a 1-char deletion vs the document's "ועוצר".
        const string misquotedSpan = "הוא מגיע אל קצה החומה ועצר. המגדלור מהבהב פעם, פעמיים";

        var normDoc = TextNormalization.NormalizeTextForAnalysis(inputText);
        Assert.True(normDoc.IndexOf(misquotedSpan, StringComparison.Ordinal) < 0,
            "fixture: the mis-quoted span must NOT be an exact substring");
        var hePos = normDoc.IndexOf("הוא", StringComparison.Ordinal);
        Assert.True(hePos >= 0, "fixture: 'הוא' must be present in the document");

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "tense", Span = misquotedSpan, Description = "tense slip (dropped vav)" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        var suggestion = Assert.Single(suggestions);

        // The highlight starts on the WHOLE word "הוא" (not the shaved "וא").
        Assert.StartsWith("הוא", suggestion.OriginalText);

        // StartOffset points exactly at the "ה" of "הוא" in the normalized text (the leading char was
        // restored, not shaved).
        Assert.Equal(hePos, suggestion.StartOffset);

        var startOffset = suggestion.StartOffset!.Value;
        var endOffset = suggestion.EndOffset!.Value;

        // The span ends on a whole word: end is at string end or sits on a word boundary (the next char
        // is whitespace or punctuation, never another word's letter). Here it ends on "פעמיים" with a
        // following comma.
        Assert.True(endOffset == normDoc.Length
                    || char.IsWhiteSpace(normDoc[endOffset])
                    || char.IsPunctuation(normDoc[endOffset]),
            "anchored span must end on a whole-word boundary");
        Assert.EndsWith("פעמיים", suggestion.OriginalText.TrimEnd());

        // The anchored OriginalText is exactly the normalized document slice it points to.
        Assert.Equal(normDoc[startOffset..endOffset], suggestion.OriginalText);

        // Start is itself a whole-word boundary (start of text or preceded by whitespace/punctuation).
        Assert.True(startOffset == 0
                    || char.IsWhiteSpace(normDoc[startOffset - 1])
                    || char.IsPunctuation(normDoc[startOffset - 1]),
            "anchored span must start on a whole-word boundary");
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_EnglishDeletionNearMiss_SnapsToWholeWords()
    {
        // English analog of the deletion leak: the document reads "stops" but the model quoted "stps"
        // (a 1-char deletion). A fixed-length window would clip a neighbouring word; the word-boundary
        // snap must keep both ends on whole words (no mid-word start/end).
        const string inputText = "She walks to the shore and he stops there quietly before the tide turns.";
        const string misquotedSpan = "he stps there quietly"; // "stps" vs document "stops"

        var normDoc = TextNormalization.NormalizeTextForAnalysis(inputText);
        Assert.True(normDoc.IndexOf(misquotedSpan, StringComparison.Ordinal) < 0,
            "fixture: the mis-quoted span must NOT be an exact substring");

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "tense", Span = misquotedSpan, Description = "tense slip (dropped letter)" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        var suggestion = Assert.Single(suggestions);

        var startOffset = suggestion.StartOffset!.Value;
        var endOffset = suggestion.EndOffset!.Value;

        // Neither end is mid-word: start is at index 0 or preceded by whitespace; end is at the input
        // end or followed by whitespace.
        Assert.True(startOffset == 0 || char.IsWhiteSpace(normDoc[startOffset - 1]),
            "anchored span must start on a whole-word boundary");
        Assert.True(endOffset == normDoc.Length || char.IsWhiteSpace(normDoc[endOffset]),
            "anchored span must end on a whole-word boundary");

        // Anchors to the real document words "stops" / "quietly" (whole, not clipped).
        Assert.Contains("stops", suggestion.OriginalText);
        Assert.StartsWith("he ", suggestion.OriginalText);
        Assert.EndsWith("quietly", suggestion.OriginalText.TrimEnd());
        Assert.Equal(normDoc[startOffset..endOffset], suggestion.OriginalText);
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_NearMatchOffsets_AreInNormalizedSpaceAndCorrect()
    {
        // The document carries a bidi mark and a hard line break that normalization strips; the near-match
        // offsets must index into the NORMALIZED document, not the raw input, and select the real text.
        var inputRaw = "‏פתיח קצר.\r\nהוא רץ אל הדלת ועוצר ליד הסף, ואז ממשיך.";
        const string misquotedSpan = "הוא רץ אל הדלת ועצר ליד הסף"; // past-tense mis-quote of "ועוצר"

        var normDoc = TextNormalization.NormalizeTextForAnalysis(inputRaw);
        const string correctSpan = "הוא רץ אל הדלת ועוצר ליד הסף";
        var expectedStart = normDoc.IndexOf(correctSpan, StringComparison.Ordinal);
        Assert.True(expectedStart >= 0);

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "tense", Span = misquotedSpan, Description = "tense slip with bidi/crlf" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputRaw);

        var suggestion = Assert.Single(suggestions);
        Assert.Equal(expectedStart, suggestion.StartOffset);
        Assert.Equal(expectedStart + correctSpan.Length, suggestion.EndOffset);
        // Offsets index normalized space and select exactly the real document text.
        Assert.Equal(correctSpan, normDoc[suggestion.StartOffset!.Value..suggestion.EndOffset!.Value]);
        Assert.Equal(correctSpan, suggestion.OriginalText);
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_ContextBleedSpan_ProseSimilarToBodyWindow_IsDropped()
    {
        // The reopened-risk regression: at Scene scope the model is still handed PRECEDING/FOLLOWING
        // context it can quote from, so a detected issue's span can be a sentence from that neighboring
        // context that is genuinely ABSENT from the analyzed body - yet PROSE-SIMILAR to a body window.
        //
        // This span ("the old harbof walt was bold and the gray wager ruse") differs from the body window
        // "the old harbor wall was cold and the grey water rose" by SIX scattered single-char edits - well
        // inside the old percentage budget (ceil(0.12 * 52) = 7) - while sharing only the short stopword run
        // "the old" (~13% of the span), NOT a multi-word content run. Before the fix the near-match fallback
        // (generous 7-edit budget + full-offset scan) uniquely anchored it onto the body window, re-opening
        // exactly the out-of-unit false-anchor the be-c01 decision closed. After the fix it is dropped: the
        // shared exact word-run is too short (< 25% of the span) AND the real distance (6) exceeds the hard
        // 4-edit cap. The morphological near-misses (one word differs by 1 char) keep a LONG exact run, so
        // they still anchor - this only sheds prose-similar context bleed.
        const string inputText =
            "Near the shore the old harbor wall was cold and the grey water rose at noon.";
        const string contextBleedSpan =
            "the old harbof walt was bold and the gray wager ruse";

        var normDoc = TextNormalization.NormalizeTextForAnalysis(inputText);
        Assert.True(normDoc.IndexOf(contextBleedSpan, StringComparison.Ordinal) < 0,
            "fixture: span must not be an exact substring of the body");

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "register", Span = contextBleedSpan, Description = "register shift vs preceding context" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        // Out-of-unit prose-similar span -> dropped, no false body anchor.
        Assert.Empty(suggestions);
    }

    [Fact]
    public void ComputeConsistencyIssueSuggestions_HebrewContextBleedSpan_ProseSimilarToBodyWindow_IsDropped()
    {
        // Hebrew analog of the context-bleed regression. The span reads like a sentence quoted from the
        // preceding scene/chapter that is absent from the analyzed body but prose-similar to a body window:
        // many content words are perturbed by a single character each (הנהר->הנחר, הקר->הקור, סגריר->סגרירי,
        // רחש->רחשי, המים->המימ, הסלעים->הסלאים), so it lands inside the old percentage budget while sharing
        // only the short connective run "ושמענו את" (~15% of the span). Before the fix it falsely anchored
        // onto the body; after the fix it is dropped (short word-run + over the 4-edit cap). A true
        // morphological slip, where only ONE word differs, keeps a long exact run and still anchors.
        const string inputText =
            "ירדנו אל הנהר הקר ביום סגריר ושמענו את רחש המים בין הסלעים החלקלקים.";
        const string contextBleedSpan =
            "ירדנו אל הנחר הקור ביום סגרירי ושמענו את רחשי המימ בין הסלאים";

        var normDoc = TextNormalization.NormalizeTextForAnalysis(inputText);
        Assert.True(normDoc.IndexOf(contextBleedSpan, StringComparison.Ordinal) < 0,
            "fixture: span must not be an exact substring of the body");

        var issues = new List<ConsistencyIssue>
        {
            new() { Type = "register", Span = contextBleedSpan, Description = "register shift vs preceding scene" }
        };

        var suggestions = _sut.ComputeConsistencyIssueSuggestions(issues, inputText);

        Assert.Empty(suggestions);
    }
}

