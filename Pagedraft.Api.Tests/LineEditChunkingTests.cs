using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

public class LineEditChunkingTests
{
    private static readonly MethodInfo ChunkForLineEditMethod = typeof(UnifiedAnalysisService)
        .GetMethod("ChunkForLineEdit", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not find ChunkForLineEdit via reflection.");

    private static List<object> Chunk(string text, int targetWords)
    {
        var result = (IEnumerable)ChunkForLineEditMethod.Invoke(null, new object[] { text, targetWords })!;
        return result.Cast<object>().ToList();
    }

    private static string GetText(object chunk) =>
        (string)chunk.GetType().GetProperty("Text")!.GetValue(chunk)!;

    private static string GetSeparatorAfter(object chunk) =>
        (string)chunk.GetType().GetProperty("SeparatorAfter")!.GetValue(chunk)!;

    private static string? GetOverlapPrefix(object chunk) =>
        (string?)chunk.GetType().GetProperty("OverlapPrefix")!.GetValue(chunk);

    private static string? GetOverlapSuffix(object chunk) =>
        (string?)chunk.GetType().GetProperty("OverlapSuffix")!.GetValue(chunk);

    [Fact]
    public void ChunkForLineEdit_EmptyText_ReturnsSingleEmptyChunk()
    {
        var chunks = Chunk("", 1500);

        var chunk = Assert.Single(chunks);
        Assert.Equal(string.Empty, GetText(chunk));
        Assert.Equal(string.Empty, GetSeparatorAfter(chunk));
        Assert.Null(GetOverlapPrefix(chunk));
        Assert.Null(GetOverlapSuffix(chunk));
    }

    [Fact]
    public void ChunkForLineEdit_SingleShortParagraph_BelowTarget_ReturnsSingleChunkWithoutOverlap()
    {
        const string text = "This is a short paragraph that should not be split.";

        var chunks = Chunk(text, 100);

        var chunk = Assert.Single(chunks);
        Assert.Equal(text.TrimEnd(), GetText(chunk));
        Assert.Null(GetOverlapPrefix(chunk));
        Assert.Null(GetOverlapSuffix(chunk));
    }

    [Fact]
    public void ChunkForLineEdit_TextJustBelowTarget_DoesNotSplit()
    {
        // 8 words, target 10 → stays as a single chunk
        const string text = "One two three four. Five six seven eight.";

        var chunks = Chunk(text, 10);

        var chunk = Assert.Single(chunks);
        Assert.Equal(text.TrimEnd(), GetText(chunk));
    }

    [Fact]
    public void ChunkForLineEdit_TextJustAboveTarget_SplitsIntoTwoChunks()
    {
        // 12 words, target 10 → should produce two chunks around the sentence boundary
        const string text = "One two three four five six. Seven eight nine ten eleven twelve.";

        var chunks = Chunk(text, 10);

        Assert.Equal(2, chunks.Count);
        Assert.StartsWith("One two three four five six.", GetText(chunks[0]), StringComparison.Ordinal);
        Assert.StartsWith("Seven eight nine ten eleven twelve.", GetText(chunks[1]), StringComparison.Ordinal);
    }

    [Fact]
    public void ChunkForLineEdit_ShortMediumLongInputs_ProduceExpectedChunkCounts()
    {
        static string BuildRepeatedSentences(int sentenceCount)
        {
            var sentences = Enumerable.Range(1, sentenceCount)
                .Select(i => $"Sentence {i}.");
            return string.Join(" ", sentences);
        }

        const int targetWords = 10; // Each sentence ~2 words → ~5 sentences per 2 chunks

        var shortText = BuildRepeatedSentences(1);  // ~2 words
        var mediumText = BuildRepeatedSentences(4); // ~8 words
        var longText = BuildRepeatedSentences(9);   // ~18 words

        var shortChunks = Chunk(shortText, targetWords);
        var mediumChunks = Chunk(mediumText, targetWords);
        var longChunks = Chunk(longText, targetWords);

        Assert.Single(shortChunks);
        Assert.Single(mediumChunks);
        Assert.Equal(2, longChunks.Count);
    }

    [Fact]
    public void ChunkForLineEdit_ComputesExpectedOverlapPrefixAndSuffix()
    {
        const string text =
            "Sentence one. Sentence two. Sentence three. Sentence four. Sentence five. Sentence six.";

        // Each sentence has 2 words; target 6 → 3 sentences per chunk → 2 chunks total
        var chunks = Chunk(text, 6);

        Assert.Equal(2, chunks.Count);

        var first = chunks[0];
        var second = chunks[1];

        Assert.Null(GetOverlapPrefix(first));
        Assert.Equal("Sentence four. Sentence five.", GetOverlapSuffix(first));

        Assert.Equal("Sentence one. Sentence two. Sentence three.", GetOverlapPrefix(second));
        Assert.Null(GetOverlapSuffix(second));
    }

    [Fact]
    public void ChunkForLineEdit_DialogueBlocks_MayOverflowTargetWithinMultiplier()
    {
        // Two short dialogue lines (3 words each) wrapped in quotes.
        // For non-dialogue, 3 + 3 words with target 5 would split;
        // for dialogue, the same content is allowed to overflow up to target * DialogueOverflowMultiplier.
        const string dialogueText = "\"First line here.\"\n\n\"Second line here.\"";
        const string nonDialogueText = "First line here.\n\nSecond line here.";

        const int targetWords = 5;

        var dialogueChunks = Chunk(dialogueText, targetWords);
        var nonDialogueChunks = Chunk(nonDialogueText, targetWords);

        Assert.Equal(1, dialogueChunks.Count);
        Assert.Equal(2, nonDialogueChunks.Count);
    }

    // ─── MergeLineEditResults tests ──────────────────────────────────

    [Fact]
    public void Merge_DisjointSuggestions_PreservesOrderAndCategories()
    {
        var chunk1 = new LineEditResult
        {
            Suggestions = new List<LineEditSuggestion>
            {
                new() { Original = "The cat sat.", Suggested = "The cat perched.", Reason = "word-choice", Category = "word-choice" },
                new() { Original = "It was fine.", Suggested = "It was adequate.", Reason = "clarity", Category = "clarity" },
            },
            OverallFeedback = "Chunk 1 feedback"
        };
        var chunk2 = new LineEditResult
        {
            Suggestions = new List<LineEditSuggestion>
            {
                new() { Original = "He ran fast.", Suggested = "He sprinted.", Reason = "flow", Category = "flow" },
                new() { Original = "She said hello.", Suggested = "She greeted him.", Reason = "style", Category = "style" },
            },
            OverallFeedback = "Chunk 2 feedback"
        };

        var merged = UnifiedAnalysisService.MergeLineEditResults(new List<LineEditResult> { chunk1, chunk2 });

        Assert.Equal(4, merged.Suggestions.Count);
        Assert.Equal("The cat sat.", merged.Suggestions[0].Original);
        Assert.Equal("word-choice", merged.Suggestions[0].Category);
        Assert.Equal("It was fine.", merged.Suggestions[1].Original);
        Assert.Equal("clarity", merged.Suggestions[1].Category);
        Assert.Equal("He ran fast.", merged.Suggestions[2].Original);
        Assert.Equal("flow", merged.Suggestions[2].Category);
        Assert.Equal("She said hello.", merged.Suggestions[3].Original);
        Assert.Equal("style", merged.Suggestions[3].Category);
    }

    [Fact]
    public void Merge_OverlapDuplicates_KeepsFirstDropsLater()
    {
        var chunk1 = new LineEditResult
        {
            Suggestions = new List<LineEditSuggestion>
            {
                new() { Original = "Opening line.", Suggested = "Better opening.", Reason = "r1", Category = "clarity" },
                new() { Original = "Overlap sentence.", Suggested = "First version.", Reason = "from chunk 1", Category = "flow" },
            },
            OverallFeedback = ""
        };
        var chunk2 = new LineEditResult
        {
            Suggestions = new List<LineEditSuggestion>
            {
                new() { Original = "Overlap sentence.", Suggested = "Second version.", Reason = "from chunk 2", Category = "style" },
                new() { Original = "Closing line.", Suggested = "Better closing.", Reason = "r2", Category = "word-choice" },
            },
            OverallFeedback = ""
        };

        var merged = UnifiedAnalysisService.MergeLineEditResults(new List<LineEditResult> { chunk1, chunk2 });

        Assert.Equal(3, merged.Suggestions.Count);
        Assert.Equal("Opening line.", merged.Suggestions[0].Original);
        Assert.Equal("Overlap sentence.", merged.Suggestions[1].Original);
        Assert.Equal("First version.", merged.Suggestions[1].Suggested);
        Assert.Equal("Closing line.", merged.Suggestions[2].Original);
    }

    [Fact]
    public void Merge_DuplicatesWithWhitespaceDifferences_StillDeduplicates()
    {
        var chunk1 = new LineEditResult
        {
            Suggestions = new List<LineEditSuggestion>
            {
                new() { Original = "Some text\r\nwith breaks.", Suggested = "Fixed A.", Reason = "r1", Category = "clarity" },
            },
            OverallFeedback = ""
        };
        var chunk2 = new LineEditResult
        {
            Suggestions = new List<LineEditSuggestion>
            {
                // Same text after normalization (line breaks stripped)
                new() { Original = "Some textwith breaks.", Suggested = "Fixed B.", Reason = "r2", Category = "flow" },
            },
            OverallFeedback = ""
        };

        var merged = UnifiedAnalysisService.MergeLineEditResults(new List<LineEditResult> { chunk1, chunk2 });

        Assert.Single(merged.Suggestions);
        Assert.Equal("Fixed A.", merged.Suggestions[0].Suggested);
    }

    [Fact]
    public void Merge_ChunksWithEmptySuggestions_ProducesValidResult()
    {
        var chunk1 = new LineEditResult
        {
            Suggestions = new List<LineEditSuggestion>
            {
                new() { Original = "First.", Suggested = "First fixed.", Reason = "r", Category = "clarity" },
            },
            OverallFeedback = "Has feedback"
        };
        var emptyChunk = new LineEditResult
        {
            Suggestions = new List<LineEditSuggestion>(),
            OverallFeedback = ""
        };
        var chunk3 = new LineEditResult
        {
            Suggestions = new List<LineEditSuggestion>
            {
                new() { Original = "Third.", Suggested = "Third fixed.", Reason = "r", Category = "style" },
            },
            OverallFeedback = "More feedback"
        };

        var merged = UnifiedAnalysisService.MergeLineEditResults(
            new List<LineEditResult> { chunk1, emptyChunk, chunk3 });

        Assert.Equal(2, merged.Suggestions.Count);
        Assert.Equal("First.", merged.Suggestions[0].Original);
        Assert.Equal("Third.", merged.Suggestions[1].Original);
    }

    [Fact]
    public void Merge_ChunksWithNullSuggestions_ProducesValidResult()
    {
        var nullSuggestionsChunk = new LineEditResult
        {
            Suggestions = null!,
            OverallFeedback = "Some feedback"
        };
        var normalChunk = new LineEditResult
        {
            Suggestions = new List<LineEditSuggestion>
            {
                new() { Original = "Only one.", Suggested = "The only one.", Reason = "r", Category = "clarity" },
            },
            OverallFeedback = ""
        };

        var merged = UnifiedAnalysisService.MergeLineEditResults(
            new List<LineEditResult> { nullSuggestionsChunk, normalChunk });

        Assert.Single(merged.Suggestions);
        Assert.Equal("Only one.", merged.Suggestions[0].Original);
    }

    [Fact]
    public void Merge_EmptyList_ReturnsEmptyResult()
    {
        var merged = UnifiedAnalysisService.MergeLineEditResults(new List<LineEditResult>());

        Assert.NotNull(merged);
        Assert.Empty(merged.Suggestions);
        Assert.Equal(string.Empty, merged.OverallFeedback);
    }

    [Fact]
    public void Merge_NullList_ReturnsEmptyResult()
    {
        var merged = UnifiedAnalysisService.MergeLineEditResults(null!);

        Assert.NotNull(merged);
        Assert.Empty(merged.Suggestions);
        Assert.Equal(string.Empty, merged.OverallFeedback);
    }

    [Fact]
    public void Merge_OverallFeedback_SingleChunk_ReturnsUnjoined()
    {
        var chunk = new LineEditResult
        {
            Suggestions = new List<LineEditSuggestion>(),
            OverallFeedback = "  Solo feedback  "
        };

        var merged = UnifiedAnalysisService.MergeLineEditResults(new List<LineEditResult> { chunk });

        Assert.Equal("Solo feedback", merged.OverallFeedback);
    }

    [Fact]
    public void Merge_OverallFeedback_MultipleChunks_JoinedWithSeparator()
    {
        var chunks = new List<LineEditResult>
        {
            new() { Suggestions = new(), OverallFeedback = "Feedback A" },
            new() { Suggestions = new(), OverallFeedback = "Feedback B" },
            new() { Suggestions = new(), OverallFeedback = "Feedback C" },
        };

        var merged = UnifiedAnalysisService.MergeLineEditResults(chunks);

        Assert.Equal("Feedback A\n\n---\n\nFeedback B\n\n---\n\nFeedback C", merged.OverallFeedback);
    }

    [Fact]
    public void Merge_OverallFeedback_SkipsEmptyAndWhitespace()
    {
        var chunks = new List<LineEditResult>
        {
            new() { Suggestions = new(), OverallFeedback = "Keep this" },
            new() { Suggestions = new(), OverallFeedback = "" },
            new() { Suggestions = new(), OverallFeedback = "   " },
            new() { Suggestions = new(), OverallFeedback = "And this" },
        };

        var merged = UnifiedAnalysisService.MergeLineEditResults(chunks);

        Assert.Equal("Keep this\n\n---\n\nAnd this", merged.OverallFeedback);
    }

    [Fact]
    public void Merge_ThreeChunksAllDisjoint_AllSuggestionsPreserved()
    {
        var chunks = new List<LineEditResult>
        {
            new()
            {
                Suggestions = new List<LineEditSuggestion>
                {
                    new() { Original = "Alpha.", Suggested = "Alpha fixed.", Reason = "r", Category = "clarity" },
                },
                OverallFeedback = "F1"
            },
            new()
            {
                Suggestions = new List<LineEditSuggestion>
                {
                    new() { Original = "Beta.", Suggested = "Beta fixed.", Reason = "r", Category = "redundancy" },
                    new() { Original = "Gamma.", Suggested = "Gamma fixed.", Reason = "r", Category = "structure" },
                },
                OverallFeedback = "F2"
            },
            new()
            {
                Suggestions = new List<LineEditSuggestion>
                {
                    new() { Original = "Delta.", Suggested = "Delta fixed.", Reason = "r", Category = "continuity" },
                },
                OverallFeedback = "F3"
            },
        };

        var merged = UnifiedAnalysisService.MergeLineEditResults(chunks);

        Assert.Equal(4, merged.Suggestions.Count);
        Assert.Equal("Alpha.", merged.Suggestions[0].Original);
        Assert.Equal("Beta.", merged.Suggestions[1].Original);
        Assert.Equal("Gamma.", merged.Suggestions[2].Original);
        Assert.Equal("Delta.", merged.Suggestions[3].Original);
        Assert.Equal("F1\n\n---\n\nF2\n\n---\n\nF3", merged.OverallFeedback);
    }

    // ─── Offset-correctness integration tests ──────────────────────

    private static readonly string[] OffsetMarkers =
    {
        "the crimson fox leaped over the ancient wall",
        "whispered secrets echoed through the abandoned cathedral",
        "a peculiar clockwork mechanism began turning",
        "silver moonlight illuminated the forgotten manuscript",
        "thunderous applause erupted from the enchanted gallery"
    };

    /// <summary>
    /// Build a synthetic ~3000-word document with <paramref name="markers"/> embedded
    /// as minimal-span phrases inside carrier sentences, separated by filler paragraphs.
    /// </summary>
    private static string BuildSyntheticText(string[] markers, int fillerSentencesPerSection = 38)
    {
        var sb = new StringBuilder();
        for (var section = 0; section <= markers.Length; section++)
        {
            if (section > 0) sb.Append("\n\n");
            for (var s = 0; s < fillerSentencesPerSection; s++)
            {
                if (s > 0) sb.Append(' ');
                sb.Append($"In region {section + 1} this is filler sentence number {s + 1} used for padding purposes here.");
            }
            if (section < markers.Length)
            {
                sb.Append($" She noticed {markers[section]} as the day drew to a close.");
            }
        }
        return sb.ToString();
    }

    private static (int Start, int End)[] ComputeExpectedOffsets(string normalizedText, string[] markers)
    {
        var offsets = new (int Start, int End)[markers.Length];
        var cursor = 0;
        for (var i = 0; i < markers.Length; i++)
        {
            var norm = TextNormalization.NormalizeTextForAnalysis(markers[i]);
            var idx = normalizedText.IndexOf(norm, cursor, StringComparison.Ordinal);
            Assert.True(idx >= 0, $"Marker '{markers[i]}' not found after position {cursor}");
            offsets[i] = (idx, idx + norm.Length);
            cursor = idx + norm.Length;
        }
        return offsets;
    }

    private static LineEditResult MakeChunkResult(
        params (string Original, string Category)[] items)
    {
        return new LineEditResult
        {
            Suggestions = items.Select(x => new LineEditSuggestion
            {
                Original = x.Original,
                Suggested = $"rewritten: {x.Original[..Math.Min(20, x.Original.Length)]}...",
                Reason = x.Category,
                Category = x.Category
            }).ToList(),
            OverallFeedback = ""
        };
    }

    [Fact]
    public void OffsetIntegration_ChunkedMergeThenCompute_GlobalOffsetsMatchExpected()
    {
        var inputText = BuildSyntheticText(OffsetMarkers);
        var wordCount = inputText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.InRange(wordCount, 2500, 3500);

        var normalized = TextNormalization.NormalizeTextForAnalysis(inputText);
        var expected = ComputeExpectedOffsets(normalized, OffsetMarkers);

        var chunk1 = MakeChunkResult(
            (OffsetMarkers[0], "clarity"), (OffsetMarkers[1], "flow"));
        var chunk2 = MakeChunkResult(
            (OffsetMarkers[2], "word-choice"), (OffsetMarkers[3], "structure"));
        var chunk3 = MakeChunkResult(
            (OffsetMarkers[4], "redundancy"));

        var merged = UnifiedAnalysisService.MergeLineEditResults(
            new List<LineEditResult> { chunk1, chunk2, chunk3 });
        Assert.Equal(5, merged.Suggestions.Count);

        var suggestions = new SuggestionDiffService()
            .ComputeLineEditSuggestions(merged, inputText);

        Assert.Equal(5, suggestions.Count);
        for (var i = 0; i < OffsetMarkers.Length; i++)
        {
            Assert.Equal(expected[i].Start, suggestions[i].StartOffset);
            Assert.Equal(expected[i].End, suggestions[i].EndOffset);
            Assert.Equal(OffsetMarkers[i], suggestions[i].OriginalText);
        }

        Assert.Equal("clarity", suggestions[0].Category);
        Assert.Equal("flow", suggestions[1].Category);
        Assert.Equal("word-choice", suggestions[2].Category);
        Assert.Equal("structure", suggestions[3].Category);
        Assert.Equal("redundancy", suggestions[4].Category);

        for (var i = 1; i < suggestions.Count; i++)
        {
            Assert.True(suggestions[i].StartOffset > suggestions[i - 1].EndOffset,
                $"Suggestion {i} start ({suggestions[i].StartOffset}) must follow " +
                $"suggestion {i - 1} end ({suggestions[i - 1].EndOffset})");
        }
    }

    [Fact]
    public void OffsetIntegration_OverlapDuplicateDropped_OffsetsStillCorrect()
    {
        var inputText = BuildSyntheticText(OffsetMarkers);
        var normalized = TextNormalization.NormalizeTextForAnalysis(inputText);
        var expected = ComputeExpectedOffsets(normalized, OffsetMarkers);

        var chunk1 = new LineEditResult
        {
            Suggestions = new List<LineEditSuggestion>
            {
                new() { Original = OffsetMarkers[0], Suggested = "v1-m0", Reason = "r", Category = "clarity" },
                new() { Original = OffsetMarkers[1], Suggested = "v1-m1", Reason = "r", Category = "flow" },
            },
            OverallFeedback = "C1"
        };
        var chunk2 = new LineEditResult
        {
            Suggestions = new List<LineEditSuggestion>
            {
                new() { Original = OffsetMarkers[1], Suggested = "v2-m1-should-drop", Reason = "r", Category = "style" },
                new() { Original = OffsetMarkers[2], Suggested = "v1-m2", Reason = "r", Category = "word-choice" },
                new() { Original = OffsetMarkers[3], Suggested = "v1-m3", Reason = "r", Category = "structure" },
            },
            OverallFeedback = "C2"
        };
        var chunk3 = new LineEditResult
        {
            Suggestions = new List<LineEditSuggestion>
            {
                new() { Original = OffsetMarkers[4], Suggested = "v1-m4", Reason = "r", Category = "redundancy" },
            },
            OverallFeedback = "C3"
        };

        var merged = UnifiedAnalysisService.MergeLineEditResults(
            new List<LineEditResult> { chunk1, chunk2, chunk3 });

        Assert.Equal(5, merged.Suggestions.Count);
        Assert.Equal("v1-m1", merged.Suggestions[1].Suggested);

        var suggestions = new SuggestionDiffService()
            .ComputeLineEditSuggestions(merged, inputText);

        Assert.Equal(5, suggestions.Count);
        for (var i = 0; i < OffsetMarkers.Length; i++)
        {
            Assert.Equal(expected[i].Start, suggestions[i].StartOffset);
            Assert.Equal(expected[i].End, suggestions[i].EndOffset);
        }
    }

    [Fact]
    public void OffsetIntegration_EmptyChunkInMiddle_SurroundingOffsetsCorrect()
    {
        var inputText = BuildSyntheticText(OffsetMarkers);
        var normalized = TextNormalization.NormalizeTextForAnalysis(inputText);
        var expected = ComputeExpectedOffsets(normalized, OffsetMarkers);

        var chunk1 = MakeChunkResult(
            (OffsetMarkers[0], "clarity"), (OffsetMarkers[1], "flow"));
        var chunk2 = new LineEditResult { Suggestions = new(), OverallFeedback = "" };
        var chunk3 = MakeChunkResult(
            (OffsetMarkers[2], "word-choice"),
            (OffsetMarkers[3], "structure"),
            (OffsetMarkers[4], "redundancy"));

        var merged = UnifiedAnalysisService.MergeLineEditResults(
            new List<LineEditResult> { chunk1, chunk2, chunk3 });
        Assert.Equal(5, merged.Suggestions.Count);

        var suggestions = new SuggestionDiffService()
            .ComputeLineEditSuggestions(merged, inputText);

        Assert.Equal(5, suggestions.Count);
        for (var i = 0; i < OffsetMarkers.Length; i++)
        {
            Assert.Equal(expected[i].Start, suggestions[i].StartOffset);
            Assert.Equal(expected[i].End, suggestions[i].EndOffset);
        }
    }
}

