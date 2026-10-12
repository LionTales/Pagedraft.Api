using System.Text;
using DiffPlex;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// Reassembles a chunked proofread run's per-chunk model outputs into ONE corrected chapter text that equals the
/// source wherever the model changed nothing (plan whole-book-proofread-and-annotated-docx-2026-10-11, c17).
///
/// <para>WHY. <c>UnifiedAnalysisService.BuildChunkSegmentsCore</c> trims every paragraph, sentence and chunk and
/// joins the pieces with a canonical separator (<c>"\n+"</c> between paragraphs, one space between sentences and
/// word-count splits). That is what the model is sent, and it must stay so. The old merge then glued the model
/// outputs back together with those separators only, so every whitespace the chunker had dropped (a paragraph's
/// trailing space or NO-BREAK SPACE, a double space after a full stop, a lone-space line, the CR of a CRLF) came
/// back as a whitespace-only DELETION in <c>SuggestionDiffService</c>: 4,007 of the 4,676 stored suggestions of
/// one 55-chapter book (c15, reproduced with an identity model), plus 37 real edits fused with that whitespace.</para>
///
/// <para>HOW. The chunker only ever rewrites WHITESPACE, so each chunk's non-whitespace characters are, in order,
/// a contiguous run of the source's. That gives an exact map from every chunk position to the source. Each chunk
/// output is diffed against the chunk it came from (the model's own edits), and those edits are replayed onto the
/// SOURCE: text the model left alone is copied from the source verbatim, whitespace included, and only the
/// edited spans carry the model's text. An edit that touches one of the chunker's whitespace runs replaces the
/// source's whole run (the model saw the canonical run, so its version of that run is the one to keep); a pure
/// insertion BESIDE a run leaves the run alone. A change that only RE-SPACES a run (whitespace for whitespace, the
/// words still apart: what <c>SanitizeResponse</c> does to every reply, NO-BREAK SPACE to space, 3+ line breaks to
/// 2) is not an edit, so the source's run stays. The text between chunks, before the first and after the last, is
/// the source's own. An identity model therefore reproduces the source byte for byte, with or without the
/// sanitizer.</para>
///
/// <para>MEASURED (2026-10-12, the 55 chapters of the book c15 measured, no GPU): identity-model replay 4,046
/// whitespace-only suggestions before, 0 after, merged text == source on 55/55; the stored ResultTexts split back
/// into their 458 per-chunk model outputs (each output's length checked against the run log) and re-merged: 4,009
/// whitespace-only rows before, 2 after (both the model's own deletion of a space before a closing quote), the
/// real edits identical in content on 55/55 chapters. What is SENT to the model is unchanged (per-chunk prompt
/// hashes identical before and after).</para>
///
/// <para>FAIL-SAFE. If a chunk's non-whitespace characters are not the source's (the chunker changed, or the
/// caller passed something else), the old separator-join merge is returned with
/// <see cref="Result.AlignedToSource"/> false, so a run is never lost to this step; the caller logs it.</para>
///
/// <para>Whitespace is <see cref="char.IsWhiteSpace(char)"/>, the set <see cref="string.Trim()"/> and the
/// chunker's <c>\s</c> both use.</para>
/// </summary>
internal static class ProofreadChunkMerge
{
    /// <param name="Text">The corrected chapter text.</param>
    /// <param name="AlignedToSource">False when the fail-safe separator-join merge was used.</param>
    internal readonly record struct Result(string Text, bool AlignedToSource);

    /// <param name="source">The exact text the chunks were cut from (the chunker's input).</param>
    /// <param name="chunkTexts">Each chunk's text as SENT to the model.</param>
    /// <param name="separatorsAfter">Each chunk's separator, used only by the fail-safe merge.</param>
    /// <param name="corrected">Each chunk's corrected text (the chunk text itself when the model's output was not used).</param>
    internal static Result Merge(
        string source,
        IReadOnlyList<string> chunkTexts,
        IReadOnlyList<string> separatorsAfter,
        IReadOnlyList<string> corrected)
    {
        if (chunkTexts.Count != corrected.Count || chunkTexts.Count != separatorsAfter.Count)
            throw new ArgumentException("chunkTexts, separatorsAfter and corrected must have the same length.");

        var maps = TryMapChunksToSource(source ?? "", chunkTexts);
        if (maps is null)
            return new Result(SeparatorJoin(chunkTexts, separatorsAfter, corrected), AlignedToSource: false);

        var sb = new StringBuilder(source!.Length + 64);
        var cursor = 0;
        for (var i = 0; i < chunkTexts.Count; i++)
        {
            var chunk = chunkTexts[i];
            var output = corrected[i] ?? "";
            var map = maps[i];
            if (map.Length == 0 || !HasNonWhitespace(chunk))
            {
                // A chunk with no text at all: nothing of the source belongs to it. Keep any non-blank output.
                if (!string.IsNullOrWhiteSpace(output) && output != chunk) sb.Append(output);
                continue;
            }

            var chunkStartInSource = Boundary(chunk, map, 0);
            sb.Append(source, cursor, chunkStartInSource - cursor);
            AppendChunk(sb, source, chunk, output, map);
            cursor = Boundary(chunk, map, chunk.Length);
        }
        sb.Append(source, cursor, source.Length - cursor);
        return new Result(sb.ToString(), AlignedToSource: true);
    }

    /// <summary>The pre-c17 merge: the outputs joined by the chunker's separators. Kept as the fail-safe only.</summary>
    internal static string SeparatorJoin(
        IReadOnlyList<string> chunkTexts, IReadOnlyList<string> separatorsAfter, IReadOnlyList<string> corrected)
    {
        var merged = new StringBuilder();
        for (var i = 0; i < chunkTexts.Count; i++)
        {
            merged.Append(corrected[i]);
            if (i < chunkTexts.Count - 1 && !string.IsNullOrEmpty(separatorsAfter[i]))
                merged.Append(separatorsAfter[i]);
        }
        return merged.ToString();
    }

    /// <summary>
    /// For each chunk, an array the length of the chunk: the source index of every non-whitespace character, -1 for
    /// whitespace. Null when the chunks are not the source's non-whitespace characters in order, or when a chunk
    /// starts or ends with whitespace, or when a chunk's whitespace runs do not line up one-to-one with the source's
    /// (a run in the chunk must sit where the source has whitespace, and two touching characters in the chunk must
    /// touch in the source).
    /// </summary>
    private static int[][]? TryMapChunksToSource(string source, IReadOnlyList<string> chunkTexts)
    {
        var maps = new int[chunkTexts.Count][];
        var k = 0;
        for (var i = 0; i < chunkTexts.Count; i++)
        {
            var chunk = chunkTexts[i] ?? "";
            var map = new int[chunk.Length];
            var prev = -1;
            var wsSincePrev = false;
            for (var j = 0; j < chunk.Length; j++)
            {
                var c = chunk[j];
                if (char.IsWhiteSpace(c))
                {
                    if (prev < 0) return null; // a chunk never starts with whitespace (the chunker trims)
                    map[j] = -1;
                    wsSincePrev = true;
                    continue;
                }
                while (k < source.Length && char.IsWhiteSpace(source[k])) k++;
                if (k >= source.Length || source[k] != c) return null;
                if (prev >= 0)
                {
                    var touching = k == map[prev] + 1;
                    if (touching == wsSincePrev) return null;
                }
                map[j] = k;
                prev = j;
                wsSincePrev = false;
                k++;
            }
            if (wsSincePrev) return null; // a chunk never ends with whitespace
            maps[i] = map;
        }
        for (; k < source.Length; k++)
            if (!char.IsWhiteSpace(source[k])) return null; // source text no chunk carries
        return maps;
    }

    /// <summary>
    /// The source position of chunk boundary <paramref name="p"/> (0..chunk.Length). Right after a non-whitespace
    /// character it is right after that character in the source, so a whitespace run that follows belongs to what
    /// comes after p; at the end of a whitespace run it is the next character's source position. A boundary strictly
    /// inside a run never reaches here (edits are widened to whole runs).
    /// </summary>
    private static int Boundary(string chunk, int[] map, int p)
    {
        if (p == 0) return map[0];
        if (!char.IsWhiteSpace(chunk[p - 1])) return map[p - 1] + 1;
        return map[p]; // end of a whitespace run (a chunk never ends with whitespace, so p < chunk.Length)
    }

    private static void AppendChunk(StringBuilder sb, string source, string chunk, string output, int[] map)
    {
        if (string.Equals(chunk, output, StringComparison.Ordinal))
        {
            var from = Boundary(chunk, map, 0);
            sb.Append(source, from, Boundary(chunk, map, chunk.Length) - from);
            return;
        }

        var blocks = new Differ().CreateCharacterDiffs(chunk, output, ignoreWhitespace: false, ignoreCase: false).DiffBlocks
            .OrderBy(b => b.DeleteStartA).ToList();
        var respaced = RespacedRunBlocks(chunk, output, blocks);
        var edits = new List<(int X, int Y, int FirstA, int FirstB, int LastAEnd, int LastBEnd)>();
        foreach (var b in blocks)
        {
            if (respaced.Contains(b)) continue;
            var a = b.DeleteStartA;
            var aEnd = a + b.DeleteCountA;
            var x = a;
            var y = aEnd;
            if (b.DeleteCountA > 0)
            {
                // Touching a chunker whitespace run: widen to the whole run, both ends.
                if (char.IsWhiteSpace(chunk[x])) while (x > 0 && char.IsWhiteSpace(chunk[x - 1])) x--;
                if (char.IsWhiteSpace(chunk[y - 1])) while (y < chunk.Length && char.IsWhiteSpace(chunk[y])) y++;
            }
            else if (a > 0 && a < chunk.Length && char.IsWhiteSpace(chunk[a - 1]) && char.IsWhiteSpace(chunk[a]))
            {
                // An insertion strictly inside a run: the run is the model's now.
                while (x > 0 && char.IsWhiteSpace(chunk[x - 1])) x--;
                while (y < chunk.Length && char.IsWhiteSpace(chunk[y])) y++;
            }

            var edit = (X: x, Y: y, FirstA: a, FirstB: b.InsertStartB, LastAEnd: aEnd, LastBEnd: b.InsertStartB + b.InsertCountB);
            if (edits.Count > 0 && edit.X < edits[^1].Y)
            {
                var prev = edits[^1];
                edits[^1] = (prev.X, Math.Max(prev.Y, edit.Y), prev.FirstA, prev.FirstB, edit.LastAEnd, edit.LastBEnd);
            }
            else
            {
                edits.Add(edit);
            }
        }

        var p = 0;
        foreach (var e in edits)
        {
            // Unchanged chunk text before the edit: the source's own characters, whitespace included.
            var from = Boundary(chunk, map, p);
            var to = e.X == p ? from : Boundary(chunk, map, e.X);
            sb.Append(source, from, to - from);
            // The edit: the chunk's own run text around the model's change, then the model's text.
            sb.Append(chunk, e.X, e.FirstA - e.X);
            sb.Append(output, e.FirstB, e.LastBEnd - e.FirstB);
            sb.Append(chunk, e.LastAEnd, e.Y - e.LastAEnd);
            p = e.Y;
        }
        var tailFrom = Boundary(chunk, map, p);
        sb.Append(source, tailFrom, Boundary(chunk, map, chunk.Length) - tailFrom);
    }

    /// <summary>
    /// The diff blocks that only RE-SPACE a whitespace run of the chunk: every block touching the run swaps
    /// whitespace for whitespace, and the run is still there afterwards (the words on either side stay apart). Those
    /// are dropped, so the source's own run is kept. <c>SanitizeResponse</c> makes exactly these changes to every
    /// response (each horizontal run, NO-BREAK SPACE included, becomes one space; three or more line breaks become
    /// two), and a CR, a NO-BREAK SPACE or a double space the model normalizes is no proofreading edit an author can
    /// see once the client trims it. A run the output DELETES (two words joined) is kept as an edit, and so is a
    /// space inserted where the chunk had none (a word split).
    /// </summary>
    private static HashSet<DiffPlex.Model.DiffBlock> RespacedRunBlocks(
        string chunk, string output, IReadOnlyList<DiffPlex.Model.DiffBlock> blocks)
    {
        var result = new HashSet<DiffPlex.Model.DiffBlock>();
        if (blocks.Count == 0) return result;
        var i = 0;
        while (i < chunk.Length)
        {
            if (!char.IsWhiteSpace(chunk[i])) { i++; continue; }
            var rs = i;
            while (i < chunk.Length && char.IsWhiteSpace(chunk[i])) i++;
            var re = i;

            var touching = new List<DiffPlex.Model.DiffBlock>();
            foreach (var b in blocks)
            {
                if (b.DeleteStartA > re) break; // sorted by DeleteStartA
                var touches = b.DeleteCountA > 0
                    ? b.DeleteStartA < re && BlockEnd(b) > rs
                    : b.DeleteStartA >= rs && b.DeleteStartA <= re;
                if (touches) touching.Add(b);
            }
            if (touching.Count == 0) continue;
            var allWhitespace = touching.All(b =>
                IsAllWhitespace(chunk, b.DeleteStartA, b.DeleteCountA) && IsAllWhitespace(output, b.InsertStartB, b.InsertCountB));
            if (!allWhitespace) continue;
            var outputRunLength = (re - rs) + touching.Sum(b => b.InsertCountB - b.DeleteCountA);
            if (outputRunLength > 0)
                foreach (var b in touching) result.Add(b);
        }
        return result;

        static int BlockEnd(DiffPlex.Model.DiffBlock b) => b.DeleteStartA + b.DeleteCountA;
    }

    private static bool IsAllWhitespace(string s, int start, int count)
    {
        for (var k = start; k < start + count; k++)
            if (!char.IsWhiteSpace(s[k])) return false;
        return true;
    }

    private static bool HasNonWhitespace(string s)
    {
        foreach (var c in s)
            if (!char.IsWhiteSpace(c)) return true;
        return false;
    }
}
