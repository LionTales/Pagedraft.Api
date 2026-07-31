using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Unit-level semantics of <see cref="ProviderTuningResolver"/> — the ONE implementation of the
/// <c>"{Provider}_{TaskType}"</c> → <c>"{Provider}"</c> → class-default tuning precedence (model-tier
/// plan, p1-1). Before p1-1 that precedence was written out longhand in seven places and FOUR of them
/// (the cloud providers) had dropped the per-task rung entirely.
///
/// These are pure in-memory assertions: no config file, no network. The companion class
/// <see cref="ProviderTuningConfigParityTests"/> pins the SHIPPED values, and
/// <see cref="ProviderTuningWirePayloadTests"/> pins that the resolved values actually reach the wire.
/// </summary>
public class ProviderTuningResolverTests
{
    private static Dictionary<string, ProviderTuningOptions> Settings() => new()
    {
        ["Ollama"] = new ProviderTuningOptions { Temperature = 0.1, NumPredict = 111, NumCtx = 8192 },
        ["Ollama_BookReview"] = new ProviderTuningOptions { Temperature = 0.9, NumPredict = 6144, NumCtx = 16384 },
        ["OpenRouter"] = new ProviderTuningOptions { Temperature = 0.2, MaxTokens = 5120 },
        ["OpenRouter_BookReview"] = new ProviderTuningOptions { Temperature = 0.2, MaxTokens = 9999, NumCtx = 32768 }
    };

    // ---- Object-level rungs (what a provider sends) ------------------------------------------------

    [Fact]
    public void Resolve_PrefersTheTaskSpecificKeyOverTheFlatProviderKey()
    {
        var tuning = ProviderTuningResolver.Resolve(Settings(), "Ollama", AiTaskType.BookReview);

        Assert.Equal(6144, tuning.NumPredict);
        Assert.Equal(16384, tuning.NumCtx);
        Assert.Equal(0.9, tuning.Temperature);
    }

    [Fact]
    public void Resolve_FallsBackToTheFlatProviderKeyWhenNoTaskEntryExists()
    {
        var tuning = ProviderTuningResolver.Resolve(Settings(), "Ollama", AiTaskType.Translation);

        Assert.Equal(111, tuning.NumPredict);
        Assert.Equal(8192, tuning.NumCtx);
    }

    [Fact]
    public void Resolve_FallsBackToTheClassDefaultWhenNeitherKeyExists()
    {
        var tuning = ProviderTuningResolver.Resolve(Settings(), "NoSuchProvider", AiTaskType.BookReview);

        var expected = new ProviderTuningOptions();
        Assert.Equal(expected.Temperature, tuning.Temperature);
        Assert.Equal(expected.MaxTokens, tuning.MaxTokens);
        Assert.Equal(expected.NumPredict, tuning.NumPredict);
        Assert.Equal(expected.RepeatPenalty, tuning.RepeatPenalty);
        Assert.Equal(expected.NumCtx, tuning.NumCtx);
    }

    [Fact]
    public void Resolve_HandlesANullSettingsMap()
    {
        var tuning = ProviderTuningResolver.Resolve(null, "Ollama", AiTaskType.Proofread);

        Assert.Equal(new ProviderTuningOptions().NumCtx, tuning.NumCtx);
    }

    /// <summary>
    /// The p1-1 CAPABILITY EXTENSION, stated as a test: a non-Ollama provider now gets its own per-task
    /// rung. Before p1-1 the cloud providers looked up the FLAT key only, so this would have returned
    /// MaxTokens 5120 (and there was no way at all to raise a cloud task's limit).
    /// </summary>
    [Fact]
    public void Resolve_HonoursThePerTaskRungForNonOllamaProvidersToo()
    {
        var tuning = ProviderTuningResolver.Resolve(Settings(), "OpenRouter", AiTaskType.BookReview);

        Assert.Equal(9999, tuning.MaxTokens);
        Assert.Equal(32768, tuning.NumCtx);
    }

    [Fact]
    public void Resolve_NonOllamaProviderStillFallsBackToItsFlatKeyForUnkeyedTasks()
    {
        var tuning = ProviderTuningResolver.Resolve(Settings(), "OpenRouter", AiTaskType.Proofread);

        Assert.Equal(5120, tuning.MaxTokens);
    }

    /// <summary>
    /// THE SILENT-DEFAULT TRAP, pinned so it is documented behaviour rather than a surprise: a provider
    /// entry that EXISTS but omits a field wins the object-level lookup and supplies that field's CLASS
    /// default. The flat OpenRouter entry sets no NumCtx, so it hands back 4096 — this is exactly why the
    /// cloud BookReview budget collapses 16384 → 4096 today, and why p1-3 must add real per-task cloud
    /// entries rather than assume inheritance.
    /// </summary>
    [Fact]
    public void Resolve_AnEntryThatOmitsAFieldSuppliesTheClassDefaultRatherThanFallingThrough()
    {
        var tuning = ProviderTuningResolver.Resolve(Settings(), "OpenRouter", AiTaskType.Proofread);

        Assert.Equal(4096, tuning.NumCtx);
        Assert.Equal(4096, new ProviderTuningOptions().NumCtx);
    }

    // ---- Field-level rungs (what the budget sizers read) --------------------------------------------

    [Fact]
    public void ResolvePositiveInt_PrefersTheTaskSpecificKey()
    {
        var numCtx = ProviderTuningResolver.ResolvePositiveInt(Settings(), "Ollama", AiTaskType.BookReview, t => t.NumCtx);

        Assert.Equal(16384, numCtx);
    }

    /// <summary>
    /// The FIELD-level rung differs from the object-level one: a rung whose selected field is &lt;= 0 falls
    /// THROUGH to the next rung instead of winning. Only an explicit zero triggers it — at today's class
    /// defaults (NumCtx 4096, NumPredict 2048) an entry that merely OMITS the field still binds a positive
    /// value and wins, which is why the shipped Ollama_Proofread resolves 4096 and not the flat entry's 8192.
    /// </summary>
    [Fact]
    public void ResolvePositiveInt_FallsThroughARungWhoseFieldIsExplicitlyZero()
    {
        var settings = new Dictionary<string, ProviderTuningOptions>
        {
            ["Ollama"] = new ProviderTuningOptions { NumCtx = 8192 },
            ["Ollama_Proofread"] = new ProviderTuningOptions { NumCtx = 0, NumPredict = 4096 }
        };

        Assert.Equal(8192, ProviderTuningResolver.ResolvePositiveInt(settings, "Ollama", AiTaskType.Proofread, t => t.NumCtx));
        Assert.Equal(4096, ProviderTuningResolver.ResolvePositiveInt(settings, "Ollama", AiTaskType.Proofread, t => t.NumPredict));
    }

    [Fact]
    public void ResolvePositiveInt_FallsBackToTheFieldsClassDefaultWhenNothingMatches()
    {
        Assert.Equal(new ProviderTuningOptions().NumCtx,
            ProviderTuningResolver.ResolvePositiveInt(Settings(), "NoSuchProvider", AiTaskType.Proofread, t => t.NumCtx));
        Assert.Equal(new ProviderTuningOptions().NumPredict,
            ProviderTuningResolver.ResolvePositiveInt(null, "Ollama", AiTaskType.Proofread, t => t.NumPredict));
    }

    /// <summary>
    /// The refactor's value-preservation premise: every one of the seven pre-p1-1 inline fallbacks
    /// (<c>{ Temperature = 0.2, NumPredict = 2048 }</c> in OllamaProvider, <c>{ Temperature = 0.2,
    /// MaxTokens = 2048 }</c> in the four cloud providers) merely RESTATED the class defaults, so
    /// collapsing them onto <c>new ProviderTuningOptions()</c> changed no value. If a future edit moves a
    /// class default, this test goes red and forces the question rather than silently shifting every
    /// provider's unconfigured fallback.
    /// </summary>
    [Fact]
    public void ClassDefault_StillEqualsTheInlineFallbacksTheProvidersUsedBeforeP1_1()
    {
        var actual = new ProviderTuningOptions();
        var oldOllamaFallback = new ProviderTuningOptions { Temperature = 0.2, NumPredict = 2048 };
        var oldCloudFallback = new ProviderTuningOptions { Temperature = 0.2, MaxTokens = 2048 };

        foreach (var old in new[] { oldOllamaFallback, oldCloudFallback })
        {
            Assert.Equal(old.Temperature, actual.Temperature);
            Assert.Equal(old.MaxTokens, actual.MaxTokens);
            Assert.Equal(old.NumPredict, actual.NumPredict);
            Assert.Equal(old.RepeatPenalty, actual.RepeatPenalty);
            Assert.Equal(old.NumCtx, actual.NumCtx);
        }
    }

    [Fact]
    public void TaskKey_IsTheDocumentedUnderscoreForm()
    {
        Assert.Equal("Ollama_BookReview", ProviderTuningResolver.TaskKey("Ollama", AiTaskType.BookReview));
        Assert.Equal("OpenRouter_LinguisticAnalysis", ProviderTuningResolver.TaskKey("OpenRouter", AiTaskType.LinguisticAnalysis));
    }
}

/// <summary>
/// THE p1-1 REGRESSION PIN. p1-1 is a refactor plus a capability extension and it must not move a single
/// resolved VALUE on the Ollama path. This binds the REAL <c>Pagedraft.Api/appsettings.json</c> and asserts,
/// for EVERY <see cref="AiTaskType"/>, the exact num_ctx / num_predict / repeat_penalty the shipped config
/// resolves to — both through the provider's whole-entry lookup (what OllamaProvider actually sends) and
/// through <see cref="BookContextAssembler.ResolveNumCtxForTask"/> (what the budget sizers read).
///
/// The expected table is HAND-AUTHORED from appsettings.json as it stood on 2026-07-28, deliberately NOT
/// derived from the config at test time — a derived table would be a tautology that passes for any config.
/// Editing Ai:ProviderSettings is therefore expected to turn this red; update the table WITH the config and
/// state which task's effective window moved.
///
/// Class named *ConfigParityTests so the plan's standing
/// <c>--filter "FullyQualifiedName~ConfigParity"</c> run picks it up alongside the other config guards.
/// </summary>
public class ProviderTuningConfigParityTests
{
    /// <summary>One row of the hand-authored oracle. NumCtx/NumPredict/RepeatPenalty as of 2026-07-28.</summary>
    private readonly record struct Expected(int ProviderNumCtx, int ProviderNumPredict, double RepeatPenalty, int BudgetNumCtx, int BudgetNumPredict);

    /// <summary>
    /// The shipped resolution, task by task. Sources:
    ///   Ollama                     = { NumPredict 2048, NumCtx 8192 }
    ///   Ollama_Proofread           = { NumPredict 4096 }                                  → NumCtx unset → 4096 (class default)
    ///   Ollama_LineEdit            = { NumPredict 5120, RepeatPenalty 1.3 }               → NumCtx unset → 4096
    ///   Ollama_LinguisticAnalysis  = { NumPredict 5120, RepeatPenalty 1.2, NumCtx 16384 }
    ///   Ollama_BookReview          = { NumPredict 6144, NumCtx 16384 }
    ///   Ollama_Summarization       = { NumPredict 2048, NumCtx 16384 }
    ///   Ollama_AnalysisRepair      = { NumPredict 2048, NumCtx 16384 }
    ///   Ollama_TermRepair          = { NumPredict  256, NumCtx 16384 }
    ///   Ollama_GenericChat         = { NumPredict 2048, NumCtx 16384 }
    /// Translation has NO per-task entry and no FeatureModels key, so it falls to the flat Ollama entry.
    ///
    /// READ THE PROOFREAD/LINEEDIT ROWS: their entries omit NumCtx, so BOTH surfaces resolve the 4096 class
    /// default, NOT the flat Ollama entry's 8192. That is the silent-default trap in production config, and
    /// it means the proofread/LineEdit chunk sizer (UnifiedAnalysisService.EffectiveChunkTargetWords, which
    /// reads ResolveNumCtxForTask) sizes against the same 4096 the provider actually sends — the two agree,
    /// which is the property that matters here.
    /// </summary>
    private static readonly Dictionary<AiTaskType, Expected> Oracle = new()
    {
        [AiTaskType.Proofread] = new Expected(4096, 4096, 1.1, 4096, 4096),
        [AiTaskType.LineEdit] = new Expected(4096, 5120, 1.3, 4096, 5120),
        [AiTaskType.LinguisticAnalysis] = new Expected(16384, 5120, 1.2, 16384, 5120),
        [AiTaskType.Summarization] = new Expected(16384, 2048, 1.1, 16384, 2048),
        [AiTaskType.Translation] = new Expected(8192, 2048, 1.1, 8192, 2048),
        [AiTaskType.GenericChat] = new Expected(16384, 2048, 1.1, 16384, 2048),
        [AiTaskType.BookReview] = new Expected(16384, 6144, 1.1, 16384, 6144),
        [AiTaskType.AnalysisRepair] = new Expected(16384, 2048, 1.1, 16384, 2048),
        [AiTaskType.TermRepair] = new Expected(16384, 256, 1.1, 16384, 256)
    };

    [Fact]
    public void EveryAiTaskType_HasAnExpectedRow()
    {
        var missing = Enum.GetValues<AiTaskType>().Where(t => !Oracle.ContainsKey(t)).ToList();
        Assert.True(missing.Count == 0,
            "A new AiTaskType was added without a row in this pin, so nobody decided what context window it " +
            $"resolves to: [{string.Join(", ", missing)}]. Add its expected values (and, if it sends a large " +
            "prompt, an Ai:ProviderSettings:Ollama_<Task> entry) rather than letting it inherit 4096 silently.");
    }

    [Fact]
    public void ShippedOllamaTuning_ResolvesExactlyWhatItResolvedBeforeP1_1()
    {
        var opt = LoadShippedAiOptions();

        foreach (var (task, expected) in Oracle)
        {
            // (a) What the PROVIDER sends: the whole-entry lookup OllamaProvider performs.
            var tuning = ProviderTuningResolver.Resolve(opt.ProviderSettings, "Ollama", task);
            Assert.True(expected.ProviderNumCtx == tuning.NumCtx,
                $"{task}: provider num_ctx expected {expected.ProviderNumCtx}, got {tuning.NumCtx}.");
            Assert.True(expected.ProviderNumPredict == tuning.NumPredict,
                $"{task}: provider num_predict expected {expected.ProviderNumPredict}, got {tuning.NumPredict}.");
            Assert.True(Math.Abs(expected.RepeatPenalty - tuning.RepeatPenalty) < 1e-9,
                $"{task}: provider repeat_penalty expected {expected.RepeatPenalty}, got {tuning.RepeatPenalty}.");

            // (b) What the BUDGET SIZERS read (BookContextAssembler / the chunk sizer), via the field-level rung.
            var budgetNumCtx = BookContextAssembler.ResolveNumCtxForTask(opt, task);
            Assert.True(expected.BudgetNumCtx == budgetNumCtx,
                $"{task}: BookContextAssembler.ResolveNumCtxForTask expected {expected.BudgetNumCtx}, got {budgetNumCtx}.");
            var budgetNumPredict = ProviderTuningResolver.ResolvePositiveInt(
                opt.ProviderSettings, "Ollama", task, t => t.NumPredict);
            Assert.True(expected.BudgetNumPredict == budgetNumPredict,
                $"{task}: budget num_predict expected {expected.BudgetNumPredict}, got {budgetNumPredict}.");
        }
    }

    /// <summary>
    /// The two surfaces must AGREE on the Ollama path: what the budget sizer assumes the window is and what
    /// the provider actually sends must be the same number, or the sizer packs a prompt the model then
    /// truncates. (They can diverge whenever a task entry sets NumCtx to an explicit 0 — see
    /// ProviderTuningResolverTests' fall-through test — hence this guard.)
    /// </summary>
    [Fact]
    public void BudgetSizerAndProvider_AgreeOnNumCtx_ForEveryTask()
    {
        var opt = LoadShippedAiOptions();

        var divergent = Enum.GetValues<AiTaskType>()
            .Select(task => (task,
                provider: ProviderTuningResolver.Resolve(opt.ProviderSettings, "Ollama", task).NumCtx,
                budget: BookContextAssembler.ResolveNumCtxForTask(opt, task)))
            .Where(x => x.provider != x.budget)
            .Select(x => $"{x.task}: provider sends num_ctx={x.provider} but the sizer assumed {x.budget}")
            .ToList();

        Assert.True(divergent.Count == 0, string.Join("; ", divergent));
    }

    /// <summary>
    /// THE CLOUD ENTRY, pinned as it ships AFTER p1-3. This test was authored by p1-1 as
    /// <c>ShippedOpenRouterTuning_IsStillFlatKeyOnly_AndBindsTheClassDefaultsForTheOllamaFields</c>, recording
    /// the PRE-p1-3 state: no per-task OpenRouter key existed, so a cloud BookReview bound NumCtx 4096 purely
    /// from the class default while the provider went on to request MaxTokens 5120. p1-1 said in as many words
    /// that it would go red the moment p1-3 added a per-task cloud entry, and that it must then be RESTATED
    /// rather than deleted. This is that restatement, inverted: the per-task keys now MUST exist, and the
    /// values they resolve are pinned.
    ///
    /// WHAT MOVED (p1-3): a cloud-routed BookReview resolves NumCtx 16384 (was the 4096 class default) and
    /// MaxTokens 6144 (was the flat entry's one-size 5120). WHAT DID NOT: the flat <c>OpenRouter</c> entry is
    /// still there and still governs any task with no per-task key, which the Translation assertion below
    /// keeps honest - Translation has no Ollama_* entry either, so it is deliberately outside the mirror set
    /// and still binds 4096. Coverage of the mirror itself lives in <c>CloudTuningCoverageConfigParityTests</c>.
    /// </summary>
    [Fact]
    public void ShippedOpenRouterTuning_HasPerTaskEntries_AndNoLongerBindsTheClassDefaultWindow()
    {
        var opt = LoadShippedAiOptions();
        var settings = opt.ProviderSettings;
        Assert.NotNull(settings);

        Assert.True(settings!.ContainsKey("OpenRouter"),
            "Ai:ProviderSettings:OpenRouter is missing - the per-task entries below still need a flat fallback " +
            "for any task that has no key of its own.");

        var perTaskCloudKeys = Enum.GetValues<AiTaskType>()
            .Select(t => ProviderTuningResolver.TaskKey("OpenRouter", t))
            .Where(settings.ContainsKey)
            .ToList();
        Assert.True(perTaskCloudKeys.Count > 0,
            "The per-task OpenRouter tuning entries p1-3 added are gone. Without them a cloud-routed whole-book " +
            "task binds the ProviderTuningOptions class default NumCtx (4096) and the book budget collapses " +
            "16384 -> 4096, which fails silently: prompt truncated, every dimension null, job reports Succeeded.");

        // The route the tier would actually take, through the OBJECT rung the provider uses.
        var bookReview = ProviderTuningResolver.Resolve(settings, "OpenRouter", AiTaskType.BookReview);
        Assert.Equal(6144, bookReview.MaxTokens);   // p1-3: was the flat entry's 5120
        Assert.Equal(16384, bookReview.NumCtx);     // p1-3: was the 4096 class default
        Assert.NotEqual(new ProviderTuningOptions().NumCtx, bookReview.NumCtx);

        // The flat entry still governs a task with no per-task key. Translation is that task, on purpose: it
        // has no Ollama_* entry either, so it is outside the mirror set p1-3 defined. Stated rather than left
        // implicit, so nobody reads "p1-3 fixed the cloud path" as "every cloud task is now sized".
        Assert.False(settings.ContainsKey(ProviderTuningResolver.TaskKey("OpenRouter", AiTaskType.Translation)));
        var translation = ProviderTuningResolver.Resolve(settings, "OpenRouter", AiTaskType.Translation);
        Assert.Equal(5120, translation.MaxTokens);
        Assert.Equal(new ProviderTuningOptions().NumCtx, translation.NumCtx);
    }

    /// <summary>Loads the REAL shipped appsettings.json as raw configuration. internal so a caller that
    /// needs to see whether a key is ABSENT (rather than bound to its class default) can ask the
    /// configuration directly - <c>HarnessConfigParityTests</c> does exactly that for the RepeatPenalty the
    /// cloud entries deliberately omit, which binding alone cannot distinguish from an explicit 1.1.</summary>
    internal static IConfigurationRoot LoadShippedConfiguration()
        => new ConfigurationBuilder()
            .AddJsonFile(FindUpward(Path.Combine("Pagedraft.Api", "appsettings.json")), optional: false)
            .Build();

    /// <summary>Binds the REAL shipped appsettings.json. internal so ProviderTuningOutputKnobTests (p1-2)
    /// reads the same file through the same loader rather than keeping a second copy of this walk.</summary>
    internal static AiOptions LoadShippedAiOptions()
    {
        var config = LoadShippedConfiguration();
        var opt = new AiOptions();
        config.GetSection(AiOptions.SectionName).Bind(opt);
        Assert.True(opt.ProviderSettings is { Count: > 0 },
            "Ai:ProviderSettings bound empty from the shipped Pagedraft.Api/appsettings.json.");
        return opt;
    }

    private static string FindUpward(string relativeSubPath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relativeSubPath);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("Could not locate " + relativeSubPath + " above " + AppContext.BaseDirectory);
    }
}

/// <summary>
/// RULE 0 for p1-1: the resolved tuning is only really provider-agnostic if it reaches the WIRE. These drive
/// the real provider classes through a stubbed <see cref="IHttpClientFactory"/> and assert the captured
/// request body carries the per-task value — the assertion the seven longhand copies never had, and the one
/// that would have caught the cloud providers' missing task rung.
///
/// Class named *ProviderTuning* so it is picked up by a <c>FullyQualifiedName~ProviderTuning</c> filter
/// alongside the other two classes in this file.
/// </summary>
public class ProviderTuningWirePayloadTests
{
    // internal (not private) so ProviderTuningOutputKnobTests below drives the SAME stubs rather than
    // maintaining a second copy of them.
    internal sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _body;
        public CapturingHandler(string body) => _body = body;
        public List<string> RequestBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body) };
        }
    }

    internal sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        // A fresh client per call: OllamaProvider assigns BaseAddress after CreateClient.
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    internal static ResolvedAiRequest Request(string provider, AiTaskType task) => new()
    {
        SystemMessage = "sys",
        Instruction = "instruction",
        InputText = "input",
        Selection = new AiModelSelection { Provider = provider, Model = "some-model" },
        TaskType = task
    };

    [Fact]
    public async Task Ollama_SendsThePerTaskNumCtxAndNumPredict()
    {
        var handler = new CapturingHandler("{\"response\":\"ok\"}");
        var options = Options.Create(new AiOptions
        {
            ProviderSettings = new Dictionary<string, ProviderTuningOptions>
            {
                ["Ollama"] = new ProviderTuningOptions { NumPredict = 2048, NumCtx = 8192 },
                ["Ollama_BookReview"] = new ProviderTuningOptions { NumPredict = 6144, NumCtx = 16384 }
            }
        });
        var provider = new OllamaProvider(
            new StubHttpClientFactory(handler),
            new ConfigurationBuilder().Build(),
            options);

        await provider.CompleteAsync(Request("Ollama", AiTaskType.BookReview));

        var body = Assert.Single(handler.RequestBodies);
        Assert.Contains("\"num_ctx\":16384", body, StringComparison.Ordinal);
        Assert.Contains("\"num_predict\":6144", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE p1-1 DEFECT, as a failing-before/passing-after test: with an OpenRouter_BookReview entry present,
    /// the OpenRouter path must send THAT entry's max_tokens. Before p1-1 it looked up the flat "OpenRouter"
    /// key only and would have sent 5120 no matter what the task entry said.
    /// </summary>
    [Fact]
    public async Task OpenAiCompatible_SendsThePerTaskMaxTokens_NotJustTheFlatProviderEntry()
    {
        var handler = new CapturingHandler(
            "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Providers:OpenRouter:BaseUrl"] = "https://openrouter.example/api/v1",
                ["Ai:Providers:OpenRouter:ApiKey"] = "test-key"
            })
            .Build();
        var options = Options.Create(new AiOptions
        {
            ProviderSettings = new Dictionary<string, ProviderTuningOptions>
            {
                ["OpenRouter"] = new ProviderTuningOptions { Temperature = 0.2, MaxTokens = 5120 },
                ["OpenRouter_BookReview"] = new ProviderTuningOptions { Temperature = 0.2, MaxTokens = 9999 }
            }
        });
        var provider = new OpenAiCompatibleProvider("OpenRouter", new StubHttpClientFactory(handler), config, options);

        await provider.CompleteAsync(Request("OpenRouter", AiTaskType.BookReview));

        var body = Assert.Single(handler.RequestBodies);
        Assert.Contains("\"max_tokens\":9999", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"max_tokens\":5120", body, StringComparison.Ordinal);
    }

    /// <summary>A task with no per-task cloud entry must still use the flat provider entry (no regression).</summary>
    [Fact]
    public async Task OpenAiCompatible_FallsBackToTheFlatProviderEntryForAnUnkeyedTask()
    {
        var handler = new CapturingHandler(
            "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Providers:OpenRouter:BaseUrl"] = "https://openrouter.example/api/v1",
                ["Ai:Providers:OpenRouter:ApiKey"] = "test-key"
            })
            .Build();
        var options = Options.Create(new AiOptions
        {
            ProviderSettings = new Dictionary<string, ProviderTuningOptions>
            {
                ["OpenRouter"] = new ProviderTuningOptions { Temperature = 0.2, MaxTokens = 5120 },
                ["OpenRouter_BookReview"] = new ProviderTuningOptions { Temperature = 0.2, MaxTokens = 9999 }
            }
        });
        var provider = new OpenAiCompatibleProvider("OpenRouter", new StubHttpClientFactory(handler), config, options);

        await provider.CompleteAsync(Request("OpenRouter", AiTaskType.Proofread));

        var body = Assert.Single(handler.RequestBodies);
        Assert.Contains("\"max_tokens\":5120", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anthropic_SendsThePerTaskMaxTokens()
    {
        var handler = new CapturingHandler("{\"content\":[{\"text\":\"ok\"}]}");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Providers:Anthropic:ApiKey"] = "test-key"
            })
            .Build();
        var options = Options.Create(new AiOptions
        {
            ProviderSettings = new Dictionary<string, ProviderTuningOptions>
            {
                ["Anthropic"] = new ProviderTuningOptions { Temperature = 0.2, MaxTokens = 2048 },
                ["Anthropic_BookReview"] = new ProviderTuningOptions { Temperature = 0.2, MaxTokens = 7777 }
            }
        });
        var provider = new AnthropicProvider(new StubHttpClientFactory(handler), config, options);

        await provider.CompleteAsync(Request("Anthropic", AiTaskType.BookReview));

        var body = Assert.Single(handler.RequestBodies);
        Assert.Contains("\"max_tokens\":7777", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAi_SendsThePerTaskMaxTokens()
    {
        var handler = new CapturingHandler("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Providers:OpenAI:ApiKey"] = "test-key"
            })
            .Build();
        var options = Options.Create(new AiOptions
        {
            ProviderSettings = new Dictionary<string, ProviderTuningOptions>
            {
                ["OpenAI"] = new ProviderTuningOptions { Temperature = 0.2, MaxTokens = 2048 },
                ["OpenAI_BookReview"] = new ProviderTuningOptions { Temperature = 0.2, MaxTokens = 6666 }
            }
        });
        var provider = new OpenAiProvider(new StubHttpClientFactory(handler), config, options);

        await provider.CompleteAsync(Request("OpenAI", AiTaskType.BookReview));

        var body = Assert.Single(handler.RequestBodies);
        Assert.Contains("\"max_tokens\":6666", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Azure_SendsThePerTaskMaxTokens()
    {
        var handler = new CapturingHandler("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:Providers:Azure:Endpoint"] = "https://azure.example",
                ["Ai:Providers:Azure:DeploymentName"] = "deployment",
                ["Ai:Providers:Azure:ApiKey"] = "test-key"
            })
            .Build();
        var options = Options.Create(new AiOptions
        {
            ProviderSettings = new Dictionary<string, ProviderTuningOptions>
            {
                ["Azure"] = new ProviderTuningOptions { Temperature = 0.2, MaxTokens = 2048 },
                ["Azure_BookReview"] = new ProviderTuningOptions { Temperature = 0.2, MaxTokens = 5555 }
            }
        });
        var provider = new AzureOpenAiProvider(new StubHttpClientFactory(handler), config, options);

        await provider.CompleteAsync(Request("Azure", AiTaskType.BookReview));

        var body = Assert.Single(handler.RequestBodies);
        Assert.Contains("\"max_tokens\":5555", body, StringComparison.Ordinal);
    }
}

/// <summary>
/// p1-2: THE OUTPUT-RESERVATION CONTRACT. <see cref="ProviderTuningOptions"/> carries two independent
/// properties that mean the same thing — <c>NumPredict</c> (Ollama's <c>num_predict</c>) and <c>MaxTokens</c>
/// (the cloud families' <c>max_tokens</c>) — and every appsettings entry sets only its own family's field.
/// Before p1-2 the whole-book budget sizer read <c>NumPredict</c> unconditionally, so for the shipped
/// <c>OpenRouter</c> entry (<c>MaxTokens 5120</c>, no NumPredict) it reserved the 2048 CLASS DEFAULT while the
/// provider went on to request 5120: a silent 3072-token under-reservation of output headroom on EVERY cloud
/// call, independent of the NumCtx collapse p1-3 fixes. Under-reserving is the dangerous direction — the input
/// claims room the model then needs for its answer, and the answer truncates.
///
/// The invariant these pin: RESERVED OUTPUT == WHAT THE PROVIDER ACTUALLY REQUESTS, end to end, for every
/// registered provider. Class named *ProviderTuning* so the plan's <c>FullyQualifiedName~ProviderTuning</c>
/// filter picks it up with the rest of the file.
/// </summary>
public class ProviderTuningOutputKnobTests
{
    /// <summary>
    /// One response body that satisfies EVERY provider's parser: Ollama reads <c>response</c>,
    /// OpenAI/Azure/OpenAI-compatible read <c>choices[0].message.content</c>, Anthropic reads
    /// <c>content[0].text</c>. Lets one loop drive the real registry.
    /// </summary>
    internal const string AnyProviderResponseBody =
        "{\"response\":\"ok\",\"choices\":[{\"message\":{\"content\":\"ok\"}}],\"content\":[{\"text\":\"ok\"}]}";

    /// <summary>Credentials/endpoints for every registered provider, so each one gets past its own config guard.</summary>
    internal static IConfiguration ProviderConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ai:Providers:OpenAI:ApiKey"] = "test-key",
            ["Ai:Providers:Anthropic:ApiKey"] = "test-key",
            ["Ai:Providers:Azure:Endpoint"] = "https://azure.example",
            ["Ai:Providers:Azure:DeploymentName"] = "deployment",
            ["Ai:Providers:Azure:ApiKey"] = "test-key",
            ["Ai:Providers:OpenRouter:BaseUrl"] = "https://openrouter.example/api/v1",
            ["Ai:Providers:OpenRouter:ApiKey"] = "test-key"
        })
        .Build();

    /// <summary>
    /// BookReview routed to <paramref name="providerName"/>, with a per-task entry that sets BOTH knobs to
    /// DIFFERENT values (NumPredict 6144 / MaxTokens 9999). Reading the wrong one is therefore a visible wrong
    /// number rather than a coincidence — the pre-p1-2 bug hid precisely because 2048 == 2048 in the defaults.
    /// </summary>
    private static AiOptions OptionsRoutedTo(string providerName) => new()
    {
        DefaultProvider = "Ollama",
        DefaultModel = "default-model",
        FeatureModels = new Dictionary<string, FeatureModelOptions>
        {
            // "some-model": deliberately NOT a gpt-5* or claude-opus-4-7* id, so no provider takes its
            // alternate payload shape (max_completion_tokens / temperature-omitted) in this test.
            [nameof(AiTaskType.BookReview)] = new FeatureModelOptions { Provider = providerName, Model = "some-model" }
        },
        ProviderSettings = new Dictionary<string, ProviderTuningOptions>
        {
            [providerName] = new ProviderTuningOptions { Temperature = 0.2, NumPredict = 2048, MaxTokens = 2048 },
            [providerName + "_BookReview"] = new ProviderTuningOptions { Temperature = 0.2, NumPredict = 6144, MaxTokens = 9999 }
        }
    };

    // ---- Field selection --------------------------------------------------------------------------

    /// <summary>
    /// THE p1-2 DEFECT as a test, on the SHIPPED cloud entry shape. The second assertion is the contrast that
    /// documents it: the pre-p1-2 NumPredict read returns 2048 against a provider that will request 5120.
    /// </summary>
    [Fact]
    public void ResolveOutputTokens_CloudProvider_ReadsMaxTokens_NotTheNumPredictClassDefault()
    {
        var settings = new Dictionary<string, ProviderTuningOptions>
        {
            ["OpenRouter"] = new ProviderTuningOptions { Temperature = 0.2, MaxTokens = 5120 }
        };

        Assert.Equal(5120, ProviderTuningResolver.ResolveOutputTokens(settings, "OpenRouter", AiTaskType.BookReview));
        Assert.Equal(2048, ProviderTuningResolver.ResolvePositiveInt(
            settings, "OpenRouter", AiTaskType.BookReview, t => t.NumPredict));
    }

    /// <summary>The Ollama path must be UNMOVED: NumPredict wins even with a decoy MaxTokens present.</summary>
    [Fact]
    public void ResolveOutputTokens_Ollama_ReadsNumPredict_EvenWhenMaxTokensIsAlsoSet()
    {
        var settings = new Dictionary<string, ProviderTuningOptions>
        {
            ["Ollama"] = new ProviderTuningOptions { NumPredict = 2048, MaxTokens = 9999 },
            ["Ollama_BookReview"] = new ProviderTuningOptions { NumPredict = 6144, MaxTokens = 9999 }
        };

        Assert.Equal(6144, ProviderTuningResolver.ResolveOutputTokens(settings, "Ollama", AiTaskType.BookReview));
        Assert.Equal(2048, ProviderTuningResolver.ResolveOutputTokens(settings, "Ollama", AiTaskType.Proofread));
    }

    /// <summary>Same FIELD-level rungs as the NumCtx sibling: task key, then flat key, then class default.</summary>
    [Fact]
    public void ResolveOutputTokens_UsesTheSameRungPrecedenceForBothFamilies()
    {
        var settings = new Dictionary<string, ProviderTuningOptions>
        {
            ["OpenRouter"] = new ProviderTuningOptions { MaxTokens = 5120 },
            ["OpenRouter_BookReview"] = new ProviderTuningOptions { MaxTokens = 9999 }
        };

        Assert.Equal(9999, ProviderTuningResolver.ResolveOutputTokens(settings, "OpenRouter", AiTaskType.BookReview));
        Assert.Equal(5120, ProviderTuningResolver.ResolveOutputTokens(settings, "OpenRouter", AiTaskType.Proofread));
        Assert.Equal(new ProviderTuningOptions().MaxTokens,
            ProviderTuningResolver.ResolveOutputTokens(null, "OpenRouter", AiTaskType.BookReview));
    }

    /// <summary>
    /// An UNCLASSIFIED provider reserves the LARGER of the two knobs. Over-reserving only shrinks the input
    /// budget (safe); under-reserving truncates generated output (the failure this phase exists to prevent).
    /// This is a safety net, not a substitute for classifying — the completeness test below is the loud part.
    /// </summary>
    [Fact]
    public void ResolveOutputTokens_UnknownProvider_ReservesTheLargerKnob()
    {
        var settings = new Dictionary<string, ProviderTuningOptions>
        {
            ["Mystery"] = new ProviderTuningOptions { NumPredict = 3000, MaxTokens = 7000 }
        };

        Assert.Null(ProviderTuningResolver.OutputKnobFor("Mystery"));
        Assert.Equal(7000, ProviderTuningResolver.ResolveOutputTokens(settings, "Mystery", AiTaskType.BookReview));
    }

    [Fact]
    public void OutputKnobFor_IsCaseInsensitive_MatchingTheProviderRegistry()
    {
        Assert.Equal(ProviderOutputTokenKnob.NumPredict, ProviderTuningResolver.OutputKnobFor("ollama"));
        Assert.Equal(ProviderOutputTokenKnob.MaxTokens, ProviderTuningResolver.OutputKnobFor("openrouter"));
    }

    // ---- Completeness: no provider may ship unclassified --------------------------------------------

    /// <summary>
    /// The guard that keeps this fix from decaying: EVERY provider in the real registry must carry an explicit
    /// output-knob classification. Adding a provider to <see cref="AiProviderRegistry"/> without one would fall
    /// into the conservative unknown branch silently.
    /// </summary>
    [Fact]
    public void EveryRegisteredProvider_HasAnExplicitOutputKnobClassification()
    {
        var registered = RegisteredProviderNames();
        Assert.NotEmpty(registered);

        var unclassified = registered.Where(n => ProviderTuningResolver.OutputKnobFor(n) == null).ToList();
        Assert.True(unclassified.Count == 0,
            $"Provider(s) registered with no output-knob classification: [{string.Join(", ", unclassified)}]. " +
            "Add each to ProviderTuningResolver.KnownOutputKnobs, stating whether it sends num_predict or " +
            "max_tokens, or the budget sizer reserves output headroom by guesswork.");
    }

    // ---- The end-to-end invariant -------------------------------------------------------------------

    /// <summary>
    /// THE p1-2 ACCEPTANCE TEST: for EVERY registered provider, the output the budget sizer RESERVES
    /// (<see cref="BookContextAssembler.ResolveOutputReserveForTask"/>, routed through FeatureModels exactly as
    /// the router routes it) equals the number that provider actually PUTS ON THE WIRE. Driven through the real
    /// registry rather than a hand-listed set, so a newly registered provider is covered automatically.
    /// The per-task entry sets NumPredict 6144 and MaxTokens 9999, so reading the wrong knob is a mismatch.
    /// </summary>
    [Fact]
    public async Task ReservedOutput_EqualsWhatEachRegisteredProviderActuallyRequests()
    {
        var failures = new List<string>();

        foreach (var name in RegisteredProviderNames())
        {
            var opt = OptionsRoutedTo(name);
            var reserved = BookContextAssembler.ResolveOutputReserveForTask(opt, AiTaskType.BookReview);

            var handler = new ProviderTuningWirePayloadTests.CapturingHandler(AnyProviderResponseBody);
            var providers = AiProviderRegistry.Create(
                new ProviderTuningWirePayloadTests.StubHttpClientFactory(handler),
                ProviderConfig(),
                Options.Create(opt));

            await providers[name].CompleteAsync(ProviderTuningWirePayloadTests.Request(name, AiTaskType.BookReview));

            var body = Assert.Single(handler.RequestBodies);
            var knob = ProviderTuningResolver.OutputKnobFor(name);
            Assert.NotNull(knob);
            var wireField = knob == ProviderOutputTokenKnob.NumPredict ? "num_predict" : "max_tokens";
            var expected = $"\"{wireField}\":{reserved}";

            if (!body.Contains(expected, StringComparison.Ordinal))
                failures.Add($"{name}: the sizer reserved {reserved} output tokens but the request body carries no {expected} (body: {body})");
        }

        Assert.True(failures.Count == 0, string.Join(" | ", failures));
    }

    /// <summary>
    /// The same invariant against the SHIPPED appsettings, on the exact route the tier would take: BookReview
    /// pointed at OpenRouter. The reservation must come from the cloud family's MaxTokens, NOT the 2048
    /// NumPredict class default the sizer read before p1-2.
    ///
    /// UPDATED BY p1-3, and the two numbers that moved are why. At p1-2 there was no per-task cloud entry, so
    /// this route resolved the FLAT OpenRouter entry: reserve 5120, and NumCtx still the 4096 class default,
    /// which this test asserted on purpose so it would not imply the cloud route was safe. p1-3 added
    /// <c>OpenRouter_BookReview</c> { MaxTokens 6144, NumCtx 16384 }, so the reserve is now 6144 and the window
    /// is no longer the class default. The p1-2 invariant is UNCHANGED and still the point of this test: the
    /// reserved number comes from MaxTokens (6144), never from NumPredict, whose class default (2048) is
    /// asserted alongside as the contrast so a regression to the wrong knob is a visible wrong number.
    /// </summary>
    [Fact]
    public void ShippedCloudEntry_ReservesItsMaxTokens_NotTheNumPredictClassDefault()
    {
        var opt = LoadShippedAiOptions();
        opt.FeatureModels!["BookReview"] = new FeatureModelOptions { Provider = "OpenRouter", Model = "google/gemma-4-31b-it" };

        Assert.Equal(6144, BookContextAssembler.ResolveOutputReserveForTask(opt, AiTaskType.BookReview));
        Assert.Equal(2048, ProviderTuningResolver.ResolvePositiveInt(
            opt.ProviderSettings, "OpenRouter", AiTaskType.BookReview, t => t.NumPredict));

        // p1-3: the window on that route is no longer the class default, which is the collapse this plan exists
        // to fix. Full coverage of the cloud entries lives in CloudTuningCoverageConfigParityTests.
        Assert.Equal(16384, BookContextAssembler.ResolveNumCtxForTask(opt, AiTaskType.BookReview));
        Assert.NotEqual(new ProviderTuningOptions().NumCtx,
            BookContextAssembler.ResolveNumCtxForTask(opt, AiTaskType.BookReview));
    }

    /// <summary>
    /// PREMISE CORRECTION, pinned so nobody re-derives it wrong (p1-2). The plan says the budget "under-reserves
    /// by 3072 tokens on every cloud call". The RESERVED NUMBER was indeed 3072 short — that part is real and is
    /// what the fix above corrects — but at today's values that error does NOT change the derived budget, because
    /// <see cref="AiOptions.EffectiveBookContextTokenBudget"/> takes the MINIMUM of the output-reserve bound and
    /// the <c>BookContextBudgetFraction</c> (0.5) bound, and one of the other two bounds dominates:
    ///   • at the cloud route's collapsed num_ctx 4096 BOTH reservations bottom out on the 256 FLOOR;
    ///   • at a post-p1-3 num_ctx 16384 both land on the 8192 fraction bound, since the reserve bound only binds
    ///     once the output cap exceeds ~ctx*(1-fraction) - promptReserve - safetyMargin (~6144 there).
    /// So the wrong knob becomes MATERIAL only above that crossover — where it hands out input room the model
    /// then needs for its answer. It is a latent defect made correct now rather than a live one, and p1-3 raising
    /// num_ctx does not by itself make it bite; a cloud entry with a large MaxTokens does.
    /// </summary>
    [Fact]
    public void OutputReserve_ChangesTheDerivedBudget_OnlyOnceTheCapBindsTheFormula()
    {
        var opt = new AiOptions
        {
            BookContextBudgetFraction = 0.5,
            BookContextPromptReserveTokens = 1536,
            BookContextSafetyMarginTokens = 512
        };

        // (1) The cloud route as it stands (num_ctx collapsed to 4096): both reservations hit the floor.
        Assert.Equal(256, opt.EffectiveBookContextTokenBudget(4096, 2048));
        Assert.Equal(256, opt.EffectiveBookContextTokenBudget(4096, 5120));

        // (2) A post-p1-3 window (16384): the 0.5 fraction bound dominates, so 2048-vs-5120 is invisible here too.
        Assert.Equal(8192, opt.EffectiveBookContextTokenBudget(16384, 2048));
        Assert.Equal(8192, opt.EffectiveBookContextTokenBudget(16384, 5120));

        // (3) Above the crossover the knob is load-bearing: reading 2048 instead of 10240 would have handed the
        //     input 4096 tokens the model needs for its output.
        Assert.Equal(4096, opt.EffectiveBookContextTokenBudget(16384, 10240));
        Assert.True(opt.EffectiveBookContextTokenBudget(16384, 10240) < opt.EffectiveBookContextTokenBudget(16384, 2048));
    }

    /// <summary>
    /// NO SHIPPED VALUE MOVED on the local path. Every task routes to Ollama today, so the provider-aware
    /// accessor must return exactly the NumPredict the pre-p1-2 code returned, for EVERY task.
    /// </summary>
    [Fact]
    public void ShippedOllamaRoutes_ReserveExactlyTheirNumPredict_UnchangedByP1_2()
    {
        var opt = LoadShippedAiOptions();

        foreach (var task in Enum.GetValues<AiTaskType>())
        {
            var (provider, _) = LinguisticModelResolver.ResolveForTask(opt, task);
            Assert.Equal("Ollama", provider); // premise of this pin: nothing ships cloud-routed yet
            var preP1_2 = ProviderTuningResolver.ResolvePositiveInt(opt.ProviderSettings, provider, task, t => t.NumPredict);
            Assert.True(preP1_2 == BookContextAssembler.ResolveOutputReserveForTask(opt, task),
                $"{task}: p1-2 moved the reserved output from {preP1_2}.");
        }
    }

    /// <summary>The REAL registry's provider names. internal so the p1-3 cloud-coverage guards sweep the same
    /// set rather than hand-listing providers (a hand list is exactly how a new provider ships unguarded).</summary>
    internal static List<string> RegisteredProviderNames()
    {
        var handler = new ProviderTuningWirePayloadTests.CapturingHandler(AnyProviderResponseBody);
        return AiProviderRegistry.Create(
                new ProviderTuningWirePayloadTests.StubHttpClientFactory(handler),
                ProviderConfig(),
                Options.Create(new AiOptions()))
            .Keys.ToList();
    }

    private static AiOptions LoadShippedAiOptions()
    {
        var opt = ProviderTuningConfigParityTests.LoadShippedAiOptions();
        Assert.True(opt.FeatureModels is { Count: > 0 }, "Ai:FeatureModels bound empty from the shipped appsettings.json.");
        return opt;
    }
}
