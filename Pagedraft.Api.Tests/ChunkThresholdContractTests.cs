using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// THE CLIENT/SERVER CHUNK-THRESHOLD CONTRACT (model-tier plan, p1-4).
///
/// <c>GET /api/config/analysis-chunk-thresholds</c> is not informational: the client uses the numbers it
/// returns to decide whether a chapter goes through the ASYNC analysis-jobs flow or the SYNC <c>/analyze</c>
/// call. The server makes the same decision independently in
/// <see cref="UnifiedAnalysisService.RunAsync"/>. If the endpoint reports a LARGER per-chunk target than
/// RunAsync sizes at, the client picks sync for a chapter the server then chunks, and a long chapter
/// mis-routes. The two surfaces must therefore move in lockstep, always.
///
/// WHY THE MODEL TIER PUTS THIS AT RISK, and why the todo's "this is probably language-keyed only, so this
/// may be a NO-OP" premise is FALSE (established by p1-1, re-verified here):
/// <see cref="UnifiedAnalysisService.EffectiveChunkTargetWords"/> takes <c>min(A, B)</c> of
///   (A) a LANGUAGE ceiling  = configuredCeiling * charsPerToken(lang) / charsPerToken("en"), and
///   (B) a WINDOW-FIT bound  = ((numCtx - promptReserve - safetyMargin) / 2) * charsPerToken(lang) / 6.0,
/// and (B) reads <see cref="BookContextAssembler.ResolveNumCtxForTask"/> — i.e. the num_ctx of whichever
/// provider entry the task is ROUTED to. So a tier that changes the routed provider for Proofread/LineEdit
/// CAN move this number. It does not today only because (A) is the binding bound at every shipped window;
/// that dominance is itself pinned, in <see cref="ChunkThresholdBoundDominanceTests"/>, because a tier that
/// silently flipped the dominant bound is exactly how this contract would break unnoticed.
///
/// Pure unit tests: no DB, no LLM, no live GPU. Classes are named *ChunkThreshold* so the plan's standing
/// deterministic filter picks them up via <c>FullyQualifiedName~ChunkThreshold</c>.
/// </summary>
public class ChunkThresholdEndpointParityTests
{
    /// <summary>The wired cloud tier target (see appsettings <c>_comment_OpenRouterPerTask</c>).</summary>
    private const string CloudProvider = "OpenRouter";
    private const string CloudModel = "google/gemma-4-31b-it";

    /// <summary>
    /// Every language shape the endpoint can be asked about: Latin, both Hebrew tags, Arabic, the
    /// absent-language case the client hits before a book language is known, and an unrecognised tag.
    /// </summary>
    public static TheoryData<string?> Languages() => new() { "en", "en-US", "he", "iw", "ar", null, "", "zz-unknown" };

    // ── Parity: the endpoint returns exactly what RunAsync chunks at ──────────────────────────────────────

    /// <summary>
    /// THE PARITY TEST the todo asks for, for the same (language, tier) pair on BOTH tiers. Asserted twice
    /// over, deliberately:
    ///   (1) against the accessor RunAsync itself calls — catches the two surfaces drifting apart, and
    ///   (2) against a bound-by-bound RECOMPUTATION of the sizing formula from the config constants — so the
    ///       test is not the tautology "the same static returns the same number". If the endpoint ever passed
    ///       a different task or a different configured ceiling (the drift this contract actually died of
    ///       before: the endpoint returned the raw Latin 500 while the server chunked Hebrew at 250), (2) goes
    ///       red even if (1) were made to agree.
    /// </summary>
    [Theory]
    [MemberData(nameof(Languages))]
    public void Endpoint_ReturnsExactlyWhatRunAsyncChunksAt_OnTheLocalTier(string? language)
        => AssertEndpointMatchesChunker(ShippedOptions(), language, "local (Ollama)");

    [Theory]
    [MemberData(nameof(Languages))]
    public void Endpoint_ReturnsExactlyWhatRunAsyncChunksAt_OnTheCloudTier(string? language)
        => AssertEndpointMatchesChunker(
            ShippedOptionsRoutedTo(CloudProvider, CloudModel, AiTaskType.Proofread, AiTaskType.LineEdit),
            language,
            $"cloud ({CloudProvider})");

    /// <summary>
    /// The lockstep proof, and the reason this todo is not a no-op. A tier whose Proofread window is TIGHTER
    /// than the crossover moves the chunk target off the language ceiling — and the endpoint must move WITH
    /// it. This is the scenario that mis-routes long chapters if the endpoint is ever computed independently:
    /// the server chunks at ~170 Hebrew words while a stale endpoint still reports 250.
    /// </summary>
    [Fact]
    public void Endpoint_TracksTheChunker_WhenATierShrinksTheRoutedWindowBelowTheCrossover()
    {
        // num_ctx 3072 → generation window 1024 → 512 input tokens → 170 Hebrew words, below the 250 ceiling.
        var opt = WindowedOptions(3072);

        var dto = Thresholds(opt, "he");
        var chunkerProofread = UnifiedAnalysisService.ProofreadChunkTargetWordsFor(opt, "he");
        var chunkerLineEdit = UnifiedAnalysisService.LineEditChunkTargetWordsFor(opt, "he");

        Assert.Equal(chunkerProofread, dto.ProofreadChunkTargetWords);
        Assert.Equal(chunkerLineEdit, dto.LineEditChunkTargetWords);
        Assert.True(dto.ProofreadChunkTargetWords < 250,
            $"a tighter routed window must shrink the target below the Hebrew language ceiling 250 " +
            $"(got {dto.ProofreadChunkTargetWords}); if it does not, this test is no longer proving that the " +
            "endpoint is window-sensitive at all.");
        Assert.Equal(Recompute(opt, AiTaskType.Proofread, "he", opt.EffectiveProofreadChunkTargetWords).Target,
            dto.ProofreadChunkTargetWords);
    }

    /// <summary>
    /// A tier is per-TASK (Ai:FeatureModels is keyed by task), so it can move Proofread without moving
    /// LineEdit. The endpoint returns both in ONE payload, so a copy-paste there would report the moved value
    /// for both. Pinned: only the re-routed task moves, and each side still matches the chunker.
    /// </summary>
    [Fact]
    public void Endpoint_TracksAPerTaskTierChange_WithoutMovingTheOtherTask()
    {
        var opt = ShippedOptions();
        // Route ONLY Proofread at the cloud tier, and give that route a deliberately tight window.
        opt.FeatureModels![nameof(AiTaskType.Proofread)] =
            new FeatureModelOptions { Provider = CloudProvider, Model = CloudModel };
        opt.ProviderSettings![ProviderTuningResolver.TaskKey(CloudProvider, AiTaskType.Proofread)] =
            new ProviderTuningOptions { NumCtx = 3072, MaxTokens = 4096 };

        var dto = Thresholds(opt, "he");

        Assert.Equal(UnifiedAnalysisService.ProofreadChunkTargetWordsFor(opt, "he"), dto.ProofreadChunkTargetWords);
        Assert.Equal(UnifiedAnalysisService.LineEditChunkTargetWordsFor(opt, "he"), dto.LineEditChunkTargetWords);
        Assert.True(dto.ProofreadChunkTargetWords < dto.LineEditChunkTargetWords,
            $"only the re-routed task should have moved: proofread {dto.ProofreadChunkTargetWords}, " +
            $"lineEdit {dto.LineEditChunkTargetWords}.");
        Assert.Equal(250, dto.LineEditChunkTargetWords); // untouched local route keeps the Hebrew ceiling
    }

    /// <summary>
    /// The two DTO fields carry each task's OWN configured ceiling
    /// (<see cref="AiOptions.EffectiveProofreadChunkTargetWords"/> vs
    /// <see cref="AiOptions.EffectiveLineEditChunkTargetWords"/>). They are equal in the shipped config (both
    /// 500), which would hide an argument swap, so this drives them apart.
    /// </summary>
    [Fact]
    public void Endpoint_UsesEachTasksOwnConfiguredCeiling_NotOneCeilingForBoth()
    {
        var opt = WindowedOptions(8192);
        opt.ProofreadChunkTargetWords = 500;
        opt.LineEditChunkTargetWords = 300;

        var dto = Thresholds(opt, "en");

        Assert.Equal(500, dto.ProofreadChunkTargetWords);
        Assert.Equal(300, dto.LineEditChunkTargetWords);
        Assert.Equal(UnifiedAnalysisService.ProofreadChunkTargetWordsFor(opt, "en"), dto.ProofreadChunkTargetWords);
        Assert.Equal(UnifiedAnalysisService.LineEditChunkTargetWordsFor(opt, "en"), dto.LineEditChunkTargetWords);
    }

    /// <summary>
    /// A DIVERGENCE FOUND WHILE VERIFYING p1-4, pinned then as harmless-today and CLOSED by p3-2.
    ///
    /// Before p3-2 the chunk sizer resolved the BARE task key (<c>FeatureModels["Proofread"]</c>) while the
    /// ROUTER, for an English Proofread/LineEdit, resolved the LANGUAGE-KEYED <c>Proofread_en</c> entry - so
    /// English chunks could be sized for a different model than the one that ran. It was harmless only
    /// because <c>Proofread_en</c> happens to name the same PROVIDER as <c>Proofread</c>.
    ///
    /// p3-2 gave <see cref="BookContextAssembler.ResolveNumCtxForTask(AiOptions, AiTaskType, string?, AiTier)"/>
    /// the language and the tier, and both surfaces now go through the SAME precedence in
    /// <see cref="LinguisticModelResolver"/>, so they agree by construction rather than by coincidence. The
    /// test is kept and STRENGTHENED: it now drives a config where the <c>_en</c> variant deliberately names
    /// a DIFFERENT provider with a DIFFERENT window - the exact scenario that used to break it - and asserts
    /// the sizer follows the router there too, on both tiers.
    /// </summary>
    [Theory]
    [InlineData(AiTier.Fast)]
    [InlineData(AiTier.Thinking)]
    public void ChunkSizerAndRouter_ResolveTheSameWindow_ForAnEnglishProofreadAndLineEdit(AiTier tier)
    {
        var failures = new List<string>();

        foreach (var task in new[] { AiTaskType.Proofread, AiTaskType.LineEdit })
        {
            // The shipped config FIRST (the state that actually ships), then a split-provider config that
            // would have caught the old bug.
            foreach (var opt in new[] { ShippedOptions(), EnglishRoutedElsewhere(task) })
            {
                var sizerNumCtx = BookContextAssembler.ResolveNumCtxForTask(opt, task, "en", tier);

                // What the ROUTER resolves for an English request, through its own code path.
                var selection = AiRouter.ResolveSelectionForTest(
                    new AiRequest { InputText = "x", TaskType = task, Language = "en", Tier = tier }, opt);
                var routerNumCtx = ProviderTuningResolver.Resolve(opt.ProviderSettings, selection.Provider, task).NumCtx;

                if (routerNumCtx != sizerNumCtx)
                    failures.Add($"{task}/{tier}: an English request routes to {selection.Provider} " +
                                 $"(num_ctx {routerNumCtx}) but the chunk sizer sized against num_ctx {sizerNumCtx}. " +
                                 "The sizer and the router must resolve the SAME (task, language, tier) route, or " +
                                 "English chunks are sized for a different model than the one that runs.");
            }
        }

        Assert.True(failures.Count == 0, string.Join(" | ", failures));
    }

    /// <summary>Shipped config with the task's ENGLISH variant deliberately pointed at a different provider
    /// with a different window - the split the old sizer could not see.</summary>
    private static AiOptions EnglishRoutedElsewhere(AiTaskType task)
    {
        var opt = ShippedOptions();
        opt.FeatureModels![$"{task}_en"] = new FeatureModelOptions { Provider = CloudProvider, Model = CloudModel };
        opt.ProviderSettings![ProviderTuningResolver.TaskKey(CloudProvider, task)] =
            new ProviderTuningOptions { NumCtx = 12288, MaxTokens = 4096 };
        return opt;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────────

    private static void AssertEndpointMatchesChunker(AiOptions opt, string? language, string tierLabel)
    {
        var dto = Thresholds(opt, language);

        // (1) The accessor RunAsync calls.
        Assert.True(UnifiedAnalysisService.ProofreadChunkTargetWordsFor(opt, language) == dto.ProofreadChunkTargetWords,
            $"[{tierLabel}, lang '{language ?? "(null)"}'] endpoint reported " +
            $"{dto.ProofreadChunkTargetWords} proofread words but RunAsync chunks at " +
            $"{UnifiedAnalysisService.ProofreadChunkTargetWordsFor(opt, language)}. The client picks async-vs-sync " +
            "off the endpoint, so a larger endpoint value mis-routes long chapters to sync /analyze.");
        Assert.True(UnifiedAnalysisService.LineEditChunkTargetWordsFor(opt, language) == dto.LineEditChunkTargetWords,
            $"[{tierLabel}, lang '{language ?? "(null)"}'] endpoint reported " +
            $"{dto.LineEditChunkTargetWords} lineEdit words but RunAsync chunks at " +
            $"{UnifiedAnalysisService.LineEditChunkTargetWordsFor(opt, language)}.");

        // (2) An independent recomputation from the config constants, so this is not "the same static twice".
        Assert.Equal(
            Recompute(opt, AiTaskType.Proofread, language, opt.EffectiveProofreadChunkTargetWords).Target,
            dto.ProofreadChunkTargetWords);
        Assert.Equal(
            Recompute(opt, AiTaskType.LineEdit, language, opt.EffectiveLineEditChunkTargetWords).Target,
            dto.LineEditChunkTargetWords);
    }

    internal static AnalysisChunkThresholdsDto Thresholds(AiOptions opt, string? language, AiTier tier = AiTier.Fast)
    {
        // Goes through the endpoint's OWN parameter shape (a tier TOKEN, parsed defensively) rather than an
        // AiTier, so the parse is part of what these tests exercise.
        var action = new ConfigController(Options.Create(opt))
            .GetAnalysisChunkThresholds(language, AiTierPolicy.ToStoredValue(tier));
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        return Assert.IsType<AnalysisChunkThresholdsDto>(ok.Value);
    }

    /// <summary>The REAL shipped appsettings.json, bound through the same loader the other config-parity
    /// guards use, so a tuning edit shows up here too.</summary>
    internal static AiOptions ShippedOptions()
    {
        var opt = ProviderTuningConfigParityTests.LoadShippedAiOptions();
        Assert.True(opt.FeatureModels is { Count: > 0 }, "Ai:FeatureModels bound empty from the shipped appsettings.json.");
        return opt;
    }

    /// <summary>Shipped config with the given tasks re-routed at a provider, exactly as a tier switch would
    /// (both fields non-empty — the predicate LinguisticModelResolver and AiRouter share).</summary>
    internal static AiOptions ShippedOptionsRoutedTo(string provider, string model, params AiTaskType[] tasks)
    {
        var opt = ShippedOptions();
        foreach (var task in tasks)
            opt.FeatureModels![task.ToString()] = new FeatureModelOptions { Provider = provider, Model = model };
        return opt;
    }

    /// <summary>Synthetic options whose Proofread and LineEdit both resolve the given num_ctx (no per-task
    /// entry, so the flat provider entry governs both).</summary>
    internal static AiOptions WindowedOptions(int numCtx) => new()
    {
        DefaultProvider = "Ollama",
        DefaultModel = "test-model",
        ProofreadChunkTargetWords = 500,
        LineEditChunkTargetWords = 500,
        ProviderSettings = new Dictionary<string, ProviderTuningOptions>
        {
            ["Ollama"] = new ProviderTuningOptions { NumCtx = numCtx, NumPredict = 2048 }
        }
    };

    /// <summary>
    /// An INDEPENDENT restatement of the two bounds in
    /// <see cref="UnifiedAnalysisService.EffectiveChunkTargetWords"/>, from the config constants. Deliberately
    /// a second implementation: it is the oracle these tests compare the production result against, and it is
    /// what makes the parity assertions non-tautological. Also returns the bounds separately so
    /// <see cref="ChunkThresholdBoundDominanceTests"/> can assert WHICH one binds.
    /// </summary>
    internal static (int LanguageCeiling, int WindowFit, int Target) Recompute(
        AiOptions opt, AiTaskType task, string? language, int configuredCeiling, AiTier tier = AiTier.Fast)
    {
        const double avgCharsPerWord = 6.0; // mirrors UnifiedAnalysisService.AvgCharsPerWord
        var cptLang = BookContextAssembler.CharsPerTokenForLanguage(language);
        var cptLatin = BookContextAssembler.CharsPerTokenForLanguage("en");

        var languageCeiling = (int)Math.Floor(configuredCeiling * cptLang / cptLatin);

        var numCtx = BookContextAssembler.ResolveNumCtxForTask(opt, task, language, tier);
        var generationWindow = numCtx
            - Math.Max(0, opt.BookContextPromptReserveTokens)
            - Math.Max(0, opt.BookContextSafetyMarginTokens);
        var availableInputTokens = Math.Max(64, generationWindow / 2);
        var windowFit = (int)Math.Floor(availableInputTokens * cptLang / avgCharsPerWord);

        return (languageCeiling, windowFit, Math.Max(1, Math.Min(languageCeiling, windowFit)));
    }
}

/// <summary>
/// WHICH BOUND DOMINATES — the property a model tier would flip silently.
///
/// At every shipped window the LANGUAGE ceiling (A) is the binding bound, which is why the chunk thresholds
/// read as "language-keyed: Latin 500, Hebrew/Arabic 250" and why the tier looks like a no-op here. That is a
/// property of the current NUMBERS, not of the code: lower the routed num_ctx past the crossover and the
/// window-fit bound (B) takes over, the thresholds drop, and the client/server contract only survives because
/// the endpoint moves with it. These tests pin the dominance, the margin, and the crossover, so a tier that
/// changes the answer has to say so out loud.
/// </summary>
public class ChunkThresholdBoundDominanceTests
{
    private const string CloudProvider = "OpenRouter";
    private const string CloudModel = "google/gemma-4-31b-it";

    /// <summary>
    /// The smallest num_ctx at which the language ceiling (A) still binds, at the shipped 500-word ceiling and
    /// the shipped 1536 + 512 reserves. Derived rather than asserted from thin air:
    ///   A &lt;= B  ⟺  ceiling*cpt/4 &lt;= avail*cpt/6  ⟺  avail &gt;= 1.5 * ceiling = 750
    ///   avail = (numCtx - 1536 - 512) / 2           ⟹  numCtx &gt;= 2*750 + 2048 = 3548
    /// charsPerToken CANCELS, so the crossover is the same for Hebrew and for Latin — pinned below.
    /// </summary>
    private const int CrossoverNumCtx = 3548;

    /// <summary>
    /// THE SHIPPED STATE, local tier: bound (A) wins for both languages, with the margin recorded. Proofread
    /// and LineEdit both resolve num_ctx 4096 (their Ollama entries omit NumCtx, so they bind the
    /// ProviderTuningOptions class default), giving Hebrew 250 against a window fit of 341 and English 500
    /// against 682.
    /// </summary>
    [Theory]
    [InlineData(AiTaskType.Proofread, "he", 250, 341)]
    [InlineData(AiTaskType.Proofread, "en", 500, 682)]
    [InlineData(AiTaskType.LineEdit, "he", 250, 341)]
    [InlineData(AiTaskType.LineEdit, "en", 500, 682)]
    public void ShippedConfig_LanguageCeilingIsTheBindingBound_OnTheLocalTier(
        AiTaskType task, string language, int expectedCeiling, int expectedWindowFit)
    {
        var opt = ChunkThresholdEndpointParityTests.ShippedOptions();
        AssertBounds(opt, task, language, expectedCeiling, expectedWindowFit, 4096, "local (Ollama)");
    }

    /// <summary>
    /// THE SHIPPED STATE, cloud tier. p1-3 gave <c>OpenRouter_Proofread</c> / <c>OpenRouter_LineEdit</c> an
    /// EXPLICIT NumCtx 4096 mirroring the local window, so switching the tier changes only the MODEL and the
    /// client-facing thresholds do not move at all. Asserted as equality against the local tier rather than
    /// just as "250/500", because "the tier does not move the client contract" is the actual claim.
    /// </summary>
    [Theory]
    [InlineData(AiTaskType.Proofread, "he", 250, 341)]
    [InlineData(AiTaskType.Proofread, "en", 500, 682)]
    [InlineData(AiTaskType.LineEdit, "he", 250, 341)]
    [InlineData(AiTaskType.LineEdit, "en", 500, 682)]
    public void ShippedConfig_LanguageCeilingIsTheBindingBound_OnTheCloudTierToo(
        AiTaskType task, string language, int expectedCeiling, int expectedWindowFit)
    {
        var opt = ChunkThresholdEndpointParityTests.ShippedOptionsRoutedTo(
            CloudProvider, CloudModel, AiTaskType.Proofread, AiTaskType.LineEdit);
        AssertBounds(opt, task, language, expectedCeiling, expectedWindowFit, 4096, $"cloud ({CloudProvider})");
    }

    [Theory]
    [InlineData("he")]
    [InlineData("en")]
    [InlineData(null)]
    public void SwitchingToTheCloudTier_DoesNotMoveTheClientFacingThresholds(string? language)
    {
        var local = ChunkThresholdEndpointParityTests.Thresholds(
            ChunkThresholdEndpointParityTests.ShippedOptions(), language);
        var cloud = ChunkThresholdEndpointParityTests.Thresholds(
            ChunkThresholdEndpointParityTests.ShippedOptionsRoutedTo(
                CloudProvider, CloudModel, AiTaskType.Proofread, AiTaskType.LineEdit), language);

        Assert.True(local.ProofreadChunkTargetWords == cloud.ProofreadChunkTargetWords
                    && local.LineEditChunkTargetWords == cloud.LineEditChunkTargetWords,
            $"lang '{language ?? "(null)"}': the cloud tier moved the chunk thresholds " +
            $"(proofread {local.ProofreadChunkTargetWords} -> {cloud.ProofreadChunkTargetWords}, " +
            $"lineEdit {local.LineEditChunkTargetWords} -> {cloud.LineEditChunkTargetWords}). That is allowed, " +
            "but it means the client must re-fetch /api/config/analysis-chunk-thresholds whenever the TIER " +
            "changes, not only when the language does — today it re-fetches on bookLanguage only. Update the " +
            "client before landing a tier that moves these numbers.");
    }

    /// <summary>
    /// THE CROSSOVER, located by scanning rather than by restating the algebra, then pinned to the derived
    /// value. Below it the window-fit bound (B) binds and the thresholds drop below the language ceiling;
    /// at and above it the language ceiling (A) binds. Also pins that the crossover is LANGUAGE-INDEPENDENT
    /// (charsPerToken appears in both bounds and cancels), which is why one number covers he and en.
    /// </summary>
    [Fact]
    public void TheDominantBoundFlips_AtTheDerivedCrossoverWindow()
    {
        foreach (var language in new[] { "he", "en" })
        {
            var firstCeilingDominant = Enumerable.Range(2049, 8192 - 2049)
                .First(numCtx =>
                {
                    var b = ChunkThresholdEndpointParityTests.Recompute(
                        ChunkThresholdEndpointParityTests.WindowedOptions(numCtx),
                        AiTaskType.Proofread, language, 500);
                    return b.WindowFit >= b.LanguageCeiling;
                });

            Assert.True(CrossoverNumCtx == firstCeilingDominant,
                $"lang '{language}': the language ceiling starts binding at num_ctx {firstCeilingDominant}, not " +
                $"the derived {CrossoverNumCtx}. Either a reserve constant or the sizing formula moved; re-derive " +
                "before adjusting this number.");
        }

        // Just below: (B) binds and BOTH languages fall under their ceiling.
        var below = ChunkThresholdEndpointParityTests.WindowedOptions(CrossoverNumCtx - 1);
        var belowHe = ChunkThresholdEndpointParityTests.Recompute(below, AiTaskType.Proofread, "he", 500);
        var belowEn = ChunkThresholdEndpointParityTests.Recompute(below, AiTaskType.Proofread, "en", 500);
        Assert.True(belowHe.Target < belowHe.LanguageCeiling && belowEn.Target < belowEn.LanguageCeiling,
            $"at num_ctx {CrossoverNumCtx - 1} the window must bind: he {belowHe}, en {belowEn}.");
        Assert.Equal(belowHe.WindowFit, belowHe.Target);
        Assert.Equal(belowEn.WindowFit, belowEn.Target);

        // And the ENDPOINT reports the shrunken value, not the ceiling — the whole point of the contract.
        var dto = ChunkThresholdEndpointParityTests.Thresholds(below, "he");
        Assert.Equal(belowHe.Target, dto.ProofreadChunkTargetWords);
        Assert.True(dto.ProofreadChunkTargetWords < 250);
    }

    /// <summary>
    /// THE GUARD a future tier trips. For EVERY registered provider, routing Proofread/LineEdit there must
    /// leave the window at or above the crossover, so the client-facing thresholds stay on the stable
    /// language ceiling. Sweeps <see cref="AiProviderRegistry"/> rather than a hand-listed set, so adding a
    /// provider (or adding a per-task entry with a small window) has to be a deliberate decision here.
    /// </summary>
    [Fact]
    public void EveryRegisteredProvider_KeepsProofreadAndLineEdit_AtOrAboveTheCrossover()
    {
        var failures = new List<string>();

        foreach (var provider in ProviderTuningOutputKnobTests.RegisteredProviderNames())
        {
            foreach (var task in new[] { AiTaskType.Proofread, AiTaskType.LineEdit })
            {
                var opt = ChunkThresholdEndpointParityTests.ShippedOptionsRoutedTo(provider, "tier-model", task);
                var numCtx = BookContextAssembler.ResolveNumCtxForTask(opt, task);
                if (numCtx >= CrossoverNumCtx) continue;

                var he = ChunkThresholdEndpointParityTests.Recompute(
                    opt, task, "he", task == AiTaskType.LineEdit
                        ? opt.EffectiveLineEditChunkTargetWords
                        : opt.EffectiveProofreadChunkTargetWords);
                failures.Add(
                    $"{provider}/{task}: num_ctx {numCtx} is below the crossover {CrossoverNumCtx}, so the " +
                    $"window-fit bound now decides the chunk size (Hebrew target {he.Target} instead of " +
                    $"{he.LanguageCeiling}). That is not automatically wrong, but it MOVES the numbers " +
                    "/api/config/analysis-chunk-thresholds returns, and the client caches them per language " +
                    "only - it does not re-fetch on a route change. Size the window at or above the crossover, " +
                    "or make the client re-fetch when the tier changes.");
            }
        }

        Assert.True(failures.Count == 0, string.Join(" | ", failures));
    }

    private static void AssertBounds(
        AiOptions opt, AiTaskType task, string language,
        int expectedCeiling, int expectedWindowFit, int expectedNumCtx, string tierLabel)
    {
        var configuredCeiling = task == AiTaskType.LineEdit
            ? opt.EffectiveLineEditChunkTargetWords
            : opt.EffectiveProofreadChunkTargetWords;

        var numCtx = BookContextAssembler.ResolveNumCtxForTask(opt, task);
        Assert.True(expectedNumCtx == numCtx,
            $"[{tierLabel}] {task} resolved num_ctx {numCtx}, expected {expectedNumCtx}.");

        var bounds = ChunkThresholdEndpointParityTests.Recompute(opt, task, language, configuredCeiling);
        Assert.True(expectedCeiling == bounds.LanguageCeiling,
            $"[{tierLabel}] {task}/{language}: language ceiling (A) {bounds.LanguageCeiling}, expected {expectedCeiling}.");
        Assert.True(expectedWindowFit == bounds.WindowFit,
            $"[{tierLabel}] {task}/{language}: window fit (B) {bounds.WindowFit}, expected {expectedWindowFit}.");
        Assert.True(bounds.LanguageCeiling < bounds.WindowFit,
            $"[{tierLabel}] {task}/{language}: the LANGUAGE CEILING is supposed to be the binding bound here " +
            $"(A={bounds.LanguageCeiling}, B={bounds.WindowFit}). If the window bound has taken over, the chunk " +
            "thresholds are now a function of the routed model and the client contract has become tier-sensitive.");

        // And the production sizer agrees with the recomputation, at exactly the ceiling.
        var actual = task == AiTaskType.LineEdit
            ? UnifiedAnalysisService.LineEditChunkTargetWordsFor(opt, language)
            : UnifiedAnalysisService.ProofreadChunkTargetWordsFor(opt, language);
        Assert.Equal(bounds.Target, actual);
        Assert.Equal(expectedCeiling, actual);
    }
}
