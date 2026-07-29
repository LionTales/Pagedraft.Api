using System;
using Xunit;

// Bound through using ALIASES, not a namespace import: this file must NOT pull
// Pagedraft.Api.Tests.LanguageEngine into scope, because that is the namespace the standing
// deterministic filter excludes and the whole point of this file's location is to be outside it.
using LinguisticQualityTests = Pagedraft.Api.Tests.LanguageEngine.LinguisticQualityTests;
using ProofreadQualityTests = Pagedraft.Api.Tests.LanguageEngine.ProofreadQualityTests;

namespace Pagedraft.Api.Tests;

/// <summary>
/// DETERMINISTIC (no model, no GPU, no network) guards that were STRANDED INSIDE live-model bake-off
/// harnesses, lifted to the test project root on 2026-07-29.
///
/// WHY THIS CLASS EXISTS. Both standing verification filters key on the NAMESPACE
/// <c>Pagedraft.Api.Tests.LanguageEngine</c> - the deterministic suite excludes it, the narrow filter
/// mostly does not name it - because that folder holds the live-GPU harnesses that take 40+ minutes when
/// Ollama is up. A namespace can only exclude a whole CLASS, so a deterministic <c>[Fact]</c> written
/// next to its live siblings inherits their exclusion and runs in NEITHER filter. These two did, though
/// each is a pin on a REAL bug that was fixed once already.
///
/// Each test below BINDS the harness's own <c>internal static</c> helper rather than reimplementing it.
/// That is load-bearing: the property being pinned is "the live harness's logic is correct", so a local
/// reimplementation would pin a look-alike and stay green while the harness drifted. The helpers were
/// widened from <c>private</c> to <c>internal</c> for exactly this (same assembly, so no
/// <c>InternalsVisibleTo</c> is involved), following the precedent be-c05 set with
/// <c>HarnessConfigParityTests</c>.
///
/// NOT lifted, deliberately: <c>BookReviewWindowedCoverageTests.LargeBook_ForcesMultipleWindows_...</c>,
/// the third stranded deterministic fact. It is not a static-helper call - it needs that class's full
/// live-harness DI container, its 48-chapter gold loader, its DB seeder and its
/// <c>ITestOutputHelper</c>, so lifting it would drag the live harness's setup to the root rather than
/// leave it behind. It is reached instead by a method-scoped term on the standing narrow filter, and is
/// recorded on the allowlist in <see cref="LiveHarnessNamespaceGuardTests"/>.
/// </summary>
public class LiveHarnessDeterministicGuardTests
{
    /// <summary>
    /// Lifted from <c>ProofreadQualityTests</c>. Skip-gate logic (pure, no network): a missing OpenRouter
    /// key must skip the bake-off ONLY when EVERY candidate is OpenRouter. A MIXED list keeps running its
    /// local Ollama / other-cloud rows (the unkeyed OpenRouter rows just record NA) - the regression the
    /// old <c>Any(...)</c> gate caused.
    /// </summary>
    [Fact]
    public void ModelBakeoff_MissingOpenRouterKey_SkipsOnlyWhenEveryCandidateIsOpenRouter()
    {
        (string Provider, string Model)[] mixed = { ("OpenRouter", "vendor/cloud-model"), ("Ollama", "gemma4:12b") };
        (string Provider, string Model)[] allOpenRouter = { ("OpenRouter", "vendor/a"), ("OpenRouter", "vendor/b") };
        (string Provider, string Model)[] allOllama = { ("Ollama", "gemma4:12b"), ("Ollama", "qwen2.5:14b") };

        // The bug: a mixed list with a missing key must NOT skip -> its Ollama rows still run.
        Assert.False(ProofreadQualityTests.ShouldSkipForMissingOpenRouterKey(mixed, openRouterKeyPresent: false));
        // All-OpenRouter with no key => nothing to run => skip cleanly.
        Assert.True(ProofreadQualityTests.ShouldSkipForMissingOpenRouterKey(allOpenRouter, openRouterKeyPresent: false));
        // All-OpenRouter WITH a key => run (do not skip).
        Assert.False(ProofreadQualityTests.ShouldSkipForMissingOpenRouterKey(allOpenRouter, openRouterKeyPresent: true));
        // No OpenRouter candidate at all => the OpenRouter key gate never applies.
        Assert.False(ProofreadQualityTests.ShouldSkipForMissingOpenRouterKey(allOllama, openRouterKeyPresent: false));
        // Empty list => nothing to skip on this gate.
        Assert.False(ProofreadQualityTests.ShouldSkipForMissingOpenRouterKey(Array.Empty<(string, string)>(), openRouterKeyPresent: false));
    }

    /// <summary>
    /// Lifted from <c>LinguisticQualityTests</c>. Bug-fix guard: bake-off parsing uses the SAME extractor
    /// as production. Always-on (no live model needed) - it locks the harness's <c>ParseLinguistic</c> to
    /// <c>UnifiedAnalysisService.ExtractJson</c> so bake-off scores reflect real parsing rather than a
    /// laxer test-only reader.
    /// </summary>
    [Fact]
    public void ParseLinguistic_HebrewProseWrappedJson_ExtractsRealObjectNotPreambleBrace()
    {
        // A Hebrew preamble that itself contains balanced braces, then the REAL metrics object.
        // A first-'{' brace matcher would lock onto {הערה...} and fail; the production extractor
        // rejects prose-in-braces and finds the real object, so bake-off parsing matches production.
        const string proseWrapped = """
            לפניכם ניתוח לשוני {הערה ראשונית: הטקסט תקין}.

            {
              "grammaticalityScore": 0.95,
              "summary": "ניתוח תקין.",
              "deviations": [],
              "consistencyIssues": []
            }
            """;

        var parsed = LinguisticQualityTests.ParseLinguistic(proseWrapped);

        // The REAL object is parsed (grammaticalityScore round-trips), not the prose {הערה...} brace.
        Assert.NotNull(parsed);
        Assert.Equal(0.95, parsed!.GrammaticalityScore, precision: 5);
    }
}
