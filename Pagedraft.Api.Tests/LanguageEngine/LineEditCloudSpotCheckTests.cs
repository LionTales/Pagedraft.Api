using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace Pagedraft.Api.Tests.LanguageEngine;

/// <summary>
/// LINE EDIT — qualitative cloud spot-check (NOT a scored gold harness).
///
/// LineEdit deliberately has NO scored gold set (building one is deferred — see the proofread/line-edit
/// model memory). This test is the lightweight RUBRIC spot-check: it runs the REAL production LineEdit
/// prompt (PromptFactory LineEditHe core instruction, sent VERBATIM via the router — routing is faithful
/// to production because AiRouter.ShouldUseUnifiedInstructionVerbatim returns true for AiTaskType.LineEdit;
/// NOTE: this test uses the 2-arg GetAnalysisPrompt overload and therefore omits the injected context
/// sections — style profile / preceding-context / following-context — that production's 3-arg overload adds;
/// omitting those sections is representative here because the spot-check passages are clean, standalone
/// Hebrew text with no book context to inject)
/// over a couple of SHORT Hebrew passages reused from linguistic-gold.json, and PRINTS the raw model JSON so a
/// human can score it against the rubric:
///   - overreach        — does it propose changes a clean passage does not need / meaning-changing edits?
///   - preserve-meaning — do the suggestions keep the original meaning (no plot/content change)?
///   - valid-Hebrew     — are the suggested replacements grammatical, natural Hebrew?
///   - respect-voice    — does it keep the author's voice and register?
///
/// PURPOSE: the cloud model (default google/gemma-4-31b-it, a ~31B model that does NOT fit the 8 GB dev
/// GPU) is a PROXY for the bigger models we could host on a future GPU server for customers — i.e. it
/// measures the LineEdit quality ceiling beyond what the local 8 GB laptop can run. Compare its output
/// against the recorded local Dicta-3.0 baseline (minimal, voice-preserving) from the prior rubric
/// spot-check, or re-run this test with PROVIDER=Ollama / MODEL=&lt;dicta&gt; for a live side-by-side.
///
/// GATING — SKIP-BY-DEFAULT (CI stays green): the default provider is OpenRouter, which needs an API key
/// (env AI_OPENROUTER_APIKEY); if absent the test writes a message and returns. With PROVIDER=Ollama it
/// gates on Ollama reachability instead. Mirrors ProofreadQualityTests / LinguisticQualityTests gating.
///
/// Run cloud (default):  dotnet test --filter "FullyQualifiedName~LineEditCloudSpotCheck"
/// Run local Dicta:      set LINEEDIT_SPOTCHECK_PROVIDER=Ollama and
///                       LINEEDIT_SPOTCHECK_MODEL=hf.co/dicta-il/DictaLM-3.0-Nemotron-12B-Instruct-GGUF:latest
/// </summary>
public class LineEditCloudSpotCheckTests
{
    private readonly ITestOutputHelper _output;

    public LineEditCloudSpotCheckTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private const string OllamaBaseUrl = "http://localhost:11434";
    private const string OpenRouterBaseUrl = "https://openrouter.ai/api/v1";

    // Env knobs (no recompile). Defaults run the cloud big-model proxy on three short clean Hebrew passages.
    private const string ProviderEnvVar = "LINEEDIT_SPOTCHECK_PROVIDER"; // default OpenRouter
    private const string ModelEnvVar = "LINEEDIT_SPOTCHECK_MODEL";       // default google/gemma-4-31b-it
    private const string CaseIdsEnvVar = "LINEEDIT_SPOTCHECK_CASE_IDS";  // default clean-he-01,clean-he-02,clean-he-03

    private const string DefaultProvider = "OpenRouter";
    private const string DefaultModel = "google/gemma-4-31b-it";
    // Short, clean literary Hebrew passages reused from linguistic-gold.json (narration, narration,
    // dialogue) — clean text is the sharpest overreach test for a line editor.
    private static readonly string[] DefaultCaseIds = { "clean-he-01", "clean-he-02", "clean-he-03" };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public async Task LineEditCloudSpotCheck_RunShortHebrewPassages_PrintForRubricReview()
    {
        var provider = Environment.GetEnvironmentVariable(ProviderEnvVar) is { Length: > 0 } p ? p.Trim() : DefaultProvider;
        var model = Environment.GetEnvironmentVariable(ModelEnvVar) is { Length: > 0 } m ? m.Trim() : DefaultModel;

        // Skip-gate by provider (CI stays green when the model is unreachable).
        if (provider.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AI_OPENROUTER_APIKEY")))
            {
                _output.WriteLine("SKIPPED: provider=OpenRouter but no API key (env AI_OPENROUTER_APIKEY). " +
                                  "This is a manual cloud spot-check; skipping so CI stays green.");
                return;
            }
        }
        else if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            if (!await IsOllamaReachableAsync())
            {
                _output.WriteLine($"SKIPPED: provider=Ollama but Ollama not reachable at {OllamaBaseUrl}. Skipping.");
                return;
            }
        }
        else
        {
            _output.WriteLine($"SKIPPED: unsupported {ProviderEnvVar}='{provider}'. Supported: OpenRouter (default) or Ollama.");
            return;
        }

        var wantedIds = Environment.GetEnvironmentVariable(CaseIdsEnvVar) is { Length: > 0 } raw
            ? raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : DefaultCaseIds;
        var passages = LoadPassages(wantedIds);
        if (passages.Length == 0)
        {
            _output.WriteLine("No matching passages found in linguistic-gold.json for ids: " + string.Join(", ", wantedIds));
            return;
        }

        var router = CreateRouter(provider, model);

        _output.WriteLine($"=== LineEdit cloud rubric spot-check ({passages.Length} passages, {provider}:{model}) ===");
        _output.WriteLine("Rubric per passage: overreach? / preserve-meaning? / valid-Hebrew? / respect-voice?");
        _output.WriteLine("(This Fact only PRINTS output for manual scoring — it does not grade or assert quality.)");
        _output.WriteLine("");

        foreach (var passage in passages)
        {
            var language = string.IsNullOrWhiteSpace(passage.Language) ? "he-IL" : passage.Language.Trim();
            // Build the core LineEdit instruction (PromptFactory LineEditHe) and send it as the request
            // Instruction. Routing is faithful to production: AiRouter sends it VERBATIM for AiTaskType.LineEdit
            // (ShouldUseUnifiedInstructionVerbatim returns true; no legacy pipeline-prompt concatenation).
            // Note: the 2-arg overload used here returns the base instruction WITHOUT production's injected
            // context sections (style profile / preceding-context / following-context); that is acceptable
            // for these clean, standalone spot-check passages that have no book context to inject.
            var instruction = _promptFactory.GetAnalysisPrompt(AnalysisType.LineEdit, language);

            _output.WriteLine($"--- {passage.Id} ({language}) ---");
            _output.WriteLine("INPUT:");
            _output.WriteLine(passage.Input);
            try
            {
                var request = new AiRequest
                {
                    InputText = passage.Input,
                    Instruction = instruction,
                    TaskType = AiTaskType.LineEdit,
                    Language = language,
                    SourceId = passage.Id,
                    JsonMode = true
                };

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var response = await router.CompleteAsync(request);
                sw.Stop();

                _output.WriteLine($"OUTPUT ({sw.ElapsedMilliseconds} ms" +
                                  (response.InputTokens is int it ? $", in {it} tok" : "") +
                                  (response.OutputTokens is int ot ? $", out {ot} tok" : "") + "):");
                _output.WriteLine(PrettyOrRaw(response.Content ?? string.Empty));
            }
            catch (Exception ex)
            {
                _output.WriteLine($"ERROR: {FirstLine(ex.Message)}");
            }
            _output.WriteLine("");
        }

        // Qualitative spot-check: assert only that it ran over at least one passage (no quality gate).
        Assert.True(passages.Length > 0);
    }

    /// <summary>Pretty-print the model JSON when parseable; otherwise echo the raw content verbatim.</summary>
    private static string PrettyOrRaw(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.Length == 0) return "(empty response)";
        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return trimmed; // model may wrap JSON in prose/fences — show it as-is for the reviewer
        }
    }

    private static string FirstLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var i = s.IndexOfAny(new[] { '\r', '\n' });
        return i >= 0 ? s[..i] : s;
    }

    // ─── Passage loading (reuse linguistic-gold.json) ───

    private sealed class GoldPassage
    {
        public string Id { get; set; } = "";
        public string Input { get; set; } = "";
        public string Language { get; set; } = "he-IL";
    }

    private static GoldPassage[] LoadPassages(string[] wantedIds)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "linguistic-gold.json");
        if (!File.Exists(path))
            return Array.Empty<GoldPassage>();
        var json = File.ReadAllText(path);
        var all = JsonSerializer.Deserialize<GoldPassage[]>(json, JsonOptions) ?? Array.Empty<GoldPassage>();
        // Preserve the requested id order (the _README metadata entry has no id and is filtered out).
        return wantedIds
            .Select(id => all.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)))
            .Where(c => c is { Id.Length: > 0 } && !string.IsNullOrWhiteSpace(c.Input))
            .Select(c => c!)
            .ToArray();
    }

    // ─── Ollama reachability probe (skip-gate) — mirrors the proofread/linguistic harnesses ───

    private static async Task<bool> IsOllamaReachableAsync()
    {
        if (await ProbeAsync(OllamaBaseUrl)) return true;
        if (!OllamaBaseUrl.Contains("127.0.0.1") &&
            await ProbeAsync(OllamaBaseUrl.Replace("localhost", "127.0.0.1")))
            return true;
        return false;
    }

    private static async Task<bool> ProbeAsync(string baseUrl)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var resp = await client.GetAsync($"{baseUrl}/api/tags", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ─── Router DI (mirrors ProofreadQualityTests.CreateRouter, wired for the LineEdit task) ───

    private static readonly PromptFactory _promptFactory = new();

    private static IAiRouter CreateRouter(string provider, string model)
    {
        // Route the LineEdit task to provider/model. DefaultModel == model so OllamaProvider's
        // 404-retry-with-default cannot silently substitute a different model. OpenRouter BaseUrl is wired
        // so OpenAiCompatibleProvider resolves its endpoint; its API key comes from env AI_OPENROUTER_APIKEY.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:DefaultProvider"] = provider,
                ["Ai:Providers:Ollama:BaseUrl"] = OllamaBaseUrl,
                ["Ai:Providers:OpenRouter:BaseUrl"] = OpenRouterBaseUrl,
                ["Ai:DefaultModel"] = model,
                ["Ai:FeatureModels:LineEdit:Provider"] = provider,
                ["Ai:FeatureModels:LineEdit:Model"] = model
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        // Generous timeouts: cloud big models and CPU-spilled local models can be slow on a single call.
        services.AddHttpClient("Ollama", client => client.Timeout = TimeSpan.FromMinutes(10));
        services.AddHttpClient(string.Empty, client => client.Timeout = TimeSpan.FromMinutes(10));
        services.Configure<AiOptions>(opts =>
        {
            opts.DefaultProvider = provider;
            opts.DefaultModel = model;
            opts.FeatureModels = new Dictionary<string, FeatureModelOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["LineEdit"] = new FeatureModelOptions { Provider = provider, Model = model }
            };
            // Match production OpenRouter tuning (appsettings Ai:ProviderSettings:OpenRouter MaxTokens=5120)
            // so the JSON line-edit output is not truncated by the provider's bare 2048 fallback.
            opts.ProviderSettings = new Dictionary<string, ProviderTuningOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["OpenRouter"] = new ProviderTuningOptions { Temperature = 0.2, MaxTokens = 5120 }
            };
        });
        services.AddSingleton<PromptFactory>();
        services.AddSingleton<IReadOnlyDictionary<string, IAiAnalysisProvider>>(sp =>
        {
            var c = sp.GetRequiredService<IConfiguration>();
            var opts = sp.GetRequiredService<IOptions<AiOptions>>();
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new Dictionary<string, IAiAnalysisProvider>(StringComparer.OrdinalIgnoreCase)
            {
                ["Ollama"] = new OllamaProvider(factory, c, opts),
                ["OpenRouter"] = new OpenAiCompatibleProvider("OpenRouter", factory, c, opts)
            };
        });
        services.AddSingleton<IAiRouter, AiRouter>();

        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IAiRouter>();
    }
}
