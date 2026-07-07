using System;
using System.Linq;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Deterministic guard tests for <see cref="LatinInHebrewContentDetector"/> — the
/// gate that decides whether a repairable Hebrew analysis prose field still holds
/// residual Latin and therefore needs the repair pass (plan
/// analysis-output-repair-2026-07-03, todo p2-detector).
///
/// Covers the real Phase-0-baseline CONTENT leaks ("(Action)", "Tension",
/// "Magic vs. Nature", "High Stakes"), clean Hebrew (flags NONE), the >=2-letter
/// rule (a single Latin letter is never flagged), the proper-noun allowlist
/// (allowlisted run skipped while a non-allowlisted run in the SAME string flags),
/// and empty/whitespace safety.
///
/// NO Ollama, NO skip-gate — runs in CI always.
/// </summary>
public class LatinInHebrewContentDetectorTests
{
    // ─── Real leak strings flag correctly ───────────────────────────────────

    [Fact]
    public void ParentheticalAction_InHebrewSentence_FlagsAction()
    {
        // LinguisticAnalysis summary leak from the diagnostic: "(Action)" inside Hebrew prose.
        const string text = "הסצנה נעה במהירות אל עבר עימות (Action) שמסעיר את הקורא.";

        var runs = LatinInHebrewContentDetector.DetectLatinRuns(text);

        Assert.Equal(new[] { "Action" }, runs);
        Assert.True(LatinInHebrewContentDetector.HasNonAllowlistedLatin(text));
    }

    [Fact]
    public void TensionLeak_InHebrewMoodProgression_FlagsTension()
    {
        // LiteraryAnalysis moodProgression leak: Hebrew followed by "(Tension)".
        const string text = "מתח גבוה (Tension)";

        var runs = LatinInHebrewContentDetector.DetectLatinRuns(text);

        Assert.Equal(new[] { "Tension" }, runs);
    }

    [Fact]
    public void MagicVsNature_ThemeName_FlagsAllThreeRuns_InOrder()
    {
        // LiteraryAnalysis theme-name leak. "vs." is a 2-letter run (>=2), the period
        // ends it; the whole phrase is three separate runs — order preserved.
        const string text = "Magic vs. Nature";

        var runs = LatinInHebrewContentDetector.DetectLatinRuns(text);

        Assert.Equal(new[] { "Magic", "vs", "Nature" }, runs);
    }

    [Fact]
    public void HighStakes_InParentheses_FlagsBothWords()
    {
        // LiteraryAnalysis toneDescription leak: "(High Stakes)".
        const string text = "(High Stakes)";

        var runs = LatinInHebrewContentDetector.DetectLatinRuns(text);

        Assert.Equal(new[] { "High", "Stakes" }, runs);
    }

    // ─── Clean Hebrew prose → flags NONE ────────────────────────────────────

    [Fact]
    public void CleanHebrewProse_FlagsNothing()
    {
        const string text = "הפרק פותח בתיאור נופי, בונה מתח הדרגתי ומגיע לשיא רגשי מרשים.";

        var runs = LatinInHebrewContentDetector.DetectLatinRuns(text);

        Assert.Empty(runs);
        Assert.False(LatinInHebrewContentDetector.HasNonAllowlistedLatin(text));
    }

    // ─── The >=2-letter rule ────────────────────────────────────────────────

    [Fact]
    public void SingleLatinLetter_EmbeddedInHebrew_IsNotFlagged()
    {
        // A lone Latin letter (e.g. an initial) is NOT a run — the >=2 rule.
        const string text = "הדמות א. B מסופרת בגוף ראשון.";

        var runs = LatinInHebrewContentDetector.DetectLatinRuns(text);

        Assert.Empty(runs);
        Assert.False(LatinInHebrewContentDetector.HasNonAllowlistedLatin(text));
    }

    [Fact]
    public void TwoLatinLetters_IsFlagged_BoundaryOfTheRule()
    {
        // Exactly two letters IS a run (the boundary of >=2).
        var runs = LatinInHebrewContentDetector.DetectLatinRuns("סצנה go מהירה");

        Assert.Equal(new[] { "go" }, runs);
    }

    // ─── Proper-noun allowlist ──────────────────────────────────────────────

    [Fact]
    public void AllowlistedProperNoun_IsSkipped_WhileNonAllowlistedRunInSameStringFlags()
    {
        // "Google" is on the seeded allowlist (legit brand); "Tension" is a real leak.
        const string text = "הדמות גללה ב Google בזמן שהמתח (Tension) גבר.";

        var runs = LatinInHebrewContentDetector.DetectLatinRuns(text);

        Assert.Equal(new[] { "Tension" }, runs);
        Assert.DoesNotContain("Google", runs);
        Assert.True(LatinInHebrewContentDetector.HasNonAllowlistedLatin(text));
    }

    [Fact]
    public void AllowlistIsCaseInsensitive_AndSkipsAllowlistedRunEvenWhenAlone()
    {
        // Case-insensitive allowlist match; a string whose ONLY Latin is allowlisted flags nothing.
        var runs = LatinInHebrewContentDetector.DetectLatinRuns("היא כתבה על FACEBOOK בפוסט ארוך.");

        Assert.Empty(runs);
        Assert.False(LatinInHebrewContentDetector.HasNonAllowlistedLatin("היא כתבה על FACEBOOK בפוסט ארוך."));
    }

    // ─── Empty / whitespace safety ──────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void EmptyOrWhitespace_ReturnsEmptyList_NoException(string? text)
    {
        var runs = LatinInHebrewContentDetector.DetectLatinRuns(text);

        Assert.Empty(runs);
        Assert.False(LatinInHebrewContentDetector.HasNonAllowlistedLatin(text));
    }

    // ─── Ordering + no-dedupe contract ──────────────────────────────────────

    [Fact]
    public void RepeatedRuns_ArePreservedInOrder_AndNotDeduplicated()
    {
        // Callers count; the detector keeps every occurrence in appearance order.
        var runs = LatinInHebrewContentDetector.DetectLatinRuns("Tone וגם tone שוב Tone");

        Assert.Equal(new[] { "Tone", "tone", "Tone" }, runs);
    }
}
