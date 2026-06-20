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

    // Near-match anchoring bounds (see FindUniqueNearMatch). A genuine morphological mis-quote (the only
    // case the near-match recovers) is a 1-2 edit slip on a single word, so we hard-cap the absolute edit
    // budget rather than let the percentage threshold grant 5-10 edits on a long span - a budget that wide
    // lets a span quoted from PRECEDING/FOLLOWING context (still injected at Scene scope) drift onto a
    // merely prose-similar body window. The span must ALSO share a verbatim multi-word run covering at
    // least MinWordRunFractionOfSpan of its length, so a span that overlaps the body only in a short
    // stopword run ("the old", "ושמענו את") is dropped, not fuzzily anchored.
    private const int MaxNearMatchEditDistance = 4;
    private const double MinWordRunFractionOfSpan = 0.25;

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

            // NOTE: SnapToWordBoundaries does the same expansion for the consistency near-match path - duplication is intentional; consolidate deliberately when refactoring.
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

            var origLen = wEnd - wStart;
            var sugLen = rEnd - rStart;

            // Reject pathological suggestions where large original maps to empty/tiny replacement
            if (origLen > 40 && sugLen <= 8) continue;
            if (origLen > 25 && sugLen == 0) continue;

            // Reject suggestions where result mapping is disproportionately large
            // (typically from diff misalignment caused by AI hallucination / repetition loops)
            if (sugLen > origLen * 5 + 30) continue;

            suggestions.Add(new AnalysisSuggestion
            {
                StartOffset = wStart,
                EndOffset = wEnd,
                OriginalText = origWord,
                SuggestedText = sugWord,
                Reason = "Proofread",
                ContextBefore = normOrig[Math.Max(0, wStart - 50)..wStart],
                ContextAfter = normOrig[wEnd..Math.Min(normOrig.Length, wEnd + 50)]
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

            if (deleteEnd < origPos)
            {
                delta += block.InsertCountB - block.DeleteCountA;
            }
            else if (block.DeleteStartA < origPos)
            {
                // origPos is inside a deleted range. This can happen when we later split
                // merged ranges by words. Approximate the mapping by interpolating within
                // the inserted span, so multiple split points inside the same deleted range
                // don't all collapse to the exact same result position.
                if (block.DeleteCountA <= 0)
                {
                    return block.InsertStartB;
                }

                var clamped = Math.Max(block.DeleteStartA, Math.Min(origPos, deleteEnd));
                var rel = (double)(clamped - block.DeleteStartA) / block.DeleteCountA;
                var mappedWithin = (int)Math.Round(rel * block.InsertCountB);
                return block.InsertStartB + mappedWithin;
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

            // A range can read as multiple ORIGINAL words only because a space between them was
            // DELETED (e.g. "ל הראות" → "להראות"): the correction joins two words into one. Splitting
            // such a range into per-word sub-ranges destroys the edit - each half then maps to an
            // unchanged word and the join is lost. If every diff block touching this range deletes (or
            // inserts) only whitespace, the multi-word count is an artifact of the join; keep the range
            // whole so the join surfaces as one suggestion.
            if (IsWhitespaceOnlyEdit(diffBlocks, normOrig, mStart, mEnd))
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

    /// <summary>
    /// True when every diff block intersecting the original range [start, end) deletes only
    /// whitespace characters (and at least one such block exists). This identifies a word-join
    /// correction (a space was removed between words) that must not be split into per-word ranges.
    /// Pure insertions (DeleteCountA == 0) are ignored here: a join is driven by the whitespace the
    /// original loses, and an insertion alone never inflates the original word count.
    /// </summary>
    private static bool IsWhitespaceOnlyEdit(
        IList<DiffPlex.Model.DiffBlock> diffBlocks, string normOrig, int start, int end)
    {
        var sawDeletion = false;
        foreach (var block in diffBlocks)
        {
            if (block.DeleteCountA == 0)
                continue;
            var s = block.DeleteStartA;
            var e = s + block.DeleteCountA;
            if (e <= start || s >= end) continue; // block does not touch this range

            for (var i = s; i < e && i < normOrig.Length; i++)
            {
                if (!char.IsWhiteSpace(normOrig[i]))
                    return false; // a non-whitespace deletion → not a pure word-join
            }
            sawDeletion = true;
        }
        return sawDeletion;
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

            // If we've reached maxWordsPerSegment, close this segment at the end of the last word.
            if (wordCountInSeg >= maxWordsPerSegment)
            {
                var segEnd = wordEnd;
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
                // Unable to map this suggestion back into the normalized document text; skip it
                // so we don't persist a suggestion with invalid or misleading offsets.
                continue;
            }

            var startOffset = idx;
            var endOffset = idx + normalizedOriginal.Length;
            searchStart = endOffset;

            suggestions.Add(new AnalysisSuggestion
            {
                StartOffset = startOffset,
                EndOffset = endOffset,
                OriginalText = original,
                SuggestedText = suggested,
                Reason = reason,
                Category = category,
                ContextBefore = normalizedDocument[Math.Max(0, startOffset - 50)..startOffset],
                ContextAfter = normalizedDocument[endOffset..Math.Min(normalizedDocument.Length, endOffset + 50)]
            });
        }

        return suggestions;
    }

    /// <summary>
    /// Compute navigate-only suggestions for a structured ConsistencyAnalysis result by mapping each
    /// issue's span back into the normalized document text via IndexOf.
    ///
    /// Anchoring rule: a consistency issue is only surfaced when its span is located in the analyzed
    /// text. Matched spans get concrete StartOffset/EndOffset (in normalized space) plus context slices.
    /// Out-of-target spans (e.g. quoted from PRECEDING/FOLLOWING context) and blank spans are dropped
    /// (skipped) because they are not navigable in this unit - exactly like ComputeLineEditSuggestions
    /// skips unmatched spans. No null-offset fallback item is emitted.
    ///
    /// Ordering: consistency issues arrive in SIGNIFICANCE order (the prompt asks for "the most
    /// significant issues"), NOT document order. So each issue is located independently from offset 0
    /// rather than from a monotonic cursor; this prevents an earlier-emitted issue whose span sits late
    /// in the chapter from hiding a later-emitted issue whose span sits earlier. To keep genuinely
    /// repeated identical spans distinct, a list of already-consumed (start,end) ranges is tracked and
    /// each issue PREFERS the first occurrence of its span that does not overlap an already-claimed
    /// range. When no distinct occurrence is free but the span IS present in the text - which happens
    /// routinely because different issue types (tense vs POV/register) quote the same or overlapping
    /// 8-15 word window - the issue falls back to the first occurrence rather than being dropped, so a
    /// valid in-text anchor is never discarded just because another issue claimed the same passage.
    /// Only spans absent from the analyzed text (e.g. quoted from PRECEDING/FOLLOWING context) are dropped.
    /// </summary>
    public List<AnalysisSuggestion> ComputeConsistencyIssueSuggestions(
        IReadOnlyList<ConsistencyIssue> issues,
        string inputText)
    {
        var suggestions = new List<AnalysisSuggestion>();
        if (issues == null || issues.Count == 0)
            return suggestions;

        var normalizedDocument = TextNormalization.NormalizeTextForAnalysis(inputText);
        // Track ranges already claimed by earlier issues so two issues sharing identical span text map
        // to the first then the second occurrence (preserving the RepeatedPhrase behavior).
        var consumed = new List<(int Start, int End)>();

        foreach (var issue in issues)
        {
            var rawSpan = issue.Span ?? string.Empty;
            var normalizedSpan = TextNormalization.NormalizeTextForAnalysis(rawSpan);
            var category = "consistency-" + (issue.Type ?? string.Empty).Trim().ToLowerInvariant();
            var reason = issue.Description;

            if (string.IsNullOrWhiteSpace(normalizedSpan))
            {
                // No span text at all - not navigable, so drop the issue (no fallback item).
                continue;
            }

            // Scan every occurrence of the span from offset 0 (independent of issue emission order).
            // Prefer the first occurrence whose [start,end) range does not overlap a range already
            // claimed by an earlier issue, so genuinely repeated identical phrases map to successive
            // positions. Remember the very first occurrence regardless of overlap: it is the fallback
            // anchor when no distinct occurrence is free.
            var firstStart = -1;
            var startOffset = -1;
            var fromIndex = 0;
            while (true)
            {
                var idx = normalizedDocument.IndexOf(normalizedSpan, fromIndex, StringComparison.Ordinal);
                if (idx < 0)
                    break;

                var candidateStart = idx;
                var candidateEnd = idx + normalizedSpan.Length;

                if (firstStart < 0)
                    firstStart = candidateStart;

                var overlaps = false;
                foreach (var (cStart, cEnd) in consumed)
                {
                    if (candidateStart < cEnd && cStart < candidateEnd)
                    {
                        overlaps = true;
                        break;
                    }
                }

                if (!overlaps)
                {
                    startOffset = candidateStart;
                    break;
                }

                // This occurrence is already claimed; advance one character past its start to find the
                // next occurrence (handles overlapping repeats too).
                fromIndex = idx + 1;
            }

            if (firstStart < 0)
            {
                // Span is not an exact substring of the analyzed text. Before dropping, try a CONSERVATIVE
                // near-match: the model frequently mis-quotes a span by a 1-2 char morphological slip
                // (e.g. Hebrew present "ועוצר" written as past "ועצר"), which would otherwise lose a real,
                // detected issue. We accept a near-match ONLY when (1) the span shares a verbatim multi-word
                // run covering >= 25% of its length with the body, (2) the best window is within a hard cap
                // of 4 edits, and (3) the window is unambiguous. Anything looser is dropped so a span quoted
                // from PRECEDING/FOLLOWING context (genuinely absent here) cannot falsely anchor into the body.
                var nearMatch = FindUniqueNearMatch(normalizedDocument, normalizedSpan);
                if (nearMatch is null)
                {
                    // No exact match and no safe near-match - the span is out of target (e.g. quoted from
                    // PRECEDING/FOLLOWING context) and therefore not navigable in this unit, so drop it.
                    continue;
                }

                var (nearStart, nearEnd) = nearMatch.Value;

                // Snap the near-match window to whole-word boundaries. The fallback uses a FIXED-LENGTH
                // window, but a mis-quote that differs in LENGTH (an inserted/deleted char, e.g. Hebrew
                // "ועצר" for the document's "ועוצר") makes that window off by the length delta at one end -
                // so it can start or end MID-WORD (e.g. highlighting "וא" instead of "הוא", shaving the
                // leading "ה"). Expand the window outward over any clipped word so the highlight is always
                // clean, navigable whole words (the snap stops at whitespace/punctuation, so it completes
                // the partial word without swallowing a following separate word or adjacent punctuation).
                // Scoped to the near-match fallback ONLY: the exact-match path is already verbatim/word-aligned.
                (nearStart, nearEnd) = SnapToWordBoundaries(normalizedDocument, nearStart, nearEnd);

                // CONTRACT (intentional asymmetry with the exact-match path above): the near-match path
                // does NOT consult `consumed` to disambiguate overlapping occurrences. Two reasons make a
                // consumed-aware near-match path unnecessary here:
                //  1. FindUniqueNearMatch already DROPS any span with two non-overlapping plausible windows
                //     (bestIsTied -> null). So the only window it ever returns is the single unambiguous
                //     one; there is no "second free occurrence" to skip to even if we wanted one.
                //  2. Per f5e1391, distinct issues that legitimately share one span are BOTH kept, anchored
                //     to that shared window - overlap is ACCEPTABLE, not a bug to de-dup. The exact path
                //     encodes the same rule via its first-occurrence fallback; the near path reaches the
                //     same outcome simply by anchoring to its one unique window.
                // We still record this window in `consumed` so a LATER exact-match issue can prefer a
                // different occurrence around it (one-directional: exact reads consumed, near only writes).
                consumed.Add((nearStart, nearEnd));

                // Anchor to the REAL document text of the (snapped) window, not the model's mis-quoted span,
                // so the highlight covers actual, navigable whole-word text.
                var nearText = normalizedDocument[nearStart..nearEnd];
                suggestions.Add(new AnalysisSuggestion
                {
                    StartOffset = nearStart,
                    EndOffset = nearEnd,
                    OriginalText = nearText,
                    SuggestedText = string.Empty,
                    Reason = reason,
                    Category = category,
                    ContextBefore = normalizedDocument[Math.Max(0, nearStart - 50)..nearStart],
                    ContextAfter = normalizedDocument[nearEnd..Math.Min(normalizedDocument.Length, nearEnd + 50)]
                });
                continue;
            }

            // No distinct (non-overlapping) occurrence was free, but the span IS in the text. This is the
            // common case where different issue types (tense vs POV/register) quote the same or
            // overlapping passage and it appears only once. Fall back to the first occurrence so every
            // issue with a valid in-text anchor is kept rather than silently dropped.
            if (startOffset < 0)
                startOffset = firstStart;

            var endOffset = startOffset + normalizedSpan.Length;

            consumed.Add((startOffset, endOffset));

            suggestions.Add(new AnalysisSuggestion
            {
                StartOffset = startOffset,
                EndOffset = endOffset,
                OriginalText = rawSpan,
                SuggestedText = string.Empty,
                Reason = reason,
                Category = category,
                ContextBefore = normalizedDocument[Math.Max(0, startOffset - 50)..startOffset],
                ContextAfter = normalizedDocument[endOffset..Math.Min(normalizedDocument.Length, endOffset + 50)]
            });
        }

        return suggestions;
    }

    /// <summary>
    /// Conservative near-match anchor for a consistency span that is NOT an exact substring of the
    /// document. Returns the [start,end) of the single best-matching window in <paramref name="document"/>
    /// for <paramref name="span"/> ONLY when the match is safe:
    /// <list type="bullet">
    /// <item>(a) word-run anchored: the span shares a VERBATIM contiguous multi-word run with the document
    /// that covers at least <see cref="MinWordRunFractionOfSpan"/> of the span's length. A genuine
    /// morphological mis-quote leaves all but one word intact, so it keeps a long exact run; a span quoted
    /// from PRECEDING/FOLLOWING context overlaps the body (if at all) only in a short stopword run and is
    /// dropped here. This anchor also bounds the search to windows aligned on that run - there is no
    /// full-document scan - AND</item>
    /// <item>(b) near-miss within a HARD cap: the window's Levenshtein distance to the span is within
    /// min(ceil(0.12 * span.Length), <see cref="MaxNearMatchEditDistance"/>) - the cap keeps a long span
    /// from earning a 5-10 edit budget that a merely prose-similar window could satisfy; AND</item>
    /// <item>(c) unambiguous: no OTHER non-overlapping window is also within that threshold - if two
    /// windows are both plausible we DROP rather than guess.</item>
    /// </list>
    /// Returns null when no safe, unique near-match exists (e.g. a span quoted from surrounding context
    /// that is genuinely absent here), so such spans are still dropped.
    /// </summary>
    private static (int Start, int End)? FindUniqueNearMatch(string document, string span)
    {
        if (string.IsNullOrEmpty(span) || string.IsNullOrEmpty(document))
            return null;
        if (span.Length > document.Length)
            return null;

        // (a) Require a verbatim multi-word run shared with the document, covering a meaningful fraction of
        // the span. No run (or only a short stopword run) -> not a morphological mis-quote of any body
        // window -> drop. This is the primary guard against re-admitting out-of-unit context-bleed spans,
        // and it also gives us the candidate-window anchor (so we never scan every document offset).
        var fragment = LongestExactWordRunSubstring(document, span, out var fragmentOffsetInSpan);
        if (string.IsNullOrEmpty(fragment))
            return null;
        if (fragment.Length < MinWordRunFractionOfSpan * span.Length)
            return null;

        // (b) Hard-cap the edit budget. A real morphological slip is 1-2 edits; the percentage threshold is
        // kept only as an UPPER bound so short spans are not over-budgeted, but it can never exceed the cap.
        var threshold = Math.Min(
            Math.Max(2, (int)Math.Ceiling(0.12 * span.Length)),
            MaxNearMatchEditDistance);

        // Candidate window starts, aligned so the exact word-run fragment lands where it sits in the span.
        var candidateStarts = CollectNearMatchCandidateStarts(document, fragment, fragmentOffsetInSpan);

        // For tolerance to small length differences (an inserted/deleted char shifts the true end),
        // test a few window lengths around the span length at each candidate start.
        var bestStart = -1;
        var bestEnd = -1;
        var bestDistance = int.MaxValue;
        var bestIsTied = false;

        foreach (var start in candidateStarts)
        {
            for (var len = span.Length - threshold; len <= span.Length + threshold; len++)
            {
                if (len <= 0)
                    continue;
                if (start + len > document.Length)
                    continue;

                var window = document.Substring(start, len);
                var distance = BoundedLevenshtein(window, span, threshold);
                if (distance > threshold)
                    continue;

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestStart = start;
                    bestEnd = start + len;
                    bestIsTied = false;
                }
                else if (distance == bestDistance && bestStart >= 0)
                {
                    // Another window ties the current best. Overlapping windows (same region, slightly
                    // different length) are the SAME match; a non-overlapping tie is a true ambiguity.
                    var overlaps = start < bestEnd && bestStart < start + len;
                    if (!overlaps)
                        bestIsTied = true;
                }
            }
        }

        if (bestStart < 0)
            return null; // no window within the tight threshold

        if (bestIsTied)
            return null; // ambiguous - two non-overlapping windows equally good; do not guess

        return (bestStart, bestEnd);
    }

    /// <summary>
    /// Expand a [start,end) window so neither end clips a word in <paramref name="text"/>: move the start
    /// back over any partial word it begins inside, and the end forward over any partial word it ends
    /// inside. "Word" here means a run of word characters (letters/digits); the scan STOPS at whitespace
    /// OR punctuation. Used to clean up a near-match fallback window that, because it is fixed-length while
    /// the mis-quote differs in length (insertion/deletion), can land mid-word at one end (e.g. clipping a
    /// leading "ה" so "הוא" becomes "וא"). Stopping at punctuation - not just whitespace - means we only
    /// complete the clipped word: we never swallow a following SEPARATE word, and we never absorb adjacent
    /// punctuation that the window legitimately stopped before (e.g. a trailing comma).
    /// </summary>
    private static (int Start, int End) SnapToWordBoundaries(string text, int start, int end)
    {
        if (string.IsNullOrEmpty(text))
            return (start, end);

        // Clamp into range first so the scan below is safe.
        start = Math.Max(0, Math.Min(start, text.Length));
        end = Math.Max(start, Math.Min(end, text.Length));

        // Move start BACK only while it sits inside a word (the char just before it is a word char).
        while (start > 0 && IsWordChar(text[start - 1]))
            start--;

        // Move end FORWARD only while it sits inside a word (the char at it is a word char).
        while (end < text.Length && IsWordChar(text[end]))
            end++;

        return (start, end);
    }

    /// <summary>
    /// Build the set of candidate window start offsets for the near-match search by aligning a window to
    /// every occurrence of the distinctive exact word-run <paramref name="fragment"/> in the document, so
    /// that fragment lands where it sits inside the span (<paramref name="fragmentOffsetInSpan"/>). The
    /// caller (<see cref="FindUniqueNearMatch"/>) only invokes this once a non-empty, sufficiently long
    /// fragment is established, so there is no full-document fallback scan: a span with no distinctive
    /// shared run is dropped before reaching here.
    /// </summary>
    private static IEnumerable<int> CollectNearMatchCandidateStarts(
        string document, string fragment, int fragmentOffsetInSpan)
    {
        var starts = new HashSet<int>();
        if (string.IsNullOrEmpty(fragment))
            return starts;

        var from = 0;
        while (true)
        {
            var idx = document.IndexOf(fragment, from, StringComparison.Ordinal);
            if (idx < 0)
                break;
            // Align the window so the fragment sits where it does inside the span.
            var windowStart = idx - fragmentOffsetInSpan;
            if (windowStart >= 0 && windowStart < document.Length)
                starts.Add(windowStart);
            from = idx + 1;
        }

        return starts;
    }

    /// <summary>
    /// Find the longest contiguous run of whole words in <paramref name="span"/> that appears verbatim
    /// in <paramref name="document"/>. Returns that fragment and its character offset within the span,
    /// or empty when no multi-character word-run from the span is a document substring.
    /// </summary>
    private static string LongestExactWordRunSubstring(string document, string span, out int offsetInSpan)
    {
        offsetInSpan = 0;
        var words = SplitWordsWithOffsets(span);
        if (words.Count == 0)
            return string.Empty;

        var best = string.Empty;
        // Try every contiguous word-run, longest first, and keep the first (longest) that is a substring.
        for (var startWord = 0; startWord < words.Count; startWord++)
        {
            for (var endWord = words.Count - 1; endWord >= startWord; endWord--)
            {
                var startChar = words[startWord].Offset;
                var endChar = words[endWord].Offset + words[endWord].Length;
                var candidate = span.Substring(startChar, endChar - startChar);
                if (candidate.Length <= best.Length)
                    continue; // cannot beat current best
                if (document.IndexOf(candidate, StringComparison.Ordinal) >= 0)
                {
                    best = candidate;
                    offsetInSpan = startChar;
                }
            }
        }

        return best;
    }

    private static List<(int Offset, int Length)> SplitWordsWithOffsets(string s)
    {
        var words = new List<(int Offset, int Length)>();
        var i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i >= s.Length) break;
            var start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
            words.Add((start, i - start));
        }
        return words;
    }

    /// <summary>
    /// Levenshtein edit distance between <paramref name="a"/> and <paramref name="b"/>, short-circuiting
    /// to <c>threshold + 1</c> as soon as the whole row exceeds the threshold (so far-apart strings cost
    /// little). Used by the near-match anchor to reject anything outside the tight threshold cheaply.
    /// </summary>
    private static int BoundedLevenshtein(string a, string b, int threshold)
    {
        if (Math.Abs(a.Length - b.Length) > threshold)
            return threshold + 1;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            var rowMin = curr[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + cost);
                if (curr[j] < rowMin) rowMin = curr[j];
            }
            if (rowMin > threshold)
                return threshold + 1; // every alignment already exceeds the threshold

            (prev, curr) = (curr, prev);
        }

        return prev[b.Length];
    }

    private static bool IsMeaningfulSuggestion(AnalysisSuggestion s)
    {
        if (string.Equals(s.OriginalText, s.SuggestedText, StringComparison.Ordinal))
            return false;

        var orig = s.OriginalText ?? string.Empty;
        var sug = s.SuggestedText ?? string.Empty;
        var o = orig.Trim();
        var g = sug.Trim();
        if (string.Equals(o, g, StringComparison.Ordinal))
        {
            // Trimming makes them equal, so the ONLY change is leading/trailing whitespace on the
            // span. That is usually a span-boundary artifact (drop it) - UNLESS removing that
            // whitespace genuinely changes adjacency, i.e. it sat between this token and a
            // neighbouring non-whitespace character (e.g. a stray space before a period:
            // "מסמיקה ." → "מסמיקה."). In that case it is a real punctuation/spacing correction.
            var droppedTrailing = orig.Length > sug.Length && EndsWithWhitespace(orig) && !EndsWithWhitespace(sug);
            var droppedLeading = orig.Length > sug.Length && StartsWithWhitespace(orig) && !StartsWithWhitespace(sug);

            var contextAfter = s.ContextAfter ?? string.Empty;
            var contextBefore = s.ContextBefore ?? string.Empty;
            var realTrailingFix = droppedTrailing && contextAfter.Length > 0 && !char.IsWhiteSpace(contextAfter[0]);
            var realLeadingFix = droppedLeading && contextBefore.Length > 0 && !char.IsWhiteSpace(contextBefore[^1]);

            return realTrailingFix || realLeadingFix;
        }

        return true;
    }

    private static bool EndsWithWhitespace(string s) => s.Length > 0 && char.IsWhiteSpace(s[^1]);
    private static bool StartsWithWhitespace(string s) => s.Length > 0 && char.IsWhiteSpace(s[0]);

    private static bool IsWordChar(char c) =>
        char.IsLetterOrDigit(c);

}
