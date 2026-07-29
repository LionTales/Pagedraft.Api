using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Pagedraft.Api.Data;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// p1-3: THE CLOUD TUNING COVERAGE GUARD (model-tier-fast-thinking plan).
///
/// THE BUG THIS EXISTS TO STOP RETURNING. <c>Ai:ProviderSettings</c> used to carry <c>Ollama_*</c> per-task
/// entries and a single FLAT <c>OpenRouter</c> entry that set no NumCtx. That is not a neutral omission:
/// <see cref="ProviderTuningOptions.NumCtx"/> defaults to 4096, 4096 is <c>&gt; 0</c>, so the flat entry WON the
/// lookup and supplied 4096 rather than falling through to anything larger. Routing BookReview at a cloud
/// provider therefore collapsed the whole-book context budget 16384 -> 4096, which is precisely the failure
/// <c>_comment_Ollama_BookReview</c> was written to prevent: the input overflows the window, the prompt is
/// truncated, the model returns a lone <c>{</c>, ExtractJson fails, EVERY dimension returns null, and the job
/// reports Succeeded with 0 findings. A silent, total, green-looking failure.
///
/// THE TEST IS THE POINT, not the config edit. Adding the entries fixes today; this class is what stops the
/// hole reopening the next time a provider or a task is added. It is driven off the REAL
/// <see cref="AiProviderRegistry"/> and the REAL appsettings.json, never a hand-listed set.
///
/// Class named *ConfigParityTests so the standing <c>--filter "FullyQualifiedName~ConfigParity"</c> run picks
/// it up with the other config guards.
/// </summary>
public class CloudTuningCoverageConfigParityTests
{
    /// <summary>
    /// The context window every WHOLE-BOOK task must have on every provider. Mirrors the shipped
    /// <c>Ollama_BookReview</c> / <c>Ollama_LinguisticAnalysis</c> / <c>Ollama_Summarization</c> value ON
    /// PURPOSE: routing a task to the cloud tier must change exactly ONE variable, the MODEL, or phase 2's
    /// local-vs-cloud measurement is confounded by a different input budget.
    /// </summary>
    private const int WholeBookNumCtx = 16384;

    /// <summary>
    /// PROVIDERS WAIVED FROM THE FULL PER-TASK MIRROR, each with the reason written down. A name may only be
    /// added here together with its justification - the whole value of the guard is that skipping a provider
    /// is a recorded decision rather than an oversight.
    ///
    /// OpenAI / Azure / Anthropic: not wired tier targets. The plan's "Deferred / out of scope" section says
    /// so explicitly ("Non-OpenRouter cloud providers (Azure/OpenAI/Anthropic) as tier targets - p1-1 fixes
    /// their tuning lookup, but wiring them as tiers is separate"), their <c>Ai:Providers</c> API keys are
    /// uninterpolated placeholders, and no <c>_comment_*</c> block names any of them as a cloud counterpart.
    /// Tuning a task for a provider nothing routes to would be config asserting a decision nobody made.
    ///
    /// THE WAIVER DOES NOT COVER BookReview - see
    /// <see cref="BookReviewWindow_IsNeverTheClassDefault_UnderAnyRegisteredProvider"/>. BookReview's
    /// under-sizing failure is total AND silent, so it gets an explicit window on EVERY registered provider,
    /// waived or not.
    /// </summary>
    private static readonly IReadOnlySet<string> ProvidersWaivedFromTheFullMirror =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "OpenAI", "Azure", "Anthropic" };

    /// <summary>Tasks BookReview-floored on every provider regardless of the waiver above.</summary>
    private static readonly IReadOnlyList<AiTaskType> TasksFlooredOnEveryProvider = new[] { AiTaskType.BookReview };

    // ─── The mirror check, extracted so the guard itself can be tested ──────────────────────────────────

    /// <summary>
    /// Returns the <c>{Provider}_{Task}</c> keys that SHOULD exist and do not: for every non-Ollama provider
    /// that is not waived, one per task that Ollama has bothered to tune. "Ollama has a per-task entry" is the
    /// right trigger because that entry is itself the recorded statement that this task's defaults are wrong
    /// for it; a provider that inherits the flat entry inherits the class default with it.
    ///
    /// Extracted (rather than inlined into the [Fact]) so <see cref="MirrorCheck_ReportsAMissingCounterpart"/>
    /// and <see cref="MirrorCheck_HonoursTheWaiver"/> can prove the check is not vacuous.
    /// </summary>
    internal static List<string> MissingCloudCounterparts(
        IReadOnlyDictionary<string, ProviderTuningOptions> settings,
        IEnumerable<string> providerNames,
        IReadOnlySet<string> waivedProviders)
    {
        var ollamaTunedTasks = Enum.GetValues<AiTaskType>()
            .Where(t => settings.ContainsKey(ProviderTuningResolver.TaskKey("Ollama", t)))
            .ToList();

        return (from provider in providerNames
                where !string.Equals(provider, "Ollama", StringComparison.OrdinalIgnoreCase)
                      && !waivedProviders.Contains(provider)
                from task in ollamaTunedTasks
                let key = ProviderTuningResolver.TaskKey(provider, task)
                where !settings.ContainsKey(key)
                select key)
            .ToList();
    }

    // ─── The shipped guards ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE GUARD. Every task Ollama tunes must have a counterpart on every registered, non-waived provider.
    /// Driven off <see cref="AiProviderRegistry"/>, so registering a sixth provider fails here until someone
    /// either tunes it or writes down why it does not need tuning.
    /// </summary>
    [Fact]
    public void EveryOllamaTunedTask_HasACloudCounterpart_OnEveryConfiguredProvider()
    {
        var opt = ProviderTuningConfigParityTests.LoadShippedAiOptions();
        var missing = MissingCloudCounterparts(
            opt.ProviderSettings!, ProviderTuningOutputKnobTests.RegisteredProviderNames(), ProvidersWaivedFromTheFullMirror);

        Assert.True(missing.Count == 0,
            "Ai:ProviderSettings is missing per-task tuning for a configured provider: [" +
            string.Join(", ", missing) + "]. Without an entry the lookup binds the ProviderTuningOptions CLASS " +
            "DEFAULT (NumCtx 4096), which is > 0 and therefore WINS rather than falling through - so a " +
            "whole-book task routed there collapses its budget to 4096 and fails silently (prompt truncated, " +
            "every dimension null, job still reports Succeeded). Add the entry with an explicit NumCtx sized to " +
            "that model's real window, or waive the provider BY NAME in ProvidersWaivedFromTheFullMirror with " +
            "the reason - but note BookReview is never waivable.");
    }

    /// <summary>
    /// THE NON-WAIVABLE FLOOR the todo asks for in as many words: BookReview must never resolve the class
    /// default window under ANY registered provider. Asserted on the resolved BUDGET too, because that is the
    /// number the failure is actually made of: at num_ctx 4096 the derived budget bottoms out on
    /// <see cref="AiOptions.EffectiveBookContextTokenBudget"/>'s 256-token floor, i.e. essentially no book at
    /// all reaches the model while the job still reports success.
    /// </summary>
    [Fact]
    public void BookReviewWindow_IsNeverTheClassDefault_UnderAnyRegisteredProvider()
    {
        var classDefaultNumCtx = new ProviderTuningOptions().NumCtx;
        var failures = new List<string>();

        foreach (var provider in ProviderTuningOutputKnobTests.RegisteredProviderNames())
        {
            foreach (var task in TasksFlooredOnEveryProvider)
            {
                var opt = ProviderTuningConfigParityTests.LoadShippedAiOptions();
                // Route the task at this provider exactly as a tier switch would (both fields non-empty, which
                // is the predicate LinguisticModelResolver and AiRouter share).
                opt.FeatureModels![task.ToString()] = new FeatureModelOptions { Provider = provider, Model = "tier-model" };

                var numCtx = BookContextAssembler.ResolveNumCtxForTask(opt, task);
                var reserve = BookContextAssembler.ResolveOutputReserveForTask(opt, task);
                var budget = opt.EffectiveBookContextTokenBudget(numCtx, reserve);

                if (numCtx <= classDefaultNumCtx)
                    failures.Add($"{provider}/{task}: num_ctx resolved {numCtx}, which is the class default " +
                                 $"({classDefaultNumCtx}) - add Ai:ProviderSettings:{ProviderTuningResolver.TaskKey(provider, task)}");
                if (numCtx != WholeBookNumCtx)
                    failures.Add($"{provider}/{task}: num_ctx {numCtx} does not match the whole-book window {WholeBookNumCtx} " +
                                 "every other provider uses; a tier switch must change the MODEL, not the input budget");
                if (budget < 8192)
                    failures.Add($"{provider}/{task}: derived book budget {budget} tokens (reserve {reserve}) - " +
                                 "a whole book cannot be reviewed through that, and the job would still report success");
            }
        }

        Assert.True(failures.Count == 0, string.Join(" | ", failures));
    }

    /// <summary>
    /// A HAND-AUTHORED oracle for the CLOUD surface, the counterpart to
    /// <c>ProviderTuningConfigParityTests.Oracle</c> (which covers Ollama only, and which p1-3 therefore did
    /// NOT move - every Ai:FeatureModels entry still routes to Ollama). Deliberately not derived from the
    /// config: a derived table is a tautology that passes for any config.
    ///
    /// THE VALUES AND WHY THEY ARE THESE VALUES:
    ///   NumCtx  = the LOCAL window for the same task (16384 for whole-book/whole-chapter tasks; 4096 for
    ///             Proofread/LineEdit, whose Ollama entries omit NumCtx and therefore resolve 4096). Mirrored
    ///             on purpose so a tier switch changes only the model. Verified to sit far inside the wired
    ///             cloud candidate's real window: google/gemma-4-31b-it advertises context_length 262144 on
    ///             OpenRouter (catalog read 2026-07-28), so 16384 has 16x headroom and cannot overflow it or a
    ///             smaller-window backend that model id may be routed to.
    ///   MaxTokens = the MEASURED Ollama NumPredict for the same task, because the output SIZE is a property
    ///             of the prompt (same prompt, same JSON schema) rather than of the provider. This lowers
    ///             Summarization/GenericChat/AnalysisRepair to 2048 and TermRepair to 256 versus the flat
    ///             entry's one-size 5120 - deliberate, since on a PAID provider an oversized output cap is
    ///             direct cost exposure rather than the free headroom it is locally.
    /// Editing an Ai:ProviderSettings cloud entry is expected to turn this red; update the row WITH the config
    /// and say which task's cloud window or cap moved.
    /// </summary>
    private static readonly Dictionary<string, (int NumCtx, int MaxTokens)> CloudOracle = new(StringComparer.Ordinal)
    {
        ["OpenRouter_Proofread"] = (4096, 4096),
        ["OpenRouter_LineEdit"] = (4096, 5120),
        ["OpenRouter_LinguisticAnalysis"] = (16384, 5120),
        ["OpenRouter_BookReview"] = (16384, 6144),
        ["OpenRouter_Summarization"] = (16384, 2048),
        ["OpenRouter_AnalysisRepair"] = (16384, 2048),
        ["OpenRouter_TermRepair"] = (16384, 256),
        ["OpenRouter_GenericChat"] = (16384, 2048),
        ["OpenAI_BookReview"] = (16384, 6144),
        ["Azure_BookReview"] = (16384, 6144),
        ["Anthropic_BookReview"] = (16384, 6144)
    };

    [Fact]
    public void ShippedCloudTuning_MatchesTheHandAuthoredOracle()
    {
        var settings = ProviderTuningConfigParityTests.LoadShippedAiOptions().ProviderSettings!;

        foreach (var (key, expected) in CloudOracle)
        {
            Assert.True(settings.TryGetValue(key, out var tuning), $"Ai:ProviderSettings:{key} is missing.");
            Assert.True(expected.NumCtx == tuning!.NumCtx, $"{key}: NumCtx expected {expected.NumCtx}, got {tuning.NumCtx}.");
            Assert.True(expected.MaxTokens == tuning.MaxTokens, $"{key}: MaxTokens expected {expected.MaxTokens}, got {tuning.MaxTokens}.");
        }
    }

    /// <summary>
    /// A cloud entry must set <c>MaxTokens</c>, never <c>NumPredict</c> - the p1-2 hand-off, pinned. The cloud
    /// families put <c>max_tokens</c> on the wire and <c>BookContextAssembler.ResolveOutputReserveForTask</c>
    /// reads the field that family actually sends, so a cloud entry that set NumPredict would be sent nothing
    /// and reserved wrongly, silently, at the class default.
    /// </summary>
    [Fact]
    public void EveryCloudEntry_SetsMaxTokens_AndLeavesNumPredictAtTheClassDefault()
    {
        var settings = ProviderTuningConfigParityTests.LoadShippedAiOptions().ProviderSettings!;
        var defaults = new ProviderTuningOptions();

        foreach (var key in CloudOracle.Keys)
        {
            var tuning = settings[key];
            Assert.True(tuning.MaxTokens > 0, $"{key}: a cloud entry must set MaxTokens - that is the field its family sends.");
            Assert.True(defaults.NumPredict == tuning.NumPredict,
                $"{key}: sets NumPredict ({tuning.NumPredict}). NumPredict is Ollama's knob; a cloud provider never sends it, " +
                "so this value is invisible on the wire while looking like it was configured. Use MaxTokens.");
            // RepeatPenalty likewise has no field in the OpenAI-compatible payload (OpenAiProvider.BuildPayload
            // sends model/messages/temperature/max_tokens only), so a cloud entry that set it would be dead config.
            Assert.True(Math.Abs(defaults.RepeatPenalty - tuning.RepeatPenalty) < 1e-9,
                $"{key}: sets RepeatPenalty ({tuning.RepeatPenalty}), which no cloud payload carries - dead config.");
        }
    }

    /// <summary>A waiver for a provider that no longer exists is stale and hides nothing; clean it up.</summary>
    [Fact]
    public void EveryWaivedProvider_IsStillRegistered()
    {
        var registered = ProviderTuningOutputKnobTests.RegisteredProviderNames();
        var stale = ProvidersWaivedFromTheFullMirror
            .Where(w => !registered.Contains(w, StringComparer.OrdinalIgnoreCase))
            .ToList();

        Assert.True(stale.Count == 0,
            $"Waived provider(s) no longer in AiProviderRegistry: [{string.Join(", ", stale)}]. Remove the waiver.");
    }

    // ─── The guard's own guards: prove the mirror check is not vacuous ──────────────────────────────────

    [Fact]
    public void MirrorCheck_ReportsAMissingCounterpart()
    {
        var settings = new Dictionary<string, ProviderTuningOptions>
        {
            ["Ollama_BookReview"] = new ProviderTuningOptions { NumCtx = 16384 },
            ["OpenRouter"] = new ProviderTuningOptions { MaxTokens = 5120 }
        };

        var missing = MissingCloudCounterparts(
            settings, new[] { "Ollama", "OpenRouter" }, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(new[] { "OpenRouter_BookReview" }, missing);
    }

    [Fact]
    public void MirrorCheck_IsSatisfiedByThePresenceOfTheCounterpart()
    {
        var settings = new Dictionary<string, ProviderTuningOptions>
        {
            ["Ollama_BookReview"] = new ProviderTuningOptions { NumCtx = 16384 },
            ["OpenRouter_BookReview"] = new ProviderTuningOptions { NumCtx = 16384, MaxTokens = 6144 }
        };

        Assert.Empty(MissingCloudCounterparts(
            settings, new[] { "Ollama", "OpenRouter" }, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void MirrorCheck_HonoursTheWaiver()
    {
        var settings = new Dictionary<string, ProviderTuningOptions>
        {
            ["Ollama_BookReview"] = new ProviderTuningOptions { NumCtx = 16384 }
        };

        Assert.Empty(MissingCloudCounterparts(
            settings,
            new[] { "Ollama", "Anthropic" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Anthropic" }));
    }

    /// <summary>
    /// The trigger set is derived from the config, not hard-coded: a task Ollama does NOT tune is not demanded
    /// of the cloud either. Translation is that task in the shipped config, which is why it is legitimately
    /// absent from the cloud entries.
    /// </summary>
    [Fact]
    public void MirrorCheck_DoesNotDemandACounterpartForATaskOllamaDoesNotTune()
    {
        var settings = new Dictionary<string, ProviderTuningOptions>
        {
            ["Ollama"] = new ProviderTuningOptions { NumCtx = 8192 },
            ["OpenRouter"] = new ProviderTuningOptions { MaxTokens = 5120 }
        };

        Assert.Empty(MissingCloudCounterparts(
            settings, new[] { "Ollama", "OpenRouter" }, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));

        var shipped = ProviderTuningConfigParityTests.LoadShippedAiOptions().ProviderSettings!;
        Assert.False(shipped.ContainsKey(ProviderTuningResolver.TaskKey("Ollama", AiTaskType.Translation)));
    }
}

/// <summary>
/// p1-3 RUNTIME OBSERVABILITY. The guard class above catches a missing cloud entry at BUILD time; it cannot
/// catch a MISCONFIGURED DEPLOYMENT - an appsettings.Production override, an environment variable, or a
/// provider wired at runtime - and the plan's own analysis of this bug notes that "nothing logs a warning".
/// <see cref="BookContextAssembler.ResolveBudgetTokens"/> now warns when a WHOLE-BOOK task resolves a context
/// window at or below the <see cref="ProviderTuningOptions"/> class default, naming the provider, the task,
/// the resolved window, the collapsed budget, and the exact config key to add.
///
/// Class named *ProviderTuning* so the standing <c>FullyQualifiedName~ProviderTuning</c> filter picks it up.
/// </summary>
public class ProviderTuningUnsizedWindowWarningTests
{
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Warnings { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Warnings);
        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly List<string> _sink;
            public CapturingLogger(List<string> sink) => _sink = sink;
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Warning)
                    lock (_sink) _sink.Add(formatter(state, exception));
            }
        }
    }

    /// <summary>
    /// Builds a real <see cref="BookContextAssembler"/> through DI with a capturing logger, so the assertion is
    /// on what the service actually logs rather than on a re-implementation of the condition.
    /// </summary>
    private static (ServiceProvider Sp, CapturingLoggerProvider Logs) BuildAssembler(
        Dictionary<string, ProviderTuningOptions> providerSettings,
        Dictionary<string, FeatureModelOptions>? featureModels)
    {
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => { b.SetMinimumLevel(LogLevel.Debug); b.AddProvider(logs); });
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddSingleton<SfdtConversionService>();
        services.AddSingleton<PromptFactory>();

        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AiResponse { Content = "{}", Model = "m", Provider = "test" });
        services.AddSingleton(router.Object);

        services.Configure<AiOptions>(o =>
        {
            o.DefaultProvider = "Ollama";
            o.DefaultModel = "qwen3.5:9b";
            o.BookContextTokenBudget = 0;
            o.BookContextBudgetFraction = 0.5;
            o.ProviderSettings = providerSettings;
            if (featureModels != null) o.FeatureModels = featureModels;
        });

        services.AddScoped<ChapterBriefService>();
        services.AddScoped<BookSummaryService>();
        services.AddScoped<BookContextAssembler>();
        services.AddSingleton<AnalysisProgressTracker>();
        services.AddSingleton<BookSummaryBuildRegistry>();

        return (services.BuildServiceProvider(), logs);
    }

    /// <summary>
    /// THE DEFECT AS A LOG ASSERTION: BookReview routed at a cloud provider whose entry omits NumCtx. The
    /// budget collapses to the 256 floor and, before p1-3, absolutely nothing said so.
    /// </summary>
    [Fact]
    public void WholeBookTask_OnAProviderWithNoSizedWindow_LogsAWarningNamingProviderTaskAndValue()
    {
        var (sp, logs) = BuildAssembler(
            new Dictionary<string, ProviderTuningOptions>
            {
                // The pre-p1-3 shipped shape: a flat cloud entry with a cap but no window.
                ["OpenRouter"] = new ProviderTuningOptions { Temperature = 0.2, MaxTokens = 5120 }
            },
            new Dictionary<string, FeatureModelOptions>
            {
                ["BookReview"] = new FeatureModelOptions { Provider = "OpenRouter", Model = "google/gemma-4-31b-it" }
            });
        using var _ = sp;

        var asm = sp.GetRequiredService<BookContextAssembler>();
        var budget = asm.ResolveBudgetTokens(new[] { AiTaskType.BookReview });

        Assert.Equal(256, budget); // the collapse itself: essentially no book reaches the model
        var warning = Assert.Single(logs.Warnings);
        Assert.Contains("BookReview", warning, StringComparison.Ordinal);
        Assert.Contains("OpenRouter", warning, StringComparison.Ordinal);
        Assert.Contains("4096", warning, StringComparison.Ordinal);
        Assert.Contains("OpenRouter_BookReview", warning, StringComparison.Ordinal); // the exact key to add
    }

    /// <summary>
    /// De-duplicated per (provider, task, window): a windowed review derives the budget once per window plus
    /// twice more for the digests, and an observability signal that repeats a dozen times per run is one people
    /// filter out. One instance serves a whole review build (the assembler is AddScoped).
    /// </summary>
    [Fact]
    public void TheWarning_IsEmittedOncePerRun_NotOncePerLookup()
    {
        var (sp, logs) = BuildAssembler(
            new Dictionary<string, ProviderTuningOptions>
            {
                ["OpenRouter"] = new ProviderTuningOptions { Temperature = 0.2, MaxTokens = 5120 }
            },
            new Dictionary<string, FeatureModelOptions>
            {
                ["BookReview"] = new FeatureModelOptions { Provider = "OpenRouter", Model = "google/gemma-4-31b-it" }
            });
        using var _ = sp;

        var asm = sp.GetRequiredService<BookContextAssembler>();
        for (var i = 0; i < 12; i++) asm.ResolveBudgetTokens(new[] { AiTaskType.BookReview });

        Assert.Single(logs.Warnings);
    }

    /// <summary>
    /// NO FALSE ALARM ON A CORRECT CONFIG. This is the reason the warning lives on the whole-book path rather
    /// than inside the resolver: 4096 is a legitimate resolved window for the CHUNKED tasks (the shipped
    /// Ollama_Proofread / Ollama_LineEdit entries omit NumCtx and resolve exactly that on purpose), so a
    /// warning at the resolver would fire constantly on a correct config and be learned into invisibility.
    /// </summary>
    [Fact]
    public void ASizedWholeBookWindow_LogsNothing()
    {
        var (sp, logs) = BuildAssembler(
            new Dictionary<string, ProviderTuningOptions>
            {
                ["OpenRouter"] = new ProviderTuningOptions { Temperature = 0.2, MaxTokens = 5120 },
                ["OpenRouter_BookReview"] = new ProviderTuningOptions { Temperature = 0.2, MaxTokens = 6144, NumCtx = 16384 }
            },
            new Dictionary<string, FeatureModelOptions>
            {
                ["BookReview"] = new FeatureModelOptions { Provider = "OpenRouter", Model = "google/gemma-4-31b-it" }
            });
        using var _ = sp;

        var asm = sp.GetRequiredService<BookContextAssembler>();
        Assert.Equal(8192, asm.ResolveBudgetTokens(new[] { AiTaskType.BookReview }));
        Assert.Empty(logs.Warnings);
    }

    /// <summary>
    /// THE EMITTER-REACHABILITY PIN (review P2-5, fix be-c04). The warning exists to make the silent
    /// 4096-collapse loud on a MISCONFIGURED DEPLOYMENT, and every caller of
    /// <see cref="BookContextAssembler.ResolveBudgetTokens"/> sizes at <see cref="AiTier.Fast"/> - deliberately,
    /// and pinned inert by <c>TheWholeBookBudget_IsUnmovedByTheTier_AtTheShippedValues</c>. If the emitter
    /// checked only the tier it was HANDED, the day a whole-book task joins <see cref="AiTierPolicy.TieredTasks"/>
    /// it would resolve the FAST provider and stay silent about a THINKING route with no sized window - missing
    /// precisely the misconfiguration it was added for.
    ///
    /// So this drives the reachable shape of exactly that: <see cref="AiTaskType.LinguisticAnalysis"/> IS both a
    /// whole-book task and an allowlisted tiered task today, its FAST route is properly sized (16384), and its
    /// THINKING route lands on a flat cloud entry that sets no NumCtx. The call sizes at Fast (unchanged, and
    /// asserted, so this test also proves the fix did NOT move the budget), and the warning must still name the
    /// Thinking route, the cloud provider and the exact key to add.
    /// </summary>
    [Fact]
    public void AWholeBookTask_UnsizedOnTheThinkingRoute_WarnsEvenThoughTheBudgetIsSizedOnTheFastRoute()
    {
        var (sp, logs) = BuildAssembler(
            new Dictionary<string, ProviderTuningOptions>
            {
                // FAST route: properly sized, so a tier-blind emitter sees nothing wrong.
                ["Ollama"] = new ProviderTuningOptions { NumCtx = 8192, NumPredict = 2048 },
                ["Ollama_LinguisticAnalysis"] = new ProviderTuningOptions { NumCtx = 16384, NumPredict = 5120 },
                // THINKING route: the pre-p1-3 shape - a cap but no window, so NumCtx binds the class default.
                ["OpenRouter"] = new ProviderTuningOptions { Temperature = 0.2, MaxTokens = 5120 }
            },
            new Dictionary<string, FeatureModelOptions>
            {
                ["LinguisticAnalysis"] = new FeatureModelOptions { Provider = "Ollama", Model = "gemma4:12b" },
                ["LinguisticAnalysis_thinking"] =
                    new FeatureModelOptions { Provider = "OpenRouter", Model = "google/gemma-4-31b-it" }
            });
        using var _ = sp;

        var asm = sp.GetRequiredService<BookContextAssembler>();
        var budget = asm.ResolveBudgetTokens(new[] { AiTaskType.LinguisticAnalysis });

        // The budget is still the FAST route's (min(16384*0.5, 16384-5120-1536-512) = 8192): this fix is
        // observability only and must not move the deliberately-Fast sizing p3-3 correction 4 recorded.
        Assert.Equal(8192, budget);

        var warning = Assert.Single(logs.Warnings);
        Assert.Contains("LinguisticAnalysis", warning, StringComparison.Ordinal);
        Assert.Contains("OpenRouter", warning, StringComparison.Ordinal);
        Assert.Contains("Thinking tier(s)", warning, StringComparison.Ordinal); // the route it would have missed
        Assert.DoesNotContain("Fast tier(s)", warning, StringComparison.Ordinal); // Fast is sized: not warned about
        Assert.Contains("derived on the Fast route", warning, StringComparison.Ordinal); // budget provenance stated
        Assert.Contains("4096", warning, StringComparison.Ordinal);
        Assert.Contains("OpenRouter_LinguisticAnalysis", warning, StringComparison.Ordinal); // the exact key to add
    }

    /// <summary>
    /// The other half of tier-independence: a task the tier does NOT move (BookReview is outside
    /// <see cref="AiTierPolicy.TieredTasks"/>) resolves the same route on both tiers, so evaluating every tier
    /// must still produce ONE warning, not one per tier. The de-duplication key is the ROUTE
    /// (<c>provider|task|numCtx</c>) precisely so the identical remedy is not printed twice.
    /// </summary>
    [Fact]
    public void AnUnsizedRouteSharedByBothTiers_WarnsOnce_AndNamesBothTiers()
    {
        var (sp, logs) = BuildAssembler(
            new Dictionary<string, ProviderTuningOptions>
            {
                ["OpenRouter"] = new ProviderTuningOptions { Temperature = 0.2, MaxTokens = 5120 }
            },
            new Dictionary<string, FeatureModelOptions>
            {
                ["BookReview"] = new FeatureModelOptions { Provider = "OpenRouter", Model = "google/gemma-4-31b-it" }
            });
        using var _ = sp;

        sp.GetRequiredService<BookContextAssembler>().ResolveBudgetTokens(new[] { AiTaskType.BookReview });

        var warning = Assert.Single(logs.Warnings);
        Assert.Contains("Fast/Thinking tier(s)", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the shipped config is silent on EVERY whole-book route, local or cloud - the end-to-end statement
    /// that p1-3 actually closed the hole rather than merely instrumenting it. Since be-c04 made the emitter
    /// tier-INDEPENDENT this also covers every tier: the shipped <c>LinguisticAnalysis_thinking</c> route
    /// (OpenRouter, 16384) is checked here too, so the guarantee is "silent on every whole-book route on every
    /// tier", and it must STAY silent.
    /// </summary>
    [Fact]
    public void TheShippedConfig_WarnsForNoWholeBookTask_OnAnyRegisteredProvider()
    {
        var shipped = ProviderTuningConfigParityTests.LoadShippedAiOptions();
        var wholeBookTasks = new[]
        {
            AiTaskType.BookReview, AiTaskType.LinguisticAnalysis, AiTaskType.Summarization, AiTaskType.GenericChat
        };

        foreach (var provider in ProviderTuningOutputKnobTests.RegisteredProviderNames())
        {
            foreach (var task in wholeBookTasks)
            {
                // Only BookReview is floored on the waived providers, by decision; the others are asserted on
                // the routes that actually exist (Ollama today, OpenRouter as the wired cloud tier target).
                if (task != AiTaskType.BookReview
                    && !provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase)
                    && !provider.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase))
                    continue;

                var features = new Dictionary<string, FeatureModelOptions>(shipped.FeatureModels!)
                {
                    [task.ToString()] = new FeatureModelOptions { Provider = provider, Model = "tier-model" }
                };
                var (sp, logs) = BuildAssembler(
                    new Dictionary<string, ProviderTuningOptions>(shipped.ProviderSettings!), features);
                using var _ = sp;

                sp.GetRequiredService<BookContextAssembler>().ResolveBudgetTokens(new[] { task });

                Assert.True(logs.Warnings.Count == 0,
                    $"{provider}/{task} still resolves an unsized whole-book window: {string.Join(" | ", logs.Warnings)}");
            }
        }
    }
}
