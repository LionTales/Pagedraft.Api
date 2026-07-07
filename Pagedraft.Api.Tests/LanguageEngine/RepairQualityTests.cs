using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
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
/// Scores the value-only "repair" pass (the Analysis Output Repair layer, plan
/// analysis-output-repair-2026-07-03, todo p0-repair-gold) over each gold case in
/// <c>TestData/repair-gold.json</c>, per model. Measures whether the repair model removes leaked
/// English terms and garbled words from Hebrew analysis PROSE while preserving meaning and structure.
///
/// TEST INFRASTRUCTURE ONLY — this harness is NOT wired into production. It builds the frozen scorer
/// the later phases (p3-gate) run for real; it does not touch UnifiedAnalysisService or any prod code.
///
/// PATH FIDELITY: the repair call reuses the value-only approach proven in
/// OutputQualityDiagnostic.RepairAsync — it routes through the real IAiRouter (production appsettings
/// DI wiring), forcing the model under test via the LinguisticAnalysis verbatim-instruction task key so
/// the Hebrew value-only repair instruction is sent as-is under the analysis system frame, then applies
/// the production UnifiedAnalysisService.SanitizeResponse. Repair is VALUE-scoped (one prose string in,
/// one prose string out) — the diagnostic proved that feeding raw JSON to the repair model is
/// destructive (it translates keys / reformats JSON to markdown), so structure is held by code, never
/// by the model. gemma4:12b is the primary repair model; Dicta-3.0 is the comparison. No provider is
/// referenced directly.
///
/// METRICS (per model, aggregated over the gold set):
///   • latin-removed %   — of each case's expectedLatinRemoved terms, how many are gone from the output.
///   • structure-preserved % — embed the repaired value into a structured result, re-serialize with the
///                             pipeline's camelCase JsonOpts, re-deserialize: keys + enum fields must be
///                             byte-identical (MUST be 100% — value-only repair cannot corrupt structure).
///   • no-new-latin      — the repair introduced NO Latin run that was not already in the input.
///   • length-ratio      — |repaired| / |input| in [0.6, 1.6] (guards drop/blowup).
///   • must-preserve %   — Hebrew/English substrings that had to survive verbatim did survive.
///   • clean-control no-op — isCleanControl cases were left effectively unchanged.
///   • garble-fixed %    — (advisory) expectedGarbleFixed non-words are no longer present verbatim.
///   • LLM-judge meaning-preserved — (ADVISORY, not a gate) a judge model rates whether meaning survived.
///
/// SKIP-BY-DEFAULT + GPU: needs a live local Ollama. Probes the endpoint first and returns (passes) if
/// unreachable, so CI stays green — same gate as ProofreadQualityTests / LinguisticQualityTests /
/// OutputQualityDiagnostic. Env knobs (mirroring the proofread harness) keep a smoke run cheap:
///   REPAIR_BAKEOFF_MODELS  comma-separated model list (default: gemma4:12b, Dicta-3.0).
///   REPAIR_CASE_IDS        comma-separated gold ids to score (subset; unknown ids logged + ignored).
///   REPAIR_MAX_CASES       numeric cap applied after the id filter (0/unset = unlimited).
///   REPAIR_JUDGE           "off" to skip the advisory LLM-judge meaning line (default: on).
/// </summary>
public class RepairQualityTests
{
    private readonly ITestOutputHelper _output;

    public RepairQualityTests(ITestOutputHelper output) => _output = output;

    private const string OllamaBaseUrl = "http://localhost:11434";

    // Primary repair model + Hebrew-specialist comparison. KEEP IN SYNC with the diagnostic + the plan's
    // repair-model decision (gemma4:12b is the repair model; Dicta over-rewrites — kept only as a foil).
    private const string GemmaModel = "gemma4:12b";
    private const string DictaModel = "hf.co/dicta-il/DictaLM-3.0-Nemotron-12B-Instruct-GGUF:latest";

    private static readonly string[] DefaultModels = { GemmaModel, DictaModel };

    // Env knobs (mirror ProofreadQualityTests' subset/model knobs) — let a smoke run stay cheap.
    private const string ModelsEnvVar = "REPAIR_BAKEOFF_MODELS";
    private const string CaseIdsEnvVar = "REPAIR_CASE_IDS";
    private const string MaxCasesEnvVar = "REPAIR_MAX_CASES";
    private const string JudgeEnvVar = "REPAIR_JUDGE";

    // Length-ratio acceptance band (|repaired| / |input|).
    private const double LengthRatioMin = 0.6;
    private const double LengthRatioMax = 1.6;

    // Tighter band for the clean-control no-op check (a no-op should barely change length).
    private const double CleanNoOpRatioMin = 0.85;
    private const double CleanNoOpRatioMax = 1.15;

    // camelCase, matching the pipeline's JsonOpts (StructuredResults use [JsonPropertyName] camelCase).
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // The value-only Hebrew repair instruction, copied verbatim from OutputQualityDiagnostic.RepairAsync
    // (the prototype the plan promotes into AnalysisRepairService). Value-scoped: no JSON, no structure.
    private const string RepairInstruction =
        "אתה עורך לשוני מקצועי. לפניך טקסט ניתוח ספרותי בעברית שהופק על ידי מודל שפה ועלול להכיל " +
        "מילים או מונחים באנגלית, שגיאות כתיב או ניסוח לא תקין. משימתך: " +
        "1) החלף כל מילה או מונח שאינם בעברית במונח העברי הנכון והמקובל בשדה הספרות " +
        "(לדוגמה: narrator->מספר, tone->טון, foreshadowing->רמיזה מקדימה, imagery->דימויים, " +
        "mood->מצב רוח, tension->מתח, climax->שיא). " +
        "2) תקן שגיאות כתיב, דקדוק ותחביר ושפר את זרימת העברית. " +
        "3) שמור בדיוק על המשמעות, על התובנות ועל המבנה של הטקסט. אל תוסיף ואל תסיר תוכן או תובנות. " +
        "החזר אך ורק את הטקסט המתוקן בעברית, בלי הקדמה ובלי הסברים.";

    // Advisory LLM-judge instruction: preserve-or-not, one-word answer.
    private const string JudgeInstruction =
        "אתה שופט איכות. לפניך טקסט מקורי וטקסט מתוקן. קבע האם הטקסט המתוקן שומר על אותה משמעות, " +
        "אותן תובנות ואותן עובדות כמו המקור. שינויי ניסוח, תיקוני כתיב, והחלפת מונחים לועזיים במונחים " +
        "עבריים מקבילים הם תקינים ואינם פוגעים במשמעות. ענה במילה אחת בלבד: 'כן' אם המשמעות נשמרה, " +
        "'לא' אם המשמעות שונתה, אבדה או עוותה.";

    [Fact]
    public async Task RepairQuality_RunGoldCases_ReportScores()
    {
        if (!await IsOllamaReachableAsync())
        {
            _output.WriteLine($"SKIPPED: Ollama not reachable at {OllamaBaseUrl}. " +
                              "This repair-quality benchmark needs a live model; skipping so CI stays green.");
            return;
        }

        var cases = LoadRepairGold();
        if (cases.Length == 0)
        {
            _output.WriteLine("No gold cases in repair-gold.json.");
            return;
        }
        cases = ApplyCaseSubset(cases);
        if (cases.Length == 0)
        {
            _output.WriteLine($"No gold cases left after {CaseIdsEnvVar}/{MaxCasesEnvVar}. Nothing to run.");
            return;
        }

        var models = ResolveModels();
        var judgeEnabled = !string.Equals(
            Environment.GetEnvironmentVariable(JudgeEnvVar), "off", StringComparison.OrdinalIgnoreCase);

        var leaks = cases.Count(c => (c.ExpectedLatinRemoved?.Length ?? 0) > 0);
        var garbles = cases.Count(c => (c.ExpectedGarbleFixed?.Length ?? 0) > 0);
        var cleanControls = cases.Count(c => c.IsCleanControl);
        _output.WriteLine($"=== Repair quality ({cases.Length} gold cases, {models.Length} model(s)) ===");
        _output.WriteLine($"Gold composition: {leaks} latin-leak, {garbles} garble, {cleanControls} clean-control " +
                          $"(judge advisory: {(judgeEnabled ? "on" : "off")}).");
        _output.WriteLine($"Model list source: {(Environment.GetEnvironmentVariable(ModelsEnvVar) is { Length: > 0 } ? ModelsEnvVar + " env var" : "built-in default (gemma4:12b, Dicta-3.0)")}");
        _output.WriteLine("");

        // A single judge router (gemma4:12b) keeps the advisory meaning verdict consistent across the
        // models under test. Built once; reused for every judged case.
        var judgeRouter = judgeEnabled ? BuildRepairRouter(GemmaModel) : null;

        foreach (var model in models)
        {
            _output.WriteLine($"### MODEL: {model}");
            RepairScore score;
            try
            {
                var router = BuildRepairRouter(model);
                score = await ScoreModelAsync(router, judgeRouter, model, cases, perCaseOutput: true);
            }
            catch (Exception ex)
            {
                _output.WriteLine($"NA: {FirstLine(ex.Message)}");
                _output.WriteLine("");
                continue;
            }

            _output.WriteLine("");
            _output.WriteLine($"--- Aggregate ({model}) ---");
            _output.WriteLine($"Cases scored:           {score.Cases}");
            _output.WriteLine($"Errored:                {score.Errors}");
            _output.WriteLine($"Latin-removed:          {Pct(score.LatinRemoved, score.LatinExpected)} " +
                              $"({score.LatinRemoved}/{score.LatinExpected} expected terms gone)");
            _output.WriteLine($"Structure-preserved:    {Pct(score.StructurePreserved, score.Cases)} " +
                              $"({score.StructurePreserved}/{score.Cases}) [MUST be 100%]");
            _output.WriteLine($"No-new-latin:           {Pct(score.NoNewLatinCases, score.Cases)} " +
                              $"({score.NoNewLatinCases}/{score.Cases} cases clean; {score.NewLatinTokens} new latin token(s) total)");
            _output.WriteLine($"Length-ratio in bounds: {Pct(score.LengthInBounds, score.Cases)} " +
                              $"({score.LengthInBounds}/{score.Cases} within [{LengthRatioMin:0.0},{LengthRatioMax:0.0}])");
            _output.WriteLine($"Must-preserve survived: {Pct(score.MustPreserveSurvived, score.MustPreserveTotal)} " +
                              $"({score.MustPreserveSurvived}/{score.MustPreserveTotal} substrings)");
            _output.WriteLine($"Clean-control no-op:    {Pct(score.CleanNoOps, score.CleanControls)} " +
                              $"({score.CleanNoOps}/{score.CleanControls} controls left unchanged)");
            if (score.GarbleTotal > 0)
                _output.WriteLine($"Garble-fixed (advisory): {Pct(score.GarbleFixed, score.GarbleTotal)} " +
                                  $"({score.GarbleFixed}/{score.GarbleTotal} garbled terms corrected away)");
            if (judgeEnabled)
                _output.WriteLine($"LLM-judge meaning-preserved (ADVISORY, not a gate): " +
                                  $"{score.JudgePreserved}/{score.JudgeTotal} judged 'preserved'" +
                                  (score.JudgeErrors > 0 ? $" ({score.JudgeErrors} judge error(s))" : ""));
            _output.WriteLine("");
        }

        // Reporting benchmark, not a pass/fail gate on model quality — assert only that the run iterated
        // the gold set so the numbers surface without failing CI for model regressions. The p3-gate reads
        // these numbers and decides PASS/HALT (structure-preserved MUST be 100 there).
        Assert.True(cases.Length > 0);
    }

    /// <summary>
    /// Score one model over the gold set: run the value-only repair per case and aggregate the metrics.
    /// When <paramref name="perCaseOutput"/> is true, prints a per-case line. Per-case resilient: one
    /// failing case is counted as an error and does not abort the model.
    /// </summary>
    private async Task<RepairScore> ScoreModelAsync(
        IAiRouter router, IAiRouter? judgeRouter, string model, RepairGoldCase[] cases, bool perCaseOutput)
    {
        var s = new RepairScore();

        if (perCaseOutput)
        {
            _output.WriteLine($"{"id",-28} {"latin",7} {"struct",6} {"newLat",6} {"len",6} {"keep",6}  note");
        }

        foreach (var c in cases)
        {
            var input = c.Input ?? string.Empty;
            var expectedLatin = c.ExpectedLatinRemoved ?? Array.Empty<string>();
            var mustPreserve = c.MustPreserve ?? Array.Empty<string>();
            var garbles = c.ExpectedGarbleFixed ?? Array.Empty<string>();

            string repaired;
            try
            {
                repaired = await RepairFieldAsync(router, input);
                if (string.IsNullOrWhiteSpace(repaired)) repaired = input; // fail-safe: empty repair == no-op
            }
            catch (Exception ex)
            {
                s.Errors++;
                if (c.IsCleanControl) s.CleanControls++;
                if (perCaseOutput)
                    _output.WriteLine($"{c.Id,-28} {"ERR",7} {"-",6} {"-",6} {"-",6} {"-",6}  ERROR: {FirstLine(ex.Message)}");
                continue;
            }

            s.Cases++;

            // ── latin-removed ──
            var inputLatin = DistinctLatin(input);
            var repairedLatin = DistinctLatin(repaired);
            var latinRemovedHere = expectedLatin.Count(t => !repairedLatin.Contains(NormLatin(t)));
            s.LatinExpected += expectedLatin.Length;
            s.LatinRemoved += latinRemovedHere;

            // ── structure-preserved (embed value -> serialize -> deserialize; keys/enums intact) ──
            var structOk = StructurePreserved(repaired);
            if (structOk) s.StructurePreserved++;

            // ── no-new-latin ──
            var newLatin = repairedLatin.Where(t => !inputLatin.Contains(t)).ToList();
            if (newLatin.Count == 0) s.NoNewLatinCases++;
            s.NewLatinTokens += newLatin.Count;

            // ── length-ratio ──
            var ratio = (double)repaired.Length / Math.Max(1, input.Length);
            var lenOk = ratio >= LengthRatioMin && ratio <= LengthRatioMax;
            if (lenOk) s.LengthInBounds++;

            // ── must-preserve survival ──
            var survivedHere = mustPreserve.Count(m => repaired.Contains(m, StringComparison.Ordinal));
            s.MustPreserveTotal += mustPreserve.Length;
            s.MustPreserveSurvived += survivedHere;

            // ── clean-control no-op ──
            if (c.IsCleanControl)
            {
                s.CleanControls++;
                var noOp = survivedHere == mustPreserve.Length
                           && newLatin.Count == 0
                           && ratio >= CleanNoOpRatioMin && ratio <= CleanNoOpRatioMax;
                if (noOp) s.CleanNoOps++;
            }

            // ── garble-fixed (advisory) ──
            var garbleFixedHere = garbles.Count(g => !repaired.Contains(g, StringComparison.Ordinal));
            s.GarbleTotal += garbles.Length;
            s.GarbleFixed += garbleFixedHere;

            // ── LLM-judge meaning-preserved (advisory) — skip clean controls (nothing to change) ──
            bool? judged = null;
            if (judgeRouter != null && !c.IsCleanControl)
            {
                try
                {
                    judged = await JudgeMeaningPreservedAsync(judgeRouter, input, repaired);
                    s.JudgeTotal++;
                    if (judged == true) s.JudgePreserved++;
                }
                catch
                {
                    s.JudgeErrors++;
                }
            }

            if (perCaseOutput)
            {
                var latinCol = expectedLatin.Length > 0 ? $"{latinRemovedHere}/{expectedLatin.Length}" : "-";
                var keepCol = mustPreserve.Length > 0 ? $"{survivedHere}/{mustPreserve.Length}" : "-";
                var notes = new List<string>();
                if (!structOk) notes.Add("STRUCTURE BROKEN");
                if (newLatin.Count > 0) notes.Add("new-latin[" + string.Join(",", newLatin.Take(4)) + "]");
                if (!lenOk) notes.Add($"len-ratio {ratio:F2} OUT-OF-BOUNDS");
                if (mustPreserve.Length > 0 && survivedHere < mustPreserve.Length) notes.Add("LOST-CONTENT");
                if (c.IsCleanControl && (survivedHere < mustPreserve.Length || newLatin.Count > 0)) notes.Add("CLEAN-CONTROL DRIFT");
                if (garbles.Length > 0) notes.Add($"garble-fixed {garbleFixedHere}/{garbles.Length}");
                if (judged.HasValue) notes.Add("judge:" + (judged.Value ? "preserved" : "CHANGED"));
                _output.WriteLine($"{c.Id,-28} {latinCol,7} {(structOk ? "ok" : "BAD"),6} {newLatin.Count,6} {ratio,6:F2} {keepCol,6}  {string.Join(" ", notes)}");
            }
        }

        return s;
    }

    // ─── Repair call path (reuses OutputQualityDiagnostic.RepairAsync: verbatim value-only repair) ───
    private static async Task<string> RepairFieldAsync(IAiRouter router, string value)
    {
        var request = new AiRequest
        {
            InputText = value,
            Instruction = RepairInstruction,
            TaskType = AiTaskType.LinguisticAnalysis, // verbatim-instruction task key (not a real linguistic run)
            Language = "he-IL",
            SourceId = "repair",
            JsonMode = false
        };
        var response = await router.CompleteAsync(request);
        return UnifiedAnalysisService.SanitizeResponse(response.Content ?? string.Empty);
    }

    // ─── Advisory LLM-judge: is meaning preserved between input and repaired? ───
    private static async Task<bool?> JudgeMeaningPreservedAsync(IAiRouter judgeRouter, string input, string repaired)
    {
        var payload = $"מקור:\n{input}\n\nמתוקן:\n{repaired}";
        var request = new AiRequest
        {
            InputText = payload,
            Instruction = JudgeInstruction,
            TaskType = AiTaskType.LinguisticAnalysis, // verbatim-instruction task key
            Language = "he-IL",
            SourceId = "repair-judge",
            JsonMode = false
        };
        var response = await judgeRouter.CompleteAsync(request);
        var verdict = UnifiedAnalysisService.SanitizeResponse(response.Content ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(verdict)) return null;
        // "כן" = yes/preserved, "לא" = no/changed. Prefer the first occurrence; default to null if neither.
        var yesIdx = verdict.IndexOf("כן", StringComparison.Ordinal);
        var noIdx = verdict.IndexOf("לא", StringComparison.Ordinal);
        if (yesIdx < 0 && noIdx < 0) return null;
        if (yesIdx < 0) return false;
        if (noIdx < 0) return true;
        return yesIdx <= noIdx;
    }

    // ─── Metric helpers ───

    /// <summary>Distinct, lowercased Latin runs (>=2 letters) — the English-leak token set.</summary>
    private static HashSet<string> DistinctLatin(string text)
        => Regex.Matches(text ?? string.Empty, "[A-Za-z]{2,}")
                .Select(m => m.Value.ToLowerInvariant())
                .ToHashSet(StringComparer.Ordinal);

    private static string NormLatin(string term) => (term ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// Structure-preservation probe: place the repaired value into a representative structured result
    /// alongside sentinel structural fields (an enum + keys), round-trip through the camelCase JsonOpts,
    /// and confirm the keys + enum are byte-identical and the value survives as a single string field.
    /// Value-only repair returns a string, so this is 100% by construction — the check LOCKS IN that the
    /// repair cannot bleed into keys/enums (a raw-JSON repair, which the diagnostic proved destructive,
    /// would fail here). This is the harness analogue of the p1 scoping invariant.
    /// </summary>
    private static bool StructurePreserved(string repairedValue)
    {
        try
        {
            var probe = new LiteraryAnalysisResult
            {
                Tone = "SENTINEL_TONE",
                ToneDescription = "SENTINEL_TONE_DESC",
                NarrativeVoice = "SENTINEL_VOICE",
                Themes = new List<ThemeEntry>
                {
                    new() { Name = "SENTINEL_THEME", Description = "SENTINEL_THEME_DESC", Significance = "major" }
                },
                Summary = repairedValue
            };
            var json = JsonSerializer.Serialize(probe, JsonOptions);
            var round = JsonSerializer.Deserialize<LiteraryAnalysisResult>(json, JsonOptions);
            if (round == null) return false;

            // Keys present in the serialized JSON (camelCase, structure held by code).
            var keysOk = json.Contains("\"tone\"", StringComparison.Ordinal)
                         && json.Contains("\"toneDescription\"", StringComparison.Ordinal)
                         && json.Contains("\"narrativeVoice\"", StringComparison.Ordinal)
                         && json.Contains("\"themes\"", StringComparison.Ordinal)
                         && json.Contains("\"significance\"", StringComparison.Ordinal)
                         && json.Contains("\"summary\"", StringComparison.Ordinal);

            // Sentinel structural fields + enum byte-identical after round-trip; value intact.
            var fieldsOk = round.Tone == "SENTINEL_TONE"
                           && round.ToneDescription == "SENTINEL_TONE_DESC"
                           && round.NarrativeVoice == "SENTINEL_VOICE"
                           && round.Themes.Count == 1
                           && round.Themes[0].Name == "SENTINEL_THEME"
                           && round.Themes[0].Significance == "major"
                           && round.Summary == repairedValue;

            return keysOk && fieldsOk;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Pct(int num, int den)
        => den > 0
            ? ((double)num / den).ToString("P0", CultureInfo.InvariantCulture)
            : "n/a";

    // ─── Aggregated per-model score ───
    private sealed class RepairScore
    {
        public int Cases;
        public int Errors;
        public int LatinExpected;
        public int LatinRemoved;
        public int StructurePreserved;
        public int NoNewLatinCases;
        public int NewLatinTokens;
        public int LengthInBounds;
        public int MustPreserveTotal;
        public int MustPreserveSurvived;
        public int CleanControls;
        public int CleanNoOps;
        public int GarbleTotal;
        public int GarbleFixed;
        public int JudgeTotal;
        public int JudgePreserved;
        public int JudgeErrors;
    }

    // ─── Gold model + loading ───

    /// <summary>Gold case schema (matches repair-gold.json; the _README note has no id and is filtered).</summary>
    private sealed class RepairGoldCase
    {
        public string Id { get; set; } = "";
        public string Language { get; set; } = "he-IL";
        public string Input { get; set; } = "";
        public string[]? ExpectedLatinRemoved { get; set; }
        public string[]? MustPreserve { get; set; }
        public bool IsCleanControl { get; set; }

        /// <summary>Optional advisory list: garbled non-words the repair should correct away (no Latin).</summary>
        public string[]? ExpectedGarbleFixed { get; set; }

        public string? Notes { get; set; }
    }

    private static RepairGoldCase[] LoadRepairGold()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "repair-gold.json");
        if (!File.Exists(path))
            return Array.Empty<RepairGoldCase>();
        var json = File.ReadAllText(path);
        // The gold file's first array element is a {_README: ...} metadata note (no "id"); skip any
        // entry that lacks an id so iteration only sees real cases (mirrors LinguisticQualityTests).
        var raw = JsonSerializer.Deserialize<RepairGoldCase[]>(json, JsonOptions);
        if (raw == null) return Array.Empty<RepairGoldCase>();
        return raw.Where(c => !string.IsNullOrWhiteSpace(c.Id)).ToArray();
    }

    /// <summary>Resolve the model list from REPAIR_BAKEOFF_MODELS (comma-separated) or the default shortlist.</summary>
    private static string[] ResolveModels()
    {
        var raw = Environment.GetEnvironmentVariable(ModelsEnvVar);
        if (string.IsNullOrWhiteSpace(raw)) return DefaultModels;
        var models = raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return models.Length > 0 ? models : DefaultModels;
    }

    /// <summary>
    /// Cost-control subset (mirrors ProofreadQualityTests.ApplyCaseSubset): REPAIR_CASE_IDS keeps only
    /// those ids (file order preserved; unknown ids logged + ignored); REPAIR_MAX_CASES caps the result.
    /// Both are logged so a capped/smoke run never silently reads as full coverage.
    /// </summary>
    private RepairGoldCase[] ApplyCaseSubset(RepairGoldCase[] cases)
    {
        var selected = cases;

        var idsRaw = Environment.GetEnvironmentVariable(CaseIdsEnvVar);
        if (!string.IsNullOrWhiteSpace(idsRaw))
        {
            var wanted = idsRaw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            selected = cases.Where(c => wanted.Contains(c.Id)).ToArray();
            var missing = wanted
                .Where(w => !cases.Any(c => string.Equals(c.Id, w, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            _output.WriteLine($"[subset] {CaseIdsEnvVar} -> {selected.Length}/{cases.Length} cases selected by id." +
                              (missing.Count > 0 ? $" Unknown ids ignored: {string.Join(", ", missing)}." : ""));
        }

        var maxRaw = Environment.GetEnvironmentVariable(MaxCasesEnvVar);
        if (int.TryParse(maxRaw, out var max) && max > 0 && selected.Length > max)
        {
            _output.WriteLine($"[subset] {MaxCasesEnvVar}={max} caps {selected.Length} -> {max} cases.");
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

    // ─── Router DI (mirrors OutputQualityDiagnostic.RepairAsync/BuildRouter) ───
    // Routes the LinguisticAnalysis task (verbatim-instruction key) to `model` via Ollama, so the
    // value-only repair instruction is sent as-is. NO provider is referenced directly — selection is by
    // config, exactly like the production AiRouter path.
    private static IAiRouter BuildRepairRouter(string model)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:DefaultProvider"] = "Ollama",
                ["Ai:DefaultModel"] = model,
                ["Ai:Providers:Ollama:BaseUrl"] = OllamaBaseUrl,
                ["Ai:FeatureModels:LinguisticAnalysis:Provider"] = "Ollama",
                ["Ai:FeatureModels:LinguisticAnalysis:Model"] = model,
                ["Ai:ProviderSettings:Ollama_LinguisticAnalysis:NumCtx"] = "16384",
                ["Ai:ProviderSettings:Ollama_LinguisticAnalysis:NumPredict"] = "2048",
                ["Ai:ProviderSettings:Ollama_LinguisticAnalysis:Temperature"] = "0.2",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
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

    // ─── Ollama reachability probe (skip-gate) — mirrors the other gold harnesses ───

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
}
