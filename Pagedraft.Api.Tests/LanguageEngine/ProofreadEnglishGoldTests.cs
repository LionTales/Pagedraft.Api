using System;
using System.Linq;
using Xunit;

namespace Pagedraft.Api.Tests.LanguageEngine;

// SCOPE: Deterministic, NO-MODEL smoke test for the English proofread gold (proofread-gold-en.json).
// The bake-off that consumes this gold (ProofreadQuality_ModelBakeoff_ReportTable) is skip-by-default
// and would NOT catch a malformed English gold in CI. This class fails fast — without Ollama / any
// cloud model — if the English gold is missing, doesn't parse, or loses one of the four buckets the
// plan requires (real-error, clean-no-change, overreach-guarded incl. a right-span/wrong-meaning case,
// intentional-colloquial-dialogue).
//
// It REUSES the production loader path: ProofreadQualityTests.LoadProofreadGold(fileName) — the same
// Path.Combine(AppContext.BaseDirectory, "TestData", ...) + deserializer the bake-off uses — so this
// test pins the EXACT shape the bake-off will read, not a parallel JSON config. Mirrors the
// deterministic, no-model pattern of ProofreadOverreachScorerTests.
public class ProofreadEnglishGoldTests
{
    private const string EnglishGoldFile = "proofread-gold-en.json";

    private static HebrewRegressionCase[] LoadEnglishGold() =>
        ProofreadQualityTests.LoadProofreadGold(EnglishGoldFile);

    [Fact]
    public void EnglishGold_LoadsParsesAndHasCases()
    {
        var cases = LoadEnglishGold();
        Assert.NotEmpty(cases);
    }

    [Fact]
    public void EnglishGold_AllIdsUnique()
    {
        var cases = LoadEnglishGold();
        var ids = cases.Select(c => c.Id).ToArray();
        Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EnglishGold_EveryCaseIsEnglish()
    {
        var cases = LoadEnglishGold();
        Assert.NotEmpty(cases);
        Assert.All(cases, c =>
            Assert.StartsWith("en", c.Language, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EnglishGold_HasAtLeastOneForbiddenCorrectionsCase()
    {
        var cases = LoadEnglishGold();
        Assert.Contains(cases, c => (c.ForbiddenCorrections?.Length ?? 0) > 0);
    }

    [Fact]
    public void EnglishGold_HasAtLeastOneNoChangeCase()
    {
        var cases = LoadEnglishGold();
        Assert.Contains(cases, c => c.ShouldHaveNoChanges == true);
    }

    // The right-span/wrong-meaning overreach case (English analogue of the Hebrew עתון->עתונות case):
    // a case that BOTH credits a legitimate expected correction AND forbids a meaning-changing rewrite.
    [Fact]
    public void EnglishGold_HasAtLeastOneExpectedPlusForbiddenCase()
    {
        var cases = LoadEnglishGold();
        Assert.Contains(cases, c =>
            (c.ExpectedCorrections?.Length ?? 0) > 0 &&
            (c.ForbiddenCorrections?.Length ?? 0) > 0);
    }

    // Asserts all four required id-prefix buckets are present so that silently dropping an entire
    // bucket (e.g. removing every "en-dialect-*" case) still fails this test without touching any
    // of the per-bucket structural facts above.
    [Fact]
    public void EnglishGold_HasAllFourBuckets()
    {
        var cases = LoadEnglishGold();
        Assert.Contains(cases, c => c.Id.StartsWith("en-inj-", StringComparison.Ordinal));
        Assert.Contains(cases, c => c.Id.StartsWith("en-clean-", StringComparison.Ordinal));
        Assert.Contains(cases, c => c.Id.StartsWith("en-overreach-", StringComparison.Ordinal));
        Assert.Contains(cases, c => c.Id.StartsWith("en-dialect-", StringComparison.Ordinal));
    }
}
