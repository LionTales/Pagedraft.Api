using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;
using Xunit.Abstractions;

namespace Pagedraft.Api.Tests.LanguageEngine;

/// <summary>
/// SCRATCH / INVESTIGATION harness for decomp-c03-multipass-investigation.
///
/// NOT a shipped feature and NOT part of the gold gate. It exists to answer ONE question the
/// saturated 17-case gold set cannot: does single-pass linguistic-consistency recall (gemma4:12b,
/// the shipped c02 annotate+few-shot prompt) DEGRADE when a single planted defect is buried DEEP
/// inside a LONG multi-scene chapter, and if so, does a decomposition variant recover it (at what
/// latency)? It runs the LONG probes in TestData/linguistic-long-probes.json under:
///   (1) SINGLE-PASS      — the production path (shipped prompt over the whole chapter). Baseline.
///   (2) PER-WINDOW        — decomposition (b): paragraph windows with read-only overlap context,
///                           merge+dedupe consistencyIssues across windows.
///   (3) PER-TYPE          — decomposition (a): one focused pass per type (pov/tense/register),
///                           union+dedupe.
///   (4) SELF-CONSISTENCY  — decomposition (c): temp 0.7, N samples of the single-pass prompt,
///                           union, keep issues that recur in >= 2 samples.
/// For each probe x variant it records: did it CATCH the planted defect (recall) with the EXPECTED
/// type, false positives on the long-clean controls, and WALL-CLOCK latency. The orchestrators are
/// PROTOTYPES (own throwaway code in this file) and deliberately do NOT touch the production
/// proofread/line-edit chunked paths or AiRouter aggregation — productionizing is decomp-f02.
///
/// SKIP-BY-DEFAULT and INFORMATIONAL, exactly like LinguisticQualityTests: probes Ollama and returns
/// (passes) if unreachable; never fails CI on model quality. Run explicitly with:
///   dotnet test --filter "FullyQualifiedName~LinguisticDecompProbe" -l "console;verbosity=detailed"
/// Select variants via env DECOMP_VARIANTS (comma list of: single,window,type,selfconsistency;
/// default "single,window"). Self-consistency sample count via env DECOMP_SC_SAMPLES (default 3).
/// </summary>
public class LinguisticDecompProbeTests
{
    private readonly ITestOutputHelper _output;
    public LinguisticDecompProbeTests(ITestOutputHelper output) => _output = output;

    private const string OllamaBaseUrl = "http://localhost:11434";
    private const string Model = "gemma4:12b";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly PromptFactory _promptFactory = new();

    [Fact]
    [Trait("Category", "LiveModel")]
    public async Task LinguisticDecompProbe_LongInputs_SinglePassVsDecomposition()
    {
        if (!await IsOllamaReachableAsync())
        {
            _output.WriteLine($"SKIPPED: Ollama not reachable at {OllamaBaseUrl}. Long-probe investigation needs a live model; skipping so CI stays green.");
            return;
        }

        var probes = LoadProbes();
        if (probes.Length == 0)
        {
            _output.WriteLine("No long probes in linguistic-long-probes.json.");
            return;
        }

        var variants = ResolveVariants();
        var scSamples = ResolveScSamples();
        _output.WriteLine($"=== decomp-c03 long-input probe run ===");
        _output.WriteLine($"Model: {Model}. Probes: {probes.Length}. Variants: {string.Join(",", variants)}. SC samples: {scSamples}.");
        _output.WriteLine("");

        // One router at the production temp (0.2) for single/window/type; a hotter router (0.7) for self-consistency.
        var router = CreateRouter(temperature: 0.2);
        var hotRouter = CreateRouter(temperature: 0.7);

        var rows = new List<ResultRow>();

        foreach (var variant in variants)
        {
            foreach (var p in probes)
            {
                var sw = Stopwatch.StartNew();
                List<ConsistencyIssue> issues;
                int passes;
                try
                {
                    (issues, passes) = variant switch
                    {
                        "single"          => (await SinglePassAsync(router, p), 1),
                        "window"          => await PerWindowAsync(router, p),
                        "type"            => await PerTypeAsync(router, p),
                        "selfconsistency" => await SelfConsistencyAsync(hotRouter, p, scSamples),
                        _ => (new List<ConsistencyIssue>(), 0)
                    };
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    _output.WriteLine($"[{variant}] {p.Id}: ERROR {FirstLine(ex.Message)}");
                    rows.Add(new ResultRow(variant, p.Id, p.ExpectClean, false, false, 0, 0, sw.Elapsed, true));
                    continue;
                }
                sw.Stop();

                var caught = issues.Count > 0;
                var expected = p.ExpectedConsistencyTypes ?? Array.Empty<string>();
                var typeHit = issues.Any(i => expected.Any(t =>
                    string.Equals(t?.Trim(), i.Type?.Trim(), StringComparison.OrdinalIgnoreCase)));

                rows.Add(new ResultRow(variant, p.Id, p.ExpectClean,
                    Recall: !p.ExpectClean && caught,
                    TypeHit: !p.ExpectClean && typeHit,
                    IssueCount: issues.Count, Passes: passes, Elapsed: sw.Elapsed, Error: false));

                var typesStr = issues.Count > 0
                    ? string.Join(",", issues.Select(i => i.Type).Distinct(StringComparer.OrdinalIgnoreCase))
                    : "(none)";
                var verdict = p.ExpectClean
                    ? (issues.Count == 0 ? "clean OK" : $"FALSE POSITIVE x{issues.Count} [{typesStr}]")
                    : (caught ? (typeHit ? "CAUGHT" : "caught WRONG-TYPE") + $" [{typesStr}]" : "MISSED");
                _output.WriteLine($"[{variant,-15}] {p.Id,-22} passes={passes,2} {sw.Elapsed.TotalSeconds,7:F1}s issues={issues.Count} {verdict}");
            }
            _output.WriteLine("");
        }

        // ── Summary table: per variant, recall on planted probes, FP on clean, total wall-clock ──
        _output.WriteLine("=== Summary (per variant) ===");
        _output.WriteLine($"{"variant",-16} {"plantedRecall",14} {"typeAcc",8} {"cleanFP",8} {"avgLatency",11} {"totalWall",10}");
        foreach (var variant in variants)
        {
            var vr = rows.Where(r => r.Variant == variant && !r.Error).ToList();
            var planted = vr.Where(r => !r.ExpectClean).ToList();
            var clean = vr.Where(r => r.ExpectClean).ToList();
            var recallHits = planted.Count(r => r.Recall);
            var typeHits = planted.Count(r => r.TypeHit);
            var cleanFp = clean.Sum(r => r.IssueCount);
            var avgLatency = vr.Count > 0 ? TimeSpan.FromTicks((long)vr.Average(r => r.Elapsed.Ticks)) : TimeSpan.Zero;
            var totalWall = TimeSpan.FromTicks(vr.Sum(r => r.Elapsed.Ticks));
            _output.WriteLine($"{variant,-16} {$"{recallHits}/{planted.Count}",14} {$"{typeHits}/{planted.Count}",8} {cleanFp,8} {avgLatency.TotalSeconds,10:F1}s {totalWall.TotalSeconds,9:F1}s");
        }

        Assert.True(probes.Length > 0);
    }

    // ── Variant 1: SINGLE PASS (production path) ──────────────────────────────────────────────
    private static async Task<List<ConsistencyIssue>> SinglePassAsync(IAiRouter router, Probe p)
    {
        var instruction = _promptFactory.GetAnalysisPrompt(AnalysisType.LinguisticAnalysis, p.Language);
        var result = await CallAsync(router, instruction, p.Input, p.Language, p.Id);
        return result?.ConsistencyIssues ?? new List<ConsistencyIssue>();
    }

    // ── Variant 2: PER-WINDOW (decomposition b) ───────────────────────────────────────────────
    // Split into windows of WindowParas paragraphs with a 1-paragraph read-only overlap before/after,
    // injected as PrecedingContext/FollowingContext. Per window the in-unit anchoring rule applies:
    // the model is told to quote spans only from the analyzed (window) text. Merge + dedupe across
    // windows by (normalized span, type). Spans that do not occur verbatim in their own window are
    // dropped (mirrors SuggestionDiffService.ComputeConsistencyIssueSuggestions in spirit).
    private const int WindowParas = 3;
    private static async Task<(List<ConsistencyIssue>, int)> PerWindowAsync(IAiRouter router, Probe p)
    {
        var paras = SplitParagraphs(p.Input);
        var windows = BuildWindows(paras, WindowParas);
        var merged = new List<ConsistencyIssue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int passes = 0;
        foreach (var w in windows)
        {
            passes++;
            var ctx = new AnalysisContext
            {
                TargetText = w.Text,
                PrecedingContext = string.IsNullOrWhiteSpace(w.Before) ? null : w.Before,
                FollowingContext = string.IsNullOrWhiteSpace(w.After) ? null : w.After,
                AnalysisType = AnalysisType.LinguisticAnalysis
            };
            var instruction = _promptFactory.GetAnalysisPrompt(AnalysisType.LinguisticAnalysis, p.Language, ctx);
            var result = await CallAsync(router, instruction, w.Text, p.Language, $"{p.Id}#w{passes}");
            foreach (var issue in result?.ConsistencyIssues ?? new List<ConsistencyIssue>())
            {
                // Anchoring guard: the span must occur verbatim in THIS window's analyzed text.
                if (string.IsNullOrWhiteSpace(issue.Span)) continue;
                if (!ContainsNormalized(w.Text, issue.Span)) continue;
                var key = $"{issue.Type?.Trim().ToLowerInvariant()}|{Normalize(issue.Span)}";
                if (seen.Add(key)) merged.Add(issue);
            }
        }
        return (merged, passes);
    }

    // ── Variant 3: PER-TYPE (decomposition a) ─────────────────────────────────────────────────
    // One focused pass per consistency type. We reuse the shipped prompt but prepend a focusing
    // directive so the model attends to a single dimension; union + dedupe by (span, type).
    private static readonly string[] FocusTypes = { "pov", "tense", "register" };
    private static async Task<(List<ConsistencyIssue>, int)> PerTypeAsync(IAiRouter router, Probe p)
    {
        var merged = new List<ConsistencyIssue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int passes = 0;
        var basePrompt = _promptFactory.GetAnalysisPrompt(AnalysisType.LinguisticAnalysis, p.Language);
        foreach (var focus in FocusTypes)
        {
            passes++;
            var focusDirective = FocusDirective(p.Language, focus);
            var instruction = focusDirective + "\n\n" + basePrompt;
            var result = await CallAsync(router, instruction, p.Input, p.Language, $"{p.Id}#{focus}");
            foreach (var issue in result?.ConsistencyIssues ?? new List<ConsistencyIssue>())
            {
                if (string.IsNullOrWhiteSpace(issue.Span)) continue;
                if (!ContainsNormalized(p.Input, issue.Span)) continue;
                var key = $"{issue.Type?.Trim().ToLowerInvariant()}|{Normalize(issue.Span)}";
                if (seen.Add(key)) merged.Add(issue);
            }
        }
        return (merged, passes);
    }

    private static string FocusDirective(string language, string focus)
    {
        var isHe = language.StartsWith("he", StringComparison.OrdinalIgnoreCase);
        var label = focus switch
        {
            "pov" => isHe ? "נקודת-מבט (POV / קפיצה בין ראשים)" : "point of view (POV / head-hopping)",
            "tense" => isHe ? "זמן דקדוקי (מעבר עבר<->הווה)" : "narrative tense (past<->present shifts)",
            _ => isHe ? "רישום/טון (register)" : "register / tone",
        };
        return isHe
            ? $"מיקוד למעבר זה: התמקד אך ורק בזיהוי בעיות מסוג {label}. סרוק את כל הטקסט מתחילתו ועד סופו וחפש שינוי מסוג זה בין חלקים. דווח ב-consistencyIssues אך ורק בעיות מסוג \"{focus}\". אם אין שינוי כזה - החזר consistencyIssues ריק."
            : $"FOCUS FOR THIS PASS: detect ONLY {label} issues. Scan the entire text start to finish for a shift of this single kind between parts. In consistencyIssues report ONLY issues of type \"{focus}\". If there is no such shift, return an empty consistencyIssues array.";
    }

    // ── Variant 4: SELF-CONSISTENCY (decomposition c) ─────────────────────────────────────────
    // Sample the single-pass prompt N times at higher temperature, union, keep issues whose
    // (type, normalized span-stem) recurs in >= 2 samples (cheap agreement filter).
    private static async Task<(List<ConsistencyIssue>, int)> SelfConsistencyAsync(IAiRouter hotRouter, Probe p, int samples)
    {
        var instruction = _promptFactory.GetAnalysisPrompt(AnalysisType.LinguisticAnalysis, p.Language);
        var tally = new Dictionary<string, (int count, ConsistencyIssue issue)>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < samples; i++)
        {
            var result = await CallAsync(hotRouter, instruction, p.Input, p.Language, $"{p.Id}#sc{i}");
            // Within a single sample, count each (type, span-stem) once.
            var perSample = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var issue in result?.ConsistencyIssues ?? new List<ConsistencyIssue>())
            {
                if (string.IsNullOrWhiteSpace(issue.Span)) continue;
                if (!ContainsNormalized(p.Input, issue.Span)) continue;
                var key = $"{issue.Type?.Trim().ToLowerInvariant()}|{SpanStem(issue.Span)}";
                if (!perSample.Add(key)) continue;
                if (tally.TryGetValue(key, out var cur)) tally[key] = (cur.count + 1, cur.issue);
                else tally[key] = (1, issue);
            }
        }
        var kept = tally.Values.Where(v => v.count >= 2).Select(v => v.issue).ToList();
        return (kept, samples);
    }

    // ── Shared call: build the AiRequest exactly as production does, parse to ConsistencyIssues ──
    private static async Task<LinguisticAnalysisResult?> CallAsync(
        IAiRouter router, string instruction, string input, string language, string sourceId)
    {
        var request = new AiRequest
        {
            InputText = input,
            Instruction = instruction,
            TaskType = AiTaskType.LinguisticAnalysis,
            Language = language,
            SourceId = sourceId,
            JsonMode = true
        };
        var response = await router.CompleteAsync(request);
        return ParseLinguistic(response.Content ?? string.Empty);
    }

    // ── Paragraph / window helpers ────────────────────────────────────────────────────────────
    private static string[] SplitParagraphs(string text) =>
        text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();

    private readonly record struct Window(string Text, string? Before, string? After);

    private static List<Window> BuildWindows(string[] paras, int size)
    {
        var windows = new List<Window>();
        for (int start = 0; start < paras.Length; start += size)
        {
            var slice = paras.Skip(start).Take(size).ToArray();
            var before = start > 0 ? paras[start - 1] : null;            // 1-para read-only overlap
            var afterIdx = start + size;
            var after = afterIdx < paras.Length ? paras[afterIdx] : null; // 1-para read-only overlap
            windows.Add(new Window(string.Join("\n\n", slice), before, after));
        }
        return windows;
    }

    // ── Normalized substring helpers (mirror the anchoring-rule spirit) ───────────────────────
    private static string Normalize(string s) =>
        new string((s ?? string.Empty).Where(ch => !char.IsWhiteSpace(ch)).ToArray());

    private static bool ContainsNormalized(string haystack, string needle)
    {
        var h = Normalize(haystack);
        var n = Normalize(needle);
        return n.Length > 0 && h.IndexOf(n, StringComparison.Ordinal) >= 0;
    }

    // First ~6 normalized words of a span — a coarse stem so paraphrase-equivalent spans across
    // samples tally together for the self-consistency agreement filter.
    private static string SpanStem(string span)
    {
        var words = (span ?? string.Empty)
            .Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Take(6);
        return string.Concat(words).ToLowerInvariant();
    }

    // ── Parse (same extractor as production / the gold harness) ───────────────────────────────
    private static LinguisticAnalysisResult? ParseLinguistic(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var json = UnifiedAnalysisService.ExtractJson(content);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<LinguisticAnalysisResult>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    // ── Probe loading ─────────────────────────────────────────────────────────────────────────
    private static Probe[] LoadProbes()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "linguistic-long-probes.json");
        if (!File.Exists(path)) return Array.Empty<Probe>();
        var raw = JsonSerializer.Deserialize<Probe[]>(File.ReadAllText(path), JsonOptions);
        return raw?.Where(c => !string.IsNullOrWhiteSpace(c.Id)).ToArray() ?? Array.Empty<Probe>();
    }

    private sealed class Probe
    {
        public string Id { get; set; } = "";
        public string Input { get; set; } = "";
        public string Language { get; set; } = "he-IL";
        public bool ExpectClean { get; set; }
        public string[]? ExpectedConsistencyTypes { get; set; }
        public int MaxConsistencyIssues { get; set; }
        public int DefectAtWord { get; set; }
        public int TotalWords { get; set; }
        public string? Notes { get; set; }
    }

    private readonly record struct ResultRow(
        string Variant, string Id, bool ExpectClean,
        bool Recall, bool TypeHit, int IssueCount, int Passes, TimeSpan Elapsed, bool Error);

    private static string[] ResolveVariants()
    {
        var raw = Environment.GetEnvironmentVariable("DECOMP_VARIANTS");
        if (string.IsNullOrWhiteSpace(raw)) return new[] { "single", "window" };
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Select(s => s.ToLowerInvariant()).Distinct().ToArray();
    }

    private static int ResolveScSamples()
    {
        var raw = Environment.GetEnvironmentVariable("DECOMP_SC_SAMPLES");
        return int.TryParse(raw, out var v) && v > 0 ? v : 3;
    }

    private static string FirstLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var i = s.IndexOfAny(new[] { '\r', '\n' });
        return i >= 0 ? s[..i] : s;
    }

    // ── Ollama reachability probe (skip-gate) — mirrors LinguisticQualityTests ───
    private static async Task<bool> IsOllamaReachableAsync()
    {
        if (await ProbeAsync(OllamaBaseUrl)) return true;
        if (!OllamaBaseUrl.Contains("127.0.0.1") &&
            await ProbeAsync(OllamaBaseUrl.Replace("localhost", "127.0.0.1"))) return true;
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

    // ── Router DI (mirrors LinguisticQualityTests.CreateRouter; production params + chosen temp) ──
    private static IAiRouter CreateRouter(double temperature)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:DefaultProvider"] = "Ollama",
                ["Ai:Providers:Ollama:BaseUrl"] = OllamaBaseUrl,
                ["Ai:DefaultModel"] = Model,
                ["Ai:FeatureModels:LinguisticAnalysis:Provider"] = "Ollama",
                ["Ai:FeatureModels:LinguisticAnalysis:Model"] = Model
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddHttpClient("Ollama", client => client.Timeout = TimeSpan.FromMinutes(10));
        services.AddHttpClient(string.Empty, client => client.Timeout = TimeSpan.FromMinutes(10));

        services.Configure<AiOptions>(opts =>
        {
            opts.DefaultProvider = "Ollama";
            opts.DefaultModel = Model;
            opts.FeatureModels = new Dictionary<string, FeatureModelOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["LinguisticAnalysis"] = new FeatureModelOptions { Provider = "Ollama", Model = Model }
            };
            // Production appsettings tuning, with the chosen temperature for this router.
            opts.ProviderSettings = new Dictionary<string, ProviderTuningOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["Ollama_LinguisticAnalysis"] = new ProviderTuningOptions
                {
                    Temperature = temperature,
                    NumPredict = 5120,
                    NumCtx = 16384,
                    RepeatPenalty = 1.2
                }
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
                ["Ollama"] = new OllamaProvider(factory, c, opts)
            };
        });
        services.AddSingleton<IAiRouter, AiRouter>();
        return services.BuildServiceProvider().GetRequiredService<IAiRouter>();
    }
}
