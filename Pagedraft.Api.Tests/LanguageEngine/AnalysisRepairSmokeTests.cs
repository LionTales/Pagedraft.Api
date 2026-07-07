using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;
using Xunit.Abstractions;

namespace Pagedraft.Api.Tests.LanguageEngine;

/// <summary>
/// SMOKE (skip-gated, GPU) for the value-scoped analysis-output repair service (analysis-output-repair
/// plan, p3-repair-service). Confirms two things end-to-end against the REAL production routing:
///   (1) an AiTaskType.AnalysisRepair request routes to gemma4:12b (the "routes to itself" FeatureModels
///       key added in appsettings.json), and
///   (2) AnalysisRepairService.RepairFieldAsync on the scrambled Hebrew+English probe is ACCEPTED (not
///       fail-safe-discarded) and reduces the Latin-run count while staying Hebrew.
///
/// SKIP-BY-DEFAULT: probes the local Ollama endpoint first and passes (returns) if it is unreachable, so
/// CI stays green. When it runs it makes two gemma4:12b calls (~30-60s total). Full RepairQuality scoring
/// is p3-gate; this is only a wiring/behaviour smoke.
/// </summary>
public class AnalysisRepairSmokeTests
{
    private readonly ITestOutputHelper _output;

    public AnalysisRepairSmokeTests(ITestOutputHelper output) => _output = output;

    private const string OllamaBaseUrl = "http://localhost:11434";

    // The controlled scrambled probe from OutputQualityDiagnostic Part 2: 8 leaked English literary terms
    // embedded in Hebrew prose (narrator, tense, suspense, foreshadowing, imagery, mood, tension, climax).
    private const string ScrambledProbe =
        "הקטע כתוב בגוף ראשון, וה-narrator הוא הדמות הראשית. הטון הוא tense ומלא suspense. " +
        "יש כאן foreshadowing חזק, והסופר משתמש ב-imagery של טבע כדי לבנות את ה-mood. " +
        "הקצב מהיר וה-tension עולה בהדרגה עד ל-climax של הקרב.";

    [Fact]
    public async Task RepairField_OnScrambledProbe_RoutesToGemma_AndReducesLatin_StaysHebrew()
    {
        if (!await IsOllamaReachableAsync())
        {
            _output.WriteLine("SKIP: Ollama not reachable at " + OllamaBaseUrl);
            return;
        }

        // Load PRODUCTION appsettings.json so the NEW FeatureModels:AnalysisRepair key drives routing.
        var appSettingsPath = FindUpward(Path.Combine("Pagedraft.Api", "appsettings.json"));
        Assert.True(File.Exists(appSettingsPath), "appsettings.json not found at " + appSettingsPath);
        var config = new ConfigurationBuilder().AddJsonFile(appSettingsPath).Build();
        var router = BuildRouter(config);

        var before = LatinInHebrewContentDetector.DetectLatinRuns(ScrambledProbe);
        _output.WriteLine($"probe latin runs (before): {before.Count} -> {string.Join(", ", before)}");

        // (1) Routing confirmation: an AnalysisRepair request resolves gemma4:12b (routes to itself).
        var routingResponse = await router.CompleteAsync(new AiRequest
        {
            InputText = ScrambledProbe,
            Instruction = "החזר את הטקסט בעברית תקינה בלבד.",
            TaskType = AiTaskType.AnalysisRepair,
            Language = "he-IL",
            SourceId = "repair-smoke",
            JsonMode = false
        });
        _output.WriteLine($"routing: taskKey=AnalysisRepair -> {routingResponse.Provider}:{routingResponse.Model}");
        Assert.Contains("gemma", routingResponse.Model, StringComparison.OrdinalIgnoreCase);

        // (2) The service itself: fail-safe repair must be ACCEPTED here (Latin reduced) and stay Hebrew.
        var service = new AnalysisRepairService(router, NullLogger<AnalysisRepairService>.Instance);
        var repaired = await service.RepairFieldAsync(ScrambledProbe, "he-IL");

        var after = LatinInHebrewContentDetector.DetectLatinRuns(repaired);
        _output.WriteLine($"repaired: {repaired}");
        _output.WriteLine($"probe latin runs (after): {after.Count} -> {string.Join(", ", after)}");

        // Repair was accepted (not the fail-safe original), so the Latin-run count strictly dropped.
        Assert.True(after.Count < before.Count,
            $"expected fewer Latin runs after repair (before={before.Count}, after={after.Count}); " +
            $"equal count means the repair was fail-safe-discarded. repaired='{repaired}'");

        // Still Hebrew: contains Hebrew letters and is not an English rewrite.
        Assert.Matches("[֐-׿]", repaired);
        Assert.NotEqual(ScrambledProbe, repaired);
    }

    // ─── Router DI (mirrors OutputQualityDiagnostic.BuildRouter) ───
    private static IAiRouter BuildRouter(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddHttpClient("Ollama", c => c.Timeout = TimeSpan.FromMinutes(10));
        services.AddHttpClient(string.Empty, c => c.Timeout = TimeSpan.FromMinutes(10));
        services.Configure<AiOptions>(config.GetSection("Ai"));
        services.AddSingleton<PromptFactory>();
        services.AddSingleton<IReadOnlyDictionary<string, IAiAnalysisProvider>>(sp =>
        {
            var c = sp.GetRequiredService<IConfiguration>();
            var opts = sp.GetRequiredService<IOptions<AiOptions>>();
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new Dictionary<string, IAiAnalysisProvider>(StringComparer.OrdinalIgnoreCase)
            {
                ["Ollama"] = new OllamaProvider(factory, c, opts),
                ["Anthropic"] = new AnthropicProvider(factory, c, opts),
                ["OpenAI"] = new OpenAiProvider(factory, c, opts),
                ["OpenRouter"] = new OpenAiCompatibleProvider("OpenRouter", factory, c, opts)
            };
        });
        services.AddSingleton<IAiRouter, AiRouter>();
        return services.BuildServiceProvider().GetRequiredService<IAiRouter>();
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

    private static async Task<bool> IsOllamaReachableAsync()
    {
        if (await ProbeAsync(OllamaBaseUrl)) return true;
        if (!OllamaBaseUrl.Contains("127.0.0.1") && await ProbeAsync(OllamaBaseUrl.Replace("localhost", "127.0.0.1")))
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
        catch { return false; }
    }
}
