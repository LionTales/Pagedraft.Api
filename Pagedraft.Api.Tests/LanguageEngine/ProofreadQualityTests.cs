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
/// See TestData/README.md for the id-prefix classification convention (no schema Category field)
/// and, specifically, "How to add a character-agreement case" before adding a new agree-* entry.
///
/// PATH CHOICE — PromptFactory + IAiRouter (NOT UnifiedAnalysisService):
/// UnifiedAnalysisService.RunAsync cannot be cleanly constructed in a unit test — it requires
/// an AppDbContext, SfdtConversionService, AnalysisProgressTracker and IAnalysisContextService,
/// and it resolves its input text from a persisted Book/Chapter/Scene target rather than from a
/// raw string. So this test drives the same underlying machinery directly:
///   1. Call IAiRouter.CompleteAsync with an AiRequest built by BuildGoldRequest — see that method for
///      the two prompt shapes and why they differ per case.
///
///      PROMPT-SURFACE SCOPE LIMIT (verified 2026-08-02, correcting what this comment used to claim):
///      for a case with NO characterRegister the caller Instruction is left empty, so the router sends
///      the SHORT legacy pipeline instruction (PromptFactory.GetPrompt(Proofread, lang)) ALONE. That is
///      NOT what any production call site sends: the non-chunked path sends
///      GetAnalysisPrompt(AnalysisType.Proofread, ...) and the chunked path sends
///      BuildProofreadChunkPrompt(...), and in BOTH cases AiRouter then APPENDS the short pipeline
///      instruction, so production always sees [context preamble] + ProofreadHe/En (long) + short.
///      Numbers measured on register-less cases are therefore a measurement of the short-prompt surface
///      in isolation. Cases WITH a characterRegister ride the production long+short shape instead, so
///      the two subsets are not directly comparable to each other — report them separately.
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
    // which is gemma4:12b as of the p2-3 n=5 re-measurement (2026-07-29). It still CPU-spills partially
    // at ~8 GB VRAM (~30%/70% CPU/GPU), so the single-model local Fact is slow — this is expected.
    //
    // KEEP IN SYNC: this value must match Ai:FeatureModels:Proofread:Model in
    // Pagedraft.Api/appsettings.json (see "_comment_ProofreadModel" key there).
    // If appsettings changes, update this constant too — they are NOT read from the same source at BAKE-OFF
    // time (the harness wires its own in-memory config, deliberately, so a sweep can drive any model).
    // The duplication is no longer HAND-POLICED, though: HarnessConfigParityTests (at the assembly root,
    // where both standing test filters actually reach) binds the real appsettings.json and goes red if a
    // model swap touches only one side. That pin references this constant rather than restating its value,
    // which is why it is `internal` and not `private` — a pin that restates the value binds a look-alike.
    internal const string ProofreadModel = "gemma4:12b";

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
    // representative subset instead of the whole gold file. PROOFREAD_BAKEOFF_MAX_CASES: an additional numeric
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
    // gemma4:12b (see ProofreadModel above) is the production default. DictaLM-3.0-12B
    // (hf.co/dicta-il/DictaLM-3.0-Nemotron-12B-Instruct-GGUF:latest) is no longer the Proofread model but
    // is still the LineEdit model, so it stays in the shortlist as a comparator. DictaLM tag is :latest
    // (the locally pulled tag). 24B models are intentionally EXCLUDED because they won't fit in 8 GB VRAM.
    private static readonly string[] DefaultBakeoffModels =
    {
        "gemma4:12b",
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
    [Trait("Category", "LiveModel")]
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

        // The provider/model this run ACTUALLY used, resolved through the same parser CreateRouter uses
        // rather than restated. ReportAndGateTheStandingFloor keys its assert-vs-report decision off
        // these two locals, so if this Fact ever gains a model override the gate follows it instead of
        // silently continuing to compare a different model's numbers against g1's bar.
        var model = ProofreadModel;
        var (provider, _) = ParseCandidate(model);
        var router = CreateRouter(model);
        // GUARD OFF, not the class default: this Fact measures the MODEL and gates against bars
        // measured before the shape guard existed. Argued in full on GoldScoringDiffService.
        var diff = GoldScoringDiffService();

        var records = await ScoreModelAsync(router, diff, cases, perCaseOutput: true);

        // ONE model pass, reported per PROMPT SURFACE. The gold file mixes two surfaces whose numbers
        // are not comparable (see GoldPromptSurface), so a single blended aggregate cannot be read as a
        // model figure and cannot be reproduced by a later run against a differently-composed file.
        var split = GoldPromptSurfaces.Split(records);

        if (split.ShortOnlyCases > 0)
        {
            WriteAggregateBlock(
                $"Aggregate: {GoldPromptSurfaces.Describe(GoldPromptSurface.ShortPipelineOnly)}",
                split.ShortOnlyCases, split.ShortOnly);
        }

        if (split.ProductionCases > 0)
        {
            WriteAggregateBlock(
                $"Aggregate: {GoldPromptSurfaces.Describe(GoldPromptSurface.ProductionLongPlusShort)}",
                split.ProductionCases, split.Production);
        }

        // The THIRD surface (c3). No HebrewRegressionCase can ride it today - GoldPromptSurfaces.SurfaceOf
        // derives only the two single-shot surfaces from a gold row - so this block is unreachable from
        // this Fact. It is emitted anyway, unconditionally structured like its siblings, so that "every
        // aggregate states its surface" is true of the REPORT rather than of today's data: a chunked
        // record reaching this scorer later prints its own block instead of vanishing into ALL.
        if (split.ChunkedCases > 0)
        {
            WriteAggregateBlock(
                $"Aggregate: {GoldPromptSurfaces.Describe(GoldPromptSurface.ChunkedPerChunk)}",
                split.ChunkedCases, split.Chunked);
        }

        if (split.IsSingleSurface)
        {
            // Only one surface is populated, so the mixed block would be a duplicate of the block above
            // carrying a "mixed" label it does not deserve. Say why it is absent instead of printing it.
            _output.WriteLine("");
            _output.WriteLine($"(All {split.AllCases} scored cases ride ONE prompt surface, so there is no " +
                              "mixed-surface block: the aggregate above IS the whole run.)");
        }
        else
        {
            WriteAggregateBlock(
                "Aggregate: ALL cases, MIXED SURFACES - NOT a comparable figure (kept only for continuity " +
                "with older runs; read the two per-surface blocks above instead)",
                split.AllCases, split.All);
        }

        // THE STANDING FLOOR. Up to here this Fact is a reporting benchmark; from here it is also the
        // gold-surface half of ProofreadStandingFloor's gate (c3/be-c03). It still cannot fail for a
        // generic "model regression": it asserts ONLY when the run is on the provider/model the floor
        // was measured on AND on the gold file the bars were measured over, and reports otherwise.
        ReportAndGateTheStandingFloor(records, split, provider, model, goldFile);

        // Non-vacuity for everything above: the run scored the gold set at all.
        Assert.True(cases.Length > 0);
    }

    // ─── the standing floor: the gold-surface bars ───

    /// <summary>
    /// REPORT-AND-GATE THE GOLD-SURFACE HALF OF <see cref="ProofreadStandingFloor"/> (c3/be-c03).
    ///
    /// WHY IT IS HERE. The floor's scalar bars had no measuring consumer at all: <c>Metrics</c> and
    /// <c>EvaluateMetric</c> were read only by their own deterministic self-tests, while the floor's
    /// header claimed to be what Wave 2 changes are measured against and the successor plan's decision
    /// rule cited the <c>agree-preserve</c> bars by name. Every observation below is a plain aggregate
    /// over the per-case records this Fact ALREADY produced, so nothing extra is measured and no extra
    /// GPU time is spent - the bars were reachable all along, just unread.
    ///
    /// ASSERT-VS-REPORT, AND WHY THE CONDITION IS WHAT IT IS.
    ///  - MODEL. <c>MeasuredOnModel</c> is a historical literal, and a model swap VOIDS the floor rather
    ///    than regressing it. So the gate asserts only when the provider/model this run actually used
    ///    matches it, and reports every verdict otherwise. On today's code both constants are the same
    ///    value and a deterministic test pins them equal, so this half is currently always true - its
    ///    entire value is that on a swap the live gate goes quiet while
    ///    <c>TheFloor_NamesTheShippedProofreadModel_*</c> goes red to demand a re-measurement.
    ///  - GOLD FILE. This one is genuinely runtime-variable: <c>PROOFREAD_BAKEOFF_GOLD</c> can point
    ///    this Fact at <c>proofread-gold-en.json</c>, where NO case carries a character register, so
    ///    every <c>agree-*</c> subset is empty and <c>legacy93</c> is a different corpus entirely.
    ///  - THE BAKE-OFF IS UNTOUCHED. <c>ProofreadQuality_ModelBakeoff_ReportTable</c> is a separate
    ///    Fact and the only one that loops candidate models; nothing here can fire during a deliberate
    ///    cross-model measurement.
    ///
    /// AN ERRORED CASE VOIDS ITS SUBSET rather than folding into it - the same rule, and the same
    /// reason, as the chunked transport tripwire: <c>ScoreModelAsync</c> records a timed-out case as
    /// zero produced corrections, which is byte-identical to "the model declined to correct", so a
    /// subset containing one has not measured precision or recall at all.
    ///
    /// COMPILE-VERIFIED ONLY. This class is in the filter-EXCLUDED namespace and skips on Ollama
    /// reachability, so nothing here has been executed. What holds the line in CI is the deterministic
    /// half in <c>ProofreadStandingFloorTests</c>, including the owner partition that fails if a bar is
    /// ever added to the floor without a harness claiming it.
    /// </summary>
    private void ReportAndGateTheStandingFloor(
        IReadOnlyList<GoldCaseScore> records, SurfaceSplitScores split,
        string provider, string model, string goldFile)
    {
        const string namePrefix = "agree-name-";
        const string registerPrefix = "agree-register-";
        const string preservePrefix = "agree-preserve-";

        var onMeasuredModel =
            string.Equals(provider, ProofreadStandingFloor.MeasuredOnProvider, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(model, ProofreadStandingFloor.MeasuredOnModel, StringComparison.Ordinal);
        var onMeasuredGold = string.Equals(goldFile, DefaultGoldFile, StringComparison.OrdinalIgnoreCase);
        var gating = onMeasuredModel && onMeasuredGold;

        _output.WriteLine("");
        _output.WriteLine($"=== STANDING FLOOR, gold surfaces (measured {ProofreadStandingFloor.MeasuredOn} on " +
                          $"{ProofreadStandingFloor.MeasuredOnProvider} / {ProofreadStandingFloor.MeasuredOnModel}) ===");
        _output.WriteLine($"this run: {provider} / {model} over {goldFile}");
        if (!gating)
        {
            _output.WriteLine(
                "REPORT ONLY - the floor is not in force for this run" +
                (onMeasuredModel ? "" : $" (model differs from {ProofreadStandingFloor.MeasuredOnModel}: " +
                                        "a swap VOIDS the floor, it does not regress it - re-measure)") +
                (onMeasuredGold ? "" : $" (gold file differs from {DefaultGoldFile}: the bars are shares " +
                                       "and counts over THAT corpus and mean nothing over another)") +
                ". Verdicts below are still printed, because a number nobody prints is a number nobody checks.");
        }

        var failures = new List<string>();
        var evaluated = 0;

        // ── observations, one per wired bar. Every one is an aggregate over the records above. ──
        var observations = new Dictionary<string, double>(StringComparer.Ordinal);

        var name = Subset(records, namePrefix);
        var register = Subset(records, registerPrefix);
        var preserve = Subset(records, preservePrefix);

        if (Usable(name, namePrefix, failures) is ModelScore nameScore)
        {
            observations["agree-name.recall"] = nameScore.Recall;
            observations["agree-name.precision"] = nameScore.Precision;
            // SPURIOUS, by the scorer's own construction: it removes forbidden hits from the produced
            // pool first, then removes the expected matches, and calls whatever is left spurious. So
            // produced - matched - overreach is that leftover count exactly, not an approximation of it.
            observations["agree-name.spuriousEdits"] =
                nameScore.TotalProduced - nameScore.TotalMatched - nameScore.OverreachEdits;
        }

        if (Usable(register, registerPrefix, failures) is ModelScore registerScore)
            observations["agree-register.recall"] = registerScore.Recall;

        if (Usable(preserve, preservePrefix, failures) is ModelScore preserveScore)
        {
            // THE CASE RATE, not the edit count: the bar is "share of preservation cases producing >= 1
            // spurious edit". Deliberately NOT ModelScore.FalsePositiveRate, which counts cases that
            // produced ANY correction - a case whose only correction hit a forbidden span is an
            // overreach (its own bar, below) and must not also be booked as the punctuation tax.
            observations["agree-preserve.overCorrectionRate"] =
                preserve.Count(SpuriousEdits) / (double)preserve.Count;
            observations["agree-preserve.overreach"] = preserveScore.OverreachCaseHits;
            _output.WriteLine(
                $"    agree-preserve: {preserveScore.OverreachCaseHits} overreach case(s) of " +
                $"{preserveScore.OverreachCases} declaring a forbidden edit, over {preserve.Count} case(s)");
        }

        // legacy93 is a recall SHARE over a named corpus, so a corpus that changed size voids it even
        // though the bar's own number is untouched. Report the observation either way; refuse to
        // EVALUATE it against a bar it no longer describes.
        if (split.ShortOnlyCases != ProofreadStandingFloor.LegacyShortOnlyGoldCases)
        {
            var voided =
                $"BAR VOID legacy93.recall: measured over {split.ShortOnlyCases} register-less case(s), " +
                $"but the bar is a recall share over {ProofreadStandingFloor.LegacyShortOnlyGoldCases}. " +
                $"Observed recall {split.ShortOnly.Recall:P1} is not comparable to the 65.0% floor. " +
                "Re-measure and re-pin both, or restore the corpus.";
            _output.WriteLine(voided);
            if (gating) failures.Add(voided);
        }
        else
        {
            observations["legacy93.recall"] = split.ShortOnly.Recall;
        }

        // ── every bar this harness OWNS is reported; a missing observation is itself a finding ──
        foreach (var id in ProofreadStandingFloor.MetricEvaluators.GoldHarnessEvaluatedMetricIds)
        {
            var bar = ProofreadStandingFloor.Metric(id);
            if (!observations.TryGetValue(id, out var observed))
            {
                // Either a subset was unusable (already reported above with its cause) or a future
                // author added an id to the owner list without wiring an observation for it.
                _output.WriteLine($"  {id}: NO OBSERVATION this run - not evaluated.");
                continue;
            }

            var verdict = ProofreadStandingFloor.EvaluateMetric(bar, observed);
            evaluated++;
            var line =
                $"  {id} [{bar.Surface}] {bar.Subset}: observed {observed:F4} against " +
                $"{bar.Bound} {bar.Value:F4} {bar.Unit} -> {verdict}. {bar.Meaning}";
            _output.WriteLine(line);

            if (verdict == MetricVerdict.Regressed && gating) failures.Add("REGRESSED. " + line);
            if (verdict == MetricVerdict.ImprovedUpdateTheFloor)
                _output.WriteLine($"    ^ IMPROVED - the floor is now stale on {id}. Re-measure and re-pin " +
                                  "it, rather than leaving slack a later regression can hide inside.");
        }

        // ── the bars nothing can observe, named rather than omitted ──
        foreach (var id in ProofreadStandingFloor.MetricEvaluators.UnevaluatedMetricIds)
        {
            var bar = ProofreadStandingFloor.Metric(id);
            _output.WriteLine(
                $"  {id} [{bar.Surface}] {bar.Subset}: NOT EVALUATED - recorded figure " +
                $"({bar.Bound} {bar.Value:F4} {bar.Unit}), no automated evaluator exists. {bar.Meaning}");
        }

        // ── the punctuation split: subset totals are observable, its per-phenomenon rows are not ──
        foreach (var subset in new[] { namePrefix, registerPrefix, preservePrefix })
        {
            // The subset TOTAL, summed through PhenomenonEditsPerRun over the distinct phenomena the
            // split names for it. That total IS observable without a classifier (it is just the
            // subset's spurious-edit count), which is why it is reported while the rows are not.
            var recorded = ProofreadStandingFloor.PunctuationPhenomena
                .Where(p => string.Equals(p.Subset, subset, StringComparison.Ordinal))
                .Select(p => p.Phenomenon)
                .Distinct(StringComparer.Ordinal)
                .Sum(phenomenon => ProofreadStandingFloor.PhenomenonEditsPerRun(phenomenon, subset));
            var rows = Subset(records, subset);
            if (rows.Count == 0) continue;
            var observedSpurious = rows.Sum(SpuriousEditCount);
            var offendersObserved = rows.Where(SpuriousEdits).Select(r => r.Id)
                .OrderBy(s => s, StringComparer.Ordinal).ToArray();
            var offendersRecorded = ProofreadStandingFloor.OffendingGoldCaseIds(subset)
                .OrderBy(s => s, StringComparer.Ordinal).ToArray();
            _output.WriteLine(
                $"  punctuation split {subset}: observed {observedSpurious} spurious edit(s) this run " +
                $"against {recorded} recorded; offending rows observed [{string.Join(", ", offendersObserved)}] " +
                $"vs recorded [{string.Join(", ", offendersRecorded)}]. CHARACTERIZATION - the " +
                "per-phenomenon rows (gershayim-swap vs comma-insertion) have no classifier on this " +
                "surface and are NOT evaluated; see PunctuationPhenomena's remarks.");
        }

        Assert.True(failures.Count == 0,
            $"THE STANDING FLOOR DID NOT HOLD on the gold surfaces ({failures.Count} finding(s)). Each one " +
            "is a bar g1 measured on " + ProofreadStandingFloor.MeasuredOnProvider + " / " +
            ProofreadStandingFloor.MeasuredOnModel + " that this run no longer meets:\n\n  " +
            string.Join("\n\n  ", failures));

        // NON-VACUITY, and it is the assertion that matters most here: a gating run that evaluated
        // nothing must not read as a clean floor. Without this, an empty gold, a renamed id prefix or a
        // wholly errored pass would all produce "no failures" and look exactly like a green bar.
        if (gating)
        {
            Assert.True(evaluated > 0,
                "No gold-surface bar was evaluated against the standing floor, so 'no failures' above " +
                "means nothing. Either every agree-* subset was empty or errored, or the id prefixes the " +
                "bars name no longer match any gold row.");
            // The case this catches that `failures` cannot: an id sitting in the owner list with NO
            // observation wired for it. A void subset already produced a finding above, so reaching
            // here with failures empty and a short count means the harness CLAIMS a bar it never reads.
            Assert.True(evaluated == ProofreadStandingFloor.MetricEvaluators.GoldHarnessEvaluatedMetricIds.Count,
                $"Only {evaluated} of " +
                $"{ProofreadStandingFloor.MetricEvaluators.GoldHarnessEvaluatedMetricIds.Count} bars this " +
                "harness OWNS were evaluated, with nothing reported as void. Some bar is listed in " +
                "GoldHarnessEvaluatedMetricIds without an observation being computed for it above, so " +
                "the floor claims a consumer it does not have. A partial gate must not report as a whole one.");
        }

        // ── local helpers ──

        static IReadOnlyList<GoldCaseScore> Subset(IEnumerable<GoldCaseScore> all, string prefix) =>
            all.Where(r => r.Id.StartsWith(prefix, StringComparison.Ordinal)).ToArray();

        // produced - matched - overreach: see the agree-name.spuriousEdits comment above.
        static int SpuriousEditCount(GoldCaseScore r) => r.Produced - r.Matched - r.OverreachEdits;
        static bool SpuriousEdits(GoldCaseScore r) => SpuriousEditCount(r) > 0;

        // A subset is usable only if it is POPULATED and CLEAN. Empty means the bar's id prefix matches
        // nothing (a renamed corpus, or the English gold); errored means at least one case produced
        // nothing for transport reasons, which is indistinguishable from the model declining and so
        // voids precision and recall alike. Both are reported as findings rather than skipped quietly.
        ModelScore? Usable(IReadOnlyList<GoldCaseScore> subset, string prefix, List<string> into)
        {
            if (subset.Count == 0)
            {
                var msg = $"BARS VOID for '{prefix}': the gold file being scored ({goldFile}) has no case " +
                          "with that id prefix, so every bar over that subset is unmeasurable. On the " +
                          "English gold this is expected and the floor is not in force.";
                _output.WriteLine(msg);
                if (gating) into.Add(msg);
                return null;
            }

            var score = GoldPromptSurfaces.Aggregate(subset);
            if (score.Errors > 0)
            {
                var msg = $"BARS VOID for '{prefix}': {score.Errors} of {subset.Count} case(s) errored or " +
                          "timed out. A failed case is scored as zero produced corrections, which is " +
                          "byte-identical to 'the model declined to correct', so no precision or recall " +
                          "verdict may be drawn from this subset. Fix the transport and re-measure.";
                _output.WriteLine(msg);
                if (gating) into.Add(msg);
                return null;
            }

            return score;
        }
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
    [Trait("Category", "LiveModel")]
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

        // GUARD OFF, not the class default: a bake-off compares MODELS, so it must see each model's raw
        // output. Argued in full on GoldScoringDiffService.
        var diff = GoldScoringDiffService();

        _output.WriteLine($"=== Proofread model bake-off ({cases.Length} gold cases, {models.Length} models, default provider={defaultProvider}) ===");
        _output.WriteLine($"Model list source: {(Environment.GetEnvironmentVariable(BakeoffModelsEnvVar) is { Length: > 0 } ? BakeoffModelsEnvVar + " env var" : "built-in default shortlist")}");
        if (Environment.GetEnvironmentVariable(BakeoffCaseIdsEnvVar) is { Length: > 0 } ids)
            _output.WriteLine($"Case subset ({BakeoffCaseIdsEnvVar}): {ids}");
        var clean = cases.Count(c => c.ShouldHaveNoChanges == true);
        var overreach = cases.Count(c => (c.ForbiddenCorrections?.Length ?? 0) > 0);
        // Prompt-surface composition of the scored set, derived from the same condition BuildGoldRequest
        // branches on. The table columns aggregate BOTH surfaces (they are the mixed figure), so the
        // split has to be on the same line an operator reads the composition off.
        var shortOnlyCases = GoldPromptSurfaces.OnSurface(cases, GoldPromptSurface.ShortPipelineOnly).Length;
        var productionCases = cases.Length - shortOnlyCases;
        _output.WriteLine($"Gold composition: {clean} no-change, {overreach} overreach-guarded, {cases.Length - clean} with expected corrections; " +
                          $"prompt surfaces {shortOnlyCases} short-pipeline-only + {productionCases} production long+short " +
                          "(NOT comparable to each other - see TestData/README.md, \"The prompt-surface split\").");
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
            SurfaceSplitScores? split = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // Set BOTH Ai:DefaultModel and Ai:FeatureModels:Proofread so the router resolves to
                // this provider/model. Critically, DefaultModel == the model under test means
                // OllamaProvider's 404-retry-with-default cannot silently substitute a working model
                // for a missing one, so a missing model surfaces as an error (recorded NA) instead of
                // a false success. For cloud providers a missing API key throws and is recorded NA too.
                var router = CreateRouter(provider, model);
                // ONE pass per model; the per-surface aggregates come from partitioning its per-case
                // records, so ranking on a subset costs nothing extra in GPU time.
                var records = await ScoreModelAsync(router, diff, cases, perCaseOutput: false);
                split = GoldPromptSurfaces.Split(records);
            }
            catch (Exception ex)
            {
                // One model failing (not pulled, OOM, missing key, etc.) must NOT abort the bake-off.
                status = "NA: " + FirstLine(ex.Message);
            }
            sw.Stop();

            if (split is { } sp)
            {
                // Table columns keep reporting the WHOLE scored set (the mixed figure, unchanged), while
                // the row also carries the ranking corpus the Winner hint is computed on: the short-only
                // subset, the surface every prior model verdict for this file was measured on. When that
                // subset is empty (an id-subset run, or the English gold) the hint falls back to the whole
                // set, which is then a single surface anyway.
                var s = sp.All;
                var rank = shortOnlyCases > 0 ? sp.ShortOnly : sp.All;
                var cost = EstimateBakeoffCost(model, s.InputTokens, s.OutputTokens);
                rows.Add(new BakeoffRow(label, s.Precision, s.Recall, s.FalsePositiveRate,
                    sw.ElapsedMilliseconds, sw.ElapsedMilliseconds * 1.0 / cases.Length, status, Ok: true,
                    RankPrecision: rank.Precision, RankRecall: rank.Recall));
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
                    sw.ElapsedMilliseconds * 1.0 / cases.Length, status, Ok: false,
                    RankPrecision: 0, RankRecall: 0));
                _output.WriteLine(
                    $"{Truncate(label, modelCol).PadRight(modelCol)} " +
                    $"{"-",7} {"-",7} {"-",8} {"-",10} {"-",7} {"-",7} {sw.ElapsedMilliseconds,10} {"-",9} {"-",9}  {status}");
            }
        }

        // Winner hint (INFORMATIONAL only, not a pass/fail gate): highest recall among models that
        // produced any matches; ties broken by higher precision then lower latency.
        //
        // RANKED ON ONE SURFACE, NOT ON THE MIXED SET. The table's columns aggregate both prompt
        // surfaces, but a ranking read off a mixed corpus is not reproducible by a later run against a
        // differently-composed file, and every historical Proofread verdict for this gold was taken on
        // the SHORT-only surface. So the hint ranks on the short-only subset whenever it is populated,
        // and says so. When it is empty (an id-subset run selecting only register-carrying cases, or the
        // English gold, where no case carries a register) there is only one surface present and the hint
        // ranks on the whole scored set, again saying so.
        var rankCorpus = shortOnlyCases > 0
            ? $"{shortOnlyCases}-case short-pipeline-only subset"
            : $"{cases.Length}-case {(productionCases > 0 ? "production long+short" : "scored")} set";
        var winner = rows
            .Where(r => r.Ok && r.RankRecall > 0)
            .OrderByDescending(r => r.RankRecall)
            .ThenByDescending(r => r.RankPrecision)
            .ThenBy(r => r.TotalMs)
            .FirstOrDefault();

        _output.WriteLine("");
        if (winner is not null)
        {
            _output.WriteLine($"[informational] Winner hint (highest recall on the {rankCorpus}): {winner.Model} " +
                              $"- recall {winner.RankRecall.ToString("P0", CultureInfo.InvariantCulture)}, " +
                              $"precision {winner.RankPrecision.ToString("P0", CultureInfo.InvariantCulture)}, " +
                              $"{winner.MsPerCase:F0} ms/case. Those two figures are for the ranking corpus; the " +
                              "table's columns are the MIXED all-cases aggregate. Verify against the full table; not a gate.");
        }
        else
        {
            _output.WriteLine($"[informational] No model produced a non-zero recall on the {rankCorpus} " +
                              "(or all models were NA). No winner hint; inspect the table and ensure the models are pulled.");
        }

        // Reporting benchmark, not a quality gate — assert only that the run iterated the model list.
        Assert.True(models.Length > 0);
    }

    /// <summary>
    /// Build the <see cref="AiRequest"/> for one gold case. TWO SHAPES, chosen per case by whether the
    /// case declares a <c>characterRegister</c>:
    ///
    /// (1) NO register (every case authored before 2026-08-02, and every case that does not need one):
    /// <c>Instruction</c> is left NULL, exactly as before. AiRouter.CompleteAsync then resolves the
    /// legacy pipeline instruction from <c>PromptFactory.GetPrompt(Proofread, lang)</c> and sends it
    /// ALONE. This is byte-for-byte the historical harness behavior, so every number ever measured on
    /// those cases stays comparable. It is NOT, however, what production sends (see the class remarks).
    ///
    /// (2) WITH a register (the <c>agree-*</c> agreement class): the instruction is built by the
    /// PRODUCTION builder <c>PromptFactory.BuildProofreadChunkPrompt(language, characters, overlapPrefix:
    /// null)</c> — the same method <c>UnifiedAnalysisService.RunProofreadChunkedAsync</c> calls — so the
    /// <c>[CHARACTER_REGISTER]</c> block's byte format cannot drift from production, and the
    /// <c>ProofreadHe/En</c> body that TELLS the model what that block is for travels with it. Sending a
    /// register block WITHOUT that body would measure a guaranteed zero for a reason that has nothing to
    /// do with the model: the short pipeline instruction never mentions <c>[CHARACTER_REGISTER]</c>.
    /// The router appends the short pipeline instruction after it (Proofread is not in
    /// <c>ShouldUseUnifiedInstructionVerbatim</c>'s allowlist), which is the same long+short
    /// concatenation every real production Proofread call produces.
    ///
    /// Deterministically pinned (composed prompt string, no model) by ProofreadAgreementGoldTests.
    /// </summary>
    internal static AiRequest BuildGoldRequest(HebrewRegressionCase c)
    {
        string? instruction = null;
        if (c.CharacterRegister is { Length: > 0 } entries)
        {
            instruction = new PromptFactory().BuildProofreadChunkPrompt(
                c.Language,
                new CharacterRegister { Characters = entries },
                overlapPrefix: null);
        }

        return new AiRequest
        {
            InputText = c.Input,
            Instruction = instruction,
            TaskType = AiTaskType.Proofread,
            Language = c.Language,
            SourceId = c.Id
        };
    }

    /// <summary>
    /// Score one model over the gold set: drives the real proofread path (prompt + router + production
    /// diff) per case. Shared by the single-model scorer and the bake-off. When
    /// <paramref name="perCaseOutput"/> is true, prints a per-case line.
    ///
    /// RETURNS PER-CASE RECORDS, NOT A SINGLE AGGREGATE. The gold file holds cases on TWO prompt
    /// surfaces (see <see cref="GoldPromptSurface"/>), whose numbers are not comparable, so callers
    /// need an aggregate PER SURFACE as well as the mixed total. Emitting one
    /// <see cref="GoldCaseScore"/> per case and letting <c>GoldPromptSurfaces.Split</c> group them
    /// gives every subset from ONE model pass; scoring each subset in its own pass would double the
    /// cost of every GPU sweep for numbers that are already available.
    /// <c>GoldPromptSurfaces.Aggregate</c> over the full record set reproduces exactly the running
    /// totals this method used to return.
    /// </summary>
    private async Task<List<GoldCaseScore>> ScoreModelAsync(
        IAiRouter router, SuggestionDiffService diff, HebrewRegressionCase[] cases, bool perCaseOutput)
    {
        // One record per case. Overreach: a case may declare forbiddenCorrections, meaning-changing
        // rewrites the model must NOT make. GoldCaseScore.OverreachEdits counts every produced
        // correction that hit a forbidden edit; DeclaresForbidden / OverreachHit distinguish cases that
        // declared a forbidden edit from those that tripped at least one. This is the
        // precision/false-positive term that captures changing the RIGHT word to a WRONG
        // (meaning-changing) replacement, which the loose location-only matcher alone would otherwise
        // credit as a correct fix.
        var records = new List<GoldCaseScore>(cases.Length);

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
            var surface = GoldPromptSurfaces.SurfaceOf(c);

            int produced;
            int matched;
            var caseInputTokens = 0;   // counted even on a later failure, as the running totals used to
            var caseOutputTokens = 0;  // (the call already happened and the provider already billed it)
            var caseOverreach = 0;                    // produced corrections that hit a forbidden edit
            var missedDetail = new List<string>();   // expected corrections with no produced match
            var spuriousDetail = new List<string>();  // produced corrections matching no expected one
            var overreachDetail = new List<string>(); // produced corrections that are forbidden (overreach)
            try
            {
                var request = BuildGoldRequest(c);

                var response = await router.CompleteAsync(request);
                caseInputTokens = response.InputTokens ?? 0;
                caseOutputTokens = response.OutputTokens ?? 0;
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
                if (perCaseOutput)
                    _output.WriteLine($"{c.Id,-12} {expected.Length,8} {"ERR",8} {"-",8}  ERROR: {FirstLine(ex.Message)}");
                // Expected corrections still count toward recall denominator (a timed-out case is a
                // miss, not an exclusion), so record them but produce nothing. A no-change case that
                // produced nothing is NOT a false positive.
                records.Add(new GoldCaseScore(
                    c.Id, surface,
                    Expected: expected.Length,
                    Produced: 0,
                    Matched: 0,
                    NoChangeCase: expectsNoChanges,
                    NoChangeWithCorrection: false,
                    Errored: true,
                    InputTokens: caseInputTokens,
                    OutputTokens: caseOutputTokens,
                    OverreachEdits: 0,
                    DeclaresForbidden: forbidden.Length > 0,
                    OverreachHit: false));
                continue;
            }

            records.Add(new GoldCaseScore(
                c.Id, surface,
                Expected: expected.Length,
                Produced: produced,
                Matched: matched,
                NoChangeCase: expectsNoChanges,
                NoChangeWithCorrection: expectsNoChanges && produced > 0,
                Errored: false,
                InputTokens: caseInputTokens,
                OutputTokens: caseOutputTokens,
                OverreachEdits: caseOverreach,
                DeclaresForbidden: forbidden.Length > 0,
                OverreachHit: caseOverreach > 0));

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

        return records;
    }

    /// <summary>
    /// Print one aggregate block. Every metric line the single blended block used to carry is printed
    /// in EACH block, so a per-surface block is a full replacement for the old report rather than a
    /// summary of it. <paramref name="title"/> states which corpus the numbers came from.
    /// </summary>
    private void WriteAggregateBlock(string title, int caseCount, ModelScore score)
    {
        _output.WriteLine("");
        _output.WriteLine($"=== {title} ===");
        _output.WriteLine($"Cases:                 {caseCount}");
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
    internal static bool ShouldSkipForMissingOpenRouterKey(
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

    /// <summary>
    /// Aggregated per-model score; derived rates are computed lazily. Its semantics are unchanged by
    /// the prompt-surface split: <c>GoldPromptSurfaces.Aggregate</c> over the FULL per-case record set
    /// produces exactly what the scorer's running totals used to. It is <c>internal</c> (not private)
    /// only so the surface aggregation, and its deterministic tests, can build and read one.
    /// Every derived rate guards its denominator, so an EMPTY subset yields 0.0 / "n/a", never NaN.
    /// </summary>
    internal readonly record struct ModelScore(
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

    /// <summary>
    /// One printed table row. <c>Precision</c>/<c>Recall</c>/<c>FalsePositiveRate</c> are the MIXED
    /// all-cases figures the table columns show; <c>RankPrecision</c>/<c>RankRecall</c> are the same
    /// metrics restricted to the single-surface corpus the Winner hint ranks on (see the hint's
    /// comment). They are equal whenever the scored set rides one surface.
    /// </summary>
    private sealed record BakeoffRow(
        string Model, double Precision, double Recall, double FalsePositiveRate,
        long TotalMs, double MsPerCase, string Status, bool Ok,
        double RankPrecision, double RankRecall);

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
            // VERIFIED 2026-07-29 (p2-2) against the live OpenRouter catalog (GET /api/v1/models):
            // google/gemma-4-31b-it reports pricing.prompt 0.00000014 and pricing.completion 0.0000004
            // per token, i.e. $0.14 / $0.40 per 1M, with context_length 262144. The previous 0.20m input
            // rate was the 2026-06-20 UNVERIFIED placeholder and over-stated input cost by ~43%; the
            // $cost column is now a real figure rather than a magnitude check. Re-verify if the sweep
            // ever ranks models on cost — list prices move.
            ["google/gemma-4-31b-it"] = (0.14m, 0.40m)
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
    /// SPAN DISTINCTIVENESS INVARIANT. Both endpoint tests below are substring-tolerant
    /// (<c>origF.Contains(origP) || origP.Contains(origF)</c> for the original span, and the same
    /// containment test for the suggested one), so a forbidden entry can fire on an edit the model made
    /// SOMEWHERE ELSE in the same input whenever that other span's text happens to be a substring of
    /// (or to contain) the forbidden span. On a recall case that legitimate correction is then pulled
    /// out of the pool as overreach BEFORE recall matching runs, silently under-counting recall.
    ///
    /// THE INVARIANT a forbidden entry must satisfy, stated so it can be checked rather than believed:
    ///  (1) its <c>original</c> occurs in its own case's input at all (an absent span is inert: nothing
    ///      the model can produce will ever trip it);
    ///  (2) EVERY occurrence of it is a whole WORD - it must end at a non-letter and start a word,
    ///      allowing a Hebrew proclitic prefix (ו/ה/ב/כ/ל/מ/ש), which is why the pre-existing
    ///      <c>עתון</c> inside <c>בעתון</c> is legitimate and an arbitrary infix is not. "Every", not
    ///      "some": an occurrence sitting inside a longer word IS another word of the input containing
    ///      the span, and a legitimate correction of that word then aligns the origin test. The
    ///      proclitic exemption is a DELIBERATE HOLE in exactly that reasoning and not an oversight:
    ///      <c>בעתון</c> is a longer word containing <c>עתון</c>, so a legitimate correction of
    ///      <c>בעתון</c> does align the origin test. It is allowed because catching a model that
    ///      returns the whole clitic-carrying orthographic token is the REASON this matcher is
    ///      substring-tolerant in the first place; the hole is one clitic wide and is the price of that
    ///      capability, not a gap the data rule can close; and
    ///  (3) a forbidden with an EMPTY <c>suggested</c> forbids ANY edit at its span, so it has no second
    ///      endpoint to lock on and must additionally not CONTAIN a word the input uses elsewhere.
    ///
    /// DO NOT MAINTAIN THIS BY HAND AND DO NOT WRITE A POPULATION COUNT HERE - the previous version of
    /// this block named two forbidden cases when there were four, and was still saying two when there
    /// were 27. All three clauses are now enforced mechanically over EVERY forbidden entry of EVERY
    /// proofread gold file by <c>ProofreadAgreementGoldTests.ForbiddenSpans_*</c>, which run in the
    /// standing deterministic suite; <c>TestData/README.md</c> carries the same rule for the author.
    ///
    /// KNOWN RESIDUAL, deliberately not asserted. A MULTI-WORD forbidden span can contain a short word
    /// that also occurs elsewhere in the input (<c>agree-preserve-04</c>'s <c>מצאתי אותה</c> contains
    /// <c>את</c>, which that input also uses as a standalone object marker). That is a property of
    /// Hebrew orthography and cannot be authored away by choosing a different span, so it is bounded by
    /// the SECOND endpoint instead: the entry's non-empty <c>suggested</c> must also align with the
    /// produced replacement, and no plausible correction of <c>את</c> yields a string that relates to
    /// <c>מצאתיה</c>. Clause (3) is exactly the sub-case where that bound does not exist.
    ///
    /// WHEN TO CHANGE THE MATCHER INSTEAD. The data-side invariant holds only while produced correction
    /// spans stay TOKEN-scoped, which is what the measured runs show. A model that emits clause- or
    /// sentence-level rewrite spans would satisfy <c>origP.Contains(origF)</c> for almost any short
    /// forbidden span, and no authoring rule could prevent it; that is the trigger for tightening this
    /// method to token-boundary comparison. Know the cost first: it moves the overreach metric for the
    /// whole gold file and invalidates every overreach figure ever recorded against it, all of which
    /// were measured under the substring semantics here.
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

    /// <summary>
    /// The reachability skip-gate, shared with the other live harnesses in this namespace (g1's
    /// <c>ChunkedAgreementLiveTests</c>) so every live test skips on the SAME probe rather than on a
    /// look-alike that could disagree about whether the server is up.
    /// </summary>
    internal static async Task<bool> IsOllamaReachableAsync()
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

    /// <summary>
    /// THE SUGGESTION DIFF EVERY GOLD SCORE IS EXTRACTED WITH, and the ONE place its shape-guard
    /// posture is decided. Deliberately NOT <c>new SuggestionDiffService()</c>: that takes the
    /// <c>HebrewStyleOptions</c> CLASS DEFAULT, which ships
    /// <c>DropOrthographicallyImpossibleSuggestions = true</c>.
    ///
    /// GUARD OFF, ON PURPOSE - this harness measures the MODEL, not the product. Precision, recall,
    /// fp-rate, overreach and F0.5 are all read off what the model proposed, and a bake-off compares
    /// those numbers ACROSS models and against bars measured before the guard existed; a layer that
    /// deletes a model's most obviously wrong output would flatter whichever candidate produces the
    /// most mechanically impossible Hebrew, and it would do so invisibly.
    ///
    /// NOTE WHAT THIS IS NOT: <c>HebrewOrthographyShapeGuardTests.TheGuard_DropsNothingOnEitherGoldCorpusIdealOutput</c>
    /// and <c>.TheGuard_DropsNoDeclaredGoldCorrection</c> prove the guard is inert on the corpus's own
    /// inputs and IDEAL outputs, so switching it off here cannot change what the gold DECLARES. They
    /// say nothing about a live model's actual response, which is exactly the text this diff is run
    /// against and the only place the guard could fire. Posture pinned by
    /// <c>MeasurementHarnessGuardPostureTests</c>.
    /// </summary>
    internal static SuggestionDiffService GoldScoringDiffService() =>
        new(new Pagedraft.Api.Services.Analysis.Hebrew.HebrewStyleOptions
        {
            DropOrthographicallyImpossibleSuggestions = false
        });

    // ─── Router DI (mirrors HebrewRegressionTests.CreateLanguageEngine's Ai wiring) ───

    private static IAiRouter CreateRouter(string entry)
    {
        var (provider, model) = ParseCandidate(entry);
        return CreateRouter(provider, model);
    }

    /// <summary>
    /// The <c>Ai:ProviderSettings</c> map the bake-off DI installs. Factored out of
    /// <see cref="CreateRouter(string,string)"/> and made <c>internal</c> so
    /// <c>HarnessConfigParityTests</c> can pin it against the SHIPPED appsettings entries by resolving the
    /// very dictionary the harness uses, instead of restating its values in a second place (which would
    /// bind a look-alike and drift with the real one still unwatched).
    ///
    /// PRODUCTION TUNING PARITY (p2-3). Until this todo the harness wired NO ProviderSettings at
    /// all, so EVERY candidate — local and cloud — ran at the ProviderTuningOptions class defaults
    /// (Temperature 0.2, NumPredict/MaxTokens 2048, NumCtx 4096, RepeatPenalty 1.1) instead of the
    /// shipped Ollama_Proofread / OpenRouter_Proofread entries. That was SYMMETRIC (so p2-2's
    /// local-vs-cloud comparison stayed valid) but NOT production-faithful. Mirror the shipped
    /// appsettings values here, using each family's own output knob (NumPredict for Ollama,
    /// MaxTokens for the OpenAI-compatible/cloud families — ProviderTuningResolver.ResolveOutputTokens,
    /// p1-2).
    ///
    /// ONLY ONE RESOLVED VALUE MOVES: the output cap 2048 -> 4096. Shipped Ollama_Proofread is
    /// { Temperature 0.2, NumPredict 4096 } — it sets no NumCtx and no RepeatPenalty, so both bind
    /// the class defaults (4096 / 1.1) that the un-wired harness already used, and Temperature 0.2
    /// equals the class default too. num_predict is a STOP CONDITION, so raising it can only change
    /// a generation that would otherwise have been truncated at 2048 tokens. It matters for exactly
    /// one case class, which is why production fidelity was chosen: a repetition loop (the known
    /// Dicta failure mode behind Ollama_LineEdit's RepeatPenalty 1.3) runs to 4096 in production, so
    /// measuring at 2048 would UNDER-report that instability.
    /// </summary>
    internal static Dictionary<string, ProviderTuningOptions> BuildHarnessProviderSettings(string provider)
    {
        var proofreadTuning = new ProviderTuningOptions { Temperature = 0.2, NumPredict = 4096 };
        var settings = new Dictionary<string, ProviderTuningOptions>(StringComparer.OrdinalIgnoreCase)
        {
            ["Ollama_Proofread"] = proofreadTuning
        };
        if (!provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            // Shipped OpenRouter_Proofread is { Temperature 0.2, MaxTokens 4096, NumCtx 4096 } —
            // byte-identical to what is written here, so this is production fidelity, not a thumb on
            // the scale. RepeatPenalty is deliberately NOT mirrored: the OpenAI-compatible payload has
            // no such field (p1-3), so writing one would be dead config that reads as configured. Both
            // halves of that claim — the identity AND the deliberate omission — are pinned by
            // HarnessConfigParityTests; this comment no longer asserts them on its own authority.
            settings[$"{provider}_Proofread"] = new ProviderTuningOptions
            {
                Temperature = proofreadTuning.Temperature,
                MaxTokens = proofreadTuning.NumPredict,
                NumCtx = proofreadTuning.NumCtx
            };
        }
        return settings;
    }

    /// <summary>
    /// The live router wiring, shared with g1's <c>ChunkedAgreementLiveTests</c>. It is
    /// <c>internal</c> for the same reason <see cref="BuildHarnessProviderSettings"/> is: a second
    /// harness that re-wired its own DI would be measuring a DIFFERENT provider tuning than the gold
    /// numbers it is being compared against, and the divergence would be invisible. One wiring, one
    /// tuning, both scopes of the g1 session.
    /// </summary>
    internal static IAiRouter CreateRouter(string provider, string model)
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
        // _httpFactory.CreateClient("Ollama") and uses whatever Timeout this named client carries.
        // Production reads it from Ai:Providers:Ollama:TimeoutMinutes (Program.cs), but this harness builds
        // its own ServiceCollection and does NOT bind that key, so the timeout must be set here directly.
        // The default 2-minute timeout caused cold-start/CPU-spill models (DictaLM-12B, qwen2.5:14b) to hit
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
            opts.ProviderSettings = BuildHarnessProviderSettings(provider);
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
