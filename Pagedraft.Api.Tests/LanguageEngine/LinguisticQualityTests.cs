using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
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
/// Runs the REAL linguistic-analysis consistency path over each gold case in linguistic-gold.json
/// and reports false-positive count (clean cases), planted-issue recall, and consistency-type
/// accuracy. The sibling of ProofreadQualityTests — same skip-by-default gating, same DI wiring,
/// same env-var bake-off model list, same ITestOutputHelper results table — applied to the
/// LinguisticAnalysis `consistencyIssues` output (register / tense / pov shifts) instead of proofread
/// corrections.
///
/// PATH CHOICE — PromptFactory + IAiRouter (NOT UnifiedAnalysisService):
/// UnifiedAnalysisService.RunWithInputAsync cannot be cleanly constructed in a unit test — it needs an
/// AppDbContext, AnalysisProgressTracker and IAnalysisContextService and resolves input from a persisted
/// target. So this test drives the same underlying machinery directly, exactly mirroring
/// UnifiedAnalysisService.RunWithInputAsync (see that method ~:477):
///   1. instruction = PromptFactory.GetAnalysisPrompt(AnalysisType.LinguisticAnalysis, language[, context])
///      — the STRUCTURED JSON prompt (LinguisticEn / LinguisticHe) that actually asks the model to emit
///      `consistencyIssues`. The preceding/following gold context, when present, is injected through the
///      context overload exactly as production does (PromptFactory builds [PRECEDING_CONTEXT] /
///      [FOLLOWING_CONTEXT] sections).
///   2. Call IAiRouter.CompleteAsync with AiRequest { TaskType = AiTaskType.LinguisticAnalysis,
///      Instruction = instruction, JsonMode = true } — byte-for-byte the request
///      UnifiedAnalysisService.RunWithInputAsync builds for LinguisticAnalysis.
///   3. Parse the model JSON into LinguisticAnalysisResult and read its ConsistencyIssues.
///
/// PREMISE NOTE (deviation from the literal todo text): the todo suggested calling the router with NO
/// Instruction. That is WRONG for LinguisticAnalysis: GetPrompt(AiTaskType.LinguisticAnalysis) returns a
/// free-text "respond with headings/numbered lists" instruction that never produces `consistencyIssues`.
/// The structured JSON output is reachable ONLY via GetAnalysisPrompt(AnalysisType.LinguisticAnalysis),
/// which is what production sends. We therefore pass that instruction (matching
/// UnifiedAnalysisService.RunWithInputAsync) so the eval scores the SAME output the user sees.
///
/// GATING — this test needs a live local model (Ollama) OR a configured cloud key. It is
/// SKIP-BY-DEFAULT: it probes the Ollama endpoint first and, if every candidate is Ollama and the
/// endpoint is unreachable, writes a message and returns (passes, does not fail). For a cloud
/// (OpenRouter) provider it gates on the API key instead. This mirrors ProofreadQualityTests so CI
/// stays green when no model is reachable.
/// </summary>
public class LinguisticQualityTests
{
    private readonly ITestOutputHelper _output;

    public LinguisticQualityTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private const string OllamaBaseUrl = "http://localhost:11434";

    // OpenRouter base URL used when LINGUISTIC_BAKEOFF_PROVIDER=OpenRouter. The OpenAiCompatibleProvider
    // reads it from Ai:Providers:OpenRouter:BaseUrl (we set it in the test DI). API key comes from
    // config OR env AI_OPENROUTER_APIKEY — we set NO key here, so a cloud run with no env key is gated.
    private const string OpenRouterBaseUrl = "https://openrouter.ai/api/v1";

    // Env var (comma-separated) to override the bake-off model list WITHOUT recompiling.
    private const string BakeoffModelsEnvVar = "LINGUISTIC_BAKEOFF_MODELS";

    // Env var to select the provider for the bake-off (default Ollama). Set to "OpenRouter" to test
    // cloud models — that route gates on the OpenRouter API key being present.
    private const string BakeoffProviderEnvVar = "LINGUISTIC_BAKEOFF_PROVIDER";

    // Default bake-off shortlist: models actually pulled on the RTX 4070 laptop (~8 GB VRAM).
    // qwen3.5:9b is the production default; DictaLM is the Hebrew-specialist local model (tag :latest is
    // the locally pulled tag). 24B+ models are intentionally EXCLUDED because they won't fit in 8 GB VRAM.
    private static readonly string[] DefaultBakeoffModels =
    {
        "qwen3.5:9b",
        "qwen3.5:4b",
        "hf.co/dicta-il/DictaLM-3.0-Nemotron-12B-Instruct-GGUF:latest",
        "qwen2.5:14b"
    };

    // Single-model scorer model (used by the non-bake-off Fact). Kept identical to the production default
    // so the headline number reflects what ships.
    private const string LinguisticModel = "qwen3.5:9b";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ─── Composite scoring formula (documented once, used everywhere) ──────────────────────────────
    //
    // Per model, over the gold set:
    //   cleanFalsePositives = total consistencyIssues returned across all expectClean cases.
    //                         (We also flag any case that exceeds its own maxConsistencyIssues.)
    //   plantedRecall       = (planted cases where >= 1 consistencyIssue was returned) / (planted cases).
    //   typeAccuracy        = (planted cases where some returned issue's `type` is in the case's
    //                          expectedConsistencyTypes) / (planted cases).
    //
    // Composite (higher is better, range roughly [-inf, 1] but in practice [0, 1] since FP penalty is
    // capped by clamping the final value at 0):
    //   composite = 0.45 * plantedRecall
    //             + 0.45 * typeAccuracy
    //             - 0.10 * cleanFalsePositiveRate
    // where cleanFalsePositiveRate = cleanFalsePositives / max(1, cleanCases), clamped to [0, 1].
    // Recall and type-accuracy are weighted equally and dominate; clean false positives apply a small
    // penalty so a model that flags everything cannot win on recall alone. The final value is clamped
    // to >= 0 so a noisy model reads as 0, not a confusing negative. This is deterministic and
    // sortable; it is INFORMATIONAL only — the test never fails on model quality.
    private const double RecallWeight = 0.45;
    private const double TypeWeight = 0.45;
    private const double FpPenaltyWeight = 0.10;

    private static double Composite(double plantedRecall, double typeAccuracy, double cleanFpRate)
    {
        var raw = RecallWeight * plantedRecall + TypeWeight * typeAccuracy - FpPenaltyWeight * Math.Clamp(cleanFpRate, 0.0, 1.0);
        return Math.Max(0.0, raw);
    }

    [Fact]
    public async Task LinguisticQuality_RunGoldCases_ReportFalsePositiveRecallTypeAccuracy()
    {
        // Single-model run is Ollama-only, so gate on the Ollama probe.
        if (!await IsOllamaReachableAsync())
        {
            _output.WriteLine($"SKIPPED: Ollama not reachable at {OllamaBaseUrl}. " +
                              "This quality benchmark needs a live model; skipping so CI stays green.");
            return;
        }

        var cases = LoadLinguisticGold();
        if (cases.Length == 0)
        {
            _output.WriteLine("No gold cases in linguistic-gold.json.");
            return;
        }

        var router = CreateRouter("Ollama", LinguisticModel);

        var score = await ScoreModelAsync(router, cases, perCaseOutput: true);

        _output.WriteLine("");
        _output.WriteLine("=== Aggregate ===");
        _output.WriteLine($"Cases:                 {cases.Length}");
        _output.WriteLine($"Clean cases:           {score.CleanCases}");
        _output.WriteLine($"Clean false positives: {score.CleanFalsePositives} (consistencyIssues returned on clean cases; lower better)");
        _output.WriteLine($"  over-cap cases:      {score.CleanOverCapCases} (exceeded the case's maxConsistencyIssues)");
        _output.WriteLine($"Planted cases:         {score.PlantedCases}");
        _output.WriteLine($"Planted recall:        {score.PlantedRecall.ToString("P0", CultureInfo.InvariantCulture)} ({score.PlantedRecallHits}/{score.PlantedCases})");
        _output.WriteLine($"Type accuracy:         {score.TypeAccuracy.ToString("P0", CultureInfo.InvariantCulture)} ({score.TypeAccuracyHits}/{score.PlantedCases})");
        _output.WriteLine($"Errored/timed-out:     {score.Errors}");
        _output.WriteLine($"Composite:             {score.Composite.ToString("F3", CultureInfo.InvariantCulture)}");

        // Reporting benchmark, not a pass/fail gate on model quality — assert only that the run
        // completed over the gold set so the numbers surface without failing CI for model regressions.
        Assert.True(cases.Length > 0);
    }

    /// <summary>
    /// Bake-off runner: score the gold set against EACH model in a configurable list and emit a
    /// per-model comparison table. Models come from the <c>LINGUISTIC_BAKEOFF_MODELS</c> env var
    /// (comma-separated) or a sensible default shortlist when unset. The provider comes from
    /// <c>LINGUISTIC_BAKEOFF_PROVIDER</c> (default Ollama; set to OpenRouter to test cloud models).
    /// Modeled on ProofreadQualityTests.ProofreadQuality_ModelBakeoff_ReportTable.
    ///
    /// SKIP-BY-DEFAULT: for an Ollama sweep, if the endpoint is unreachable the test returns (passes).
    /// For an OpenRouter sweep, if no API key is present the test returns (passes). RESILIENT: if a
    /// single model errors or isn't pulled, its row is recorded NA and the loop CONTINUES. Run with:
    ///   dotnet test --filter "FullyQualifiedName~LinguisticQuality_ModelBakeoff"
    /// </summary>
    [Fact]
    public async Task LinguisticQuality_ModelBakeoff_ReportTable()
    {
        var provider = ResolveBakeoffProvider();
        var models = ResolveBakeoffModels();

        // Skip-gate by provider:
        //  - Ollama: needs a live local server. If unreachable, nothing can run -> skip (CI stays green).
        //  - OpenRouter (cloud): needs no local server, but needs an API key. If absent -> skip.
        if (provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            if (!await IsOllamaReachableAsync())
            {
                _output.WriteLine($"SKIPPED: Ollama not reachable at {OllamaBaseUrl} (provider=Ollama). " +
                                  "This bake-off needs a live model; skipping so CI stays green.");
                return;
            }
        }
        else if (provider.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase))
        {
            if (!OpenRouterKeyPresent())
            {
                _output.WriteLine("SKIPPED: provider=OpenRouter but no API key found " +
                                  "(env AI_OPENROUTER_APIKEY or config Ai:Providers:OpenRouter:ApiKey). " +
                                  "Skipping the cloud bake-off so CI stays green.");
                return;
            }
        }
        else
        {
            _output.WriteLine($"SKIPPED: unknown {BakeoffProviderEnvVar}='{provider}'. " +
                              "Supported: Ollama (default) or OpenRouter. Skipping so CI stays green.");
            return;
        }

        var cases = LoadLinguisticGold();
        if (cases.Length == 0)
        {
            _output.WriteLine("No gold cases in linguistic-gold.json.");
            return;
        }

        _output.WriteLine($"=== Linguistic-consistency model bake-off ({cases.Length} gold cases, {models.Length} models, provider={provider}) ===");
        _output.WriteLine($"Model list source: {(Environment.GetEnvironmentVariable(BakeoffModelsEnvVar) is { Length: > 0 } ? BakeoffModelsEnvVar + " env var" : "built-in default shortlist")}");
        var clean = cases.Count(c => c.ExpectClean);
        var planted = cases.Length - clean;
        _output.WriteLine($"Gold composition: {clean} clean, {planted} planted.");
        _output.WriteLine("");

        const int modelCol = 56;
        // Columns: model | clean-FP | planted-recall | type-accuracy | composite | errors | status
        _output.WriteLine($"{"model".PadRight(modelCol)} {"clean-FP",8} {"recall",7} {"type-acc",8} {"composite",9} {"errors",7}  status");
        _output.WriteLine(new string('-', modelCol + 8 + 7 + 8 + 9 + 7 + 12));

        var rows = new List<BakeoffRow>();
        foreach (var model in models)
        {
            var label = provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase) ? model : $"{provider}:{model}";
            string status = "ok";
            ModelScore? score = null;
            try
            {
                var router = CreateRouter(provider, model);
                score = await ScoreModelAsync(router, cases, perCaseOutput: false);
            }
            catch (Exception ex)
            {
                // One model failing (not pulled, OOM, missing key, etc.) must NOT abort the bake-off.
                status = "NA: " + FirstLine(ex.Message);
            }

            if (score is { } s)
            {
                var rowStatus = s.Errors > 0 ? $"ok ({s.Errors} case(s) timed out/errored)" : status;
                rows.Add(new BakeoffRow(label, s.CleanFalsePositives, s.PlantedRecall, s.TypeAccuracy, s.Composite, Ok: true));
                _output.WriteLine(
                    $"{Truncate(label, modelCol).PadRight(modelCol)} " +
                    $"{s.CleanFalsePositives,8} " +
                    $"{s.PlantedRecall.ToString("P0", CultureInfo.InvariantCulture),7} " +
                    $"{s.TypeAccuracy.ToString("P0", CultureInfo.InvariantCulture),8} " +
                    $"{s.Composite.ToString("F3", CultureInfo.InvariantCulture),9} " +
                    $"{s.Errors,7}  {rowStatus}");
            }
            else
            {
                rows.Add(new BakeoffRow(label, 0, 0, 0, 0, Ok: false));
                _output.WriteLine(
                    $"{Truncate(label, modelCol).PadRight(modelCol)} " +
                    $"{"-",8} {"-",7} {"-",8} {"-",9} {"-",7}  {status}");
            }
        }

        // Winner hint (INFORMATIONAL only — not a pass/fail gate): highest composite among models that
        // ran; ties broken by lower clean false positives.
        var winner = rows
            .Where(r => r.Ok)
            .OrderByDescending(r => r.Composite)
            .ThenBy(r => r.CleanFalsePositives)
            .FirstOrDefault();

        _output.WriteLine("");
        if (winner is not null && winner.Composite > 0)
        {
            _output.WriteLine($"[informational] Winner hint (highest composite): {winner.Model} " +
                              $"— composite {winner.Composite.ToString("F3", CultureInfo.InvariantCulture)}, " +
                              $"recall {winner.PlantedRecall.ToString("P0", CultureInfo.InvariantCulture)}, " +
                              $"type-acc {winner.TypeAccuracy.ToString("P0", CultureInfo.InvariantCulture)}, " +
                              $"clean-FP {winner.CleanFalsePositives}. Verify against the full table; not a gate.");
        }
        else
        {
            _output.WriteLine("[informational] No model produced a positive composite (or all models were NA). " +
                              "No winner hint; inspect the table and ensure the models are pulled.");
        }

        // Reporting benchmark, not a quality gate — assert only that the run iterated the model list.
        Assert.True(models.Length > 0);
    }

    /// <summary>
    /// Score one model over the gold set: drives the real linguistic-analysis path (structured prompt +
    /// router + JSON parse) per case and aggregates clean false-positives, planted recall, and type
    /// accuracy. Shared by the single-model scorer and the bake-off. When <paramref name="perCaseOutput"/>
    /// is true, prints a per-case line.
    /// </summary>
    private async Task<ModelScore> ScoreModelAsync(IAiRouter router, LinguisticGoldCase[] cases, bool perCaseOutput)
    {
        var cleanCases = 0;
        var cleanFalsePositives = 0;     // total consistencyIssues returned across all clean cases
        var cleanOverCapCases = 0;       // clean cases whose issue count exceeded maxConsistencyIssues
        var plantedCases = 0;
        var plantedRecallHits = 0;       // planted cases with >= 1 consistencyIssue returned
        var typeAccuracyHits = 0;        // planted cases where some returned type matched the expected set
        var errors = 0;

        if (perCaseOutput)
        {
            _output.WriteLine("=== Linguistic consistency per-case ===");
            _output.WriteLine($"{"id",-22} {"clean?",6} {"issues",6} {"recall",6} {"type",5}  note");
        }

        foreach (var c in cases)
        {
            // Normalize the gold language to what PromptFactory/AiRouter expect. They branch on
            // language.StartsWith("he"/"en", ...), so "he-IL" already selects the Hebrew prompt and
            // "en" selects the English prompt — no remap needed. We pass the gold value through
            // verbatim so the request language matches production. (If a future gold file used a bare
            // "he", StartsWith("he") still resolves Hebrew, so this stays correct.)
            var language = string.IsNullOrWhiteSpace(c.Language) ? "he-IL" : c.Language.Trim();

            // Build the structured instruction exactly as UnifiedAnalysisService.RunWithInputAsync does.
            // When the gold case carries preceding/following context, inject it through the context
            // overload so PromptFactory emits [PRECEDING_CONTEXT]/[FOLLOWING_CONTEXT] sections — the same
            // path production uses. Otherwise use the base (context-free) instruction.
            string instruction;
            var hasCtx = !string.IsNullOrWhiteSpace(c.PrecedingContext) || !string.IsNullOrWhiteSpace(c.FollowingContext);
            if (hasCtx)
            {
                var ctx = new AnalysisContext
                {
                    TargetText = c.Input,
                    PrecedingContext = string.IsNullOrWhiteSpace(c.PrecedingContext) ? null : c.PrecedingContext,
                    FollowingContext = string.IsNullOrWhiteSpace(c.FollowingContext) ? null : c.FollowingContext,
                    AnalysisType = AnalysisType.LinguisticAnalysis
                };
                instruction = _promptFactory.GetAnalysisPrompt(AnalysisType.LinguisticAnalysis, language, ctx);
            }
            else
            {
                instruction = _promptFactory.GetAnalysisPrompt(AnalysisType.LinguisticAnalysis, language);
            }

            List<ConsistencyIssue> issues;
            try
            {
                var request = new AiRequest
                {
                    InputText = c.Input,
                    Instruction = instruction,
                    TaskType = AiTaskType.LinguisticAnalysis,
                    Language = language,
                    SourceId = c.Id,
                    JsonMode = true
                };

                var response = await router.CompleteAsync(request);
                var result = ParseLinguistic(response.Content ?? string.Empty);
                issues = result?.ConsistencyIssues ?? new List<ConsistencyIssue>();
            }
            catch (Exception ex)
            {
                // PER-CASE RESILIENCE: one slow/cold-start case hitting HttpClient.Timeout (or any other
                // failure) must NOT abort the whole model. Count it as an error, treat it as zero issues,
                // and CONTINUE so the model still gets a full row.
                errors++;
                if (c.ExpectClean) cleanCases++; else plantedCases++;
                if (perCaseOutput)
                    _output.WriteLine($"{c.Id,-22} {(c.ExpectClean ? "clean" : "plant"),6} {"ERR",6} {"-",6} {"-",5}  ERROR: {FirstLine(ex.Message)}");
                continue;
            }

            if (c.ExpectClean)
            {
                cleanCases++;
                cleanFalsePositives += issues.Count;
                var overCap = issues.Count > c.MaxConsistencyIssues;
                if (overCap) cleanOverCapCases++;
                if (perCaseOutput)
                {
                    var note = issues.Count == 0
                        ? "clean -> no issues (good)"
                        : $"FALSE POSITIVE x{issues.Count}" + (overCap ? $" (> max {c.MaxConsistencyIssues})" : "") +
                          " [" + string.Join("; ", issues.Take(3).Select(i => $"{i.Type}:{Truncate(i.Span, 24)}")) + "]";
                    _output.WriteLine($"{c.Id,-22} {"clean",6} {issues.Count,6} {"-",6} {"-",5}  {note}");
                }
            }
            else
            {
                plantedCases++;
                var recall = issues.Count > 0;
                var expectedTypes = c.ExpectedConsistencyTypes ?? Array.Empty<string>();
                var typeHit = issues.Any(i => expectedTypes.Any(t =>
                    string.Equals(t?.Trim(), i.Type?.Trim(), StringComparison.OrdinalIgnoreCase)));
                if (recall) plantedRecallHits++;
                if (typeHit) typeAccuracyHits++;
                if (perCaseOutput)
                {
                    var returnedTypes = issues.Count > 0
                        ? string.Join(",", issues.Select(i => i.Type).Distinct(StringComparer.OrdinalIgnoreCase))
                        : "(none)";
                    var note = $"expected[{string.Join(",", expectedTypes)}] returned[{returnedTypes}]" +
                               (recall ? "" : " MISSED (no issues returned)") +
                               (recall && !typeHit ? " WRONG-TYPE" : "");
                    _output.WriteLine($"{c.Id,-22} {"plant",6} {issues.Count,6} {(recall ? "1" : "0"),6} {(typeHit ? "1" : "0"),5}  {note}");
                }
            }
        }

        var plantedRecall = plantedCases > 0 ? (double)plantedRecallHits / plantedCases : 0.0;
        var typeAccuracy = plantedCases > 0 ? (double)typeAccuracyHits / plantedCases : 0.0;
        var cleanFpRate = cleanCases > 0 ? (double)cleanFalsePositives / cleanCases : 0.0;
        var composite = Composite(plantedRecall, typeAccuracy, cleanFpRate);

        return new ModelScore(
            cleanCases, cleanFalsePositives, cleanOverCapCases,
            plantedCases, plantedRecallHits, typeAccuracyHits, errors,
            plantedRecall, typeAccuracy, composite);
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

    /// <summary>Resolve the bake-off provider from the env var; defaults to Ollama.</summary>
    private static string ResolveBakeoffProvider()
    {
        var raw = Environment.GetEnvironmentVariable(BakeoffProviderEnvVar);
        return string.IsNullOrWhiteSpace(raw) ? "Ollama" : raw.Trim();
    }

    /// <summary>True when an OpenRouter API key is reachable (env AI_OPENROUTER_APIKEY).</summary>
    private static bool OpenRouterKeyPresent()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AI_OPENROUTER_APIKEY"));

    private static string FirstLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var i = s.IndexOfAny(new[] { '\r', '\n' });
        return i >= 0 ? s[..i] : s;
    }

    private static string Truncate(string? s, int max)
    {
        s ??= string.Empty;
        return s.Length <= max ? s : s[..(max - 1)] + "...";
    }

    /// <summary>Aggregated per-model score; derived rates are computed by ScoreModelAsync and carried here.</summary>
    private readonly record struct ModelScore(
        int CleanCases, int CleanFalsePositives, int CleanOverCapCases,
        int PlantedCases, int PlantedRecallHits, int TypeAccuracyHits, int Errors,
        double PlantedRecall, double TypeAccuracy, double Composite);

    private sealed record BakeoffRow(
        string Model, int CleanFalsePositives, double PlantedRecall, double TypeAccuracy, double Composite, bool Ok);

    // ─── Structured JSON parse (mirrors UnifiedAnalysisService.TryExtractAndReserialize shape) ───

    /// <summary>
    /// Parse the model's content into a LinguisticAnalysisResult. Uses the SAME extractor that
    /// production and the chapter-baseline builder use (<see cref="UnifiedAnalysisService.ExtractJson"/>:
    /// markdown fences, BOM/bidi stripping, prose-in-braces rejection, and a markdown-strip retry), so
    /// bake-off parsing matches real parsing and cannot mis-rank models. A local first-'{' brace matcher
    /// would reject Hebrew prose-wrapped JSON that production accepts. Returns null on any failure — the
    /// caller treats that as zero issues.
    /// </summary>
    private static LinguisticAnalysisResult? ParseLinguistic(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;
        var json = UnifiedAnalysisService.ExtractJson(content);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<LinguisticAnalysisResult>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ─── Bug-fix guard: bake-off parsing uses the SAME extractor as production ───
    // Always-on (no live model needed). Locks the harness to UnifiedAnalysisService.ExtractJson so
    // scores reflect real parsing.

    [Fact]
    public void ParseLinguistic_HebrewProseWrappedJson_ExtractsRealObjectNotPreambleBrace()
    {
        // A Hebrew preamble that itself contains balanced braces, then the REAL metrics object.
        // A first-'{' brace matcher would lock onto {הערה...} and fail; the production extractor
        // rejects prose-in-braces and finds the real object, so bake-off parsing matches production.
        const string proseWrapped = """
            לפניכם ניתוח לשוני {הערה ראשונית: הטקסט תקין}.

            {
              "grammaticalityScore": 0.95,
              "summary": "ניתוח תקין.",
              "deviations": [],
              "consistencyIssues": []
            }
            """;

        var parsed = ParseLinguistic(proseWrapped);

        // The REAL object is parsed (grammaticalityScore round-trips), not the prose {הערה...} brace.
        Assert.NotNull(parsed);
        Assert.Equal(0.95, parsed!.GrammaticalityScore, precision: 5);
    }

    // ─── Gold loading ───

    private static LinguisticGoldCase[] LoadLinguisticGold()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "linguistic-gold.json");
        if (!File.Exists(path))
            return Array.Empty<LinguisticGoldCase>();
        var json = File.ReadAllText(path);
        // The gold file's first array element is a {_README: ...} metadata note (no "id"); skip any
        // entry that lacks an id so iteration only sees real cases.
        var raw = JsonSerializer.Deserialize<LinguisticGoldCase[]>(json, JsonOptions);
        if (raw == null) return Array.Empty<LinguisticGoldCase>();
        return raw.Where(c => !string.IsNullOrWhiteSpace(c.Id)).ToArray();
    }

    /// <summary>Gold case schema (matches linguistic-gold.json entries; the _README note has no id and is filtered out).</summary>
    private sealed class LinguisticGoldCase
    {
        public string Id { get; set; } = "";
        public string Input { get; set; } = "";
        public string Language { get; set; } = "he-IL";
        public string? PrecedingContext { get; set; }
        public string? FollowingContext { get; set; }
        public bool ExpectClean { get; set; }
        public string[]? ExpectedConsistencyTypes { get; set; }
        public int MaxConsistencyIssues { get; set; }
        public string? Notes { get; set; }
    }

    // ─── Ollama reachability probe (skip-gate) — mirrors ProofreadQualityTests ───

    private static async Task<bool> IsOllamaReachableAsync()
    {
        // Probe both the configured host and the explicit IPv4 loopback: .NET's "localhost" can resolve
        // to ::1 (IPv6) while Ollama binds 127.0.0.1, which would otherwise make a reachable server look
        // down and skip the run unnecessarily.
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

    // ─── Router DI (mirrors ProofreadQualityTests.CreateRouter) ───

    // Shared prompt factory — the structured instruction is the same for every model, so build it once.
    private static readonly PromptFactory _promptFactory = new();

    private static IAiRouter CreateRouter(string provider, string model)
    {
        // Override Ai:DefaultProvider/DefaultModel AND Ai:FeatureModels:LinguisticAnalysis in the SAME
        // in-memory builder the router resolves through, so the LinguisticAnalysis task routes to
        // `provider`/`model`. DefaultModel is also set (in both IConfiguration and AiOptions) so
        // OllamaProvider's 404-retry-with-default can't silently fall back to a different, working model
        // when `model` isn't pulled. Cloud providers read their API key from config OR env var — we set
        // NO key here, so an OpenRouter run with no env key throws InvalidOperationException, which the
        // bake-off records as an NA row (and the skip-gate normally prevents reaching).
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:DefaultProvider"] = provider,
                ["Ai:Providers:Ollama:BaseUrl"] = OllamaBaseUrl,
                ["Ai:Providers:OpenRouter:BaseUrl"] = OpenRouterBaseUrl,
                ["Ai:DefaultModel"] = model,
                ["Ai:FeatureModels:LinguisticAnalysis:Provider"] = provider,
                ["Ai:FeatureModels:LinguisticAnalysis:Model"] = model
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        // Bake-off timeout: OllamaProvider resolves its HttpClient via _httpFactory.CreateClient("Ollama")
        // and uses whatever Timeout this named client carries — there is NO Ollama timeout config key, so
        // the only place to raise it is here. The default 2-minute timeout caused cold-start/CPU-spill
        // models to hit HttpClient.Timeout on a single slow case. Raise to 10 minutes for the harness ONLY
        // (production wiring in Program.cs is untouched).
        services.AddHttpClient("Ollama", client => client.Timeout = TimeSpan.FromMinutes(10));
        // Cloud / OpenAI-compatible providers resolve their HttpClient via the DEFAULT, unnamed
        // _httpFactory.CreateClient() — register it with the same generous timeout.
        services.AddHttpClient(string.Empty, client => client.Timeout = TimeSpan.FromMinutes(10));
        services.Configure<AiOptions>(opts =>
        {
            opts.DefaultProvider = provider;
            opts.DefaultModel = model;
            opts.FeatureModels = new Dictionary<string, FeatureModelOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["LinguisticAnalysis"] = new FeatureModelOptions { Provider = provider, Model = model }
            };
        });
        services.AddSingleton<PromptFactory>();
        services.AddSingleton<IReadOnlyDictionary<string, IAiAnalysisProvider>>(sp =>
        {
            var c = sp.GetRequiredService<IConfiguration>();
            var opts = sp.GetRequiredService<IOptions<AiOptions>>();
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            // Register every provider the bake-off can drive (keyed case-insensitively). The router
            // selects by the LinguisticAnalysis feature provider. Cloud/OpenRouter providers throw at
            // call time if their API key is absent — that surfaces as an NA row, not a registration
            // failure. OpenRouter uses the generic OpenAiCompatibleProvider with provider name "OpenRouter".
            var dict = new Dictionary<string, IAiAnalysisProvider>(StringComparer.OrdinalIgnoreCase)
            {
                ["Ollama"] = new OllamaProvider(factory, c, opts),
                ["Anthropic"] = new AnthropicProvider(factory, c, opts),
                ["OpenAI"] = new OpenAiProvider(factory, c, opts),
                ["OpenRouter"] = new OpenAiCompatibleProvider("OpenRouter", factory, c, opts)
            };
            return dict;
        });
        services.AddSingleton<IAiRouter, AiRouter>();

        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IAiRouter>();
    }
}
