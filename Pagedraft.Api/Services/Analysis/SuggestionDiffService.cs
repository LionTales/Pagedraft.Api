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

        resultText = StripInternalMarkers(resultText);
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
            if (current.Start <= last.End)
                merged[^1] = (last.Start, Math.Max(last.End, current.End));
            else
                merged.Add(current);
        }

        // Step 3: build a cumulative delta array from the diff blocks so we can map
        // any original position to its result position.
        // For an original position P that is NOT inside any deleted range:
        //   resultPos = P + cumDelta
        // where cumDelta = sum of (InsertCountB - DeleteCountA) for every block
        // whose deleted range ends at or before P.
        var blocks = diff.DiffBlocks;

        // Step 4: map each merged original word range to the result and build suggestions.
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
    /// Compute suggestions for a structured LineEditResult by mapping each suggestion's original fragment
    /// back to the original document text via IndexOf (same as the existing frontend implementation).
    /// </summary>
    public List<AnalysisSuggestion> ComputeLineEditSuggestions(LineEditResult structured, string originalText)
    {
        var suggestions = new List<AnalysisSuggestion>();
        if (structured?.Suggestions == null || structured.Suggestions.Count == 0)
            return suggestions;
        var normalizedDocument = TextNormalization.NormalizeTextForAnalysis(StripInternalMarkers(originalText));

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

    /// <summary>
    /// Strip internal analysis markers that should never be visible to users or affect offsets,
    /// such as [TEXT_TO_CORRECT]...[/TEXT_TO_CORRECT].
    /// </summary>
    private static string StripInternalMarkers(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text
            .Replace("[TEXT_TO_CORRECT]", string.Empty, StringComparison.Ordinal)
            .Replace("[/TEXT_TO_CORRECT]", string.Empty, StringComparison.Ordinal);
    }
}
