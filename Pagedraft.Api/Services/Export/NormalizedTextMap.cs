using System;
using System.Collections.Generic;
using System.Text;

namespace Pagedraft.Api.Services.Export;

/// <summary>
/// A NORMALIZED view of a raw string with a map from every normalized character back to the raw
/// characters it stands for. It exists so a suggestion, whose <c>OriginalText</c>, contexts and offsets
/// live on the normalized axis of <c>AnalysisResult.SourceTextSnapshot</c>, can be located in raw
/// document text (a DOCX's <c>w:t</c> nodes) and the hit translated back to raw positions.
///
/// <para>THE RULES ARE A MIRROR, NOT A NEW DEFINITION. The snapshot is produced as
/// <c>TextNormalization.NormalizeTextForAnalysis(SyncfusionWatermarkStripper.StripSyncfusionWatermark(contentText))</c>,
/// so this applies, in this order: (1) every run of <c>\r</c>/<c>\n</c> becomes one <c>\n</c>, (2) every run
/// of spaces/tabs becomes one space, (3) <c>string.Trim()</c> at both ends (<c>char.IsWhiteSpace</c>),
/// (4) bidi controls (U+200E, U+200F, U+202A-U+202E, U+2066-U+2069) are dropped and each remaining line
/// break becomes a space. The order matters: a bidi control between two spaces survives step 2, so
/// <c>"a ‏ b"</c> normalizes to <c>"a  b"</c> (two spaces) on the server too. The watermark removal of
/// the stripper is NOT mirrored: a DOCX the API exports carries no trial watermark text, and removing
/// text would make the map lie about what the reader sees. The parity is pinned by
/// <c>NormalizedTextMapTests</c> against the two production functions themselves.</para>
///
/// <para>Each normalized character maps to a half-open raw range: one raw character for an ordinary
/// character, the whole collapsed run for a collapsed space or line break. Dropped bidi controls belong to
/// no normalized character; a raw span built from two normalized ends therefore includes any bidi control
/// that sits strictly inside it.</para>
/// </summary>
internal sealed class NormalizedTextMap
{
    private readonly int[] _rawStart;
    private readonly int[] _rawEnd;

    private NormalizedTextMap(string text, int[] rawStart, int[] rawEnd, int rawLength)
    {
        Text = text;
        _rawStart = rawStart;
        _rawEnd = rawEnd;
        RawLength = rawLength;
    }

    /// <summary>The normalized text, on the same axis as <c>AnalysisResult.SourceTextSnapshot</c>.</summary>
    public string Text { get; }

    /// <summary>Length of the raw string the map was built from.</summary>
    public int RawLength { get; }

    /// <summary>First raw index the normalized character at <paramref name="normalizedIndex"/> stands for.</summary>
    public int RawStartOf(int normalizedIndex) => _rawStart[normalizedIndex];

    /// <summary>Raw index one past the last raw character the normalized character stands for.</summary>
    public int RawEndOf(int normalizedIndex) => _rawEnd[normalizedIndex];

    /// <summary>
    /// The half-open raw span covering the normalized span <c>[normalizedStart, normalizedStart + length)</c>.
    /// <paramref name="length"/> must be at least 1; a zero-length position is
    /// <see cref="ToRawInsertionPoint"/>.
    /// </summary>
    public (int Start, int End) ToRawSpan(int normalizedStart, int length)
    {
        if (length < 1) throw new ArgumentOutOfRangeException(nameof(length), "A span has at least one character.");
        if (normalizedStart < 0 || normalizedStart + length > Text.Length)
            throw new ArgumentOutOfRangeException(nameof(normalizedStart));
        return (_rawStart[normalizedStart], _rawEnd[normalizedStart + length - 1]);
    }

    /// <summary>
    /// The raw position of the gap BEFORE normalized index <paramref name="normalizedIndex"/> (0..Text.Length).
    /// The gap sticks to the preceding character (right after its raw range), because an insertion is
    /// anchored by the text before it; at index 0 it is the raw start of the first character.
    /// </summary>
    public int ToRawInsertionPoint(int normalizedIndex)
    {
        if (normalizedIndex < 0 || normalizedIndex > Text.Length)
            throw new ArgumentOutOfRangeException(nameof(normalizedIndex));
        if (Text.Length == 0) return 0;
        return normalizedIndex == 0 ? _rawStart[0] : _rawEnd[normalizedIndex - 1];
    }

    /// <summary>Builds the normalized view of <paramref name="raw"/> and its index map.</summary>
    public static NormalizedTextMap Build(string? raw)
    {
        raw ??= string.Empty;
        var items = new List<Item>(raw.Length);

        // Step 1: [\r\n]+ -> "\n" (SyncfusionWatermarkStripper).
        for (var i = 0; i < raw.Length;)
        {
            var ch = raw[i];
            if (ch == '\r' || ch == '\n')
            {
                var j = i;
                while (j < raw.Length && (raw[j] == '\r' || raw[j] == '\n')) j++;
                items.Add(new Item('\n', i, j));
                i = j;
                continue;
            }
            items.Add(new Item(ch, i, i + 1));
            i++;
        }

        // Step 2: [ \t]+ -> " " (SyncfusionWatermarkStripper).
        var collapsed = new List<Item>(items.Count);
        foreach (var item in items)
        {
            if (IsSpaceOrTab(item.Ch) && collapsed.Count > 0 && collapsed[^1].IsCollapsedSpace)
            {
                collapsed[^1] = collapsed[^1] with { End = item.End };
                continue;
            }
            collapsed.Add(IsSpaceOrTab(item.Ch) ? new Item(' ', item.Start, item.End, IsCollapsedSpace: true) : item);
        }

        // Step 3: Trim() (SyncfusionWatermarkStripper) - char.IsWhiteSpace at both ends.
        var first = 0;
        var last = collapsed.Count - 1;
        while (first <= last && char.IsWhiteSpace(collapsed[first].Ch)) first++;
        while (last >= first && char.IsWhiteSpace(collapsed[last].Ch)) last--;

        // Step 4: drop bidi controls, line break -> space (TextNormalization.NormalizeTextForAnalysis).
        var text = new StringBuilder(Math.Max(0, last - first + 1));
        var starts = new List<int>(text.Capacity);
        var ends = new List<int>(text.Capacity);
        for (var k = first; k <= last; k++)
        {
            var item = collapsed[k];
            if (IsBidiControl(item.Ch)) continue;
            text.Append(item.Ch == '\n' || item.Ch == '\r' ? ' ' : item.Ch);
            starts.Add(item.Start);
            ends.Add(item.End);
        }

        return new NormalizedTextMap(text.ToString(), starts.ToArray(), ends.ToArray(), raw.Length);
    }

    private static bool IsSpaceOrTab(char ch) => ch == ' ' || ch == '\t';

    /// <summary>Same set as <c>TextNormalization.IsBidiControl</c> (private there).</summary>
    internal static bool IsBidiControl(char ch)
    {
        var code = (int)ch;
        return code == 0x200E
               || code == 0x200F
               || (code >= 0x202A && code <= 0x202E)
               || (code >= 0x2066 && code <= 0x2069);
    }

    private readonly record struct Item(char Ch, int Start, int End, bool IsCollapsedSpace = false);
}
