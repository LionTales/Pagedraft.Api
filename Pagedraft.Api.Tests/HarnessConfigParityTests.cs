using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Xunit;

// USING ALIASES, NOT namespace imports. The two bake-off harnesses live in
// Pagedraft.Api.Tests.LanguageEngine, which BOTH standing verification filters exclude (the
// deterministic suite excludes that namespace outright; the narrow filter never names it). An alias
// binds the two CLASSES without importing the namespace, so nothing here drifts back into it and this
// file's own FullyQualifiedName stays at the assembly root where the filters reach.
using LinguisticHarness = Pagedraft.Api.Tests.LanguageEngine.LinguisticQualityTests;
using ProofreadHarness = Pagedraft.Api.Tests.LanguageEngine.ProofreadQualityTests;

namespace Pagedraft.Api.Tests;

/// <summary>
/// THE HARNESS-VS-SHIPPED COMPLETENESS ORACLE (fix-plan be-c05, review finding P2-6).
///
/// The two live bake-off harnesses cannot read <c>appsettings.json</c> at run time - they build their own
/// in-memory config on purpose, so a sweep can drive any model or provider - and so they restate three
/// things the shipped file also states:
///   1. <c>ProofreadQualityTests.ProofreadModel</c> restates <c>Ai:FeatureModels:Proofread:Model</c>;
///   2. <c>ProofreadQualityTests.BuildHarnessProviderSettings</c> restates
///      <c>Ai:ProviderSettings:Ollama_Proofread</c> and its <c>OpenRouter_Proofread</c> mirror;
///   3. <c>LinguisticQualityTests.LinguisticTuningDefaults</c> / <c>BuildHarnessProviderSettings</c> restate
///      <c>Ai:ProviderSettings:Ollama_LinguisticAnalysis</c> and its <c>OpenRouter_LinguisticAnalysis</c> mirror.
/// Each of those restatements sits under a comment that ASSERTS the two sides agree ("byte-identical",
/// "identical to the values written here"). Until this class existed, nothing checked it: a model swap or a
/// tuning edit that touched only the shipped file left the harness silently measuring a DIFFERENT
/// configuration than production runs, and the comment went on claiming otherwise. Every measurement the
/// harness has ever produced rests on that agreement, so the agreement is what gets pinned.
///
/// TWO PLACEMENT RULES, both load-bearing:
///   • The class is named <c>*ConfigParityTests</c> and lives at the assembly root (namespace
///     <c>Pagedraft.Api.Tests</c>), so BOTH standing filters reach it - the deterministic suite
///     (<c>FullyQualifiedName!~Pagedraft.Api.Tests.LanguageEngine</c>) and the narrow config-guard run
///     (<c>FullyQualifiedName~ConfigParity</c>). Putting it next to the constants it pins would reproduce
///     this finding's own defect: a guard in the excluded namespace never runs. Do not move it.
///   • It REFERENCES the harness members (made <c>internal</c> for this) instead of copying their values.
///     A pin that restates the value binds a look-alike and drifts alongside the thing it was meant to watch.
///
/// These tests are deterministic: no model, no GPU, no network. They read one JSON file off disk.
/// </summary>
public class HarnessConfigParityTests
{
    /// <summary>Resolve BOTH sides through the production precedence (<see cref="ProviderTuningResolver"/>)
    /// and compare every field. Resolving rather than comparing raw entries is deliberate: what has to match
    /// is the EFFECTIVE tuning each side sends, which is what the harness is claiming to reproduce.</summary>
    private static void AssertResolvedTuningAgrees(
        string provider,
        AiTaskType task,
        IReadOnlyDictionary<string, ProviderTuningOptions> harnessSettings,
        string harnessMemberName)
    {
        var shipped = ProviderTuningResolver.Resolve(
            ProviderTuningConfigParityTests.LoadShippedAiOptions().ProviderSettings, provider, task);
        var harness = ProviderTuningResolver.Resolve(harnessSettings, provider, task);

        var diffs = new List<string>();
        void CompareDouble(string field, double s, double h)
        {
            if (Math.Abs(s - h) > 1e-9)
                diffs.Add($"{field}: shipped {s.ToString(CultureInfo.InvariantCulture)} vs harness {h.ToString(CultureInfo.InvariantCulture)}");
        }
        void CompareInt(string field, int s, int h)
        {
            if (s != h) diffs.Add($"{field}: shipped {s} vs harness {h}");
        }

        CompareDouble("Temperature", shipped.Temperature, harness.Temperature);
        CompareInt("NumPredict", shipped.NumPredict, harness.NumPredict);
        CompareInt("MaxTokens", shipped.MaxTokens, harness.MaxTokens);
        CompareInt("NumCtx", shipped.NumCtx, harness.NumCtx);
        CompareDouble("RepeatPenalty", shipped.RepeatPenalty, harness.RepeatPenalty);

        Assert.True(diffs.Count == 0,
            $"The {provider}/{task} tuning the bake-off harness installs no longer matches what the shipped " +
            $"appsettings.json resolves: [{string.Join("; ", diffs)}]. The harness restates these values in " +
            $"{harnessMemberName} under a comment that claims they are identical to production, and it cannot " +
            "read the file at run time. Whichever side moved, move the other one too (or, if the divergence " +
            "is deliberate, say so in that comment and change this pin) - otherwise the next bake-off measures " +
            "a configuration nothing ships.");
    }

    /// <summary>
    /// (1) THE FINDING ITSELF. <c>ProofreadQualityTests.ProofreadModel</c> is the model the single-model
    /// local gold Fact scores, and it exists only to be the shipped default. p2-3 swapped that default from
    /// DictaLM-3.0 to gemma4:12b by editing both sides by hand; this is the check that a future swap does not
    /// have to remember. Ordinal on purpose: an Ollama model tag is case-sensitive on the wire.
    /// </summary>
    [Fact]
    public void TheProofreadHarnessModelConstant_EqualsTheShippedProofreadFeatureModel()
    {
        var features = ProviderTuningConfigParityTests.LoadShippedAiOptions().FeatureModels;
        Assert.True(features is { Count: > 0 }, "Ai:FeatureModels bound empty from the shipped appsettings.json.");
        Assert.True(features!.TryGetValue("Proofread", out var proofread) && proofread != null,
            "Ai:FeatureModels:Proofread is gone from the shipped appsettings.json. The proofread bake-off " +
            "harness names the model that key used to carry, so deleting the key does not make the harness " +
            "stop measuring - it makes it measure a model nothing routes to.");

        Assert.True(string.Equals(proofread!.Model, ProofreadHarness.ProofreadModel, StringComparison.Ordinal),
            $"Ai:FeatureModels:Proofread:Model is '{proofread.Model}' but the proofread bake-off harness scores " +
            $"'{ProofreadHarness.ProofreadModel}' (ProofreadQualityTests.ProofreadModel). The harness builds its own " +
            "in-memory config, so the two are separate literals kept in step by a KEEP IN SYNC comment - and one of " +
            "them has now moved without the other. Every proofread quality number in the plans was measured on the " +
            "harness constant, so a mismatch means the gold scores no longer describe the shipped model.");
    }

    /// <summary>
    /// (2) The LOCAL proofread tuning. Shipped <c>Ollama_Proofread</c> is { Temperature 0.2, NumPredict 4096 }
    /// and deliberately sets neither NumCtx nor RepeatPenalty, so both bind class defaults; resolving both
    /// sides means this pin follows a future edit that ADDS one of those fields on either side.
    /// </summary>
    [Fact]
    public void TheProofreadHarnessOllamaTuning_ResolvesWhatTheShippedOllama_ProofreadEntryResolves()
        => AssertResolvedTuningAgrees(
            "Ollama", AiTaskType.Proofread,
            ProofreadHarness.BuildHarnessProviderSettings("Ollama"),
            "ProofreadQualityTests.BuildHarnessProviderSettings");

    /// <summary>
    /// (3) The CLOUD proofread mirror, which the harness derives from its Ollama entry by swapping the output
    /// knob (NumPredict -> MaxTokens, p1-2). Shipped <c>OpenRouter_Proofread</c> is
    /// { Temperature 0.2, MaxTokens 4096, NumCtx 4096 }.
    /// </summary>
    [Fact]
    public void TheProofreadHarnessCloudMirror_ResolvesWhatTheShippedOpenRouter_ProofreadEntryResolves()
        => AssertResolvedTuningAgrees(
            "OpenRouter", AiTaskType.Proofread,
            ProofreadHarness.BuildHarnessProviderSettings("OpenRouter"),
            "ProofreadQualityTests.BuildHarnessProviderSettings");

    /// <summary>
    /// (4) The LOCAL linguistic tuning: { Temperature 0.2, NumPredict 5120, RepeatPenalty 1.2, NumCtx 16384 }.
    /// Pinned against <c>LinguisticTuningDefaults()</c>, NOT <c>ResolveLinguisticTuning()</c>, because the
    /// latter honours the LINGUISTIC_* sweep env vars - pinning that would turn this test red for anyone
    /// running a decoding-param sweep, which is a legitimate harness use, not config drift.
    /// </summary>
    [Fact]
    public void TheLinguisticHarnessOllamaTuning_ResolvesWhatTheShippedOllama_LinguisticAnalysisEntryResolves()
        => AssertResolvedTuningAgrees(
            "Ollama", AiTaskType.LinguisticAnalysis,
            LinguisticHarness.BuildHarnessProviderSettings("Ollama", LinguisticHarness.LinguisticTuningDefaults()),
            "LinguisticQualityTests.LinguisticTuningDefaults");

    /// <summary>
    /// (5) The CLOUD linguistic mirror. Shipped <c>OpenRouter_LinguisticAnalysis</c> is
    /// { Temperature 0.2, MaxTokens 5120, NumCtx 16384 }. The NumCtx half is the one that matters most: p1-3
    /// exists because a cloud entry without it collapses a whole-book window 16384 -> 4096 and the job still
    /// reports success.
    /// </summary>
    [Fact]
    public void TheLinguisticHarnessCloudMirror_ResolvesWhatTheShippedOpenRouter_LinguisticAnalysisEntryResolves()
        => AssertResolvedTuningAgrees(
            "OpenRouter", AiTaskType.LinguisticAnalysis,
            LinguisticHarness.BuildHarnessProviderSettings("OpenRouter", LinguisticHarness.LinguisticTuningDefaults()),
            "LinguisticQualityTests.BuildHarnessProviderSettings");

    /// <summary>
    /// (6) THE RECORDED EXCEPTION. RepeatPenalty is the one field the cloud mirrors do NOT carry, and that is
    /// a decision, not an oversight: the OpenAI-compatible payload has no such field (p1-3), so writing one
    /// would be dead config that reads as configured. Asserted on BOTH sides so the absence is pinned rather
    /// than looking like something a later editor should "complete".
    ///
    /// The shipped half is checked against the RAW configuration, not the bound object: binding cannot tell an
    /// absent RepeatPenalty from an explicit 1.1, and the claim being recorded is about the KEY.
    /// </summary>
    [Fact]
    public void TheCloudTuningMirrors_OmitRepeatPenalty_OnBothTheShippedAndTheHarnessSide()
    {
        var config = ProviderTuningConfigParityTests.LoadShippedConfiguration();
        foreach (var key in new[] { "OpenRouter_Proofread", "OpenRouter_LinguisticAnalysis" })
        {
            Assert.True(config[$"Ai:ProviderSettings:{key}:RepeatPenalty"] is null,
                $"Ai:ProviderSettings:{key} now carries a RepeatPenalty. The OpenAI-compatible payload has no " +
                "repeat_penalty field (p1-3), so this value is never sent: it is dead config that reads as " +
                "configured, and it makes the harness's cloud mirror - which deliberately omits it - look like " +
                "the incomplete one. Remove it, or, if a cloud family that DOES accept it was added, say so here.");
        }

        var classDefault = new ProviderTuningOptions().RepeatPenalty;
        var proofreadMirror = ProofreadHarness.BuildHarnessProviderSettings("OpenRouter")["OpenRouter_Proofread"];
        Assert.True(Math.Abs(proofreadMirror.RepeatPenalty - classDefault) < 1e-9,
            "ProofreadQualityTests' cloud mirror now sets RepeatPenalty. It must not: the shipped cloud entry " +
            "has no such key and the payload has no such field, so the harness would be measuring a knob " +
            "production neither ships nor sends.");

        var linguisticMirror = LinguisticHarness
            .BuildHarnessProviderSettings("OpenRouter", LinguisticHarness.LinguisticTuningDefaults())["OpenRouter_LinguisticAnalysis"];
        Assert.True(Math.Abs(linguisticMirror.RepeatPenalty - classDefault) < 1e-9,
            "LinguisticQualityTests' cloud mirror now sets RepeatPenalty - same reason as above. Note the LOCAL " +
            "linguistic entry legitimately carries 1.2; it is only the cloud mirror that must leave it unset.");

        // The contrast that makes the exception readable: the OLLAMA linguistic entry DOES carry RepeatPenalty,
        // so the cloud omission is a per-family decision and not a blanket "we never tune repeat_penalty".
        var shippedLocalPenalty = config["Ai:ProviderSettings:Ollama_LinguisticAnalysis:RepeatPenalty"];
        Assert.True(shippedLocalPenalty != null,
            "Ai:ProviderSettings:Ollama_LinguisticAnalysis lost its RepeatPenalty. It is the anti-repetition " +
            "guard for structured linguistic output on a small local model, and the harness still installs " +
            $"{LinguisticHarness.LinguisticTuningDefaults().RepeatPenalty.ToString(CultureInfo.InvariantCulture)} - " +
            "so dropping it here means the bake-off and production now decode differently.");
        Assert.True(
            double.TryParse(shippedLocalPenalty, NumberStyles.Float, CultureInfo.InvariantCulture, out var localPenalty)
            && Math.Abs(localPenalty - LinguisticHarness.LinguisticTuningDefaults().RepeatPenalty) < 1e-9,
            $"Ai:ProviderSettings:Ollama_LinguisticAnalysis:RepeatPenalty is '{shippedLocalPenalty}' but the " +
            "harness installs " +
            $"{LinguisticHarness.LinguisticTuningDefaults().RepeatPenalty.ToString(CultureInfo.InvariantCulture)}.");
    }
}
