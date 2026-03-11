using System;
using System.Collections.Generic;
using System.Linq;
using DiffPlex;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// Computes proofread and line-edit suggestions on the server, mirroring the existing
/// frontend proofread-diff.ts behavior as closely as possible.
/// </summary>
public class SuggestionDiffService
{
    private const int MaxSuggestionCountForProofread = 2_000;
    private const int MergeGapThreshold = 1;
    // Target one-word-level proofread suggestions; larger rewrites belong in Line Edit.
    private const int MaxWordsPerSuggestion = 1;

    /// <summary>
    /// Compute proofread suggestions by diffing original document text with the proofread result text.
    /// Offsets are in the normalized original text and later mapped back when applying highlights.
    ///
    /// Algorithm:
    ///  1. Character-level diff via DiffPlex.
    ///  2. For each diff block, expand the affected range in the original to word boundaries.
    ///  3. Merge overlapping/adjacent word ranges so multiple blocks within one word become one range.
    ///  4. Map each merged original range to its corresponding result range using cumulative position deltas.
    ///  5. Extract original and suggested text from those ranges → one clean suggestion per word.
    /// </summary>
    public List<AnalysisSuggestion> ComputeProofreadSuggestions(string originalText, string resultText)
    {
        if (string.IsNullOrWhiteSpace(originalText) || string.IsNullOrWhiteSpace(resultText))
            return new List<AnalysisSuggestion>();

        var normOrig = TextNormalization.NormalizeTextForAnalysis(originalText);
        var normResult = TextNormalization.NormalizeTextForAnalysis(resultText);

        var differ = new Differ();
        var diff = differ.CreateCharacterDiffs(normOrig, normResult, ignoreCase: false, ignoreWhitespace: false);

        if (diff.DiffBlocks.Count == 0)
            return new List<AnalysisSuggestion>();

        // Step 1: expand each diff block to word boundaries in the original text.
        var wordRanges = new List<(int Start, int End)>();
        foreach (var block in diff.DiffBlocks)
        {
            if (block.DeleteCountA == 0 && block.InsertCountB == 0)
                continue;

            var s = block.DeleteStartA;
            var e = s + block.DeleteCountA;

            // Expand to word boundaries
            while (s > 0 && IsWordChar(normOrig[s - 1]))
                s--;
            while (e < normOrig.Length && IsWordChar(normOrig[e]))
                e++;

            // For pure insertions (deleteCount == 0) at a word boundary, s == e.
            // Expand in both directions to capture the enclosing word.
            if (s == e)
            {
                while (s > 0 && IsWordChar(normOrig[s - 1]))
                    s--;
                while (e < normOrig.Length && IsWordChar(normOrig[e]))
                    e++;
            }

            if (s < e)
                wordRanges.Add((s, e));
        }

        if (wordRanges.Count == 0)
            return new List<AnalysisSuggestion>();

        // Step 2: sort and merge overlapping/adjacent word ranges.
        wordRanges.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));
        var merged = new List<(int Start, int End)> { wordRanges[0] };
        for (var i = 1; i < wordRanges.Count; i++)
        {
            var current = wordRanges[i];
            var last = merged[^1];
            if (current.Start <= last.End + MergeGapThreshold)
                merged[^1] = (last.Start, Math.Max(last.End, current.End));
            else
                merged.Add(current);
        }

        // Step 3: split any oversized merged range back into individual diff-block-aligned sub-ranges.
        merged = SplitOversizedRanges(merged, diff.DiffBlocks, normOrig);

        // Step 4: build cumulative delta via diff blocks to map original positions to result positions.
        var blocks = diff.DiffBlocks;

        // Step 5: map each merged original word range to the result and build suggestions.
        var suggestions = new List<AnalysisSuggestion>();
        foreach (var (wStart, wEnd) in merged)
        {
            var origWord = normOrig[wStart..wEnd];

            var rStart = OrigToResultPos(wStart, blocks);
            var rEnd = OrigToResultPos(wEnd, blocks);

            rStart = Math.Max(0, Math.Min(rStart, normResult.Length));
            rEnd = Math.Max(rStart, Math.Min(rEnd, normResult.Length));

            var sugWord = rStart < rEnd ? normResult[rStart..rEnd] : string.Empty;

            if (string.Equals(origWord, sugWord, StringComparison.Ordinal))
                continue;

            // Layer 3: reject pathological suggestions where large original maps to empty/tiny replacement
            var origLen = wEnd - wStart;
            var sugLen = rEnd - rStart;
            if (origLen > 40 && sugLen <= 8) continue;
            if (origLen > 25 && sugLen == 0) continue;

            suggestions.Add(new AnalysisSuggestion
            {
                StartOffset = wStart,
                EndOffset = wEnd,
                OriginalText = origWord,
                SuggestedText = sugWord,
                Reason = "Proofread"
            });
        }

        suggestions = suggestions.Where(IsMeaningfulSuggestion).ToList();

        if (suggestions.Count > MaxSuggestionCountForProofread)
            return new List<AnalysisSuggestion>();

        return suggestions;
    }

    /// <summary>
    /// Map a position in the original (normalized) text to the corresponding position
    /// in the result text, accounting for all diff blocks whose deleted range ends
    /// before or at the given position.
    ///
    /// Word-boundary positions should never fall inside a deleted range because we
    /// expand to word boundaries. The graceful fallback handles it just in case.
    /// </summary>
    private static int OrigToResultPos(int origPos, IList<DiffPlex.Model.DiffBlock> blocks)
    {
        var delta = 0;
        foreach (var block in blocks)
        {
            var deleteEnd = block.DeleteStartA + block.DeleteCountA;

            if (deleteEnd <= origPos)
            {
                delta += block.InsertCountB - block.DeleteCountA;
            }
            else if (block.DeleteStartA < origPos)
            {
                // origPos is inside a deleted range (shouldn't happen for word boundaries).
                // Graceful fallback: map to the end of the corresponding insertion.
                return block.InsertStartB + block.InsertCountB;
            }
            else
            {
                break;
            }
        }
        return origPos + delta;
    }

    /// <summary>
    /// Split any merged range that spans more than <see cref="MaxWordsPerSuggestion"/> words back
    /// into individual diff-block-aligned sub-ranges. This prevents a cluster of nearby
    /// character-level edits from fusing into one giant suggestion.
    /// </summary>
    private static List<(int Start, int End)> SplitOversizedRanges(
        List<(int Start, int End)> merged,
        IList<DiffPlex.Model.DiffBlock> diffBlocks,
        string normOrig)
    {
        var result = new List<(int Start, int End)>(merged.Count);
        foreach (var (mStart, mEnd) in merged)
        {
            if (CountWords(normOrig, mStart, mEnd) <= MaxWordsPerSuggestion)
            {
                result.Add((mStart, mEnd));
                continue;
            }

            // Re-expand each diff block within this merged range into its own word-boundary range
            var subRanges = new List<(int Start, int End)>();
            foreach (var block in diffBlocks)
            {
                if (block.DeleteCountA == 0 && block.InsertCountB == 0)
                    continue;
                var s = block.DeleteStartA;
                var e = s + block.DeleteCountA;
                if (e <= mStart || s >= mEnd) continue;

                while (s > 0 && IsWordChar(normOrig[s - 1])) s--;
                while (e < normOrig.Length && IsWordChar(normOrig[e])) e++;
                if (s == e)
                {
                    while (s > 0 && IsWordChar(normOrig[s - 1])) s--;
                    while (e < normOrig.Length && IsWordChar(normOrig[e])) e++;
                }
                if (s < e)
                    subRanges.Add((s, e));
            }

            if (subRanges.Count == 0)
            {
                result.Add((mStart, mEnd));
                continue;
            }

            // Merge only truly overlapping sub-ranges (no gap tolerance)
            subRanges.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));
            var subMerged = new List<(int Start, int End)> { subRanges[0] };
            for (var i = 1; i < subRanges.Count; i++)
            {
                var cur = subRanges[i];
                var prev = subMerged[^1];
                if (cur.Start < prev.End) // strict overlap only
                    subMerged[^1] = (prev.Start, Math.Max(prev.End, cur.End));
                else
                    subMerged.Add(cur);
            }

            // Finally, enforce MaxWordsPerSuggestion by splitting any remaining
            // multi-word ranges into per-word segments.
            foreach (var (s, e) in subMerged)
            {
                if (CountWords(normOrig, s, e) <= MaxWordsPerSuggestion)
                {
                    result.Add((s, e));
                }
                else
                {
                    result.AddRange(SplitRangeByWords(normOrig, s, e, MaxWordsPerSuggestion));
                }
            }
        }
        return result;
    }

    private static int CountWords(string text, int start, int end)
    {
        var count = 0;
        var inWord = false;
        for (var i = start; i < end && i < text.Length; i++)
        {
            if (IsWordChar(text[i]))
            {
                if (!inWord) { count++; inWord = true; }
            }
            else
            {
                inWord = false;
            }
        }
        return count;
    }

    private static IEnumerable<(int Start, int End)> SplitRangeByWords(string text, int start, int end, int maxWordsPerSegment)
    {
        var i = start;
        var length = Math.Min(end, text.Length);
        var segStart = start;
        var wordCountInSeg = 0;

        while (i < length)
        {
            // Find next word
            while (i < length && !IsWordChar(text[i])) i++;
            if (i >= length) break;

            var wordStart = i;
            while (i < length && IsWordChar(text[i])) i++;
            var wordEnd = i;

            if (wordCountInSeg == 0)
            {
                segStart = wordStart;
            }
            wordCountInSeg++;

            // If we've reached maxWordsPerSegment, close this segment just before the next word.
            if (wordCountInSeg >= maxWordsPerSegment)
            {
                var segEnd = i;
                while (segEnd < length && !IsWordChar(text[segEnd])) segEnd++;
                yield return (segStart, segEnd);
                wordCountInSeg = 0;
            }
        }

        // Remainder segment, if any words accumulated and not yet yielded
        if (wordCountInSeg > 0)
        {
            yield return (segStart, length);
        }
    }

    /// <summary>
    /// Compute suggestions for a structured LineEditResult by mapping each suggestion's original fragment
    /// back to the original document text via IndexOf (same as the existing frontend implementation).
    /// </summary>
    public List<AnalysisSuggestion> ComputeLineEditSuggestions(LineEditResult structured, string originalText)
    {
        var suggestions = new List<AnalysisSuggestion>();
        if (structured?.Suggestions == null || structured.Suggestions.Count == 0)
            return suggestions;
        var normalizedDocument = TextNormalization.NormalizeTextForAnalysis(originalText);

        var searchStart = 0;
        foreach (var s in structured.Suggestions)
        {
            var original = s.Original ?? string.Empty;
            var suggested = s.Suggested ?? string.Empty;
            var reason = s.Reason;
            var category = s.Category;

            if (string.IsNullOrWhiteSpace(original) && string.IsNullOrWhiteSpace(suggested))
                continue;

            var normalizedOriginal = TextNormalization.NormalizeTextForAnalysis(original);
            var idx = normalizedDocument.IndexOf(normalizedOriginal, searchStart, StringComparison.Ordinal);
            if (idx < 0)
            {
                suggestions.Add(new AnalysisSuggestion
                {
                    OriginalText = original,
                    SuggestedText = suggested,
                    Reason = reason,
                    Category = category
                });
                continue;
            }

            var startOffset = idx;
            var endOffset = idx + normalizedOriginal.Length;
            searchStart = endOffset;

            suggestions.Add(new AnalysisSuggestion
            {
                StartOffset = startOffset,
                EndOffset = endOffset,
                OriginalText = normalizedOriginal,
                SuggestedText = suggested,
                Reason = reason,
                Category = category
            });
        }

        return suggestions;
    }

    private static bool IsMeaningfulSuggestion(AnalysisSuggestion s)
    {
        if (string.Equals(s.OriginalText, s.SuggestedText, StringComparison.Ordinal))
            return false;

        var o = (s.OriginalText ?? string.Empty).Trim();
        var g = (s.SuggestedText ?? string.Empty).Trim();
        if (string.Equals(o, g, StringComparison.Ordinal))
            return false;

        return true;
    }

    private static bool IsWordChar(char c) =>
        char.IsLetterOrDigit(c);

}
