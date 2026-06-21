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

    // OpenRouter base URL used when PROOFREAD_BAKEOFF_PROVIDER=OpenRouter. The OpenAiCompatibleProvider
    // reads it from Ai:Providers:OpenRouter:BaseUrl (we set it in the test DI). The API key comes from
    // config OR env AI_OPENROUTER_APIKEY — we set NO key here, so a cloud run with no env key is gated.
    // Mirrors LinguisticQualityTests.OpenRouterBaseUrl (the reference cloud bake-off implementation).
    private const string OpenRouterBaseUrl = "https://openrouter.ai/api/v1";

    // Single-model scorer uses the production default proofread model (Ai:FeatureModels:Proofread),
    // which is DictaLM-3.0-12B. At ~8 GB VRAM it CPU-spills on the dev box, so the single-model
    // local Fact is slow (minutes per case) — this is expected.
    //
    // KEEP IN SYNC: this value must match Ai:FeatureModels:Proofread:Model in
    // Pagedraft.Api/appsettings.json (see "_comment_ProofreadModel" key there).
    // If appsettings changes, update this constant too — they are NOT read from the same source at test time.
    private const string ProofreadModel = "hf.co/dicta-il/DictaLM-3.0-Nemotron-12B-Instruct-GGUF:latest";

    // Env var (comma-separated) to override the bake-off model list WITHOUT recompiling.
    private const string BakeoffModelsEnvVar = "PROOFREAD_BAKEOFF_MODELS";

    // Env var to select the provider for the bake-off (default Ollama). Set to "OpenRouter" to test
    // cloud models — that route gates on the OpenRouter API key being present. Mirrors
    // LinguisticQualityTests.BakeoffProviderEnvVar. Un-prefixed entries in PROOFREAD_BAKEOFF_MODELS use
    // this provider as their default (a per-entry "anthropic:"/"openai:"/"openrouter:" prefix still wins).
    private const string BakeoffProviderEnvVar = "PROOFREAD_BAKEOFF_PROVIDER";

    // ─── Cost control (cloud runs cost money — keep them small) ──────────────────────────────────────
    // PROOFREAD_BAKEOFF_CASE_IDS: comma-separated gold ids. When set, the bake-off scores ONLY those
    // cases (file order preserved, unknown ids logged + ignored) so a CLOUD sweep runs a small,
    // representative subset instead of all 90 cases. PROOFREAD_BAKEOFF_MAX_CASES: an additional numeric
    // cap applied AFTER the id filter (0/unset = unlimited). Both apply to the bake-off ONLY; the
    // single-model local Fact still runs the full gold. Subsetting is always logged (no silent caps).
    private const string BakeoffCaseIdsEnvVar = "PROOFREAD_BAKEOFF_CASE_IDS";
    private const string BakeoffMaxCasesEnvVar = "PROOFREAD_BAKEOFF_MAX_CASES";

    // Env var to select WHICH gold file the bake-off scores (default the Hebrew gold). Set to
    // "proofread-gold-en.json" to score the English gold through the SAME scorer/columns
    // (precision/recall/fp-rate/overreach/F0.5) — no scoring-logic fork. Resolved + logged at the
    // call site (no silent default), mirroring the other PROOFREAD_BAKEOFF_* knobs.
    private const string BakeoffGoldEnvVar = "PROOFREAD_BAKEOFF_GOLD";
    private const string DefaultGoldFile = "proofread-gold.json";

    // Default bake-off shortlist: models actually pulled on the RTX 4070 laptop (~8 GB VRAM).
    // hf.co/dicta-il/DictaLM-3.0-Nemotron-12B-Instruct-GGUF:latest (DictaLM-3.0-12B) is the production default (Ai:FeatureModels:Proofread in appsettings).
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

        var goldFile = ResolveGoldFileName();
        _output.WriteLine($"Loading proofread gold: {goldFile} ({BakeoffGoldEnvVar} env)");
        var cases = LoadProofreadGold(goldFile);
        if (cases.Length == 0)
        {
            _output.WriteLine($"No gold cases in {goldFile}.");
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
        _output.WriteLine($"Overreach edits:       {score.OverreachEdits} (meaning-changing rewrites the model must NOT make)");
        _output.WriteLine($"Overreach cases:       {score.OverreachCaseHits} of {score.OverreachCases} cases that declare a forbidden edit");
        _output.WriteLine($"Overreach rate:        {score.OverreachRate.ToString("P1", CultureInfo.InvariantCulture)}");

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
        var defaultProvider = ResolveBakeoffProvider();
        var models = ResolveBakeoffModels();
        var parsed = models.Select(m => ParseCandidate(m, defaultProvider)).ToArray();

        // Skip-gate by provider mix (preserves the prior per-candidate cloud support):
        //  - OpenRouter (cloud): needs an API key. Skip the whole sweep ONLY when EVERY candidate is
        //    OpenRouter and no key is present (nothing else to run). A MIXED list still runs its local Ollama
        //    / other-cloud rows and records NA for the unkeyed OpenRouter ones - same as a missing
        //    Anthropic/OpenAI key, which yields an NA row rather than blocking the sweep.
        //  - Ollama (local): needs a live server. If EVERY candidate is Ollama and Ollama is down there is
        //    nothing to run -> skip. If a cloud candidate is present, proceed even with Ollama down.
        if (ShouldSkipForMissingOpenRouterKey(parsed, OpenRouterKeyPresent()))
        {
            _output.WriteLine("SKIPPED: every candidate is OpenRouter but no API key found " +
                              "(env AI_OPENROUTER_APIKEY or config Ai:Providers:OpenRouter:ApiKey). " +
                              "Skipping the cloud bake-off so CI stays green.");
            return;
        }
        var allOllama = parsed.All(p => p.Provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase));
        if (allOllama && !await IsOllamaReachableAsync())
        {
            _output.WriteLine($"SKIPPED: Ollama not reachable at {OllamaBaseUrl} and all candidates are Ollama. " +
                              "This bake-off needs a live model; skipping so CI stays green.");
            return;
        }

        var goldFile = ResolveGoldFileName();
        _output.WriteLine($"Loading proofread gold: {goldFile} ({BakeoffGoldEnvVar} env)");
        var cases = LoadProofreadGold(goldFile);
        if (cases.Length == 0)
        {
            _output.WriteLine($"No gold cases in {goldFile}.");
            return;
        }
        // COST CONTROL: subset the gold for cloud runs (PROOFREAD_BAKEOFF_CASE_IDS / _MAX_CASES). Always
        // logged inside ApplyCaseSubset so a capped run never silently reads as full coverage.
        cases = ApplyCaseSubset(cases);
        if (cases.Length == 0)
        {
            _output.WriteLine("No gold cases left after applying PROOFREAD_BAKEOFF_CASE_IDS / " +
                              "PROOFREAD_BAKEOFF_MAX_CASES. Nothing to run.");
            return;
        }

        var diff = new SuggestionDiffService();

        _output.WriteLine($"=== Proofread model bake-off ({cases.Length} gold cases, {models.Length} models, default provider={defaultProvider}) ===");
        _output.WriteLine($"Model list source: {(Environment.GetEnvironmentVariable(BakeoffModelsEnvVar) is { Length: > 0 } ? BakeoffModelsEnvVar + " env var" : "built-in default shortlist")}");
        if (Environment.GetEnvironmentVariable(BakeoffCaseIdsEnvVar) is { Length: > 0 } ids)
            _output.WriteLine($"Case subset ({BakeoffCaseIdsEnvVar}): {ids}");
        var clean = cases.Count(c => c.ShouldHaveNoChanges == true);
        var overreach = cases.Count(c => (c.ForbiddenCorrections?.Length ?? 0) > 0);
        _output.WriteLine($"Gold composition: {clean} no-change, {overreach} overreach-guarded, {cases.Length - clean} with expected corrections.");
        _output.WriteLine("");

        const int modelCol = 64;
        // 'overreach' = forbidden-edit cases tripped / cases that declare a forbidden edit (the PRECISION
        // GATE signal: a meaning-changing rewrite of the right word). Lower is better; 0/N is ideal.
        _output.WriteLine($"{"model".PadRight(modelCol)} {"prec",7} {"recall",7} {"fp-rate",8} {"overreach",10} {"f0.5",7} {"errors",7} {"total ms",10} {"ms/case",9} {"$cost",9}  status");
        _output.WriteLine(new string('-', modelCol + 7 + 7 + 8 + 10 + 7 + 7 + 10 + 9 + 9 + 14));

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
                var overreachCol = s.OverreachCases > 0
                    ? $"{s.OverreachCaseHits}/{s.OverreachCases}"
                    : "-";
                _output.WriteLine(
                    $"{Truncate(label, modelCol).PadRight(modelCol)} " +
                    $"{s.PrecisionDisplay("P0"),7} " +
                    $"{s.Recall.ToString("P0", CultureInfo.InvariantCulture),7} " +
                    $"{s.FalsePositiveRate.ToString("P0", CultureInfo.InvariantCulture),8} " +
                    $"{overreachCol,10} " +
                    $"{s.F0Point5.ToString("F3", CultureInfo.InvariantCulture),7} " +
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
                    $"{"-",7} {"-",7} {"-",8} {"-",10} {"-",7} {"-",7} {sw.ElapsedMilliseconds,10} {"-",9} {"-",9}  {status}");
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
    /// Skip-gate logic (pure, no network): a missing OpenRouter key must skip the bake-off ONLY when every
    /// candidate is OpenRouter. A MIXED list keeps running its local Ollama / other-cloud rows (the unkeyed
    /// OpenRouter rows just record NA) - the regression the old <c>Any(...)</c> gate caused.
    /// </summary>
    [Fact]
    public void ModelBakeoff_MissingOpenRouterKey_SkipsOnlyWhenEveryCandidateIsOpenRouter()
    {
        (string Provider, string Model)[] mixed = { ("OpenRouter", "vendor/cloud-model"), ("Ollama", "gemma4:12b") };
        (string Provider, string Model)[] allOpenRouter = { ("OpenRouter", "vendor/a"), ("OpenRouter", "vendor/b") };
        (string Provider, string Model)[] allOllama = { ("Ollama", "gemma4:12b"), ("Ollama", "qwen2.5:14b") };

        // The bug: a mixed list with a missing key must NOT skip -> its Ollama rows still run.
        Assert.False(ShouldSkipForMissingOpenRouterKey(mixed, openRouterKeyPresent: false));
        // All-OpenRouter with no key => nothing to run => skip cleanly.
        Assert.True(ShouldSkipForMissingOpenRouterKey(allOpenRouter, openRouterKeyPresent: false));
        // All-OpenRouter WITH a key => run (do not skip).
        Assert.False(ShouldSkipForMissingOpenRouterKey(allOpenRouter, openRouterKeyPresent: true));
        // No OpenRouter candidate at all => the OpenRouter key gate never applies.
        Assert.False(ShouldSkipForMissingOpenRouterKey(allOllama, openRouterKeyPresent: false));
        // Empty list => nothing to skip on this gate.
        Assert.False(ShouldSkipForMissingOpenRouterKey(Array.Empty<(string, string)>(), openRouterKeyPresent: false));
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
        // Overreach: a case may declare forbiddenCorrections — meaning-changing rewrites the model must
        // NOT make. overreachEdits counts every produced correction that hit a forbidden edit;
        // overreachCases / overreachCaseHits count cases that declared a forbidden edit vs. those that
        // tripped at least one. This is the precision/false-positive term that captures changing the
        // RIGHT word to a WRONG (meaning-changing) replacement — which the loose location-only matcher
        // alone would otherwise credit as a correct fix.
        var overreachEdits = 0;
        var overreachCases = 0;
        var overreachCaseHits = 0;

        if (perCaseOutput)
        {
            _output.WriteLine("=== Proofread quality per-case ===");
            _output.WriteLine($"{"id",-12} {"expected",8} {"produced",8} {"matched",8}  note");
        }

        foreach (var c in cases)
        {
            var expected = c.ExpectedCorrections ?? Array.Empty<ProofreadCorrection>();
            var forbidden = c.ForbiddenCorrections ?? Array.Empty<ProofreadCorrection>();
            var expectsNoChanges = c.ShouldHaveNoChanges == true;
            if (forbidden.Length > 0)
                overreachCases++;

            int produced;
            int matched;
            var caseOverreach = 0;                    // produced corrections that hit a forbidden edit
            var missedDetail = new List<string>();   // expected corrections with no produced match
            var spuriousDetail = new List<string>();  // produced corrections matching no expected one
            var overreachDetail = new List<string>(); // produced corrections that are forbidden (overreach)
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

                // OVERREACH FIRST: pull out any produced correction that hits a forbidden edit BEFORE
                // matching expected. This is what makes the scorer penalize a meaning-changing rewrite of
                // the RIGHT word: case (b) עתון→עתונות produces the right ORIGINAL span (עתון) but a wrong,
                // meaning-changing replacement (עתונות, the press) instead of the ktiv fix (עיתון). The
                // loose location-only matcher (CorrectionsMatch: origP == origE) would otherwise credit
                // that as a correct fix. By removing forbidden edits from the pool first, the wrong edit
                // is NOT eligible to satisfy the expected ktiv correction (so recall is not falsely
                // inflated) AND it is counted as an overreach (a precision/false-positive signal). A
                // forbidden entry with an empty Suggested matches ANY replacement at that span ("must not
                // touch this span at all"), covering case (a)'s רגשית→<anything>.
                foreach (var f in forbidden)
                {
                    var fOrig = NormalizeForMatch(f.Original);
                    var fSug = NormalizeForMatch(f.Suggested);
                    var fIdx = producedUnmatched.FindIndex(s => ForbiddenMatch(
                        NormalizeForMatch(s.OriginalText), NormalizeForMatch(s.SuggestedText), fOrig, fSug));
                    if (fIdx >= 0)
                    {
                        var hit = producedUnmatched[fIdx];
                        caseOverreach++;
                        overreachDetail.Add($"[{hit.OriginalText} → {hit.SuggestedText}]");
                        producedUnmatched.RemoveAt(fIdx);
                    }
                }

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
            overreachEdits += caseOverreach;
            if (caseOverreach > 0)
                overreachCaseHits++;

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
                if (overreachDetail.Count > 0)
                    note += (note.Length > 0 ? " " : "") + "OVERREACH " + string.Join(",", overreachDetail);
                _output.WriteLine($"{c.Id,-12} {expected.Length,8} {produced,8} {matched,8}  {note}");
            }
        }

        return new ModelScore(totalExpected, totalProduced, totalMatched, noChangeCases, noChangeWithCorrection,
            errors, inputTokens, outputTokens, overreachEdits, overreachCases, overreachCaseHits);
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

    /// <summary>Resolve the bake-off default provider from the env var; defaults to Ollama.</summary>
    private static string ResolveBakeoffProvider()
    {
        var raw = Environment.GetEnvironmentVariable(BakeoffProviderEnvVar);
        return string.IsNullOrWhiteSpace(raw) ? "Ollama" : raw.Trim();
    }

    /// <summary>
    /// Resolve WHICH gold file the bake-off scores from <c>PROOFREAD_BAKEOFF_GOLD</c>; defaults to the
    /// Hebrew gold (<c>proofread-gold.json</c>). Set to <c>proofread-gold-en.json</c> to score the
    /// English gold through the same scorer. The resolved name is logged at the call site (no silent default).
    /// </summary>
    private static string ResolveGoldFileName()
    {
        var raw = Environment.GetEnvironmentVariable(BakeoffGoldEnvVar);
        return string.IsNullOrWhiteSpace(raw) ? DefaultGoldFile : raw.Trim();
    }

    /// <summary>True when an OpenRouter API key is reachable (env AI_OPENROUTER_APIKEY).</summary>
    private static bool OpenRouterKeyPresent()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AI_OPENROUTER_APIKEY"));

    /// <summary>
    /// Whether the WHOLE bake-off must be skipped for a missing OpenRouter key. Only when EVERY candidate is
    /// OpenRouter (so there is genuinely nothing else to run). A MIXED list is NOT skipped: its non-OpenRouter
    /// rows (local Ollama, other cloud) still run while the unkeyed OpenRouter rows record NA via the
    /// per-candidate try/catch - matching the pre-OpenRouter bake-off behavior. Using <c>Any</c> here (the
    /// old gate) wrongly skipped a mixed list's local rows.
    /// </summary>
    private static bool ShouldSkipForMissingOpenRouterKey(
        IReadOnlyList<(string Provider, string Model)> parsed, bool openRouterKeyPresent)
    {
        if (openRouterKeyPresent || parsed.Count == 0) return false;
        return parsed.All(p => p.Provider.Equals("OpenRouter", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Cost-control subset for the bake-off: when PROOFREAD_BAKEOFF_CASE_IDS is set, keep only those gold
    /// ids (file order preserved; unknown ids logged + ignored). Then, if PROOFREAD_BAKEOFF_MAX_CASES &gt; 0,
    /// cap the result to that many cases. Both are logged so a capped CLOUD run never silently reads as
    /// full coverage. With neither set, returns the full set unchanged.
    /// </summary>
    private HebrewRegressionCase[] ApplyCaseSubset(HebrewRegressionCase[] cases)
    {
        var selected = cases;

        var idsRaw = Environment.GetEnvironmentVariable(BakeoffCaseIdsEnvVar);
        if (!string.IsNullOrWhiteSpace(idsRaw))
        {
            var wanted = idsRaw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            selected = cases.Where(c => wanted.Contains(c.Id)).ToArray();
            var missing = wanted
                .Where(w => !cases.Any(c => string.Equals(c.Id, w, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            _output.WriteLine($"[subset] {BakeoffCaseIdsEnvVar} -> {selected.Length}/{cases.Length} cases selected by id." +
                              (missing.Count > 0 ? $" Unknown ids ignored: {string.Join(", ", missing)}." : ""));
        }

        var maxRaw = Environment.GetEnvironmentVariable(BakeoffMaxCasesEnvVar);
        if (int.TryParse(maxRaw, out var max) && max > 0 && selected.Length > max)
        {
            _output.WriteLine($"[subset] {BakeoffMaxCasesEnvVar}={max} caps {selected.Length} -> {max} cases " +
                              $"(cost control; the remaining {selected.Length - max} are NOT run).");
            selected = selected.Take(max).ToArray();
        }

        return selected;
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
        int Errors, int InputTokens, int OutputTokens,
        int OverreachEdits, int OverreachCases, int OverreachCaseHits)
    {
        public double Precision => TotalProduced > 0 ? (double)TotalMatched / TotalProduced : 0.0;
        public double Recall => TotalExpected > 0 ? (double)TotalMatched / TotalExpected : 0.0;
        public double FalsePositiveRate => NoChangeCases > 0 ? (double)NoChangeWithCorrection / NoChangeCases : 0.0;
        /// <summary>Fraction of forbidden-edit cases on which the model made the meaning-changing rewrite.</summary>
        public double OverreachRate => OverreachCases > 0 ? (double)OverreachCaseHits / OverreachCases : 0.0;
        // F-beta with beta=0.5 — weights precision 2x over recall; 0 when both are 0. This is the precision-gate ranking metric.
        public double F0Point5 => (0.25 * Precision + Recall) > 0 ? (1.25 * Precision * Recall) / (0.25 * Precision + Recall) : 0.0;
        /// <summary>Precision formatted for display; "n/a" when nothing was produced (rate is undefined).</summary>
        public string PrecisionDisplay(string format) =>
            TotalProduced > 0 ? Precision.ToString(format, CultureInfo.InvariantCulture) : "n/a";
    }

    private sealed record BakeoffRow(
        string Model, double Precision, double Recall, double FalsePositiveRate,
        long TotalMs, double MsPerCase, string Status, bool Ok);

    // ─── Cloud-model pricing (USD per 1M tokens, input/output) for the bake-off $cost column ───
    // Ollama (local) and unknown models have no entry -> cost prints blank/0. OpenRouter ids are keyed by
    // their full id (provider/model). The gemma-4-31b-it rate is an APPROXIMATE OpenRouter list price for
    // a sanity-check magnitude only (the subset run is ~13 short cases, so the true spend is sub-cent
    // regardless) — verify against the live OpenRouter pricing page before relying on the figure.
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
            ["gpt-5.4-nano"] = (0.20m, 1.25m),
            // TODO(2026-06-20): pin against the live OpenRouter pricing page before relying on $cost column.
            // UNVERIFIED placeholder magnitude only — real subset spend is sub-cent regardless of exact rate.
            ["google/gemma-4-31b-it"] = (0.20m, 0.40m)
        };

    /// <summary>Approx USD cost for a model's token totals; 0 when the model has no price entry (Ollama/unknown).</summary>
    private static decimal EstimateBakeoffCost(string model, int inputTokens, int outputTokens)
    {
        if (!ModelPricing.TryGetValue(model, out var price)) return 0m;
        return (inputTokens / 1_000_000m) * price.InputPerM + (outputTokens / 1_000_000m) * price.OutputPerM;
    }

    // ─── Match normalization: trim + collapse whitespace ───

    internal static string NormalizeForMatch(string? s)
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

    /// <summary>
    /// True when a produced correction (origP→sugP) is the forbidden/overreach edit (origF→sugF).
    /// A forbidden edit is identified by its ORIGINAL span: the model touched the span it must not have
    /// rewritten (or must only have ktiv-fixed, never meaning-changed). Span alignment is the same
    /// niqqud-/whitespace-tolerant origin test CorrectionsMatch uses (equal OR one contains the other).
    /// When the forbidden entry's SUGGESTED is non-empty, the produced replacement must ALSO line up with
    /// it (so only the specific meaning-changing rewrite trips, e.g. עתון→עתונות); when the forbidden
    /// SUGGESTED is empty, ANY produced replacement at that span trips it ("must not touch this span").
    /// </summary>
    /// <remarks>
    /// SPAN DISTINCTIVENESS INVARIANT: this method uses substring-tolerant matching —
    /// <c>(origF.Contains(origP) || origP.Contains(origF))</c> for the original span, and the same
    /// containment test for the suggested span. This is correct for today's two tiny single-token
    /// forbidden cases (overreach-ms-01 <c>רגשית</c>, overreach-ms-02 <c>עתון</c>→<c>עתונות</c>),
    /// but it is a latent risk: if a future forbidden case uses a short, non-distinctive span, a
    /// legitimate correction at a DIFFERENT span whose text happens to be a substring of (or contain)
    /// the forbidden span could be wrongly pulled out as overreach, under-counting recall.
    ///
    /// Therefore: forbidden-correction spans MUST be distinctive enough that no legitimate correction
    /// at another span is a substring of (or contains) them. If that invariant is ever broken — i.e.
    /// you add a forbidden entry whose <c>original</c> is a common Hebrew word that may appear in
    /// other corrections — tighten the matching to exact / token-boundary comparison instead of the
    /// current substring containment.
    /// </remarks>
    internal static bool ForbiddenMatch(string origP, string sugP, string origF, string sugF)
    {
        // Must be the same erroneous location.
        var originAligns = (origP.Length > 0 && origP == origF) ||
            (origP.Length > 0 && origF.Length > 0 &&
             (origF.Contains(origP, StringComparison.Ordinal) || origP.Contains(origF, StringComparison.Ordinal)));
        if (!originAligns)
            return false;

        // Empty forbidden suggested → forbid ANY edit at this span.
        if (sugF.Length == 0)
            return true;

        // Otherwise the produced replacement must be the specific forbidden (meaning-changing) one.
        return sugP.Length > 0 &&
            (sugP == sugF || sugF.Contains(sugP, StringComparison.Ordinal) || sugP.Contains(sugF, StringComparison.Ordinal));
    }

    // ─── Gold loading ───

    /// <summary>
    /// Load + deserialize a proofread gold file from <c>TestData/{fileName}</c>. Defaults to the Hebrew
    /// gold (<c>proofread-gold.json</c>) so the regression/quality callers are unchanged; the bake-off
    /// passes a name resolved from <c>PROOFREAD_BAKEOFF_GOLD</c> (e.g. <c>proofread-gold-en.json</c>).
    /// <c>internal static</c> so the deterministic English-gold smoke test can reuse this exact loader
    /// (same path + deserializer) instead of duplicating the JSON config.
    /// </summary>
    internal static HebrewRegressionCase[] LoadProofreadGold(string fileName = DefaultGoldFile)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", fileName);
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
        ["openrouter"] = "OpenRouter",
        ["ollama"] = "Ollama"
    };

    /// <summary>
    /// Split a bake-off candidate entry into (provider, model). An entry is only treated as
    /// provider-prefixed when it starts with a KNOWN provider prefix followed by a colon
    /// ("anthropic:", "openai:", "openrouter:", "ollama:"), in which case we split on the FIRST colon.
    /// Otherwise the whole entry is a model tag for <paramref name="defaultProvider"/> (default Ollama).
    /// A bare Ollama tag may itself contain a colon (e.g. "qwen3.5:9b"); an OpenRouter id uses slashes
    /// and may carry a variant suffix (e.g. "google/gemma-4-31b-it", "...:free") — both stay intact
    /// because neither leading token is a known provider prefix.
    /// </summary>
    private static (string Provider, string Model) ParseCandidate(string entry, string defaultProvider = "Ollama")
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
        return (defaultProvider, trimmed);
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
        // isn't pulled. Cloud providers read their API key from config OR env var (AI_ANTHROPIC_APIKEY /
        // AI_OPENAI_APIKEY / AI_OPENROUTER_APIKEY) — we deliberately set NO key here, so a cloud run with
        // no env key throws InvalidOperationException, which the bake-off records as an NA row. The
        // OpenRouter BaseUrl is wired so OpenAiCompatibleProvider can resolve its endpoint in the test DI.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:DefaultProvider"] = provider,
                ["Ai:Providers:Ollama:BaseUrl"] = OllamaBaseUrl,
                ["Ai:Providers:OpenRouter:BaseUrl"] = OpenRouterBaseUrl,
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
            // OpenRouter uses the generic OpenAiCompatibleProvider with provider name "OpenRouter"
            // (mirrors LinguisticQualityTests so the cloud bake-off path is identical across harnesses).
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
