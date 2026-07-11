using System.Linq;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Deterministic tests for the BIDIRECTIONAL span API of
/// <see cref="LatinInHebrewContentDetector"/> (todo d1-bidirectional-detector):
/// <see cref="LatinInHebrewContentDetector.DetectForeignRuns"/> /
/// <see cref="LatinInHebrewContentDetector.HasForeignRuns"/>, plus the compatibility
/// contract that the original Latin-only wrapper (<c>DetectLatinRuns</c> /
/// <c>HasNonAllowlistedLatin</c>) still returns exactly what the existing count-scan
/// callers (GlossaryRepairPass, AnalysisRepairService) expect.
///
/// Covers: exact offsets/lengths, both directions (Latin-in-Hebrew AND Hebrew-in-Latin),
/// boundaries (digits/punct/whitespace split runs and never create phantom runs), NO
/// false runs on single-script text, the >=2-letter rule in both directions, the
/// allowlist through the span API, and the wrapper bridge. NO Ollama — runs in CI always.
/// </summary>
public class ForeignRunDetectorTests
{
    // ─── (a) Exact offsets + lengths for a known string ─────────────────────

    [Fact]
    public void DetectForeignRuns_HebrewExpected_LatinRun_HasExactOffsetAndLength()
    {
        // מ ת ח ' ' ג ב ו ה ' ' ( T … => "Tension" starts at index 10, length 7.
        const string text = "מתח גבוה (Tension)";

        var runs = LatinInHebrewContentDetector.DetectForeignRuns(text, ExpectedScript.Hebrew);

        Assert.Equal(new[] { new ForeignRun("Tension", 10, 7) }, runs);
        // Offset round-trips to the original substring.
        Assert.Equal("Tension", text.Substring(runs[0].Start, runs[0].Length));
    }

    [Fact]
    public void DetectForeignRuns_MultipleRuns_PreserveOrderAndOffsets()
    {
        // "Magic vs. Nature" — three Latin runs; period/space are boundaries.
        const string text = "Magic vs. Nature";

        var runs = LatinInHebrewContentDetector.DetectForeignRuns(text, ExpectedScript.Hebrew);

        Assert.Equal(
            new[]
            {
                new ForeignRun("Magic", 0, 5),
                new ForeignRun("vs", 6, 2),
                new ForeignRun("Nature", 10, 6),
            },
            runs);
    }

    // ─── (b) Bidirectional: Hebrew-in-Latin flags the Hebrew, not the Latin ──

    [Fact]
    public void DetectForeignRuns_LatinExpected_HebrewRun_IsFlaggedWithOffset_LatinIsNot()
    {
        // T h e ' ' w o r d ' ' ש ל ו ם … => "שלום" starts at index 9, length 4.
        const string text = "The word שלום here";

        var runs = LatinInHebrewContentDetector.DetectForeignRuns(text, ExpectedScript.Latin);

        Assert.Equal(new[] { new ForeignRun("שלום", 9, 4) }, runs);
        Assert.Equal("שלום", text.Substring(runs[0].Start, runs[0].Length));
    }

    [Fact]
    public void DetectForeignRuns_LatinExpected_MixedMultipleHebrewRuns_OrderAndOffsets()
    {
        // "Go שלום stop עולם" — two Hebrew runs; the Latin words are native (not flagged).
        const string text = "Go שלום stop עולם";

        var runs = LatinInHebrewContentDetector.DetectForeignRuns(text, ExpectedScript.Latin);

        Assert.Equal(
            new[]
            {
                new ForeignRun("שלום", 3, 4),
                new ForeignRun("עולם", 13, 4),
            },
            runs);
    }

    // ─── (c) Boundaries: digits / punctuation / whitespace split runs ────────

    [Fact]
    public void DetectForeignRuns_DigitsSplitRuns_NoPhantomRuns()
    {
        // a b c 1 2 3 d e f => "abc"(0,3) and "def"(6,3); digits are boundaries, not runs.
        var runs = LatinInHebrewContentDetector.DetectForeignRuns("abc123def", ExpectedScript.Hebrew);

        Assert.Equal(
            new[]
            {
                new ForeignRun("abc", 0, 3),
                new ForeignRun("def", 6, 3),
            },
            runs);
    }

    [Fact]
    public void DetectForeignRuns_PunctAndWhitespaceSplitRuns()
    {
        // g o , ' ' s t o p => "go"(0,2) and "stop"(4,4).
        var runs = LatinInHebrewContentDetector.DetectForeignRuns("go, stop", ExpectedScript.Hebrew);

        Assert.Equal(
            new[]
            {
                new ForeignRun("go", 0, 2),
                new ForeignRun("stop", 4, 4),
            },
            runs);
    }

    [Theory]
    [InlineData("12 34 567")]        // digits only
    [InlineData("!!! ??? ... ()")]   // punctuation only
    [InlineData("   \t\n  ")]        // whitespace only
    public void DetectForeignRuns_DigitsPunctWhitespaceOnly_ProduceNoRuns(string text)
    {
        Assert.Empty(LatinInHebrewContentDetector.DetectForeignRuns(text, ExpectedScript.Hebrew));
        Assert.Empty(LatinInHebrewContentDetector.DetectForeignRuns(text, ExpectedScript.Latin));
        Assert.False(LatinInHebrewContentDetector.HasForeignRuns(text, ExpectedScript.Hebrew));
        Assert.False(LatinInHebrewContentDetector.HasForeignRuns(text, ExpectedScript.Latin));
    }

    // ─── (d) NO false runs on single-script text (expected == actual) ────────

    [Fact]
    public void SingleScript_HebrewOnly_ExpectedHebrew_FlagsNothing()
    {
        const string text = "הפרק פותח בתיאור נופי ובונה מתח הדרגתי אל שיא רגשי.";

        Assert.Empty(LatinInHebrewContentDetector.DetectForeignRuns(text, ExpectedScript.Hebrew));
        Assert.False(LatinInHebrewContentDetector.HasForeignRuns(text, ExpectedScript.Hebrew));
    }

    [Fact]
    public void SingleScript_LatinOnly_ExpectedLatin_FlagsNothing()
    {
        const string text = "The quick brown fox jumps over the lazy dog.";

        Assert.Empty(LatinInHebrewContentDetector.DetectForeignRuns(text, ExpectedScript.Latin));
        Assert.False(LatinInHebrewContentDetector.HasForeignRuns(text, ExpectedScript.Latin));
    }

    [Fact]
    public void SingleScript_FlagsWhenScriptIsForeign()
    {
        // Same single-script strings ARE flagged when the expected script is the OTHER one.
        Assert.True(LatinInHebrewContentDetector.HasForeignRuns("The quick brown fox", ExpectedScript.Hebrew));
        Assert.True(LatinInHebrewContentDetector.HasForeignRuns("הפרק פותח בתיאור", ExpectedScript.Latin));
    }

    // ─── The >=2-letter rule in BOTH directions ─────────────────────────────

    [Fact]
    public void SingleForeignLetter_IsNeverARun_BothDirections()
    {
        // A lone Latin letter in Hebrew prose, and a lone Hebrew letter in Latin prose.
        Assert.Empty(LatinInHebrewContentDetector.DetectForeignRuns("הדמות א. B מסופרת.", ExpectedScript.Hebrew));
        Assert.Empty(LatinInHebrewContentDetector.DetectForeignRuns("The letter א stands alone.", ExpectedScript.Latin));
    }

    [Fact]
    public void TwoForeignLetters_IsFlagged_BoundaryOfTheRule_BothDirections()
    {
        var latin = LatinInHebrewContentDetector.DetectForeignRuns("סצנה go מהירה", ExpectedScript.Hebrew);
        Assert.Equal(new[] { new ForeignRun("go", 5, 2) }, latin);

        // c o d e ' ' ב א => "בא" at index 5, length 2.
        var hebrew = LatinInHebrewContentDetector.DetectForeignRuns("code בא here", ExpectedScript.Latin);
        Assert.Equal(new[] { new ForeignRun("בא", 5, 2) }, hebrew);
    }

    // ─── Allowlist still applies through the span API (Latin/Hebrew-expected) ─

    [Fact]
    public void DetectForeignRuns_AllowlistedBrand_IsSkipped_NonAllowlistedInSameStringFlags()
    {
        // "Google" is allowlisted; "Tension" is a real leak — only Tension is returned.
        const string text = "הדמות גללה ב Google בזמן שהמתח (Tension) גבר.";

        var runs = LatinInHebrewContentDetector.DetectForeignRuns(text, ExpectedScript.Hebrew);

        Assert.Equal(new[] { "Tension" }, runs.Select(r => r.Text).ToArray());
        Assert.DoesNotContain(runs, r => r.Text == "Google");
        Assert.True(LatinInHebrewContentDetector.HasForeignRuns(text, ExpectedScript.Hebrew));
    }

    // ─── Empty / whitespace safety ──────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void EmptyOrWhitespace_ReturnsEmpty_NoException_BothDirections(string? text)
    {
        Assert.Empty(LatinInHebrewContentDetector.DetectForeignRuns(text, ExpectedScript.Hebrew));
        Assert.Empty(LatinInHebrewContentDetector.DetectForeignRuns(text, ExpectedScript.Latin));
        Assert.False(LatinInHebrewContentDetector.HasForeignRuns(text, ExpectedScript.Hebrew));
        Assert.False(LatinInHebrewContentDetector.HasForeignRuns(text, ExpectedScript.Latin));
    }

    // ─── (e) Wrapper bridge: old count API mirrors the new span API exactly ──

    [Theory]
    [InlineData("הסצנה נעה אל עבר עימות (Action) שמסעיר את הקורא.")]
    [InlineData("Magic vs. Nature")]
    [InlineData("Tone וגם tone שוב Tone")]
    [InlineData("הדמות גללה ב Google בזמן שהמתח (Tension) גבר.")]
    [InlineData("הפרק פותח בתיאור נופי בלבד.")]
    public void DetectLatinRuns_Wrapper_EqualsSpanTextProjection_HebrewExpected(string text)
    {
        var spanTexts = LatinInHebrewContentDetector
            .DetectForeignRuns(text, ExpectedScript.Hebrew)
            .Select(r => r.Text)
            .ToArray();

        // Old callers use DetectLatinRuns(...).Count and enumerate the strings.
        Assert.Equal(spanTexts, LatinInHebrewContentDetector.DetectLatinRuns(text).ToArray());
        Assert.Equal(
            LatinInHebrewContentDetector.HasForeignRuns(text, ExpectedScript.Hebrew),
            LatinInHebrewContentDetector.HasNonAllowlistedLatin(text));
    }
}
