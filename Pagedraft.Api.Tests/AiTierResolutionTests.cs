using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Services.Analysis.Hebrew;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// THE TIER PRECEDENCE, RUNG BY RUNG (model-tier-fast-thinking plan, p3-2), asserted through the ROUTER's
/// own resolution path.
///
/// <code>
///   1. {task}_{lang}   e.g. Proofread_en        - English Proofread/LineEdit only, UNTIERED
///   2. {task}_{tier}   e.g. Proofread_thinking  - allowlisted tasks only, Thinking only
///   3. {task}
///   4. DefaultProvider / DefaultModel
/// </code>
///
/// THE ORDER IS THE ENFORCEMENT, not a style choice. p2-4 gave a GO for HEBREW Proofread and an explicit
/// NO-GO for <c>Proofread_en</c> (never measured against cloud). <c>Proofread_en</c> is NOT an
/// <see cref="AiTaskType"/> - it is a FeatureModels key SUFFIX - so "Hebrew only" cannot be expressed in the
/// task allowlist and has to be expressed as precedence: rung 1 outranks rung 2, therefore an English book
/// on the thinking tier resolves the local English model and can never reach the tier rung. p3-2's todo text
/// proposed the INVERSE order (<c>{task}_{lang}_{tier}</c> first, then <c>{task}_{tier}</c>, then
/// <c>{task}_{lang}</c>), under which an English book WOULD reach the tier rung and violate that NO-GO. The
/// negative rungs are pinned here as hard as the positive ones for exactly that reason.
///
/// Class named *AiRouter* so the standing deterministic filter picks it up.
/// </summary>
public class AiRouterTierPrecedenceTests
{
    private const string Local = "Ollama";
    private const string Cloud = "OpenRouter";

    internal static AiOptions Options(params (string Key, string Provider, string Model)[] features)
    {
        var opt = new AiOptions
        {
            DefaultProvider = "DefaultProv",
            DefaultModel = "default-model",
            FeatureModels = new Dictionary<string, FeatureModelOptions>(StringComparer.Ordinal)
        };
        foreach (var (key, provider, model) in features)
            opt.FeatureModels[key] = new FeatureModelOptions { Provider = provider, Model = model };
        return opt;
    }

    internal static (string Provider, string Model) Route(
        AiOptions opt, AiTaskType task, string? language, AiTier tier)
    {
        var selection = AiRouter.ResolveSelectionForTest(
            new AiRequest { InputText = "x", TaskType = task, Language = language!, Tier = tier }, opt);
        return (selection.Provider, selection.Model);
    }

    // ── Rung 1: {task}_{lang} ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE E3 ASSERTION, and the single most important test in this file. All three rungs are configured and
    /// the book is on the THINKING tier - and English Proofread still resolves the LOCAL English model,
    /// because the language rung outranks the tier rung. Under the inverted order in p3-2's todo text this
    /// returns the cloud model and the p2-4 Proofread_en NO-GO is violated silently.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("en-US")]
    [InlineData("EN-GB")]
    public void EnglishProofread_OnTheThinkingTier_StillResolvesTheLocalEnglishModel(string language)
    {
        var opt = Options(
            ("Proofread_en", Local, "local-en"),
            ("Proofread_thinking", Cloud, "cloud"),
            ("Proofread", Local, "local-he"));

        Assert.Equal((Local, "local-en"), Route(opt, AiTaskType.Proofread, language, AiTier.Thinking));
        // and identically on Fast, i.e. the tier changed nothing at all for English.
        Assert.Equal(
            Route(opt, AiTaskType.Proofread, language, AiTier.Fast),
            Route(opt, AiTaskType.Proofread, language, AiTier.Thinking));
    }

    /// <summary>The language rung is scoped to the two tasks that ship a <c>_en</c> variant, unchanged from
    /// the pre-tier router.</summary>
    [Fact]
    public void TheLanguageRung_AppliesToProofreadAndLineEditOnly()
    {
        var opt = Options(
            ("LinguisticAnalysis_en", Cloud, "should-never-be-read"),
            ("LinguisticAnalysis", Local, "local"));

        Assert.Equal((Local, "local"), Route(opt, AiTaskType.LinguisticAnalysis, "en", AiTier.Fast));
    }

    // ── Rung 2: {task}_{tier} ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(AiTaskType.Proofread, "he")]
    [InlineData(AiTaskType.Proofread, "iw")]
    [InlineData(AiTaskType.LinguisticAnalysis, "he")]
    [InlineData(AiTaskType.LinguisticAnalysis, "en")] // no _en variant exists for this task, so the tier binds
    public void AnAllowlistedTask_OnTheThinkingTier_ResolvesTheTierEntry(AiTaskType task, string language)
    {
        var opt = Options(
            ($"{task}_thinking", Cloud, "cloud"),
            ($"{task}", Local, "local"));

        Assert.Equal((Cloud, "cloud"), Route(opt, task, language, AiTier.Thinking));
        Assert.Equal((Local, "local"), Route(opt, task, language, AiTier.Fast));
    }

    /// <summary>
    /// THE E2 ASSERTION at runtime: for EVERY task outside <see cref="AiTierPolicy.TieredTasks"/> the tier is
    /// ignored, asserted with a <c>{task}_thinking</c> entry deliberately PRESENT so the test proves the
    /// allowlist is consulted, not merely that the key is absent from the shipped config.
    /// </summary>
    [Fact]
    public void ANonAllowlistedTask_IgnoresTheTier_EvenWhenATierEntryExistsForIt()
    {
        var failures = new List<string>();

        foreach (var task in Enum.GetValues<AiTaskType>().Where(t => !AiTierPolicy.IsTiered(t)))
        {
            var opt = Options(
                ($"{task}_thinking", Cloud, "cloud-should-not-be-reachable"),
                ($"{task}", Local, "local"));

            var thinking = Route(opt, task, "he", AiTier.Thinking);
            if (thinking != (Local, "local"))
                failures.Add($"{task} is not in AiTierPolicy.TieredTasks but resolved {thinking} on the " +
                             "thinking tier. Either the allowlist stopped being consulted, or this task was " +
                             "added to it without a measurement - p2-4 gave a GO for LinguisticAnalysis and " +
                             "HEBREW Proofread only.");
        }

        Assert.True(failures.Count == 0, string.Join(" | ", failures));
    }

    /// <summary>
    /// THE NEGATIVE RUNG: <c>{task}_{lang}_{tier}</c> is NOT consulted and no such key may exist. Configured
    /// here deliberately; the resolution must ignore it and fall to the bare task entry.
    /// </summary>
    [Theory]
    [InlineData("Proofread_en_thinking")]
    [InlineData("Proofread_thinking_en")]
    public void ACompositeLanguageAndTierKey_IsNeverARung(string compositeKey)
    {
        var opt = Options(
            (compositeKey, Cloud, "cloud-should-not-be-reachable"),
            ("Proofread", Local, "local"));

        Assert.Equal((Local, "local"), Route(opt, AiTaskType.Proofread, "en", AiTier.Thinking));
        Assert.Equal((Local, "local"), Route(opt, AiTaskType.Proofread, "he", AiTier.Thinking));
    }

    /// <summary>A <c>{task}_fast</c> key is dead config: Fast IS the untiered baseline, so no tier rung is
    /// consulted on it at all.</summary>
    [Fact]
    public void AFastTierKey_IsNeverARung()
    {
        var opt = Options(
            ("Proofread_fast", Cloud, "should-not-be-reachable"),
            ("Proofread", Local, "local"));

        Assert.Equal((Local, "local"), Route(opt, AiTaskType.Proofread, "he", AiTier.Fast));
        Assert.Null(AiTierPolicy.TierKeyFor(AiTaskType.Proofread, AiTier.Fast));
    }

    // ── Rungs 3 and 4, and the shared both-non-empty predicate ────────────────────────────────────────────

    [Fact]
    public void WithNoEntryAtAll_BothTiersFallToTheConfiguredDefaults()
    {
        var opt = Options();

        Assert.Equal(("DefaultProv", "default-model"), Route(opt, AiTaskType.Proofread, "he", AiTier.Fast));
        Assert.Equal(("DefaultProv", "default-model"), Route(opt, AiTaskType.Proofread, "he", AiTier.Thinking));
    }

    /// <summary>
    /// THE SHARED BOTH-NON-EMPTY PREDICATE, applied to the NEW rung. A half-configured tier entry (only
    /// Provider, or only Model) must fall THROUGH to the bare task entry rather than route with a blank half
    /// - which for the thinking tier is also the safe direction, since falling through means staying local.
    /// </summary>
    [Theory]
    [InlineData(Cloud, "")]
    [InlineData("", "cloud")]
    [InlineData("", "")]
    public void AHalfConfiguredTierEntry_FallsThroughToTheBareTaskEntry(string provider, string model)
    {
        var opt = Options(
            ("Proofread_thinking", provider, model),
            ("Proofread", Local, "local"));

        Assert.Equal((Local, "local"), Route(opt, AiTaskType.Proofread, "he", AiTier.Thinking));
    }

    /// <summary>
    /// The same predicate on the LANGUAGE rung, and the consequence stated out loud because it is the one
    /// way E3's protection can be lost: a half-configured <c>Proofread_en</c> falls through, and the next
    /// rung is the TIER rung, so an English book on the thinking tier would reach the cloud. That is correct
    /// fall-through behaviour, not a bug - but it is why
    /// <see cref="AiTierConfigParityTests.TheShippedProofreadEnEntry_IsFullyConfigured_SoEnglishCannotReachTheTierRung"/>
    /// pins the shipped entry as fully configured.
    /// </summary>
    [Fact]
    public void AHalfConfiguredLanguageEntry_FallsThroughToTheTierRung()
    {
        var opt = Options(
            ("Proofread_en", Local, ""),               // half-configured
            ("Proofread_thinking", Cloud, "cloud"),
            ("Proofread", Local, "local"));

        Assert.Equal((Cloud, "cloud"), Route(opt, AiTaskType.Proofread, "en", AiTier.Thinking));
    }

    /// <summary>Regression pin: the pre-tier language rung still behaves exactly as it did, for both tasks
    /// that have one, and a non-English tag does not reach it.</summary>
    [Theory]
    [InlineData(AiTaskType.Proofread, "en", "local-en")]
    [InlineData(AiTaskType.Proofread, "he", "local")]
    [InlineData(AiTaskType.LineEdit, "en-US", "local-en")]
    [InlineData(AiTaskType.LineEdit, "iw", "local")]
    [InlineData(AiTaskType.LineEdit, "", "local")]
    public void TheLanguageRung_IsUnchangedFromThePreTierRouter(AiTaskType task, string language, string expected)
    {
        var opt = Options(
            ($"{task}_en", Local, "local-en"),
            ($"{task}", Local, "local"));

        Assert.Equal((Local, expected), Route(opt, task, language, AiTier.Fast));
    }

    /// <summary>An unstamped request (null Tier) means Fast, so a call site that was never taught about the
    /// tier can never route to paid cloud by omission.</summary>
    [Fact]
    public void AnUnstampedRequest_ResolvesAsFast()
    {
        var opt = Options(
            ("Proofread_thinking", Cloud, "cloud"),
            ("Proofread", Local, "local"));

        var selection = AiRouter.ResolveSelectionForTest(
            new AiRequest { InputText = "x", TaskType = AiTaskType.Proofread, Language = "he" }, opt);

        Assert.Equal(Local, selection.Provider);
        Assert.Equal("local", selection.Model);
    }
}

/// <summary>
/// ROUTER-vs-RESOLVER AGREEMENT (p3-2). <see cref="LinguisticModelResolver"/> exists because a staleness
/// gate resolving differently from the router is the failure mode the extraction was meant to make
/// impossible - so the tier had to be added to BOTH, and these tests assert they agree for EVERY
/// (task, language, tier) triple rather than for the handful anyone thought to check.
///
/// p3-2 went one step further than "two implementations that agree": the router now DELEGATES to the
/// resolver, so there is one implementation and it cannot drift from itself. These tests are therefore
/// wiring assertions as much as behaviour assertions - they go red if someone re-inlines the precedence into
/// <c>AiRouter.ResolveSelection</c>, which is exactly how the divergence would come back.
///
/// Class named *LinguisticModelResolver* so the standing deterministic filter picks it up.
/// </summary>
public class LinguisticModelResolverTierAgreementTests
{
    private static readonly string?[] Languages = { "he", "he-IL", "iw", "en", "en-US", "EN", "", "zz-unknown", null };
    private static readonly AiTier[] Tiers = { AiTier.Fast, AiTier.Thinking };

    /// <summary>
    /// A config with EVERY rung populated for every task, so no triple silently falls to the defaults and
    /// agrees vacuously: a bare key per task, an <c>_en</c> key for every task (including the ones that must
    /// ignore it), and a <c>_thinking</c> key for every task (including the ones outside the allowlist).
    /// </summary>
    private static AiOptions FullyPopulated()
    {
        var features = new List<(string, string, string)>();
        foreach (var task in Enum.GetValues<AiTaskType>())
        {
            features.Add(($"{task}", "Ollama", $"bare-{task}"));
            features.Add(($"{task}_en", "Ollama", $"en-{task}"));
            features.Add(($"{task}_thinking", "OpenRouter", $"thinking-{task}"));
            features.Add(($"{task}_en_thinking", "OpenRouter", $"composite-{task}"));
        }
        return AiRouterTierPrecedenceTests.Options(features.ToArray());
    }

    [Fact]
    public void RouterAndResolver_ResolveIdentically_ForEveryTaskLanguageAndTier()
        => AssertAgreement(FullyPopulated(), "fully-populated");

    /// <summary>The same sweep against the REAL shipped appsettings.json, so a config edit that splits the
    /// two surfaces is caught on the config that actually ships.</summary>
    [Fact]
    public void RouterAndResolver_ResolveIdentically_OnTheShippedConfig()
        => AssertAgreement(ProviderTuningConfigParityTests.LoadShippedAiOptions(), "shipped appsettings.json");

    /// <summary>Sparse config: every triple falls through to the defaults. Guards the last rung, where the
    /// router applies its own "Ollama" / "qwen2.5:14b" literals if the configured defaults are null.</summary>
    [Fact]
    public void RouterAndResolver_ResolveIdentically_WhenNothingIsConfigured()
        => AssertAgreement(AiRouterTierPrecedenceTests.Options(), "empty FeatureModels");

    private static void AssertAgreement(AiOptions opt, string label)
    {
        var failures = new List<string>();

        foreach (var task in Enum.GetValues<AiTaskType>())
        foreach (var language in Languages)
        foreach (var tier in Tiers)
        {
            var viaResolver = LinguisticModelResolver.ResolveForTask(opt, task, language, tier);
            var viaRouter = AiRouter.ResolveSelectionForTest(
                new AiRequest { InputText = "x", TaskType = task, Language = language!, Tier = tier }, opt);

            if (viaResolver.provider != viaRouter.Provider || viaResolver.model != viaRouter.Model)
                failures.Add($"[{label}] {task}/{language ?? "(null)"}/{tier}: the resolver says " +
                             $"({viaResolver.provider}, {viaResolver.model}) but the router routes to " +
                             $"({viaRouter.Provider}, {viaRouter.Model}). Every staleness gate and every budget " +
                             "sizer reads the resolver while the model that ACTUALLY runs is the router's - so a " +
                             "divergence here means caches are keyed on a model nobody ran.");
        }

        Assert.True(failures.Count == 0, string.Join(" | ", failures));
    }

    /// <summary>
    /// The 2-arg overload the four non-allowlisted consumers still call
    /// (ChapterBriefService, BookSummaryService x2, BookReviewService) must equal the tier-aware form on
    /// Fast, for EVERY task - that is what makes "those call sites compile untouched" also mean "those call
    /// sites behave untouched".
    /// </summary>
    [Fact]
    public void TheTwoArgOverload_EqualsTheTierAwareFormOnFast_ForEveryTask()
    {
        foreach (var opt in new[] { FullyPopulated(), ProviderTuningConfigParityTests.LoadShippedAiOptions() })
        foreach (var task in Enum.GetValues<AiTaskType>())
        {
            Assert.Equal(
                LinguisticModelResolver.ResolveForTask(opt, task),
                LinguisticModelResolver.ResolveForTask(opt, task, AiTier.Fast));
            Assert.Equal(
                LinguisticModelResolver.ResolveForTask(opt, task),
                LinguisticModelResolver.ResolveForTask(opt, task, language: null, AiTier.Fast));
        }
    }

    /// <summary>
    /// THE E2 ASSERTION on the resolver side (the router side is
    /// <c>AiRouterTierPrecedenceTests.ANonAllowlistedTask_IgnoresTheTier_EvenWhenATierEntryExistsForIt</c>):
    /// a task outside the allowlist resolves identically on both tiers, with a tier entry present.
    /// </summary>
    [Fact]
    public void ANonAllowlistedTask_ResolvesIdenticallyOnBothTiers()
    {
        var opt = FullyPopulated();
        var failures = new List<string>();

        foreach (var task in Enum.GetValues<AiTaskType>().Where(t => !AiTierPolicy.IsTiered(t)))
        foreach (var language in Languages)
        {
            var fast = LinguisticModelResolver.ResolveForTask(opt, task, language, AiTier.Fast);
            var thinking = LinguisticModelResolver.ResolveForTask(opt, task, language, AiTier.Thinking);
            if (fast != thinking)
                failures.Add($"{task}/{language ?? "(null)"}: Fast resolves {fast} but Thinking resolves {thinking}.");
        }

        Assert.True(failures.Count == 0, string.Join(" | ", failures));
    }

    /// <summary>Symmetric statement, so this file also records what the tier IS allowed to do: each
    /// allowlisted task DOES move on Hebrew, or the feature is a silent no-op.</summary>
    [Fact]
    public void EveryAllowlistedTask_ActuallyMovesOnTheThinkingTier_InTheShippedConfig()
    {
        var opt = ProviderTuningConfigParityTests.LoadShippedAiOptions();

        foreach (var task in AiTierPolicy.TieredTasks)
        {
            var fast = LinguisticModelResolver.ResolveForTask(opt, task, "he", AiTier.Fast);
            var thinking = LinguisticModelResolver.ResolveForTask(opt, task, "he", AiTier.Thinking);
            Assert.True(fast != thinking,
                $"{task} is in AiTierPolicy.TieredTasks but resolves the same route ({fast}) on both tiers in the " +
                "shipped config, so the tier would be a silent no-op for it. Add Ai:FeatureModels:" +
                $"{task}_thinking, or remove the task from the allowlist.");
        }
    }

    /// <summary>Defensive parse: the stored column is a free-text string, so everything that is not the
    /// exact thinking token must degrade to Fast rather than throw or fail open into paid cloud routing.</summary>
    [Theory]
    [InlineData(null, AiTier.Fast)]
    [InlineData("", AiTier.Fast)]
    [InlineData("   ", AiTier.Fast)]
    [InlineData("fast", AiTier.Fast)]
    [InlineData("Fast", AiTier.Fast)]
    [InlineData("banana", AiTier.Fast)]
    [InlineData("Thinking2", AiTier.Fast)]
    [InlineData("thinking", AiTier.Thinking)]
    [InlineData("Thinking", AiTier.Thinking)]
    [InlineData("THINKING", AiTier.Thinking)]
    [InlineData("  thinking  ", AiTier.Thinking)]
    public void TheStoredTierValue_IsParsedDefensively(string? stored, AiTier expected)
        => Assert.Equal(expected, AiTierPolicy.Parse(stored));

    [Fact]
    public void TheStoredForm_RoundTripsThroughTheParser()
    {
        foreach (var tier in Tiers)
            Assert.Equal(tier, AiTierPolicy.Parse(AiTierPolicy.ToStoredValue(tier)));
    }
}

/// <summary>
/// (E4) THE CONFIG-PARITY GUARD, mirroring p1-3's <c>CloudTuningCoverageConfigParityTests</c> pattern:
/// binds the REAL appsettings.json and fails on any config state that would widen the tier past what p2-4
/// measured. It is the layer that survives a future config editor who has never read the plan - E1/E2/E3 are
/// code, this one watches the file.
///
/// Class named *ConfigParity* so the standing <c>--filter "FullyQualifiedName~ConfigParity"</c> run picks it
/// up with the other config guards.
/// </summary>
public class AiTierConfigParityTests
{
    private const string TierSuffix = "_" + AiTierPolicy.ThinkingKeySuffix;

    private static IReadOnlyDictionary<string, FeatureModelOptions> ShippedFeatureModels()
    {
        var opt = ProviderTuningConfigParityTests.LoadShippedAiOptions();
        Assert.True(opt.FeatureModels is { Count: > 0 }, "Ai:FeatureModels bound empty from the shipped appsettings.json.");
        return opt.FeatureModels!;
    }

    /// <summary>Config keys that are documentation, not routing.</summary>
    private static bool IsComment(string key) => key.StartsWith("_comment", StringComparison.OrdinalIgnoreCase);

    // ── The four checks the plan names for this guard ─────────────────────────────────────────────────────

    /// <summary>
    /// (1) No <c>{task}_thinking</c> key for a task outside <see cref="AiTierPolicy.TieredTasks"/>. Enumerates
    /// the ENUM rather than the config keys, so a NEWLY ADDED AiTaskType cannot inherit a tier silently -
    /// the same enum-completeness discipline <c>ProviderTuningConfigParityTests</c> already uses.
    /// </summary>
    [Fact]
    public void NoTierEntryExists_ForATaskOutsideTheAllowlist()
    {
        var features = ShippedFeatureModels();
        var offenders = Enum.GetValues<AiTaskType>()
            .Where(t => !AiTierPolicy.IsTiered(t))
            .Select(t => $"{t}{TierSuffix}")
            .Where(features.ContainsKey)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Ai:FeatureModels carries a thinking-tier entry for a task the tier is not allowed to move: [" +
            string.Join(", ", offenders) + "]. p2-4's GO is PER TASK - LinguisticAnalysis and HEBREW Proofread " +
            "only. BookReview and Proofread_en are NO-GO because they are UNMEASURED (the BookReview bake-off " +
            "harness cannot even route OpenRouter); LineEdit/Summarization/AnalysisRepair/GenericChat are " +
            "unmeasured; TermRepair is routing-only by a standing cost/privacy decision. Adding the key is not " +
            "enough to make it route either - AiTierPolicy.TieredTasks would have to change too - so this key " +
            "is dead config that documents a decision nobody made.");
    }

    /// <summary>
    /// (2) No <c>Proofread_en_{tier}</c> in ANY spelling. The composite key is deliberately not a rung, so
    /// writing one produces config that silently does nothing while reading as though English were tiered.
    /// Checked as a SHAPE over every key rather than against two hand-written spellings.
    /// </summary>
    [Fact]
    public void NoCompositeLanguageAndTierKey_ExistsInAnyForm()
    {
        var offenders = ShippedFeatureModels().Keys
            .Where(k => !IsComment(k))
            .Where(k => k.Contains("_en", StringComparison.OrdinalIgnoreCase)
                        && k.Contains(TierSuffix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Ai:FeatureModels carries a composite language+tier key: [" + string.Join(", ", offenders) + "]. " +
            "There is no {task}_{lang}_{tier} rung and there must not be one: the whole way 'the GO is for " +
            "HEBREW Proofread only' is enforced is that {task}_{lang} OUTRANKS {task}_{tier}, so English " +
            "resolves Proofread_en and never reaches the tier. A composite key is unreachable config that " +
            "reads like the opposite.");
    }

    /// <summary>
    /// (3) TermRepair must not enter the allowlist. A SEPARATELY JUSTIFIED assertion rather than a special
    /// case of the sweep above, because its exclusion rests on a different argument: it is routing-only "by
    /// decision (cost/privacy), not because it fails a bar" (appsettings <c>_comment_TermRepair</c>), so it
    /// would stay excluded even if a future measurement showed cloud winning on it.
    /// </summary>
    [Fact]
    public void TermRepair_IsNotInTheAllowlist()
    {
        Assert.False(AiTierPolicy.IsTiered(AiTaskType.TermRepair),
            "TermRepair was added to AiTierPolicy.TieredTasks. Its cloud entry is deliberately ROUTING-ONLY by a " +
            "standing cost/privacy decision that is INDEPENDENT of quality (appsettings _comment_TermRepair), and " +
            "the plan lists it under 'Deferred / out of scope' with 'must not be swept in implicitly'. A quality " +
            "measurement is not sufficient to reverse this; the cost/privacy decision has to be reversed first.");
    }

    /// <summary>
    /// (4) A task outside the allowlist must RESOLVE identically on both tiers, in the shipped config. The
    /// key-shape checks above are static; this is the behavioural half, and it is what actually protects the
    /// NO-GO tasks.
    /// </summary>
    [Fact]
    public void EveryNonAllowlistedTask_ResolvesIdenticallyOnBothTiers_InTheShippedConfig()
    {
        var opt = ProviderTuningConfigParityTests.LoadShippedAiOptions();
        var failures = new List<string>();

        foreach (var task in Enum.GetValues<AiTaskType>().Where(t => !AiTierPolicy.IsTiered(t)))
        foreach (var language in new string?[] { "he", "en", null })
        {
            var fast = LinguisticModelResolver.ResolveForTask(opt, task, language, AiTier.Fast);
            var thinking = LinguisticModelResolver.ResolveForTask(opt, task, language, AiTier.Thinking);
            if (fast != thinking)
                failures.Add($"{task}/{language ?? "(null)"}: {fast} on fast but {thinking} on thinking.");
        }

        Assert.True(failures.Count == 0, string.Join(" | ", failures));
    }

    // ── Additional guards this todo found were load-bearing ───────────────────────────────────────────────

    /// <summary>
    /// The allowlist itself, pinned against p2-4's verdict table. Any edit to
    /// <see cref="AiTierPolicy.TieredTasks"/> turns this red and has to state which measurement justified it.
    /// </summary>
    [Fact]
    public void TheAllowlist_IsExactlyTheTasksP2_4GaveAGo()
    {
        Assert.Equal(
            new[] { AiTaskType.LinguisticAnalysis, AiTaskType.Proofread }.OrderBy(t => t).ToArray(),
            AiTierPolicy.TieredTasks.OrderBy(t => t).ToArray());

        // Named negatives, so the failure message says WHY rather than just showing a set difference.
        Assert.False(AiTierPolicy.IsTiered(AiTaskType.BookReview),
            "BookReview is NO-GO because it is UNMEASURED: BookReviewQualityTests.CreateRouter hard-codes " +
            "provider=Ollama and never registers OpenRouter, so no cloud-routed BookReview has ever been run. " +
            "Close that harness gap and run one end to end before adding it.");
        Assert.False(AiTierPolicy.IsTiered(AiTaskType.LineEdit),
            "LineEdit is unmeasured against cloud and has its own decoding quirk (Ollama_LineEdit RepeatPenalty " +
            "1.3 exists because Dicta fell into a 335s repetition loop). It needs its own bake-off.");
    }

    /// <summary>
    /// THE E3 DEPENDENCY, made explicit. Rung 1 only protects English if it actually FIRES, and the shared
    /// both-non-empty predicate means a half-configured <c>Proofread_en</c> falls THROUGH - to the tier rung.
    /// So "English cannot reach the tier" rests on this entry staying fully configured while
    /// <c>Proofread_thinking</c> exists. Pinned rather than assumed.
    /// </summary>
    [Fact]
    public void TheShippedProofreadEnEntry_IsFullyConfigured_SoEnglishCannotReachTheTierRung()
    {
        var features = ShippedFeatureModels();
        if (!features.ContainsKey($"{AiTaskType.Proofread}{TierSuffix}"))
            return; // no Proofread tier entry shipped, so there is nothing for English to fall through to.

        Assert.True(features.TryGetValue("Proofread_en", out var en),
            "Ai:FeatureModels:Proofread_thinking exists but Proofread_en does not. Without the English entry the " +
            "language rung cannot fire, so an English book on the thinking tier falls straight to the tier rung " +
            "and routes to cloud - violating p2-4's Proofread_en NO-GO.");
        Assert.False(string.IsNullOrEmpty(en!.Provider) || string.IsNullOrEmpty(en.Model),
            "Ai:FeatureModels:Proofread_en is HALF-configured (Provider='" + en.Provider + "', Model='" + en.Model +
            "'). The shared both-non-empty predicate makes a half-configured entry fall through to the NEXT rung, " +
            "which is the tier rung, so an English book on the thinking tier would silently route to cloud.");

        // And the end-to-end statement, not just the field check.
        var opt = ProviderTuningConfigParityTests.LoadShippedAiOptions();
        foreach (var language in new[] { "en", "en-US", "EN-GB" })
            Assert.Equal(
                LinguisticModelResolver.ResolveForTask(opt, AiTaskType.Proofread, language, AiTier.Fast),
                LinguisticModelResolver.ResolveForTask(opt, AiTaskType.Proofread, language, AiTier.Thinking));
    }

    /// <summary>
    /// A shipped tier entry must be fully configured, or the tier is a silent no-op that still shows as
    /// "thinking" in the UI. (The FE-visible half of this - falling back VISIBLY - is p3-4's mandate.)
    /// </summary>
    [Fact]
    public void EveryShippedTierEntry_IsFullyConfigured()
    {
        foreach (var (key, value) in ShippedFeatureModels().Where(kv => kv.Key.EndsWith(TierSuffix, StringComparison.Ordinal)))
            Assert.False(string.IsNullOrEmpty(value.Provider) || string.IsNullOrEmpty(value.Model),
                $"Ai:FeatureModels:{key} is half-configured, so the both-non-empty predicate makes it fall through " +
                "and the thinking tier silently runs the local model.");
    }

    /// <summary>
    /// THE p1 COUPLING. A tier entry names a PROVIDER, and the provider is what builds the
    /// <c>{Provider}_{TaskType}</c> tuning key that sizes NumCtx and the output reservation (p1-1..p1-3). A
    /// tier entry pointing at a provider with no per-task tuning re-opens exactly the silent-default hole
    /// p1-3 closed: the flat entry binds the 4096 class default, which is &gt; 0 and therefore WINS.
    /// </summary>
    [Fact]
    public void EveryShippedTierEntry_HasPerTaskTuningForItsProvider()
    {
        var opt = ProviderTuningConfigParityTests.LoadShippedAiOptions();

        foreach (var task in AiTierPolicy.TieredTasks)
        {
            var key = $"{task}{TierSuffix}";
            if (!opt.FeatureModels!.TryGetValue(key, out var entry)) continue;

            var tuningKey = ProviderTuningResolver.TaskKey(entry.Provider, task);
            Assert.True(opt.ProviderSettings!.ContainsKey(tuningKey),
                $"Ai:FeatureModels:{key} routes {task} at {entry.Provider}, but Ai:ProviderSettings:{tuningKey} " +
                "does not exist. The flat provider entry would then bind ProviderTuningOptions' 4096 class " +
                "default - which is > 0 and therefore WINS the lookup rather than falling through - so the tier " +
                "would silently size this task's window (and, via EffectiveChunkTargetWords, the client's " +
                "chunk-threshold contract) against a number nobody chose. That is the p1-3 bug, via the tier.");
        }
    }

    /// <summary>
    /// No key in <c>Ai:FeatureModels</c> may have a shape the resolver does not consult. Catches a typo
    /// (<c>Proofread_Thinking</c>, <c>proofread_thinking</c>, <c>Proofread_he</c>) which would otherwise be
    /// invisible config that looks configured and does nothing.
    /// </summary>
    [Fact]
    public void EveryFeatureModelsKey_HasAShapeTheResolverActuallyConsults()
    {
        var recognised = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in Enum.GetValues<AiTaskType>())
        {
            recognised.Add(task.ToString());
            if (task is AiTaskType.Proofread or AiTaskType.LineEdit) recognised.Add($"{task}_en");
            if (AiTierPolicy.IsTiered(task)) recognised.Add($"{task}{TierSuffix}");
        }

        var unrecognised = ShippedFeatureModels().Keys
            .Where(k => !IsComment(k) && !recognised.Contains(k))
            .ToList();

        Assert.True(unrecognised.Count == 0,
            "Ai:FeatureModels contains key(s) no resolution rung will ever look up: [" +
            string.Join(", ", unrecognised) + "]. The consulted shapes are exactly: '{Task}', '{Task}_en' " +
            "(Proofread/LineEdit only) and '{Task}_" + AiTierPolicy.ThinkingKeySuffix + "' (allowlisted tasks " +
            "only), all case-sensitive. A key with any other shape is dead config that reads as though it routes.");
    }

    /// <summary>
    /// The tier does NOT flip any shipped default: every book is on fast until it opts in. p2-4's GO is a
    /// quality finding, and the thinking tier sends an unpublished manuscript to a third party.
    /// </summary>
    [Fact]
    public void TheShippedDefaultRoute_IsUnchangedByTheTier()
    {
        var opt = ProviderTuningConfigParityTests.LoadShippedAiOptions();

        Assert.Equal(AiTier.Fast, AiTierPolicy.Parse(null));
        Assert.Equal(("Ollama", "gemma4:12b"), LinguisticModelResolver.ResolveForTask(opt, AiTaskType.Proofread, "he", AiTier.Fast));
        Assert.Equal(("Ollama", "gemma4:12b"), LinguisticModelResolver.ResolveForTask(opt, AiTaskType.LinguisticAnalysis, "he", AiTier.Fast));
        Assert.Equal(("Ollama", "gemma4:12b"), LinguisticModelResolver.ResolveForTask(opt, AiTaskType.Proofread, "en", AiTier.Fast));

        // And a brand-new Book entity is on the local tier by construction, not merely by config.
        Assert.Equal(AiTier.Fast, AiTierPolicy.Parse(new Book().AiTier));
    }
}

/// <summary>
/// THE CLIENT/SERVER CHUNK-THRESHOLD CONTRACT UNDER A TIER (p3-2, extending p1-4).
///
/// p1-4 pinned that <c>GET /api/config/analysis-chunk-thresholds</c> and <c>RunAsync</c> return the same
/// number for the same LANGUAGE. Once a tier can change which provider Proofread/LineEdit route to, the pair
/// becomes (language, TIER) - because the sizing's bound (B) reads that provider's NumCtx. p3-1 stated the
/// numbers to protect: <c>OpenRouter_Proofread</c> declares NumCtx 4096, equal to the local effective 4096,
/// and the crossover below which bound (B) starts binding is 3548 - so at the SHIPPED values the thresholds
/// do NOT move. That is PINNED here rather than assumed, together with the mutation that proves the pin is
/// not vacuous.
///
/// Class named *ChunkThreshold* so the standing deterministic filter picks it up.
/// </summary>
public class ChunkThresholdTierParityTests
{
    public static TheoryData<string?, AiTier> LanguagesAndTiers()
    {
        var data = new TheoryData<string?, AiTier>();
        foreach (var language in new string?[] { "en", "en-US", "he", "iw", "ar", null, "", "zz-unknown" })
        foreach (var tier in new[] { AiTier.Fast, AiTier.Thinking })
            data.Add(language, tier);
        return data;
    }

    /// <summary>THE PARITY TEST, now over (language, tier): the endpoint returns exactly what RunAsync chunks
    /// at, asserted both against the accessor RunAsync calls and against the independent recomputation.</summary>
    [Theory]
    [MemberData(nameof(LanguagesAndTiers))]
    public void Endpoint_ReturnsExactlyWhatRunAsyncChunksAt_ForEveryLanguageAndTier(string? language, AiTier tier)
    {
        var opt = ChunkThresholdEndpointParityTests.ShippedOptions();
        var dto = ChunkThresholdEndpointParityTests.Thresholds(opt, language, tier);

        Assert.True(UnifiedAnalysisService.ProofreadChunkTargetWordsFor(opt, language, tier) == dto.ProofreadChunkTargetWords,
            $"[{tier}, lang '{language ?? "(null)"}'] endpoint reported {dto.ProofreadChunkTargetWords} proofread " +
            $"words but RunAsync chunks at {UnifiedAnalysisService.ProofreadChunkTargetWordsFor(opt, language, tier)}. " +
            "The client picks async-vs-sync off the endpoint, so a larger endpoint value mis-routes long chapters.");
        Assert.Equal(UnifiedAnalysisService.LineEditChunkTargetWordsFor(opt, language, tier), dto.LineEditChunkTargetWords);

        Assert.Equal(
            ChunkThresholdEndpointParityTests.Recompute(
                opt, AiTaskType.Proofread, language, opt.EffectiveProofreadChunkTargetWords, tier).Target,
            dto.ProofreadChunkTargetWords);
        Assert.Equal(
            ChunkThresholdEndpointParityTests.Recompute(
                opt, AiTaskType.LineEdit, language, opt.EffectiveLineEditChunkTargetWords, tier).Target,
            dto.LineEditChunkTargetWords);
    }

    /// <summary>
    /// THE NUMBER p3-1 SAID TO PIN RATHER THAN ASSUME: at the shipped values the thinking tier does NOT move
    /// the client-facing thresholds, because <c>OpenRouter_Proofread</c>'s declared NumCtx (4096) equals the
    /// local effective 4096 and both sit above the 3548 crossover. Asserted on the WINDOW as well as on the
    /// word counts, so the reason is pinned and not only the result.
    /// </summary>
    [Theory]
    [InlineData("he", 250)]
    [InlineData("en", 500)]
    [InlineData(null, 250)]
    public void TheShippedThinkingTier_DoesNotMoveTheClientFacingThresholds(string? language, int expected)
    {
        var opt = ChunkThresholdEndpointParityTests.ShippedOptions();

        var fast = ChunkThresholdEndpointParityTests.Thresholds(opt, language, AiTier.Fast);
        var thinking = ChunkThresholdEndpointParityTests.Thresholds(opt, language, AiTier.Thinking);

        Assert.Equal(expected, fast.ProofreadChunkTargetWords);
        Assert.Equal(fast.ProofreadChunkTargetWords, thinking.ProofreadChunkTargetWords);
        Assert.Equal(fast.LineEditChunkTargetWords, thinking.LineEditChunkTargetWords);

        // The REASON: the tier moves the provider but not the window.
        Assert.Equal(
            BookContextAssembler.ResolveNumCtxForTask(opt, AiTaskType.Proofread, language, AiTier.Fast),
            BookContextAssembler.ResolveNumCtxForTask(opt, AiTaskType.Proofread, language, AiTier.Thinking));
        Assert.Equal(4096, BookContextAssembler.ResolveNumCtxForTask(opt, AiTaskType.Proofread, language, AiTier.Thinking));
        Assert.NotEqual(
            LinguisticModelResolver.ResolveForTask(opt, AiTaskType.Proofread, "he", AiTier.Fast),
            LinguisticModelResolver.ResolveForTask(opt, AiTaskType.Proofread, "he", AiTier.Thinking));
    }

    /// <summary>
    /// AND THE PIN IS NOT VACUOUS. Drop the tier route's window below the 3548 crossover and the thresholds
    /// DO move - on the thinking tier only, with the endpoint tracking the chunker. This is the failure the
    /// parity test exists to catch, reproduced deliberately.
    /// </summary>
    [Fact]
    public void ATierWhoseWindowIsBelowTheCrossover_MovesBothSurfacesTogether_AndOnlyOnThatTier()
    {
        var opt = ChunkThresholdEndpointParityTests.ShippedOptions();
        opt.ProviderSettings!["OpenRouter_Proofread"] = new ProviderTuningOptions { NumCtx = 3072, MaxTokens = 4096 };

        var fast = ChunkThresholdEndpointParityTests.Thresholds(opt, "he", AiTier.Fast);
        var thinking = ChunkThresholdEndpointParityTests.Thresholds(opt, "he", AiTier.Thinking);

        Assert.Equal(250, fast.ProofreadChunkTargetWords);
        Assert.True(thinking.ProofreadChunkTargetWords < 250,
            $"the tighter tier window must shrink the target below the Hebrew ceiling (got " +
            $"{thinking.ProofreadChunkTargetWords}); otherwise this test no longer proves the endpoint is " +
            "tier-sensitive at all.");

        // The endpoint tracks the chunker on the moved tier - the property the contract is made of.
        Assert.Equal(
            UnifiedAnalysisService.ProofreadChunkTargetWordsFor(opt, "he", AiTier.Thinking),
            thinking.ProofreadChunkTargetWords);
        // LineEdit is untouched: the tier is per task, and LineEdit is not even allowlisted.
        Assert.Equal(fast.LineEditChunkTargetWords, thinking.LineEditChunkTargetWords);
    }

    /// <summary>The endpoint's tier parameter is a free-text query string, so it is parsed defensively:
    /// absent, empty or unrecognised means the local tier, and an old client keeps its old numbers.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("banana")]
    [InlineData("fast")]
    public void TheEndpointsTierParameter_DefaultsToTheLocalTier(string? tier)
    {
        var opt = ChunkThresholdEndpointParityTests.ShippedOptions();
        opt.ProviderSettings!["OpenRouter_Proofread"] = new ProviderTuningOptions { NumCtx = 3072, MaxTokens = 4096 };

        var controller = new Controllers.ConfigController(Microsoft.Extensions.Options.Options.Create(opt));
        var action = controller.GetAnalysisChunkThresholds("he", tier);
        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(action.Result);
        var dto = Assert.IsType<Controllers.AnalysisChunkThresholdsDto>(ok.Value);

        Assert.Equal(250, dto.ProofreadChunkTargetWords);
    }
}

/// <summary>
/// THE STAMP (p3-2). <see cref="AiRequest.Tier"/> is a value the CALLER sets - the router never looks a book
/// up - so "the tier works" is a claim about call sites, not about resolution. These tests drive the REAL
/// <see cref="UnifiedAnalysisService"/> against an in-memory database with a book on the thinking tier and
/// assert that every request it hands the router carries the tier.
///
/// THE ONE THAT MATTERS MOST is the CHUNKED Proofread path. A long chapter never reaches RunAsync's
/// single-shot request, so a tier stamped only there would leave exactly the chapters long enough to matter
/// running on the local model while the UI said "thinking".
///
/// Class named *AiRouter* so the standing deterministic filter picks it up.
/// </summary>
public class AiRouterTierStampingTests
{
    private const string LinguisticJson =
        "{\"syntaxMetrics\":{\"sentenceCount\":2},\"morphologyMetrics\":{\"wordCount\":9}," +
        "\"styleMetrics\":{\"formality\":\"literary\"},\"grammaticalityScore\":0.9,\"summary\":\"סיכום\"," +
        "\"deviations\":[],\"consistencyIssues\":[]}";

    /// <summary>~600 Hebrew words, comfortably past the 250-word Hebrew chunk target, so the chunked path
    /// is taken and produces several requests.</summary>
    private static string LongHebrewText() =>
        string.Join(" ", Enumerable.Range(0, 600).Select(i => $"מילה{i % 40}"));

    private static string ShortHebrewText() => "שלום עולם. זהו טקסט קצר לבדיקה.";

    private sealed record Harness(
        UnifiedAnalysisService Service, List<AiRequest> Captured, AppDbContext Db, Guid ChapterId);

    private static async Task<Harness> BuildAsync(
        string? storedTier, AnalysisType type, string inputText, string llmContent, bool seedBook = true)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new AppDbContext(options);

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        if (seedBook)
        {
            db.Books.Add(new Book { Id = bookId, Title = "T", Language = "he", AiTier = storedTier });
            await db.SaveChangesAsync();
        }

        var captured = new List<AiRequest>();
        var routerMock = new Mock<IAiRouter>();
        routerMock
            .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AiRequest, CancellationToken>((req, _) => { lock (captured) captured.Add(req); })
            .ReturnsAsync((AiRequest req, CancellationToken _) => new AiResponse
            {
                // Proofread merges the model's output back into the chapter text, so echo the input for that
                // task and return the structured payload for the JSON tasks.
                Content = llmContent ?? req.InputText,
                Provider = "test",
                Model = "test"
            });

        var contextMock = new Mock<IAnalysisContextService>();
        contextMock
            .Setup(c => c.BuildContextAsync(
                It.IsAny<AnalysisScope>(), chapterId, type, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AnalysisContext
            {
                TargetText = inputText,
                Scope = AnalysisScope.Chapter,
                AnalysisType = type,
                BookId = seedBook ? bookId : Guid.NewGuid(),
                ChapterId = chapterId,
                SceneId = null
            });

        var service = new UnifiedAnalysisService(
            db, routerMock.Object, new PromptFactory(), new SfdtConversionService(),
            Options.Create(new AiOptions()), NullLogger<UnifiedAnalysisService>.Instance,
            new AnalysisProgressTracker(), contextMock.Object, new SuggestionDiffService(),
            new KtivMaleChecker(new HebrewStyleOptions { EnforceKtivMale = false }),
            new AnalysisRepairService(new Mock<IAiRouter>().Object, NullLogger<AnalysisRepairService>.Instance),
            new DynamicTermRepairService(new Mock<IAiRouter>().Object, NullLogger<DynamicTermRepairService>.Instance),
            new StubBookEntityProvider());

        return new Harness(service, captured, db, chapterId);
    }

    /// <summary>Single-shot LinguisticAnalysis - the simplest allowlisted route.</summary>
    [Theory]
    [InlineData("thinking", AiTier.Thinking)]
    [InlineData("fast", AiTier.Fast)]
    [InlineData(null, AiTier.Fast)]
    [InlineData("banana", AiTier.Fast)]
    public async Task SingleShotRun_StampsTheBooksTier(string? stored, AiTier expected)
    {
        var h = await BuildAsync(stored, AnalysisType.LinguisticAnalysis, ShortHebrewText(), LinguisticJson);
        await using var _ = h.Db;

        await h.Service.RunAsync(
            AnalysisScope.Chapter, AnalysisType.LinguisticAnalysis, h.ChapterId,
            customPrompt: null, language: "he", jobId: null, ct: CancellationToken.None);

        var request = Assert.Single(h.Captured);
        Assert.Equal(expected, request.Tier);
    }

    /// <summary>
    /// THE CHUNKED PROOFREAD PATH. Asserts (a) the run really did chunk - more than one request - and (b)
    /// EVERY chunk request carries the tier. A stamp on only the first chunk would pass (a) and fail (b).
    /// </summary>
    [Fact]
    public async Task ChunkedProofread_StampsTheBooksTier_OnEveryChunk()
    {
        var h = await BuildAsync("thinking", AnalysisType.Proofread, LongHebrewText(), llmContent: null!);
        await using var _ = h.Db;

        await h.Service.RunAsync(
            AnalysisScope.Chapter, AnalysisType.Proofread, h.ChapterId,
            customPrompt: null, language: "he", jobId: null, ct: CancellationToken.None);

        Assert.True(h.Captured.Count > 1,
            $"expected the chunked path (>1 request) but only {h.Captured.Count} request(s) were made - this test " +
            "is no longer exercising RunProofreadChunkedAsync at all.");
        Assert.All(h.Captured, r => Assert.Equal(AiTier.Thinking, r.Tier));
        Assert.All(h.Captured, r => Assert.Equal(AiTaskType.Proofread, r.TaskType));
    }

    /// <summary>
    /// The chunked LineEdit path. LineEdit is NOT allowlisted, so the stamped value is inert - but the STAMP
    /// must still be there, or adding LineEdit to the allowlist later would silently miss exactly the long
    /// chapters, which is the same defect one task over.
    /// </summary>
    [Fact]
    public async Task ChunkedLineEdit_StampsTheBooksTier_OnEveryChunk()
    {
        var h = await BuildAsync("thinking", AnalysisType.LineEdit, LongHebrewText(), "{\"suggestions\":[]}");
        await using var _ = h.Db;

        await h.Service.RunAsync(
            AnalysisScope.Chapter, AnalysisType.LineEdit, h.ChapterId,
            customPrompt: null, language: "he", jobId: null, ct: CancellationToken.None);

        Assert.True(h.Captured.Count > 1,
            $"expected the chunked path (>1 request) but only {h.Captured.Count} request(s) were made.");
        Assert.All(h.Captured, r => Assert.Equal(AiTier.Thinking, r.Tier));
    }

    /// <summary>
    /// FAIL-SAFE DIRECTION. A run whose book row does not exist resolves to the LOCAL tier, never to paid
    /// cloud. "We could not tell" must not mean "send the manuscript to a third party".
    /// </summary>
    [Fact]
    public async Task AMissingBook_ResolvesToTheLocalTier()
    {
        var h = await BuildAsync(
            "thinking", AnalysisType.LinguisticAnalysis, ShortHebrewText(), LinguisticJson, seedBook: false);
        await using var _ = h.Db;

        await h.Service.RunAsync(
            AnalysisScope.Chapter, AnalysisType.LinguisticAnalysis, h.ChapterId,
            customPrompt: null, language: "he", jobId: null, ct: CancellationToken.None);

        Assert.Equal(AiTier.Fast, Assert.Single(h.Captured).Tier);
    }

    /// <summary>
    /// End to end through the REAL router: a thinking-tier book's Hebrew proofread reaches the cloud
    /// provider, and the same book's ENGLISH proofread does not. This is the p2-4 restriction asserted on the
    /// composition of caller + stamp + resolution, rather than on the resolver alone.
    /// </summary>
    [Theory]
    [InlineData("he", "OpenRouter")]
    [InlineData("en", "Ollama")]
    public void AThinkingBooksProofread_ReachesCloudOnlyForHebrew(string language, string expectedProvider)
    {
        var opt = ProviderTuningConfigParityTests.LoadShippedAiOptions();
        var selection = AiRouter.ResolveSelectionForTest(
            new AiRequest
            {
                InputText = "x",
                TaskType = AiTaskType.Proofread,
                Language = language,
                Tier = AiTier.Thinking
            }, opt);

        Assert.Equal(expectedProvider, selection.Provider);
    }
}

/// <summary>
/// ONE TIER READ PER RUN (pre-PR review finding P2-4, fix be-c03).
///
/// A chunked run used to resolve <c>Book.AiTier</c> TWICE and let the two reads decide DIFFERENT things:
/// <c>RunAsync</c>'s read picked the chunk SIZE (the sizing's bound (B) reads the ROUTED provider's num_ctx,
/// see <see cref="UnifiedAnalysisService.ProofreadChunkTargetWordsFor"/>), and a second read inside
/// <c>RunProofreadChunkedAsync</c> / <c>RunLineEditChunkedAsync</c> picked the ROUTE that every chunk's
/// <see cref="AiRequest.Tier"/> was stamped with. A flip between the two therefore sized chunks for one
/// provider and sent them to another.
///
/// THIS WAS NEVER A LIVE BUG AND THESE TESTS DO NOT CLAIM IT WAS. At the shipped values both tiers resolve
/// Proofread at num_ctx 4096 (<c>OpenRouter_Proofread</c> declares 4096 explicitly, precisely so switching
/// tier does not move the client-facing chunk thresholds), so the two reads could only ever have produced the
/// same chunk target; that no-movement result is pinned by
/// <see cref="ChunkThresholdTierParityTests.TheShippedThinkingTier_DoesNotMoveTheClientFacingThresholds"/>.
/// What is pinned HERE is the structural property that replaced it: the run reads the tier ONCE, so the two
/// decisions cannot disagree even under a config where the tiers really do size differently.
///
/// HOW THE COUNT IS MEASURED. <see cref="BookTierReadCountingDbContext"/> overrides
/// <see cref="Microsoft.EntityFrameworkCore.DbContext.Set{TEntity}"/> and counts every access to the
/// <c>Book</c> set. <c>UnifiedAnalysisService</c> touches <c>Books</c> from exactly one place - the shared
/// <c>BookAiTierResolver</c> behind <c>ResolveBookTierAsync</c> - so that count IS the number of tier reads.
///
/// Class named *AiTier* so the standing deterministic filter picks it up.
/// </summary>
public class AiTierSingleReadPerRunTests
{
    private const string LocalProvider = "Ollama";
    private const string CloudProvider = "OpenRouter";

    /// <summary>~600 Hebrew words, well past any per-chunk target below, so the chunked path is taken.</summary>
    private static string LongHebrewText() =>
        string.Join(" ", Enumerable.Range(0, 600).Select(i => $"מילה{i % 40}"));

    /// <summary>
    /// Options in which the two tiers really do size chunks DIFFERENTLY - the thinking route declares a
    /// window below the crossover, so bound (B) starts binding and the Hebrew target drops under the 250-word
    /// language ceiling. The shipped config deliberately does NOT look like this (both tiers are at 4096);
    /// this is the hostile config the structural guarantee has to hold in, not a claim about production.
    /// </summary>
    private static AiOptions TierSensitiveOptions() => new()
    {
        DefaultProvider = LocalProvider,
        DefaultModel = "default-model",
        FeatureModels = new Dictionary<string, FeatureModelOptions>(StringComparer.Ordinal)
        {
            ["Proofread"] = new FeatureModelOptions { Provider = LocalProvider, Model = "local-proofread" },
            ["Proofread_thinking"] = new FeatureModelOptions { Provider = CloudProvider, Model = "cloud-proofread" }
        },
        ProviderSettings = new Dictionary<string, ProviderTuningOptions>(StringComparer.OrdinalIgnoreCase)
        {
            [$"{LocalProvider}_Proofread"] = new ProviderTuningOptions { NumCtx = 4096 },
            [$"{CloudProvider}_Proofread"] = new ProviderTuningOptions { NumCtx = 3072, MaxTokens = 4096 }
        }
    };

    /// <summary>
    /// Counts every access to the <c>Book</c> DbSet and lets a test fire a side effect just BEFORE the read
    /// executes. The side effect is how a flip is landed strictly BETWEEN a first and a hypothetical second
    /// tier read without threads - the same deterministic-race idiom as the tracker suite's side-effecting
    /// clock. <c>Set&lt;TEntity&gt;()</c> is the seam because <c>AppDbContext.Books</c> is
    /// <c>=&gt; Set&lt;Book&gt;()</c>, so every use of it goes through here.
    /// </summary>
    private sealed class BookTierReadCountingDbContext : AppDbContext
    {
        private readonly Action<int>? _beforeBooksRead;
        private int _bookSetAccesses;

        public BookTierReadCountingDbContext(
            DbContextOptions<AppDbContext> options, Action<int>? beforeBooksRead = null)
            : base(options) => _beforeBooksRead = beforeBooksRead;

        /// <summary>Set to true only once the seed data is in place, so seeding is not counted.</summary>
        public bool Counting { get; set; }

        public int BookSetAccesses => Volatile.Read(ref _bookSetAccesses);

        public override DbSet<TEntity> Set<TEntity>()
        {
            if (Counting && typeof(TEntity) == typeof(Book))
            {
                // Increment OUTSIDE the null-conditional call: `_beforeBooksRead?.Invoke(Increment(...))`
                // short-circuits its own argument when the callback is null, so the count would stay 0.
                var access = Interlocked.Increment(ref _bookSetAccesses);
                _beforeBooksRead?.Invoke(access);
            }

            return base.Set<TEntity>();
        }
    }

    /// <summary>
    /// THE ASSERTION THE FIX IS ABOUT: a chunked proofread run reads <c>Book.AiTier</c> exactly ONCE, and both
    /// the chunk SIZE and every chunk's stamped tier come from that one value.
    ///
    /// A flip to the local tier is armed on the SECOND Books read. Against the two-read code that flip lands
    /// between "size the chunks" and "stamp the requests", so the chunks stay sized for the cloud window while
    /// every request is routed local - the exact disagreement the fix removes. Against the one-read code the
    /// second read never happens, the flip never fires, and the run is internally consistent.
    /// </summary>
    [Fact]
    public async Task AChunkedProofreadRun_ReadsTheBooksTierExactlyOnce_AndSizesAndStampsFromThatOneValue()
    {
        var opts = TierSensitiveOptions();
        var text = LongHebrewText();

        // GUARD THE GUARD (part 1): in this config the tier really does move the chunk target, so the chunk
        // COUNT below identifies which tier sized the run rather than being true of both.
        var thinkingTarget = UnifiedAnalysisService.ProofreadChunkTargetWordsFor(opts, "he", AiTier.Thinking);
        var fastTarget = UnifiedAnalysisService.ProofreadChunkTargetWordsFor(opts, "he", AiTier.Fast);
        Assert.True(thinkingTarget < fastTarget,
            $"this config no longer makes the tiers size differently (thinking {thinkingTarget}, fast " +
            $"{fastTarget}), so the chunk-count assertion below would pass on either tier and prove nothing.");

        var chunksAtThinking = UnifiedAnalysisService.ChunkForProofreadForTest(text, thinkingTarget).Count;
        var chunksAtFast = UnifiedAnalysisService.ChunkForProofreadForTest(text, fastTarget).Count;
        Assert.True(chunksAtThinking > 1 && chunksAtThinking != chunksAtFast,
            $"expected the two tiers to yield different chunk counts (thinking {chunksAtThinking}, fast " +
            $"{chunksAtFast}) and the run to actually chunk.");

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("AiTierSingleRead_" + Guid.NewGuid()).Options;

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var flips = 0;

        // ARMED ON THE SECOND READ ONLY. A separate context (its own change tracker, exactly like the HTTP PUT
        // that would do this in production) writes the new value before the second read's query runs.
        await using var db = new BookTierReadCountingDbContext(dbOptions, beforeBooksRead: access =>
        {
            if (access != 2) return;
            Interlocked.Increment(ref flips);
            using var writer = new AppDbContext(dbOptions);
            var row = writer.Books.Single(b => b.Id == bookId);
            row.AiTier = AiTierPolicy.FastStoredValue;
            writer.SaveChanges();
        });

        db.Books.Add(new Book
        {
            Id = bookId, Title = "Single Read Book", Language = "he",
            AiTier = AiTierPolicy.ThinkingStoredValue
        });
        await db.SaveChangesAsync();
        db.Counting = true;

        var captured = new List<AiRequest>();
        var routerMock = new Mock<IAiRouter>();
        routerMock
            .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AiRequest, CancellationToken>((req, _) => { lock (captured) captured.Add(req); })
            .ReturnsAsync((AiRequest req, CancellationToken _) => new AiResponse
            {
                Content = req.InputText, Provider = "test", Model = "test"
            });

        var contextMock = new Mock<IAnalysisContextService>();
        contextMock
            .Setup(c => c.BuildContextAsync(
                It.IsAny<AnalysisScope>(), chapterId, AnalysisType.Proofread,
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AnalysisContext
            {
                TargetText = text,
                Scope = AnalysisScope.Chapter,
                AnalysisType = AnalysisType.Proofread,
                BookId = bookId,
                ChapterId = chapterId,
                SceneId = null
            });

        var service = new UnifiedAnalysisService(
            db, routerMock.Object, new PromptFactory(), new SfdtConversionService(),
            Options.Create(opts), NullLogger<UnifiedAnalysisService>.Instance,
            new AnalysisProgressTracker(), contextMock.Object, new SuggestionDiffService(),
            new KtivMaleChecker(new HebrewStyleOptions { EnforceKtivMale = false }),
            new AnalysisRepairService(new Mock<IAiRouter>().Object, NullLogger<AnalysisRepairService>.Instance),
            new DynamicTermRepairService(new Mock<IAiRouter>().Object, NullLogger<DynamicTermRepairService>.Instance),
            new StubBookEntityProvider());

        await service.RunAsync(
            AnalysisScope.Chapter, AnalysisType.Proofread, chapterId,
            customPrompt: null, language: "he", jobId: null, ct: CancellationToken.None);

        // GUARD THE GUARD (part 2): the run really took the chunked path.
        Assert.True(captured.Count > 1,
            $"expected the chunked path (>1 request) but only {captured.Count} request(s) were made - this " +
            "test is no longer exercising RunProofreadChunkedAsync at all.");

        // 1. THE LOAD-BEARING COUNT. One run, one Book.AiTier read.
        Assert.True(db.BookSetAccesses == 1,
            $"the run read Book.AiTier {db.BookSetAccesses} time(s). RunAsync resolves the tier once and passes " +
            "it to the chunked runner; a second read is a second chance for the chunk SIZE and the chunk ROUTE " +
            "to be decided from different values.");
        Assert.Equal(0, flips); // ... so the armed mid-run flip never had a read to land between.

        // 2. THE ROUTE came from that value.
        Assert.All(captured, r => Assert.Equal(AiTier.Thinking, r.Tier));

        // 3. ... and so did the SIZE. The chunk count is the one the THINKING window produces, not the local one.
        Assert.True(chunksAtThinking == captured.Count,
            $"the run produced {captured.Count} chunks; the tier it stamped (thinking, target {thinkingTarget}) " +
            $"produces {chunksAtThinking} and the local tier (target {fastTarget}) produces {chunksAtFast}. " +
            "The chunk size and the chunk route were decided from different tier values.");
    }

    /// <summary>
    /// The same property on the chunked LINEEDIT path. LineEdit is not allowlisted, so its stamped tier is
    /// inert today; the read count is not, and it is the half that would silently double every run's Books
    /// queries. Asserted with the shipped-shaped (tier-invariant) sizing, so this one is purely about the
    /// number of reads.
    /// </summary>
    [Fact]
    public async Task AChunkedLineEditRun_AlsoReadsTheBooksTierExactlyOnce()
    {
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase("AiTierSingleRead_" + Guid.NewGuid()).Options;

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();

        await using var db = new BookTierReadCountingDbContext(dbOptions);
        db.Books.Add(new Book
        {
            Id = bookId, Title = "Single Read LineEdit Book", Language = "he",
            AiTier = AiTierPolicy.ThinkingStoredValue
        });
        await db.SaveChangesAsync();
        db.Counting = true;

        var captured = new List<AiRequest>();
        var routerMock = new Mock<IAiRouter>();
        routerMock
            .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .Callback<AiRequest, CancellationToken>((req, _) => { lock (captured) captured.Add(req); })
            .ReturnsAsync(new AiResponse { Content = "{\"suggestions\":[]}", Provider = "test", Model = "test" });

        var contextMock = new Mock<IAnalysisContextService>();
        contextMock
            .Setup(c => c.BuildContextAsync(
                It.IsAny<AnalysisScope>(), chapterId, AnalysisType.LineEdit,
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AnalysisContext
            {
                TargetText = LongHebrewText(),
                Scope = AnalysisScope.Chapter,
                AnalysisType = AnalysisType.LineEdit,
                BookId = bookId,
                ChapterId = chapterId,
                SceneId = null
            });

        var service = new UnifiedAnalysisService(
            db, routerMock.Object, new PromptFactory(), new SfdtConversionService(),
            Options.Create(new AiOptions()), NullLogger<UnifiedAnalysisService>.Instance,
            new AnalysisProgressTracker(), contextMock.Object, new SuggestionDiffService(),
            new KtivMaleChecker(new HebrewStyleOptions { EnforceKtivMale = false }),
            new AnalysisRepairService(new Mock<IAiRouter>().Object, NullLogger<AnalysisRepairService>.Instance),
            new DynamicTermRepairService(new Mock<IAiRouter>().Object, NullLogger<DynamicTermRepairService>.Instance),
            new StubBookEntityProvider());

        await service.RunAsync(
            AnalysisScope.Chapter, AnalysisType.LineEdit, chapterId,
            customPrompt: null, language: "he", jobId: null, ct: CancellationToken.None);

        Assert.True(captured.Count > 1,
            $"expected the chunked path (>1 request) but only {captured.Count} request(s) were made.");
        Assert.True(db.BookSetAccesses == 1,
            $"the run read Book.AiTier {db.BookSetAccesses} time(s); RunAsync's single read must be the only one.");
        Assert.All(captured, r => Assert.Equal(AiTier.Thinking, r.Tier));
    }
}
