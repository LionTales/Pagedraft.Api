using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.LanguageEngine;
using Pagedraft.Api.Services.LanguageEngine.Contracts;
using LanguageEngineImpl = Pagedraft.Api.Services.LanguageEngine.LanguageEngine;
using Pagedraft.Api.Services.LanguageEngine.Detect;
using Pagedraft.Api.Services.LanguageEngine.Metrics;
using Pagedraft.Api.Services.LanguageEngine.Normalize;
using Pagedraft.Api.Services.LanguageEngine.Rewrite;
using Pagedraft.Api.Services.LanguageEngine.Analyze;
using Xunit;
using Xunit.Abstractions;

namespace Pagedraft.Api.Tests.LanguageEngine;

public class HebrewRegressionTests
{
    private readonly ITestOutputHelper _output;

    public HebrewRegressionTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void LoadHebrewRegressionJson_ExistsAndDeserializes()
    {
        var path = GetTestDataPath();
        Assert.True(File.Exists(path), $"Test data file not found: {path}");
        var json = File.ReadAllText(path);
        var cases = JsonSerializer.Deserialize<HebrewRegressionCase[]>(json, JsonOptions);
        Assert.NotNull(cases);
        Assert.NotEmpty(cases);
    }

    [Fact]
    public async Task NormalizeStage_MatchesExpected_ForCasesWithExpectedNormalized()
    {
        var cases = LoadCases();
        var engine = CreateNormalizeEngine();

        foreach (var c in cases)
        {
            if (string.IsNullOrEmpty(c.ExpectedNormalized)) continue;

            var normalized = await engine.NormalizeAsync(c.Input, c.Language);
            Assert.True(
                normalized == c.ExpectedNormalized,
                $"Case {c.Id}: Expected normalized \"{c.ExpectedNormalized}\", got \"{normalized}\".");
        }
    }

    [Fact]
    public async Task FullPipeline_NormalizeAndDetect_ReturnsResult_ForAllCases()
    {
        var cases = LoadCases();
        var languageEngine = CreateLanguageEngine();

        foreach (var c in cases)
        {
            var request = new LanguageEngineRequest
            {
                InputText = c.Input,
                Language = c.Language,
                Options = new LanguageEngineOptions
                {
                    EnableNormalize = true,
                    EnableDetect = true,
                    EnableRewrite = false,
                    EnableAnalyze = false
                }
            };

            LanguageEngineResult result;
            try
            {
                result = await languageEngine.ProcessAsync(request);
            }
            catch (System.Exception ex)
            {
                Assert.Fail($"Case {c.Id}: Pipeline threw: {ex.Message}");
                return;
            }

            Assert.NotNull(result);
            if (c.ExpectedNormalized != null)
                Assert.Equal(c.ExpectedNormalized, result.NormalizedText);

            if (c.ExpectedIssueCategories != null && c.ExpectedIssueCategories.Length >= 0)
            {
                var languageToolUnavailable = result.Metadata.TryGetValue("languageToolUnavailable", out var unav) && unav is true;
                if (!languageToolUnavailable)
                {
                    var categories = result.Issues.Select(i => i.Category).Distinct().ToHashSet();
                    foreach (var expectedCat in c.ExpectedIssueCategories)
                    {
                        if (!string.IsNullOrEmpty(expectedCat))
                            Assert.True(categories.Contains(expectedCat), $"Case {c.Id}: Expected issue category '{expectedCat}' in {string.Join(", ", categories)}");
                    }
                }
            }
        }
    }

    /// <summary>Optional benchmark: run regression cases and report latency. Run with dotnet test --filter "BenchmarkRegression".</summary>
    [Fact(Skip = "Optional benchmark - run manually when comparing models or measuring latency")]
    public async Task BenchmarkRegression_RunAllCases_ReportLatency()
    {
        var cases = LoadCases();
        if (cases.Length == 0)
        {
            _output.WriteLine("No test cases in hebrew-regression.json.");
            return;
        }

        var languageEngine = CreateLanguageEngine();
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        var normalizeOnlySw = System.Diagnostics.Stopwatch.StartNew();
        var normalizeEngine = CreateNormalizeEngine();

        foreach (var c in cases)
        {
            _ = await normalizeEngine.NormalizeAsync(c.Input, c.Language);
        }
        normalizeOnlySw.Stop();

        foreach (var c in cases)
        {
            var request = new LanguageEngineRequest
            {
                InputText = c.Input,
                Language = c.Language,
                Options = new LanguageEngineOptions
                {
                    EnableNormalize = true,
                    EnableDetect = true,
                    EnableRewrite = false,
                    EnableAnalyze = false
                }
            };
            _ = await languageEngine.ProcessAsync(request);
        }
        totalSw.Stop();

        _output.WriteLine($"Hebrew regression benchmark: {cases.Length} cases");
        _output.WriteLine($"  Normalize-only total: {normalizeOnlySw.ElapsedMilliseconds} ms ({normalizeOnlySw.ElapsedMilliseconds * 1.0 / cases.Length:F0} ms/case)");
        _output.WriteLine($"  Full pipeline (normalize+detect) total: {totalSw.ElapsedMilliseconds} ms ({totalSw.ElapsedMilliseconds * 1.0 / cases.Length:F0} ms/case)");
    }

    private static HebrewRegressionCase[] LoadCases()
    {
        var path = GetTestDataPath();
        if (!File.Exists(path))
            return Array.Empty<HebrewRegressionCase>();
        var json = File.ReadAllText(path);
        var cases = JsonSerializer.Deserialize<HebrewRegressionCase[]>(json, JsonOptions);
        return cases ?? Array.Empty<HebrewRegressionCase>();
    }

    private static string GetTestDataPath()
    {
        var baseDir = AppContext.BaseDirectory;
        return Path.Combine(baseDir, "TestData", "hebrew-regression.json");
    }

    private static INormalizeEngine CreateNormalizeEngine()
    {
        return new HebrewNormalizeEngine();
    }

    private static ILanguageEngine CreateLanguageEngine()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:DefaultProvider"] = "Ollama",
                ["Ai:Providers:Ollama:BaseUrl"] = "http://localhost:11434",
                ["LanguageEngine:LanguageTool:ServerUrl"] = "http://localhost:8081"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddHttpClient("LanguageTool", client =>
        {
            client.BaseAddress = new Uri("http://localhost:8081");
            client.Timeout = TimeSpan.FromSeconds(5);
        });
        services.AddHttpClient("Ollama", client => client.Timeout = TimeSpan.FromMinutes(2));
        services.Configure<LanguageToolOptions>(opts =>
        {
            opts.Enabled = true;
            opts.ServerUrl = "http://localhost:8081";
        });
        services.Configure<AiOptions>(opts =>
        {
            opts.DefaultProvider = "Ollama";
            opts.DefaultModel = "qwen2.5:14b";
        });
        services.AddSingleton<ILanguageEngineMetrics, LoggingLanguageEngineMetrics>();
        services.AddSingleton<INormalizeEngine, HebrewNormalizeEngine>();
        services.AddSingleton<IDetectEngine, LanguageToolEngine>();
        services.AddSingleton<IRewriteEngine, LlmRewriteEngine>();
        services.AddSingleton<IAnalyzeEngine, LinguisticAnalysisEngine>();
        services.AddSingleton<ILanguageEngine, LanguageEngineImpl>();
        services.AddSingleton<PromptFactory>();
        services.AddSingleton<IReadOnlyDictionary<string, IAiAnalysisProvider>>(sp =>
        {
            var c = sp.GetRequiredService<IConfiguration>();
            var opts = sp.GetRequiredService<IOptions<AiOptions>>();
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var dict = new Dictionary<string, IAiAnalysisProvider>(StringComparer.OrdinalIgnoreCase)
            {
                ["Ollama"] = new OllamaProvider(factory, c, opts)
            };
            return dict;
        });
        services.AddSingleton<IAiRouter, AiRouter>();

        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<ILanguageEngine>();
    }
}
