using System.Linq;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Export;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// <see cref="NormalizedTextMap"/> must produce EXACTLY the text the server's proofread snapshot is built
/// from (<c>NormalizeTextForAnalysis(StripSyncfusionWatermark(raw))</c>), or a suggestion's
/// <c>OriginalText</c>/contexts stop matching. Fixtures are multi-paragraph and carry bidi controls on
/// purpose (memory pagedraft-suggestion-normalized-axis: single-line fixtures cannot see the axis bug).
/// </summary>
public class NormalizedTextMapTests
{
    public static TheoryData<string> ParityInputs => new()
    {
        "",
        "   ",
        "\r\n\r\n",
        "רוני התעוררה בבוקר.",
        "שורה ראשונה\r\nשורה שנייה",
        "שורה ראשונה\r\n\r\n\r\nשורה שנייה\n\nשלישית",
        "  רווח מוביל ורווח סוגר  ",
        "\r\n  פסקה אחרי שורות ריקות\r\n",
        "מילה\t\tטאב    רווחים",
        "של‏ום עולם",
        "א ‏ ב",
        "א\n‏\nב",
        "‫פתיחה מוטמעת‬ ואחריה טקסט",
        "⁧איזולציה⁩ ‎(ltr)‎",
        "ירושלים עיר",
        "first line\n \nsecond line",
        "mixed עברית and English\r\n\twith a tab",
        "‏\r\n‏רק בקרה בתחילה",
    };

    [Theory]
    [MemberData(nameof(ParityInputs))]
    public void Text_matches_the_server_snapshot_rules_exactly(string raw)
    {
        var expected = TextNormalization.NormalizeTextForAnalysis(
            SyncfusionWatermarkStripper.StripSyncfusionWatermark(raw)) ?? string.Empty;

        Assert.Equal(expected, NormalizedTextMap.Build(raw).Text);
    }

    [Theory]
    [MemberData(nameof(ParityInputs))]
    public void Every_normalized_char_maps_to_the_raw_char_it_stands_for(string raw)
    {
        var map = NormalizedTextMap.Build(raw);
        for (var i = 0; i < map.Text.Length; i++)
        {
            var start = map.RawStartOf(i);
            var end = map.RawEndOf(i);
            Assert.True(start < end && end <= raw.Length);
            var source = raw[start..end];
            if (map.Text[i] == ' ')
                Assert.True(source.All(c => c == ' ' || c == '\t' || c == '\r' || c == '\n'), $"index {i}: '{source}'");
            else
                Assert.Equal(map.Text[i].ToString(), source);
            if (i > 0) Assert.True(map.RawEndOf(i - 1) <= start, "ranges are ordered and disjoint");
        }
    }

    [Fact]
    public void A_span_across_a_paragraph_boundary_covers_every_collapsed_line_break()
    {
        const string raw = "שורה ראשונה\r\n\r\nשורה שנייה";
        var map = NormalizedTextMap.Build(raw);
        const string needle = "ראשונה שורה";
        var at = map.Text.IndexOf(needle, System.StringComparison.Ordinal);
        Assert.True(at >= 0);

        var (start, end) = map.ToRawSpan(at, needle.Length);

        Assert.Equal("ראשונה\r\n\r\nשורה", raw[start..end]);
    }

    [Fact]
    public void A_bidi_control_inside_a_word_is_dropped_from_the_text_but_inside_the_raw_span()
    {
        const string raw = "אמר של‏ום לכולם";
        var map = NormalizedTextMap.Build(raw);
        Assert.Equal("אמר שלום לכולם", map.Text);

        var at = map.Text.IndexOf("שלום", System.StringComparison.Ordinal);
        var (start, end) = map.ToRawSpan(at, "שלום".Length);

        Assert.Equal("של‏ום", raw[start..end]);
    }

    [Fact]
    public void A_bidi_control_at_a_span_edge_stays_outside_the_raw_span()
    {
        const string raw = "‏שלום‏ עולם";
        var map = NormalizedTextMap.Build(raw);
        var (start, end) = map.ToRawSpan(0, "שלום".Length);
        Assert.Equal("שלום", raw[start..end]);
    }

    [Fact]
    public void Collapsed_whitespace_maps_back_to_the_whole_run()
    {
        const string raw = "מילה \t  אחרת";
        var map = NormalizedTextMap.Build(raw);
        Assert.Equal("מילה אחרת", map.Text);
        var (start, end) = map.ToRawSpan(4, 1);
        Assert.Equal(" \t  ", raw[start..end]);
    }

    [Fact]
    public void Insertion_point_sticks_to_the_preceding_character()
    {
        const string raw = "  אב‏ גד";
        var map = NormalizedTextMap.Build(raw);
        Assert.Equal("אב גד", map.Text);

        Assert.Equal(2, map.ToRawInsertionPoint(0)); // leading spaces trimmed: first char is raw 2
        Assert.Equal(4, map.ToRawInsertionPoint(2)); // right after 'ב', before the RLM
        Assert.Equal(raw.Length, map.ToRawInsertionPoint(map.Text.Length));
    }

    [Fact]
    public void Empty_and_null_build_an_empty_map()
    {
        Assert.Equal(string.Empty, NormalizedTextMap.Build(null).Text);
        var map = NormalizedTextMap.Build("‏ \r\n ");
        Assert.Equal(string.Empty, map.Text);
        Assert.Equal(0, map.ToRawInsertionPoint(0));
    }
}
