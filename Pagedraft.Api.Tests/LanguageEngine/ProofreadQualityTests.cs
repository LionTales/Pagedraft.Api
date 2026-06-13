using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
/// Runs the REAL proofread path over each gold case in proofread-gold.json and reports
/// correction precision, recall, and false-positive rate.
///
/// PATH CHOICE — PromptFactory + IAiRouter (NOT UnifiedAnalysisService):
/// UnifiedAnalysisService.RunAsync cannot be cleanly constructed in a unit test — it requires
/// an AppDbContext, SfdtConversionService, AnalysisProgressTracker and IAnalysisContextService,
/// and it resolves its input text from a persisted Book/Chapter/Scene target rather than from a
/// raw string. So this test drives the same underlying machinery directly:
///   1. Call IAiRouter.CompleteAsync with an AiRequest { TaskType = AiTaskType.Proofread } and NO caller
///      Instruction. The router itself resolves the proofread system message + instruction via
///      PromptFactory.GetPrompt(AiTaskType.Proofread, lang) — the exact prompt UnifiedAnalysisService
///      sends for non-chunked proofread — using the same router/provider call UnifiedAnalysisService
///      makes. Leaving the caller Instruction empty makes the router resolve that prompt EXACTLY ONCE
///      (matching production); for Proofread, passing a PromptFactory-built Instruction would make the
///      router append the proofread prompt to itself. See AiRouter.CompleteAsync.
///   3. Extract corrections from the model's corrected text using the PRODUCTION
///      SuggestionDiffService.ComputeProofreadSuggestions(input, correctedText) — the same diff
///      UnifiedAnalysisService.AttachSuggestions uses to turn proofread output into corrections.
/// This keeps every step (prompt, LLM call, correction extraction) on production code; only the
/// DB/persistence wrapper is bypassed.
///
/// GATING — this test needs a live Ollama. It is SKIP-BY-DEFAULT: it probes the Ollama endpoint
/// first and, if unreachable, writes a message and returns (passes, does not fail). This mirrors
/// HebrewRegressionTests.BenchmarkRegression being a manual/optional run and ensures CI stays green
/// when Ollama is absent.
/// </summary>
public class ProofreadQualityTests
{
    private readonly ITestOutputHelper _output;

    public ProofreadQualityTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private const string OllamaBaseUrl = "http://localhost:11434";
    // Single-model scorer uses the production default proofread model (Ai:FeatureModels:Proofread),
    // which is GPU-resident on the dev box. Heavier models (qwen2.5:14b, DictaLM-12B) spill to CPU.
    //
    // KEEP IN SYNC: this value must match Ai:FeatureModels:Proofread:Model in
    // Pagedraft.Api/appsettings.json (see "_comment_ProofreadModel" key there).
    // If appsettings changes, update this constant too — they are NOT read from the same source at test time.
    private const string ProofreadModel = "qwen3.5:9b";

    // Env var (comma-separated) to override the bake-off model list WITHOUT recompiling.
    private const string BakeoffModelsEnvVar = "PROOFREAD_BAKEOFF_MODELS";

    // Default bake-off shortlist: models actually pulled on the RTX 4070 laptop (~8 GB VRAM).
    // qwen3.5:9b is the production default (Ai:FeatureModels:Proofread in appsettings).
    // DictaLM tag is :latest (the locally pulled tag). 24B models are intentionally EXCLUDED
    // because they won't fit in 8 GB VRAM.
    private static readonly string[] DefaultBakeoffModels =
    {
        "qwen3.5:9b",
        "qwen3.5:4b",
        "hf.co/dicta-il/DictaLM-3.0-Nemotron-12B-Instruct-GGUF:latest",
        "qwen2.5:14b"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public async Task ProofreadQuality_RunGoldCases_ReportPrecisionRecallFalsePositive()
    {
        if (!await IsOllamaReachableAsync())
        {
            _output.WriteLine($"SKIPPED: Ollama not reachable at {OllamaBaseUrl}. " +
                              "This quality benchmark needs a live model; skipping so CI stays green.");
            return;
        }

        var cases = LoadProofreadGold();
        if (cases.Length == 0)
        {
            _output.WriteLine("No gold cases in proofread-gold.json.");
            return;
        }

        var router = CreateRouter(ProofreadModel);
        var diff = new SuggestionDiffService();

        var score = await ScoreModelAsync(router, diff, cases, perCaseOutput: true);

        _output.WriteLine("");
        _output.WriteLine("=== Aggregate ===");
        _output.WriteLine($"Cases:                 {cases.Length}");
        _output.WriteLine($"Expected corrections:  {score.TotalExpected}");
        _output.WriteLine($"Produced corrections:  {score.TotalProduced}");
        _output.WriteLine($"Matched corrections:   {score.TotalMatched}");
        _output.WriteLine($"Errored/timed-out:     {score.Errors}");
        _output.WriteLine($"Precision:             {score.PrecisionDisplay("P1")}");
        _output.WriteLine($"Recall:                {score.Recall.ToString("P1", CultureInfo.InvariantCulture)}");
        _output.WriteLine($"No-change cases:       {score.NoChangeCases}");
        _output.WriteLine($"  with a correction:   {score.NoChangeWithCorrection}");
        _output.WriteLine($"False-positive rate:   {score.FalsePositiveRate.ToString("P1", CultureInfo.InvariantCulture)}");

        // This is a reporting benchmark, not a pass/fail gate on model quality — assert only that
        // the run completed over the gold set so the test surfaces the numbers without failing CI
        // for model regressions (which would be noisy and environment-dependent).
        Assert.True(cases.Length > 0);
    }

    /// <summary>
    /// Bake-off runner: score the gold set against EACH model in a configurable list and emit a
    /// per-model quality+latency table. Models come from the <c>PROOFREAD_BAKEOFF_MODELS</c> env var
    /// (comma-separated) or a sensible default shortlist when unset, so the list can change WITHOUT
    /// recompiling. Modeled on HebrewRegressionTests.BenchmarkRegression_RunAllCases_ReportLatency.
    ///
    /// SKIP-BY-DEFAULT: if Ollama is unreachable the test returns (passes). RESILIENT: if a single
    /// model errors or isn't pulled, its row is recorded as NA and the loop CONTINUES — one missing
    /// model never aborts the whole run. Run with:
    ///   dotnet test --filter "FullyQualifiedName~ProofreadQuality_ModelBakeoff"
    /// </summary>
    [Fact]
    public async Task ProofreadQuality_ModelBakeoff_ReportTable()
    {
        var models = ResolveBakeoffModels();
        var parsed = models.Select(ParseCandidate).ToArray();

        // Skip-gate refinement: only the Ollama reachability gate gates a LOCAL-only sweep. If every
        // candidate is an Ollama model and Ollama is down, there is nothing to run -> skip (as before).
        // But if ANY candidate is a cloud provider (Anthropic/OpenAI), the run must proceed even with
        // Ollama down — cloud needs no local server; a missing API key simply yields an NA row.
        var allOllama = parsed.All(p => p.Provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase));
        if (allOllama && !await IsOllamaReachableAsync())
        {
            _output.WriteLine($"SKIPPED: Ollama not reachable at {OllamaBaseUrl} and all candidates are Ollama. " +
                              "This bake-off needs a live model; skipping so CI stays green.");
            return;
        }

        var cases = LoadProofreadGold();
        if (cases.Length == 0)
        {
            _output.WriteLine("No gold cases in proofread-gold.json.");
            return;
        }

        var diff = new SuggestionDiffService();

        _output.WriteLine($"=== Proofread model bake-off ({cases.Length} gold cases, {models.Length} models) ===");
        _output.WriteLine($"Model list source: {(Environment.GetEnvironmentVariable(BakeoffModelsEnvVar) is { Length: > 0 } ? BakeoffModelsEnvVar + " env var" : "built-in default shortlist")}");
        _output.WriteLine("");

        const int modelCol = 64;
        _output.WriteLine($"{"model".PadRight(modelCol)} {"prec",7} {"recall",7} {"fp-rate",8} {"errors",7} {"total ms",10} {"ms/case",9} {"$cost",9}  status");
        _output.WriteLine(new string('-', modelCol + 7 + 7 + 8 + 7 + 10 + 9 + 9 + 14));

        var rows = new List<BakeoffRow>();
        foreach (var (provider, model) in parsed)
        {
            // Label: "provider:model" for cloud candidates, bare model tag for Ollama.
            var label = CandidateLabel(provider, model);
            string status = "ok";
            ModelScore? score = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // Set BOTH Ai:DefaultModel and Ai:FeatureModels:Proofread so the router resolves to
                // this provider/model. Critically, DefaultModel == the model under test means
                // OllamaProvider's 404-retry-with-default cannot silently substitute a working model
                // for a missing one, so a missing model surfaces as an error (recorded NA) instead of
                // a false success. For cloud providers a missing API key throws and is recorded NA too.
                var router = CreateRouter(provider, model);
                score = await ScoreModelAsync(router, diff, cases, perCaseOutput: false);
            }
            catch (Exception ex)
            {
                // One model failing (not pulled, OOM, missing key, etc.) must NOT abort the bake-off.
                status = "NA: " + FirstLine(ex.Message);
            }
            sw.Stop();

            if (score is { } s)
            {
                var cost = EstimateBakeoffCost(model, s.InputTokens, s.OutputTokens);
                rows.Add(new BakeoffRow(label, s.Precision, s.Recall, s.FalsePositiveRate,
                    sw.ElapsedMilliseconds, sw.ElapsedMilliseconds * 1.0 / cases.Length, status, Ok: true));
                // A model that completed every gold case but had some cases time out is flagged so
                // its precision/recall is read with the right caveat. Cloud rows also surface token
                // totals (Ollama reports none) so the $cost column can be sanity-checked.
                var rowStatus = s.Errors > 0 ? $"ok ({s.Errors} case(s) timed out/errored)" : status;
                if (s.InputTokens > 0 || s.OutputTokens > 0)
                    rowStatus += $" [in {s.InputTokens} / out {s.OutputTokens} tok]";
                _output.WriteLine(
                    $"{Truncate(label, modelCol).PadRight(modelCol)} " +
                    $"{s.PrecisionDisplay("P0"),7} " +
                    $"{s.Recall.ToString("P0", CultureInfo.InvariantCulture),7} " +
                    $"{s.FalsePositiveRate.ToString("P0", CultureInfo.InvariantCulture),8} " +
                    $"{s.Errors,7} " +
                    $"{sw.ElapsedMilliseconds,10} " +
                    $"{sw.ElapsedMilliseconds * 1.0 / cases.Length,9:F0} " +
                    $"{(cost > 0 ? "$" + cost.ToString("F4", CultureInfo.InvariantCulture) : "-"),9}  {rowStatus}");
            }
            else
            {
                rows.Add(new BakeoffRow(label, 0, 0, 0, sw.ElapsedMilliseconds,
                    sw.ElapsedMilliseconds * 1.0 / cases.Length, status, Ok: false));
                _output.WriteLine(
                    $"{Truncate(label, modelCol).PadRight(modelCol)} " +
                    $"{"-",7} {"-",7} {"-",8} {"-",7} {sw.ElapsedMilliseconds,10} {"-",9} {"-",9}  {status}");
            }
        }

        // Winner hint (INFORMATIONAL only — not a pass/fail gate): highest recall among models that
        // produced any matches; ties broken by higher precision then lower latency.
        var winner = rows
            .Where(r => r.Ok && r.Recall > 0)
            .OrderByDescending(r => r.Recall)
            .ThenByDescending(r => r.Precision)
            .ThenBy(r => r.TotalMs)
            .FirstOrDefault();

        _output.WriteLine("");
        if (winner is not null)
        {
            _output.WriteLine($"[informational] Winner hint (highest recall): {winner.Model} " +
                              $"— recall {winner.Recall.ToString("P0", CultureInfo.InvariantCulture)}, " +
                              $"precision {winner.Precision.ToString("P0", CultureInfo.InvariantCulture)}, " +
                              $"{winner.MsPerCase:F0} ms/case. Verify against the full table; not a gate.");
        }
        else
        {
            _output.WriteLine("[informational] No model produced a non-zero recall (or all models were NA). " +
                              "No winner hint; inspect the table and ensure the models are pulled.");
        }

        // Reporting benchmark, not a quality gate — assert only that the run iterated the model list.
        Assert.True(models.Length > 0);
    }

    /// <summary>
    /// Score one model over the gold set: drives the real proofread path (prompt + router + production
    /// diff) per case and aggregates precision/recall/false-positive. Shared by the single-model scorer
    /// and the bake-off. When <paramref name="perCaseOutput"/> is true, prints a per-case line.
    /// </summary>
    private async Task<ModelScore> ScoreModelAsync(
        IAiRouter router, SuggestionDiffService diff, HebrewRegressionCase[] cases, bool perCaseOutput)
    {
        var totalExpected = 0;     // sum of expected corrections across all cases that have any
        var totalProduced = 0;     // sum of produced corrections across all cases
        var totalMatched = 0;      // produced corrections that matched an expected one
        var noChangeCases = 0;
        var noChangeWithCorrection = 0;
        var errors = 0;            // cases that threw (timeout/OOM/etc.) and were skipped
        var inputTokens = 0;       // summed across cases (cloud providers report token usage)
        var outputTokens = 0;

        if (perCaseOutput)
        {
            _output.WriteLine("=== Proofread quality per-case ===");
            _output.WriteLine($"{"id",-12} {"expected",8} {"produced",8} {"matched",8}  note");
        }

        foreach (var c in cases)
        {
            var expected = c.ExpectedCorrections ?? Array.Empty<ProofreadCorrection>();
            var expectsNoChanges = c.ShouldHaveNoChanges == true;

            int produced;
            int matched;
            var missedDetail = new List<string>();   // expected corrections with no produced match
            var spuriousDetail = new List<string>();  // produced corrections matching no expected one
            try
            {
                // Instruction is intentionally LEFT EMPTY: AiRouter.CompleteAsync resolves the proofread
                // system message + instruction from PromptFactory.GetPrompt(Proofread, lang) itself. For
                // AiTaskType.Proofread, ShouldUseUnifiedInstructionVerbatim is false, so passing a
                // PromptFactory-built Instruction here would make the router APPEND the proofread prompt
                // to itself (model sees it twice). A clean production proofread call passes no caller
                // Instruction, so we leave it empty to resolve the prompt EXACTLY ONCE. See AiRouter.cs
                // CompleteAsync (resolvedInstruction).
                var request = new AiRequest
                {
                    InputText = c.Input,
                    TaskType = AiTaskType.Proofread,
                    Language = c.Language,
                    SourceId = c.Id
                };

                var response = await router.CompleteAsync(request);
                inputTokens += response.InputTokens ?? 0;
                outputTokens += response.OutputTokens ?? 0;
                // Use the PRODUCTION sanitizer so the eval measures the exact corrected text
                // UnifiedAnalysisService feeds to the diff (UnifiedAnalysisService.RunAsync calls
                // SanitizeResponse(response.Content ?? "") for the non-chunked Proofread path).
                var correctedText = UnifiedAnalysisService.SanitizeResponse(response.Content ?? string.Empty);
                if (string.IsNullOrWhiteSpace(correctedText))
                    correctedText = c.Input; // production falls back to input when the model echoes/empties

                // Extract corrections using the production proofread diff.
                var producedSuggestions = diff.ComputeProofreadSuggestions(c.Input, correctedText);
                produced = producedSuggestions.Count;

                // Match a produced correction to an expected one when they describe the SAME edit.
                // A correction is identified by BOTH its erroneous span (original) and its replacement
                // (suggested). Matching on suggested-text alone is too strict: word-level diffs and
                // whole-phrase gold spans differ in granularity (e.g. doubled-word "בה בה"→"בה",
                // stray-space-before-period "מסמיקה ."→"מסמיקה."), so a real correction is counted as
                // a miss. We accept a match when EITHER the normalized original OR the normalized
                // suggested span lines up (niqqud-/whitespace-tolerant), which credits the same edit
                // regardless of where the diff drew the word boundary.
                var producedUnmatched = producedSuggestions.ToList();
                matched = 0;
                foreach (var e in expected)
                {
                    var expOrig = NormalizeForMatch(e.Original);
                    var expSug = NormalizeForMatch(e.Suggested);
                    var idx = producedUnmatched.FindIndex(s => CorrectionsMatch(
                        NormalizeForMatch(s.OriginalText), NormalizeForMatch(s.SuggestedText), expOrig, expSug));
                    if (idx >= 0)
                    {
                        matched++;
                        producedUnmatched.RemoveAt(idx);
                    }
                    else
                    {
                        missedDetail.Add($"[{e.Original} → {e.Suggested}]");
                    }
                }
                foreach (var s in producedUnmatched)
                    spuriousDetail.Add($"[{s.OriginalText} → {s.SuggestedText}]");
            }
            catch (Exception ex)
            {
                // PER-CASE RESILIENCE: one slow/cold-start case hitting HttpClient.Timeout (or any
                // other failure) must NOT abort the whole model. Count it as an error, treat it as
                // zero produced corrections, and CONTINUE so the model still gets a full row. This
                // is what lets DictaLM-12B / qwen2.5:14b finish their gold-set rows even when a
                // single CPU-spilled case times out.
                errors++;
                if (perCaseOutput)
                    _output.WriteLine($"{c.Id,-12} {expected.Length,8} {"ERR",8} {"-",8}  ERROR: {FirstLine(ex.Message)}");
                // Expected corrections still count toward recall denominator (a timed-out case is a
                // miss, not an exclusion), so add them but produce nothing.
                totalExpected += expected.Length;
                if (expectsNoChanges)
                    noChangeCases++; // produced nothing → not a false positive
                continue;
            }

            totalExpected += expected.Length;
            totalProduced += produced;
            totalMatched += matched;

            if (expectsNoChanges)
            {
                noChangeCases++;
                if (produced > 0)
                    noChangeWithCorrection++;
            }

            if (perCaseOutput)
            {
                var note = expectsNoChanges
                    ? (produced > 0 ? "shouldHaveNoChanges → FALSE POSITIVE" : "shouldHaveNoChanges → clean")
                    : "";
                if (missedDetail.Count > 0)
                    note += (note.Length > 0 ? " " : "") + "MISSED " + string.Join(",", missedDetail);
                if (spuriousDetail.Count > 0)
                    note += (note.Length > 0 ? " " : "") + "SPURIOUS " + string.Join(",", spuriousDetail);
                _output.WriteLine($"{c.Id,-12} {expected.Length,8} {produced,8} {matched,8}  {note}");
            }
        }

        return new ModelScore(totalExpected, totalProduced, totalMatched, noChangeCases, noChangeWithCorrection,
            errors, inputTokens, outputTokens);
    }

    /// <summary>Resolve the bake-off model list from the env var (comma-separated) or the default shortlist.</summary>
    private static string[] ResolveBakeoffModels()
    {
        var raw = Environment.GetEnvironmentVariable(BakeoffModelsEnvVar);
        if (string.IsNullOrWhiteSpace(raw))
            return DefaultBakeoffModels;
        var models = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return models.Length > 0 ? models : DefaultBakeoffModels;
    }

    private static string FirstLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var i = s.IndexOfAny(new[] { '\r', '\n' });
        return i >= 0 ? s[..i] : s;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>Aggregated per-model score; derived rates are computed lazily.</summary>
    private readonly record struct ModelScore(
        int TotalExpected, int TotalProduced, int TotalMatched, int NoChangeCases, int NoChangeWithCorrection,
        int Errors, int InputTokens, int OutputTokens)
    {
        public double Precision => TotalProduced > 0 ? (double)TotalMatched / TotalProduced : 0.0;
        public double Recall => TotalExpected > 0 ? (double)TotalMatched / TotalExpected : 0.0;
        public double FalsePositiveRate => NoChangeCases > 0 ? (double)NoChangeWithCorrection / NoChangeCases : 0.0;
        /// <summary>Precision formatted for display; "n/a" when nothing was produced (rate is undefined).</summary>
        public string PrecisionDisplay(string format) =>
            TotalProduced > 0 ? Precision.ToString(format, CultureInfo.InvariantCulture) : "n/a";
    }

    private sealed record BakeoffRow(
        string Model, double Precision, double Recall, double FalsePositiveRate,
        long TotalMs, double MsPerCase, string Status, bool Ok);

    // ─── Cloud-model pricing (USD per 1M tokens, input/output) for the bake-off $cost column ───
    // Ollama (local) and unknown models have no entry -> cost prints blank/0.
    private static readonly Dictionary<string, (decimal InputPerM, decimal OutputPerM)> ModelPricing =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-opus-4-8"] = (5m, 25m),
            ["claude-sonnet-4-6"] = (3m, 15m),
            ["claude-haiku-4-5"] = (1m, 5m),
            ["claude-fable-5"] = (10m, 50m),
            ["gpt-5.5"] = (5m, 30m),
            ["gpt-5.4"] = (2.5m, 15m),
            ["gpt-5"] = (0.625m, 5m),
            ["gpt-5.4-nano"] = (0.20m, 1.25m)
        };

    /// <summary>Approx USD cost for a model's token totals; 0 when the model has no price entry (Ollama/unknown).</summary>
    private static decimal EstimateBakeoffCost(string model, int inputTokens, int outputTokens)
    {
        if (!ModelPricing.TryGetValue(model, out var price)) return 0m;
        return (inputTokens / 1_000_000m) * price.InputPerM + (outputTokens / 1_000_000m) * price.OutputPerM;
    }

    // ─── Match normalization: trim + collapse whitespace ───

    private static string NormalizeForMatch(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        // Trim, collapse whitespace, and strip Hebrew cantillation te'amim (U+0591–U+05AF) and niqqud
        // points (U+05B0–U+05BD, U+05BF rafe, U+05C1 shin dot, U+05C2 sin dot, U+05C4 upper dot,
        // U+05C5 lower dot, U+05C7 qamats qatan) so a model that returns vocalized text still matches
        // an unvocalized gold span and vice versa.  Hebrew PUNCTUATION code points are intentionally
        // excluded so punctuation corrections are not normalized away: U+05BE maqaf, U+05C0 paseq,
        // U+05C3 sof pasuq, U+05C6 nun hafukha.
        var stripped = Regex.Replace(s, "[\u0591-\u05AF\u05B0-\u05BD\u05BF\u05C1\u05C2\u05C4\u05C5\u05C7]", string.Empty);
        return Regex.Replace(stripped.Trim(), @"\s+", " ");
    }

    /// <summary>
    /// True when a produced correction (origP→sugP) describes the same edit as an expected one
    /// (origE→sugE). We credit a match when EITHER endpoint aligns: the suggested replacements are
    /// equal, OR the original erroneous spans are equal, OR one original span contains the other
    /// (the word-level diff may emit a sub-span of the gold's whole-phrase original, or vice versa).
    /// Endpoint comparisons use the niqqud-/whitespace-normalized strings already passed in.
    /// </summary>
    private static bool CorrectionsMatch(string origP, string sugP, string origE, string sugE)
    {
        if (sugP.Length > 0 && sugP == sugE) return true;
        if (origP.Length > 0 && origP == origE) return true;
        // Span-granularity tolerance: a single-word produced original inside the gold's multi-word
        // original (e.g. produced "בה"→"" inside gold "בה בה"→"בה"), or the reverse.
        if (origP.Length > 0 && origE.Length > 0 &&
            (origE.Contains(origP, StringComparison.Ordinal) || origP.Contains(origE, StringComparison.Ordinal)))
            return true;
        return false;
    }

    // ─── Gold loading ───

    private static HebrewRegressionCase[] LoadProofreadGold()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "proofread-gold.json");
        if (!File.Exists(path))
            return Array.Empty<HebrewRegressionCase>();
        var json = File.ReadAllText(path);
        var cases = JsonSerializer.Deserialize<HebrewRegressionCase[]>(json, JsonOptions);
        return cases ?? Array.Empty<HebrewRegressionCase>();
    }

    // ─── Ollama reachability probe (skip-gate) ───

    private static async Task<bool> IsOllamaReachableAsync()
    {
        // Probe both the configured host and the explicit IPv4 loopback: .NET's "localhost"
        // can resolve to ::1 (IPv6) while Ollama binds 127.0.0.1, which would otherwise make a
        // reachable server look down and skip the run unnecessarily.
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

    // ─── Router DI (mirrors HebrewRegressionTests.CreateLanguageEngine's Ai wiring) ───

    // ─── Provider:model parsing for bake-off candidates ───

    // Known provider prefixes. We only treat a candidate as provider-prefixed when it begins with one
    // of these (case-insensitive) so Ollama tags that contain a colon (e.g. "qwen3.5:9b") are NEVER
    // misread as "qwen3.5" provider + "9b" model. Maps the lowercased prefix to its canonical name.
    private static readonly Dictionary<string, string> KnownProviderPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["anthropic"] = "Anthropic",
        ["openai"] = "OpenAI",
        ["ollama"] = "Ollama"
    };

    /// <summary>
    /// Split a bake-off candidate entry into (provider, model). An entry is only treated as
    /// provider-prefixed when it starts with a KNOWN provider prefix followed by a colon
    /// ("anthropic:", "openai:", "ollama:"), in which case we split on the FIRST colon. Otherwise the
    /// whole entry is an Ollama model tag (which may itself contain colons, e.g. "qwen3.5:9b").
    /// </summary>
    private static (string Provider, string Model) ParseCandidate(string entry)
    {
        var trimmed = entry.Trim();
        var colon = trimmed.IndexOf(':');
        if (colon > 0)
        {
            var prefix = trimmed[..colon];
            if (KnownProviderPrefixes.TryGetValue(prefix, out var canonical))
            {
                var model = trimmed[(colon + 1)..].Trim();
                return (canonical, model);
            }
        }
        return ("Ollama", trimmed);
    }

    /// <summary>Human-readable bake-off row label: "provider:model" for cloud, bare model for Ollama.</summary>
    private static string CandidateLabel(string provider, string model)
        => provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase) ? model : $"{provider}:{model}";

    // ─── Router DI (mirrors HebrewRegressionTests.CreateLanguageEngine's Ai wiring) ───

    private static IAiRouter CreateRouter(string entry)
    {
        var (provider, model) = ParseCandidate(entry);
        return CreateRouter(provider, model);
    }

    private static IAiRouter CreateRouter(string provider, string model)
    {
        // Override Ai:DefaultProvider/DefaultModel AND Ai:FeatureModels:Proofread in the SAME in-memory
        // builder the router resolves through, so the proofread task routes to `provider`/`model`.
        // DefaultModel is also set (in both IConfiguration and AiOptions) so OllamaProvider's
        // 404-retry-with-default can't silently fall back to a different, working model when `model`
        // isn't pulled. Cloud providers read their API key from config OR env var (AI_ANTHROPIC_APIKEY
        // / AI_OPENAI_APIKEY) — we deliberately set NO key here, so a cloud run with no env key throws
        // InvalidOperationException, which the bake-off records as an NA row.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:DefaultProvider"] = provider,
                ["Ai:Providers:Ollama:BaseUrl"] = OllamaBaseUrl,
                ["Ai:DefaultModel"] = model,
                ["Ai:FeatureModels:Proofread:Provider"] = provider,
                ["Ai:FeatureModels:Proofread:Model"] = model
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        // Bake-off timeout: the production OllamaProvider resolves its HttpClient via
        // _httpFactory.CreateClient("Ollama") and uses whatever Timeout this named client carries —
        // there is NO Ollama timeout config key, so the only place to raise it is here. The default
        // 2-minute timeout caused cold-start/CPU-spill models (DictaLM-12B, qwen2.5:14b) to hit
        // HttpClient.Timeout on a single slow case and fail the whole model row. Raise to 10 minutes
        // for the test harness ONLY (production wiring in Program.cs is untouched).
        services.AddHttpClient("Ollama", client => client.Timeout = TimeSpan.FromMinutes(10));
        // The cloud providers (Anthropic, OpenAI) resolve their HttpClient via the DEFAULT, unnamed
        // _httpFactory.CreateClient() — register it with the same generous timeout so a slow cloud
        // call doesn't hit the 2-minute default.
        services.AddHttpClient(string.Empty, client => client.Timeout = TimeSpan.FromMinutes(10));
        services.Configure<AiOptions>(opts =>
        {
            opts.DefaultProvider = provider;
            opts.DefaultModel = model;
            opts.FeatureModels = new Dictionary<string, FeatureModelOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["Proofread"] = new FeatureModelOptions { Provider = provider, Model = model }
            };
        });
        services.AddSingleton<PromptFactory>();
        services.AddSingleton<IReadOnlyDictionary<string, IAiAnalysisProvider>>(sp =>
        {
            var c = sp.GetRequiredService<IConfiguration>();
            var opts = sp.GetRequiredService<IOptions<AiOptions>>();
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            // Register ALL providers (keyed case-insensitively) so the bake-off can drive any of them;
            // the router selects by the Proofread feature provider. Cloud providers throw at call time
            // if their API key is absent — that surfaces as an NA row, not a registration failure.
            var dict = new Dictionary<string, IAiAnalysisProvider>(StringComparer.OrdinalIgnoreCase)
            {
                ["Ollama"] = new OllamaProvider(factory, c, opts),
                ["Anthropic"] = new AnthropicProvider(factory, c, opts),
                ["OpenAI"] = new OpenAiProvider(factory, c, opts)
            };
            return dict;
        });
        services.AddSingleton<IAiRouter, AiRouter>();

        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IAiRouter>();
    }
}
