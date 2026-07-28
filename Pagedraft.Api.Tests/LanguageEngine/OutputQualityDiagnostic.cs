using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;
using Xunit.Abstractions;

namespace Pagedraft.Api.Tests.LanguageEngine;

/// <summary>
/// DIAGNOSTIC (not a pass/fail gate). Runs the REAL chapter-level analysis tasks over a real Hebrew
/// passage using PRODUCTION routing (loads the API's appsettings.json), captures the raw + sanitized
/// output the user actually sees, scans each output for Latin/English leakage, and then prototypes a
/// second-pass "repair" layer (a self-critique/cleanup LLM pass) to measure whether it removes English
/// terms and improves fluency WITHOUT destroying content.
///
/// PATH FIDELITY: each task builds the exact AiRequest that UnifiedAnalysisService.RunWithInputAsync
/// builds (Instruction = PromptFactory.GetAnalysisPrompt(type, lang); TaskType = AnalysisTaskMapping
/// .ToAiTaskType(type); JsonMode only for LineEdit/LinguisticAnalysis) and sends it through the real
/// IAiRouter, then applies UnifiedAnalysisService.SanitizeResponse — identical to production. Because
/// routing comes from the real appsettings.json, each task hits the same model production uses
/// (reported per task via response.Model).
///
/// OPT-IN LIVE DIAGNOSTICS (excluded from a normal suite): the three live measurement [Fact]s below
/// (DumpRealAnalysisOutputs_AndPrototypeRepairPass, MeasureDynamicTermRepair_LocalVsCloud,
/// MeasureLegitimateTermPreservation_LocalVsCloud) drive multi-minute local-GPU (and optionally cloud)
/// harnesses. They are gated TWO ways so a bare local `dotnet test` (even with Ollama running) does
/// NOT launch them:
///   1) each carries [Trait("Category", "LiveDiagnostic")], so `--filter "Category!=LiveDiagnostic"`
///      excludes them by category (independent of fragile name-substring matching);
///   2) each opts in via the PAGEDRAFT_RUN_LIVE_DIAGNOSTICS env var, checked BEFORE the Ollama probe so
///      a bare run short-circuits in milliseconds. Unset (or not 1/true) => logs a skip and returns.
/// Run them on demand with the env var set, e.g.:
///   PAGEDRAFT_RUN_LIVE_DIAGNOSTICS=1 dotnet test \
///     --filter "FullyQualifiedName~MeasureDynamicTermRepair_LocalVsCloud"
/// This is how the e-plan's d5/d6/e4/e5 measurement runs must invoke them.
///
/// GPU: needs a live local Ollama. Even when opted in it also probes the endpoint first and returns
/// (passes) if unreachable, so CI stays green. When it runs it drives the 8-12 GB local models
/// sequentially. Output goes to ITestOutputHelper and a UTF-8 markdown file (env DIAG_OUT_DIR or temp).
/// </summary>
public class OutputQualityDiagnostic
{
    private readonly ITestOutputHelper _output;

    public OutputQualityDiagnostic(ITestOutputHelper output) => _output = output;

    private const string OllamaBaseUrl = "http://localhost:11434";
    private static readonly PromptFactory _promptFactory = new();

    // Candidate models for the repair prototype: the Hebrew specialist and the general structured model.
    private const string DictaModel = "hf.co/dicta-il/DictaLM-3.0-Nemotron-12B-Instruct-GGUF:latest";
    private const string GemmaModel = "gemma4:12b";

    // OPT-IN GATE for the three live GPU/cloud measurement diagnostics in this file. They drive
    // multi-minute local-GPU (and optionally cloud) harnesses, so a bare `dotnet test` (even on a dev
    // box with Ollama up) must NOT launch them; only the parent-driven measurement workflow opts in.
    // Two independent, complementary guards:
    //   1) [Trait("Category", "LiveDiagnostic")] on each method, so `--filter "Category!=LiveDiagnostic"`
    //      excludes them by category, independent of fragile name-substring matching (the documented
    //      GPU-filter trap where `~DynamicTermRepair` substring-matched the live method).
    //   2) this env-var opt-in, checked BEFORE the Ollama-reachability probe so a bare run short-circuits
    //      in milliseconds without touching the network.
    // Set PAGEDRAFT_RUN_LIVE_DIAGNOSTICS=1 (or true) to run them on demand.
    private const string RunLiveDiagnosticsEnvVar = "PAGEDRAFT_RUN_LIVE_DIAGNOSTICS";

    // Returns true (and logs a skip message) when the live-diagnostic opt-in env var is NOT set to 1/true.
    // Callers early-return on true, mirroring the existing IsOllamaReachableAsync early-return style
    // (dependency-free; this project does not reference SkippableFact).
    private bool LiveDiagnosticsOptedOut()
    {
        var v = Environment.GetEnvironmentVariable(RunLiveDiagnosticsEnvVar);
        var optedIn = string.Equals(v, "1", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        if (optedIn) return false;
        _output.WriteLine($"SKIP: live diagnostic (set {RunLiveDiagnosticsEnvVar}=1 to run this live diagnostic).");
        return true;
    }

    [Fact]
    [Trait("Category", "LiveDiagnostic")]
    public async Task DumpRealAnalysisOutputs_AndPrototypeRepairPass()
    {
        if (LiveDiagnosticsOptedOut()) return;

        if (!await IsOllamaReachableAsync())
        {
            _output.WriteLine("SKIP: Ollama not reachable at " + OllamaBaseUrl);
            return;
        }

        var report = new StringBuilder();
        void Emit(string s) { _output.WriteLine(s); report.AppendLine(s); }

        Emit("# PageDraft analysis-output diagnostic");
        Emit("");

        // DIAG_INPUTS (';'-separated ABSOLUTE Hebrew-passage file paths) drives a multi-passage sweep; unset
        // = the single default docs/test-text.txt (exactly the pre-sweep behavior). Passages are the OUTER
        // loop and analysis types the INNER loop, so each task's model routing (hence the loaded Ollama
        // model) is not churned just because a new passage started and stays warm across the whole sweep.
        var passages = LoadPassages(Emit, out var usingDiagInputs);
        Emit(usingDiagInputs
            ? $"Inputs: {passages.Count} passage(s) from DIAG_INPUTS (outer loop = passages, inner = analysis types; models stay warm across passages)"
            : "Input: single default passage `docs/test-text.txt`");
        foreach (var (path, text) in passages)
            Emit($"- `{Path.GetFileName(path)}` ({text.Length} chars, ~{WordCount(text)} words)");
        Emit("");

        var appSettingsPath = FindUpward(Path.Combine("Pagedraft.Api", "appsettings.json"));
        Assert.True(File.Exists(appSettingsPath), "appsettings.json not found at " + appSettingsPath);
        var prodRouter = BuildRouter(new ConfigurationBuilder().AddJsonFile(appSettingsPath).Build());
        Emit($"Routing loaded from: `{appSettingsPath}`");
        Emit("");

        // Resolve each task's router ONCE and reuse it for EVERY passage: a DIAG_MODELS override router is
        // built a single time and the per-task model never changes just because a new passage started, so
        // the outer-passage / inner-type sweep keeps each model warm instead of reloading it per passage.
        var routerCache = new Dictionary<AnalysisType, (IAiRouter router, string note)>();
        (IAiRouter router, string note) GetRouterFor(AnalysisType type)
        {
            if (!routerCache.TryGetValue(type, out var entry))
            {
                var r = ResolveRouterForTask(appSettingsPath, prodRouter, type, out var note);
                entry = (r, note);
                routerCache[type] = entry;
            }
            return entry;
        }

        var tasks = new[]
        {
            AnalysisType.Summarization,
            AnalysisType.LiteraryAnalysis,
            AnalysisType.LinguisticAnalysis,
            AnalysisType.Proofread,
            AnalysisType.LineEdit,
        };

        // A representative Hebrew reader-question for QA (production feeds "chapter summaries + question" as
        // InputText; here the passage stands in for the summaries). Only QAResult.answer is CONTENT prose.
        const string qaQuestion = "מהו הקונפליקט המרכזי בקטע, וכיצד הוא בא לידי ביטוי בדמות הראשית?";

        // Summarization/LiteraryAnalysis outputs feed the Part 2 repair prototype. Cleared at the start of
        // each passage so, after the sweep, it holds the LAST passage's live outputs (identical to today
        // for a single passage). Part 2 runs ONCE after the sweep on these + the controlled scrambled probe.
        var captured = new Dictionary<AnalysisType, string>();

        // ── OUTER LOOP: one full analysis-type set per passage (models stay warm across passages) ──
        for (int pi = 0; pi < passages.Count; pi++)
        {
            var (passagePath, passageText) = passages[pi];
            captured.Clear();

            Emit("---");
            Emit("");
            Emit($"## PASSAGE {pi + 1}/{passages.Count}: `{Path.GetFileName(passagePath)}` ({passageText.Length} chars, ~{WordCount(passageText)} words)");
            Emit("");

            // ── Part 1: real chapter-level analysis tasks, production routing ──
            Emit("### Part 1 — real task outputs (production routing)");
            Emit("");

            foreach (var t in tasks)
            {
                Emit($"#### TASK: {t}");
                try
                {
                    // Per-task model routing: prodRouter (appsettings FeatureModels) unless DIAG_MODELS
                    // overrides this task; `routeNote` records which was used so f4 can tell tiers apart.
                    var (router, routeNote) = GetRouterFor(t);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var (model, raw, clean) = await RunTaskAsync(router, t, passageText, "he-IL");
                    sw.Stop();
                    captured[t] = clean;
                    Emit($"model: `{model}` ({routeNote})  |  {sw.Elapsed.TotalSeconds:F0}s  |  raw {raw.Length} chars -> sanitized {clean.Length} chars");
                    Emit($"latin-leak scan: {ScanForLatin(clean)}");
                    Emit("");
                    Emit("```");
                    Emit(clean.Trim());
                    Emit("```");
                }
                catch (Exception ex)
                {
                    Emit($"ERROR: {ex.GetType().Name}: {FirstLine(ex.Message)}");
                }
                Emit("");
            }

            // ── Part 1b: book-level + whole-book review + QA outputs, CONTENT-vs-STRUCTURAL leak split ──
            // These tasks return STRUCTURED JSON whose property KEYS and label/enum VALUES are English by
            // SCHEMA (defined by the prompt itself), so a naive whole-output Latin scan (Part 1) would
            // over-count "leaks". Each capture here parses the JSON and scans ONLY the free-form PROSE
            // values (the real leak surface) as CONTENT, reporting schema English separately as STRUCTURAL.
            Emit("### Part 1b — book-level + book-review + QA outputs (CONTENT-vs-STRUCTURAL leak split)");
            Emit("");
            Emit("CONTENT = English that leaked into Hebrew PROSE values (the leak metric f4 tracks).");
            Emit("STRUCTURAL = English that is legitimately part of the JSON SCHEMA (property keys + prompt-defined");
            Emit("enum values like role/status/verdict/confidence): expected, NOT a leak.");
            Emit("");

            // BookOverview — CONTENT: summary only. STRUCTURAL: genre/subGenre/targetAudience/
            // languageRegister (short schema-ish labels) + numeric literatureLevel/estimatedReadingTime + keys.
            await CaptureAndSplitAsync(AnalysisType.BookOverview, passageText, raw =>
                TryParse<BookOverviewResult>(raw, out var r) && r != null
                    ? (true, new[] { r.Summary })
                    : (false, Array.Empty<string>()));

            // CharacterAnalysis — CONTENT: character description/arc, relationship prose, summary. STRUCTURAL:
            // character/relationship NAMES (proper nouns, may legitimately be Latin) + role enum + keys.
            // NULL-GUARD (mirrors RepairableFields.For(CharacterAnalysisResult)): `characters`/`relationships`
            // are `= new()` but System.Text.Json OVERWRITES them with null on `"characters": null` /
            // `"relationships": null`; `?? Empty` keeps the extractor from NRE-ing and aborting the sweep.
            await CaptureAndSplitAsync(AnalysisType.CharacterAnalysis, passageText, raw =>
                TryParse<CharacterAnalysisResult>(raw, out var r) && r != null
                    ? (true, (r.Characters ?? Enumerable.Empty<CharacterEntry>())
                            .SelectMany(c => new[] { c.Description, c.Arc })
                            .Concat((r.Relationships ?? Enumerable.Empty<CharacterRelationship>())
                                .Select(rel => rel.Relationship))
                            .Append(r.Summary).ToArray())
                    : (false, Array.Empty<string>()));

            // StoryAnalysis — CONTENT: plotStructure prose (setup/rising/climax/falling/resolution), pacing,
            // conflict descriptions, summary. STRUCTURAL: conflict type + status enums + keys.
            await CaptureAndSplitAsync(AnalysisType.StoryAnalysis, passageText, raw =>
            {
                if (!TryParse<StoryAnalysisResult>(raw, out var r) || r == null)
                    return (false, Array.Empty<string>());
                // NULL-GUARD (mirrors RepairableFields.For(StoryAnalysisResult)): `plotStructure` and
                // `conflicts` are `= new()` but System.Text.Json OVERWRITES them with null on
                // `"plotStructure": null` / `"conflicts": null`; fall back to an empty PlotStructure /
                // empty conflicts so a null does not NRE and abort the sweep.
                var plot = r.PlotStructure ?? new PlotStructure();
                var content = new[]
                    {
                        plot.Setup, plot.RisingAction, plot.Climax,
                        plot.FallingAction, plot.Resolution, r.Pacing, r.Summary
                    }.Concat((r.Conflicts ?? Enumerable.Empty<ConflictEntry>()).Select(c => c.Description)).ToArray();
                return (true, content);
            });

            // QA — CONTENT: answer only. STRUCTURAL: keys (answer/citations/chapterNumber/chapterTitle/
            // relevantExcerpt/confidence) + the confidence enum. QA runs OUTSIDE JsonMode and its body often
            // breaks strict JSON (citation excerpts carry unescaped quotes), so it uses a dedicated tolerant
            // extractor (CaptureQaAsync -> TryExtractQaAnswer) that recovers the `answer` prose or, failing
            // that, emits an explicit QA-PARSE-FALLBACK marker rather than mis-scanning the whole output.
            await CaptureQaAsync(passageText + "\n\nשאלה: " + qaQuestion);

            // BookReview — driven the way BookReviewService issues its combined-dimension call (see
            // RunBookReviewCombinedAsync). CONTENT: findings[].rationale + suggestedAction prose. STRUCTURAL:
            // dimension/verdict enums + severity int + evidence excerpts (quotes) + keys.
            Emit("#### TASK: BookReview (combined-dimension call)");
            try
            {
                var (brRouter, brNote) = GetRouterFor(AnalysisType.BookReview);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (model, raw, clean) = await RunBookReviewCombinedAsync(brRouter, passageText, "he-IL");
                sw.Stop();
                Emit($"model: `{model}` ({brNote})  |  {sw.Elapsed.TotalSeconds:F0}s  |  raw {raw.Length} chars -> sanitized {clean.Length} chars");
                var (parsed, content) = TryParse<BookReviewResult>(raw, out var review) && review?.Findings != null
                    ? (true, (IReadOnlyList<string>)review.Findings
                        .SelectMany(f => new[] { f.Rationale, f.SuggestedAction ?? string.Empty }).ToArray())
                    : (false, Array.Empty<string>());
                EmitContentStructuralSplit(raw, clean, parsed, content, Emit);
                Emit("```");
                Emit(clean.Trim());
                Emit("```");
            }
            catch (Exception ex)
            {
                Emit($"ERROR: {ex.GetType().Name}: {FirstLine(ex.Message)}");
            }
            Emit("");
        }

        // Local capture-and-split for the RunTaskAsync-routed structured tasks (BookOverview /
        // CharacterAnalysis / StoryAnalysis). Shares the per-task memoized routing (GetRouterFor) + the
        // CONTENT/STRUCTURAL split. Declared at method scope and invoked once per passage in the loop above.
        async Task CaptureAndSplitAsync(
            AnalysisType type, string taskInput,
            Func<string, (bool parsed, IReadOnlyList<string> content)> extractContent)
        {
            Emit($"#### TASK: {type}");
            try
            {
                var (router, routeNote) = GetRouterFor(type);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (model, raw, clean) = await RunTaskAsync(router, type, taskInput, "he-IL");
                sw.Stop();
                Emit($"model: `{model}` ({routeNote})  |  {sw.Elapsed.TotalSeconds:F0}s  |  raw {raw.Length} chars -> sanitized {clean.Length} chars");
                var (parsed, content) = extractContent(raw);
                EmitContentStructuralSplit(raw, clean, parsed, content, Emit);
                Emit("```");
                Emit(clean.Trim());
                Emit("```");
            }
            catch (Exception ex)
            {
                Emit($"ERROR: {ex.GetType().Name}: {FirstLine(ex.Message)}");
            }
            Emit("");
        }

        // QA-specific capture: same routing/timing/sanitize as CaptureAndSplitAsync, but pulls the `answer`
        // prose with TryExtractQaAnswer (structured parse first, then a targeted "answer"-field regex that
        // survives a malformed body). On success it reports a real CONTENT-vs-STRUCTURAL split; on failure it
        // emits QA-PARSE-FALLBACK + a one-line reason so the sweep reader knows QA was NOT cleanly split
        // (rather than silently letting structural terms inflate the CONTENT count).
        async Task CaptureQaAsync(string qaInput)
        {
            Emit("#### TASK: QA");
            try
            {
                var (router, routeNote) = GetRouterFor(AnalysisType.QA);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (model, raw, clean) = await RunTaskAsync(router, AnalysisType.QA, qaInput, "he-IL");
                sw.Stop();
                Emit($"model: `{model}` ({routeNote})  |  {sw.Elapsed.TotalSeconds:F0}s  |  raw {raw.Length} chars -> sanitized {clean.Length} chars");
                if (TryExtractQaAnswer(raw, out var answer, out var qaRoute))
                {
                    Emit($"QA-EXTRACT: {qaRoute}");
                    EmitContentStructuralSplit(raw, clean, true, new[] { answer }, Emit);
                }
                else
                {
                    Emit($"QA-PARSE-FALLBACK: {qaRoute}; whole-output scan (structural terms may inflate this): {ScanForLatin(clean)}");
                }
                Emit("```");
                Emit(clean.Trim());
                Emit("```");
            }
            catch (Exception ex)
            {
                Emit($"ERROR: {ex.GetType().Name}: {FirstLine(ex.Message)}");
            }
            Emit("");
        }

        // ── Part 2: second-pass repair prototype ──
        Emit("## Part 2 — repair-pass prototype (self-critique / cleanup layer)");
        Emit("");
        if (passages.Count > 1)
        {
            Emit($"NOTE: the live-output repair inputs below come from the LAST swept passage " +
                 $"(`{Path.GetFileName(passages[^1].path)}`); the controlled scrambled probe is passage-independent.");
            Emit("");
        }
        Emit("Repair instruction asks the model to (1) replace any non-Hebrew term with its correct Hebrew");
        Emit("equivalent, (2) fix spelling/grammar/fluency, (3) preserve meaning, insights and structure.");
        Emit("");

        // A CONTROLLED scrambled probe guarantees a measurable English->Hebrew cleanup signal even if the
        // live run happens to be clean; the live summarization output is repaired too for realism.
        const string scrambledProbe =
            "הקטע כתוב בגוף ראשון, וה-narrator הוא הדמות הראשית. הטון הוא tense ומלא suspense. " +
            "יש כאן foreshadowing חזק, והסופר משתמש ב-imagery של טבע כדי לבנות את ה-mood. " +
            "הקצב מהיר וה-tension עולה בהדרגה עד ל-climax של הקרב.";

        var repairInputs = new List<(string label, string text)>
        {
            ("controlled-scrambled-probe", scrambledProbe),
        };
        if (captured.TryGetValue(AnalysisType.Summarization, out var summ) && !string.IsNullOrWhiteSpace(summ))
            repairInputs.Add(("live-summarization-output", summ.Trim()));
        if (captured.TryGetValue(AnalysisType.LiteraryAnalysis, out var lit) && !string.IsNullOrWhiteSpace(lit))
            repairInputs.Add(("live-literary-output", lit.Trim()));

        foreach (var (label, text) in repairInputs)
        {
            Emit($"### REPAIR INPUT: {label}");
            Emit($"latin-leak scan (before): {ScanForLatin(text)}");
            Emit("```");
            Emit(text);
            Emit("```");
            Emit("");
            foreach (var rm in new[] { GemmaModel, DictaModel })
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var repaired = await RepairAsync(rm, text);
                    sw.Stop();
                    Emit($"#### repaired by `{rm}`  ({sw.Elapsed.TotalSeconds:F0}s)");
                    Emit($"latin-leak scan (after): {ScanForLatin(repaired)}");
                    Emit("```");
                    Emit(repaired.Trim());
                    Emit("```");
                }
                catch (Exception ex)
                {
                    Emit($"#### repaired by `{rm}`: ERROR {ex.GetType().Name}: {FirstLine(ex.Message)}");
                }
                Emit("");
            }
        }

        // ── Persist the report ──
        var outDir = Environment.GetEnvironmentVariable("DIAG_OUT_DIR");
        if (string.IsNullOrWhiteSpace(outDir)) outDir = Path.GetTempPath();
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, "analysis-diagnostic-report.md");
        File.WriteAllText(outPath, report.ToString(), new UTF8Encoding(false));
        _output.WriteLine("REPORT WRITTEN: " + outPath);
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════
    // d5 CLEANING (RECALL) GATE — MeasureDynamicTermRepair_LocalVsCloud
    //
    // Drives the SHIPPED DynamicTermRepairService (d1 detector + d2 classifier + d3 span-scoped IAiRouter
    // TermRepair call) over a deterministic LEAK SET of Hebrew literary prose (PreservationFixtureBooks.LeakCases
    // — 10 values, each leaking ONE lowercase English abstract noun the glossary cannot reach).
    //
    // TWO ARMS (be-c08 — THE reason this method changed):
    //   • ARM A — ENTITY-FREE. Exactly what e4/d5 measured (its recorded table is labelled "entity-free"). The
    //     CONTROL.
    //   • ARM B — WITH THE REAL PER-BOOK ENTITY SET, from the REAL BookEntityProvider over a REAL DbContext,
    //     for an ADVERSARIAL book (PreservationFixtureBooks.AdversarialBookId): a Hebrew manuscript whose ONE
    //     English epigraph line shows `Confusion` / `Nostalgia` / `Tension` / `Catharsis` CAPITALIZED,
    //     mid-sentence, once each — so the provider HARVESTS all four as MANUSCRIPT-tier entities. THE
    //     PRODUCTION PATH.
    //
    // WHAT ARM B EXISTS TO CATCH. The entity lever is a LEAVE lever: anything in the set is spared. be-c04
    // measured, on the real 80-chapter manuscript fixture, that with CASE-INSENSITIVE membership that single
    // epigraph flipped 3 of the 10 leaks (confusion / nostalgia / catharsis) from REPAIR to LEAVE — a 30%
    // recall regression bought with one sentence. be-c04's fix was to match MANUSCRIPT-harvested tokens
    // CASE-SENSITIVELY (BookEntitySet) while stored DECLARED names stay case-insensitive: a leak is lowercase
    // by construction, a name's manuscript evidence is capitalized by construction. THAT FIX HAS NEVER RUN
    // AGAINST THE REAL LEAK SET THROUGH THE REAL MODEL. Arm B is that run.
    //
    // THE GATE: ARM B's cleaned% must MATCH ARM A's (~100%). If ARM B cleans FEWER leaks, the entity lever is
    // sparing real leaks and be-c08 HALTs. The harness computes that comparison and the per-case diff itself —
    // the parent does not eyeball two tables.
    //
    // Per (value, arm, tier) it MEASURES from the OUTPUT (RULE 0 — not from the service internals):
    //   • CLEANED?  — re-run the d1 detector on the repaired value; the Latin leak run must be gone.
    //   • OVER-REWRITE? — the change must be CONFINED to the classified REPAIR span(s); anything else changing
    //     is an over-rewrite. The bar is 0. (A value the entity set GATED has zero repair spans, so ANY change
    //     to it is an over-rewrite — which is correct: nothing was allowed to change.)
    //   • MODEL CALLS — the count of REPAIR-classified runs (the service makes one marked-span call per run).
    //     ZERO means the value never reached the model — for a LEAK that is the regression signal.
    //   • LATENCY   — the model latency the service reports.
    // Also a small FIELD-VALUE-SCOPE contrast (whole value handed to the model, no span marking), to show the
    // Stage-2 blast radius that span-scope avoids.
    //
    // TIERS: LOCAL (Ollama|gemma4:12b) is the measured tier — the user scoped be-c08 to LOCAL. The CLOUD tier
    // (OpenRouter|google/gemma-4-31b-it) stays in the table but SKIPS CLEANLY when no OpenRouter key is
    // configured (checked BEFORE any call, so it costs nothing and never fails the run).
    // Report -> DIAG_OUT_DIR/d5-termrepair-measure.md (RULE 0 artifact the parent inspects).
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════
    [Fact]
    [Trait("Category", "LiveDiagnostic")]
    public async Task MeasureDynamicTermRepair_LocalVsCloud()
    {
        if (LiveDiagnosticsOptedOut()) return;
        if (!await IsOllamaReachableAsync())
        {
            _output.WriteLine("SKIP: Ollama not reachable at " + OllamaBaseUrl);
            return;
        }

        var appSettingsPath = FindUpward(Path.Combine("Pagedraft.Api", "appsettings.json"));
        Assert.True(File.Exists(appSettingsPath), "appsettings.json not found at " + appSettingsPath);

        var report = new StringBuilder();
        void Emit(string s) { _output.WriteLine(s); report.AppendLine(s); }

        Emit("# d5 — Dynamic TermRepair CLEANING (recall) measure: entity-free (ARM A) vs the REAL per-book entity set (ARM B)");
        Emit("");
        Emit($"Run: {DateTime.Now:yyyy-MM-dd HH:mm}  |  routing base: `{appSettingsPath}`");
        Emit("Instrument: `OutputQualityDiagnostic.MeasureDynamicTermRepair_LocalVsCloud` driving the shipped");
        Emit("`DynamicTermRepairService.RepairValueAsync` (d1 detect → d2 classify → d3 span-scoped TermRepair).");
        Emit("");

        // ── MEASUREMENT SET (shared with the deterministic BookEntityFixtureSeedTests) ────────────────────
        // q1 appends the SYNOPSIS-shaped leak values (the same single-Latin-run contract, at multi-paragraph
        // synopsis LENGTH) so the cleaning/recall side of the gate is measured for that shape too.
        //
        // SCOPING, DERIVED FROM THE SHIPPED CONFIG (be-c01, re-derived after f2). The GATED CORPUS - the values
        // the d5 aggregate and the d5 CLEANING VERDICT are computed over - comes from `ShippedRepairCorpus`,
        // which reads the real `Ai:AnalysisRepair:PerType` map out of appsettings.json through the SAME
        // `AnalysisRepairGate.Evaluate` predicate the production call sites consult. It is NOT a hardcoded
        // split. be-c01 originally excluded the SYNOPSIS values because `Synopsis` was not a repaired type;
        // `f2` then ENABLED it in both appsettings files, and the old positional literal (`i < shippedSeedCount`)
        // left this instrument's ship/HALT decision blind to a type the product DOES repair. Deriving
        // membership means the next enable or rollback moves the corpus by itself, at both call sites at once
        // (pinned deterministically by `ShippedRepairCorpusTests`).
        // The FIXTURE still says which SUBSET a value belongs to, by LABEL IDENTITY rather than position. The
        // corpus and its scope come from ONE shared builder that `ShippedRepairCorpusTests` also calls, so the
        // deterministic pin binds THIS scope rather than a look-alike rebuilt beside it.
        // Index lists over `seeds` (and therefore over every array parallel to it: repairRunsA/B, outcomes).
        var (seeds, scope) = ShippedRepairCorpus.D5CleaningCorpus();
        var gatedSeedIdx = scope.Gated;
        var proseSeedIdx = scope.AnalysisProse;
        var synopsisSeedIdx = scope.Synopsis;
        bool IsGatedSeed(int i) => scope.IsGated(i);
        Emit($"Measurement set: {seeds.Count} Hebrew prose values, {proseSeedIdx.Count} ANALYSIS-PROSE "
             + $"(2 known real leaks + {proseSeedIdx.Count - 2} seeded out-of-glossary) plus "
             + $"{synopsisSeedIdx.Count} q1 SYNOPSIS-shaped (multi-paragraph editorial prose), "
             + "each leaking exactly one Latin run.");
        Emit($"**SCOPE OF THE VERDICT: the d5 aggregate and the d5 CLEANING VERDICT below are computed over the "
             + $"{gatedSeedIdx.Count} values of the GATED CORPUS. {scope.CorpusSentence}** Both subsets are also "
             + "broken out separately below, so the pre-f2 figures of record stay quotable off this report.");
        Emit("");

        // ── THE ADVERSARIAL BOOK + ITS REAL ENTITY SET (be-c08) ───────────────────────────────────────────
        // The e4 d5 table was labelled "entity-free": the ENTITY lever — the one thing that can make the gate
        // SPARE a real leak — was never on the measured path. It is now, and through the REAL provider, not a
        // hand-built stand-in.
        using var fixtureBooks = await PreservationFixtureBooks.CreateAsync();
        var adversarialEntities = await fixtureBooks.AdversarialBookEntitiesAsync();
        var advSet = adversarialEntities as BookEntitySet;

        Emit("## The adversarial book (ARM B's entity source)");
        Emit("");
        Emit("A Hebrew-native book (`Language=he`) seeded into a REAL `AppDbContext`, read back through the REAL");
        Emit("`BookEntityProvider.GetEntitiesAsync(bookId, language)`. Its ch0 carries ONE English epigraph line —");
        Emit($"`{PreservationFixtureBooks.AdversarialEpigraph}` — whose Title-Case words are four of the leak words,");
        Emit("each appearing EXACTLY ONCE, MID-SENTENCE, in the whole book (all the harvest condition needs).");
        Emit("");
        Emit($"Provider set: **{adversarialEntities.Count}** entities"
             + (advSet is not null
                 ? $" — {advSet.ManuscriptTokens.Count} manuscript-harvested (case-SENSITIVE), {advSet.DeclaredNames.Count} declared (case-insensitive)"
                 : " (NOT a BookEntitySet — tiers unavailable)"));
        Emit("");
        Emit("| leak word (as it appears in the manuscript) | harvested by the provider? | tier |");
        Emit("|---|---|---|");
        foreach (var (word, harvested, tier) in PreservationFixtureBooks.AdversarialHarvestReport(adversarialEntities))
        {
            Emit($"| `{word}` | {(harvested ? "**yes**" : "NO")} | {tier} |");
        }
        Emit("");

        var harvestedLeakWords = PreservationFixtureBooks.AdversarialHarvestReport(adversarialEntities)
            .Where(r => r.Harvested && r.Tier == "manuscript").Select(r => r.Word).ToList();
        if (harvestedLeakWords.Count == 0)
        {
            Emit("**WARNING — the adversarial book harvested NONE of the leak words.** ARM B is then not adversarial at");
            Emit("all and this gate proves nothing about the entity lever. Fix the seeding before trusting the result.");
            Emit("");
        }
        else
        {
            Emit($"**The lever is armed: {harvestedLeakWords.Count} leak word(s) ARE manuscript-tier entities "
                 + $"(`{string.Join("`, `", harvestedLeakWords)}`).** Under the PRE-be-c04 case-INSENSITIVE membership each");
            Emit("would have spared its LOWERCASE twin in the analysis prose. Whether they still get cleaned below IS the");
            Emit("be-c04 two-tier fix, measured end to end.");
            Emit("");
        }

        // ── Deterministic classification per arm (ZERO model calls) ───────────────────────────────────────
        // What the entity set does to the LEAK set is decided by the CLASSIFIER, before any model is involved.
        // Computing it up front means the entity-spared-leak regression is caught deterministically (and is
        // reported below) rather than being inferred from a model output that could differ for other reasons.
        var repairRunsA = new List<ForeignRun>[seeds.Count];
        var repairRunsB = new List<ForeignRun>[seeds.Count];
        // SCOPE: this check stays GLOBAL, over BOTH subsets, deliberately, and it does NOT consult the gated
        // corpus at all. It is DETERMINISTIC and GPU-independent: a leak the classifier refuses to send to the
        // model is a CODE DEFECT in the entity lever, not a stochastic model quality number, so narrowing it
        // to the gated corpus would only hide a real be-c04 regression in a subset a future rollback might
        // un-gate. Only the STOCHASTIC aggregate (cleaned %, over-rewrite) follows the gated corpus.
        var entitySparedLeaks = new List<string>();
        var entitySparedProse = new List<string>();
        var entitySparedSynopsis = new List<string>();
        for (var i = 0; i < seeds.Count; i++)
        {
            var runs = LatinInHebrewContentDetector.DetectForeignRuns(seeds[i].Value, ExpectedScript.Hebrew);
            repairRunsA[i] = ForeignRunClassifier.RunsToRepair(runs, seeds[i].Value, ExpectedScript.Hebrew, null).ToList();
            repairRunsB[i] = ForeignRunClassifier.RunsToRepair(runs, seeds[i].Value, ExpectedScript.Hebrew, adversarialEntities).ToList();
            if (repairRunsA[i].Count > 0 && repairRunsB[i].Count == 0)
            {
                entitySparedLeaks.Add(seeds[i].Leak);
                (scope.IsSynopsis(i) ? entitySparedSynopsis : entitySparedProse)
                    .Add(seeds[i].Leak);
            }
        }

        Emit("### Deterministic classification (no model): does the entity set SPARE any leak?");
        Emit("");
        Emit("(Scope: BOTH subsets, gated or not. This one is deterministic and a failure is a CODE DEFECT in the "
             + "entity lever, so it is deliberately NOT narrowed to the gated corpus.)");
        Emit("");
        if (entitySparedLeaks.Count == 0)
        {
            Emit("**NO leak is spared by the entity set.** Every one of the "
                 + $"{seeds.Count} leaks ({proseSeedIdx.Count} analysis-prose + {synopsisSeedIdx.Count} SYNOPSIS) is "
                 + "still classified REPAIR with the adversarial book's REAL entity set, "
                 + "including the lowercase twins of the harvested words. The be-c04 case-SENSITIVE manuscript tier holds.");
        }
        else
        {
            Emit($"**REGRESSION - the entity set spares {entitySparedLeaks.Count} REAL leak(s): "
                 + $"`{string.Join("`, `", entitySparedLeaks)}`** "
                 + $"({entitySparedProse.Count} in the ANALYSIS-PROSE subset, {entitySparedSynopsis.Count} in the "
                 + "SYNOPSIS subset). These never reach the model in ARM B, so they cannot be");
            Emit("cleaned. This is the be-c04 failure mode; be-c08 HALTs.");
        }
        Emit("");

        // ── Tiers × arms ─────────────────────────────────────────────────────────────────────────────────
        var tiers = new (string name, string provider, string model)[]
        {
            ("LOCAL",  "Ollama",     "gemma4:12b"),
            ("CLOUD",  "OpenRouter", "google/gemma-4-31b-it"),
        };

        var arms = new (string name, IReadOnlySet<string>? entities, string desc)[]
        {
            ("A", null,                "ENTITY-FREE (the e4 control — no per-book entity lever)"),
            ("B", adversarialEntities, "REAL BookEntityProvider set for the ADVERSARIAL book (the production path)"),
        };

        var perTier = new List<TierMeasurement>();
        // (tier, arm) -> per-case outcomes, so the cross-arm diff below is computed, not eyeballed.
        var armOutcomes = new Dictionary<(string tier, string arm), LeakOutcome[]>();

        foreach (var (tierName, provider, model) in tiers)
        {
            Emit("---");
            Emit("");
            Emit($"## Tier {tierName}: `{provider}|{model}`");
            Emit("");

            // CLOUD is OUT OF SCOPE for be-c08 (the user scoped the run to LOCAL). Skip it BEFORE any call when
            // no OpenRouter key is configured, so it costs nothing and never fails the run.
            if (provider == "OpenRouter" && !CloudKeyAvailable())
            {
                Emit("SKIPPED: no OpenRouter API key configured (env `AI_OPENROUTER_APIKEY`). be-c08 is scoped to the");
                Emit("LOCAL tier; the cloud tier stays routing-only and is not measured here.");
                Emit("");
                perTier.Add(new TierMeasurement
                {
                    Name = tierName,
                    Model = $"{provider}|{model}",
                    Blocked = true,
                    BlockReason = "SKIPPED (no OpenRouter key; be-c08 is LOCAL-only)",
                });
                continue;
            }

            var router = BuildTermRepairTierRouter(appSettingsPath, provider, model);
            var service = new DynamicTermRepairService(router, NullLogger<DynamicTermRepairService>.Instance);

            // Preflight: one direct router call so a total-tier outage (e.g. cloud 401/network) is reported
            // BLOCKED after ONE call instead of N per-seed timeouts. The service's fail-safe would otherwise
            // mask the fault as a silent revert.
            var (ok, preInfo) = await PreflightTierAsync(router);
            if (!ok)
            {
                Emit($"BLOCKED: tier `{tierName}` preflight failed → {preInfo}");
                Emit("(No numbers recorded for this tier — a fail-safe revert must NOT read as a clean 0-over-rewrite pass.)");
                Emit("");
                perTier.Add(new TierMeasurement { Name = tierName, Model = $"{provider}|{model}", Blocked = true, BlockReason = preInfo });
                continue;
            }
            Emit($"preflight OK → served by `{preInfo}`");
            Emit("");

            foreach (var (armName, armEntities, armDesc) in arms)
            {
                Emit($"### ARM {armName} — {armDesc}");
                Emit("");

                var m = new TierMeasurement { Name = $"{tierName}/ARM {armName}", Model = $"{provider}|{model}" };
                var outcomes = new LeakOutcome[seeds.Count];

                Emit("| seed | corpus | leak (Latin) | model calls | cleaned? | over-rewrite? | before→after span | latency ms |");
                Emit("|---|---|---|---|---|---|---|---|");

                for (var i = 0; i < seeds.Count; i++)
                {
                    var (label, leak, value) = (seeds[i].Label, seeds[i].Leak, seeds[i].Value);
                    // Every row is labelled with the SUBSET it belongs to, and every row in the GATED CORPUS
                    // accumulates into `m` (the tier/arm aggregate the decision table + verdict read). Both
                    // subsets are gated under the shipped config; the label keeps them separately readable.
                    var corpus = scope.SubsetLabel(i) + (IsGatedSeed(i) ? "" : ", NOT gated");
                    var runs = LatinInHebrewContentDetector.DetectForeignRuns(value, ExpectedScript.Hebrew);
                    // Seeds are authored single-leak; guard anyway so an authoring slip is visible, not silent.
                    if (runs.Count != 1)
                    {
                        Emit($"| {label} | {corpus} | {leak} | - | SEED-ERR | runs={runs.Count} | (expected exactly 1 Latin run) | - |");
                        outcomes[i] = new LeakOutcome { SeedError = true };
                        continue;
                    }
                    var run = runs[0];

                    // The REPAIR spans THIS arm's entity set produces = the number of marked-span model calls the
                    // service will make. 0 => the value never reaches the model (for a LEAK, that is the regression).
                    var repairRuns = armName == "A" ? repairRunsA[i] : repairRunsB[i];
                    var entitySpared = repairRunsA[i].Count > 0 && repairRuns.Count == 0;

                    var result = await service.RepairValueAsync(value, ExpectedScript.Hebrew, "he-IL", armEntities);

                    if (result.Fault is not null)
                    {
                        // A surfaced fault on a preflight-OK tier = a genuine per-call failure; record it honestly.
                        Emit($"| {label} | {corpus} | {leak} | {repairRuns.Count} | FAULT | - | {FirstLine(result.Fault.Message)} | {result.LatencyMs} |");
                        if (IsGatedSeed(i)) m.Faults++;
                        outcomes[i] = new LeakOutcome { Fault = true, ModelCalls = repairRuns.Count, EntitySpared = entitySpared };
                        continue;
                    }

                    var cleaned = !LatinInHebrewContentDetector.HasForeignRuns(result.Value, ExpectedScript.Hebrew);
                    var changed = !string.Equals(value, result.Value, StringComparison.Ordinal);
                    // Generalised over-rewrite check: the change must be CONFINED to THIS ARM's repair spans. With
                    // zero repair spans nothing was allowed to change, so any change is an over-rewrite.
                    var overRewrite = changed && !ChangeConfinedToRepairSpans(value, repairRuns, result.Value);
                    var (spanScoped, replacement) = SpanScopeCheck(value, run.Start, run.Length, result.Value);

                    // GATED-CORPUS rows accumulate into the aggregate the decision table + the d5 CLEANING
                    // VERDICT are computed from. A row OUTSIDE the gated corpus (only possible if the shipped
                    // PerType map stops repairing its type) is still measured and still recorded in
                    // `outcomes`, so the subset sections below can report it.
                    if (IsGatedSeed(i))
                    {
                        m.Total++;
                        if (cleaned) m.Cleaned++;
                        if (overRewrite) m.OverRewrite++;
                        m.ModelCalls += repairRuns.Count;
                        if (repairRuns.Count > 0) m.Latencies.Add(result.LatencyMs);
                    }

                    outcomes[i] = new LeakOutcome
                    {
                        Cleaned = cleaned,
                        OverRewrite = overRewrite,
                        ModelCalls = repairRuns.Count,
                        EntitySpared = entitySpared,
                        Repaired = result.Value,
                        LatencyMs = result.LatencyMs,
                    };

                    string beforeAfter;
                    if (repairRuns.Count == 0)
                    {
                        beforeAfter = entitySpared
                            ? $"`{leak}` → **UNCHANGED (spared by the ENTITY SET — 0 model calls)**"
                            : $"`{leak}` → UNCHANGED (classifier LEAVE — 0 model calls)";
                    }
                    else if (overRewrite)
                    {
                        beforeAfter = $"`{leak}` → OVER-REWRITE (full: {Trunc(result.Value, 60)})";
                    }
                    else
                    {
                        beforeAfter = $"`{leak}` → `{(spanScoped ? replacement.Trim() : Trunc(result.Value, 40))}`";
                    }

                    Emit($"| {label} | {corpus} | {leak} | {repairRuns.Count} | {(cleaned ? "yes" : "**NO**")} | {(overRewrite ? "**YES**" : "no")} | {beforeAfter} | {(repairRuns.Count > 0 ? result.LatencyMs.ToString() : "-")} |");
                }

                Emit("");
                var (med, p90) = LatencyStats(m.Latencies);
                m.MedianMs = med; m.P90Ms = p90;
                Emit($"**{tierName} / ARM {armName} summary (GATED CORPUS, {gatedSeedIdx.Count} values):** measured {m.Total}/{gatedSeedIdx.Count}"
                     + (m.Faults > 0 ? $" ({m.Faults} per-call fault(s))" : "")
                     + $"  |  cleaned {m.Cleaned}/{m.Total}"
                     + (m.Total > 0 ? $" ({100.0 * m.Cleaned / m.Total:F0}%)" : "")
                     + $"  |  over-rewrite {m.OverRewrite}  |  model calls {m.ModelCalls}"
                     + $"  |  latency median {med} ms / p90 {p90} ms");
                // LABELLED SUBSETS of that same aggregate, so the pre-f2 ANALYSIS-PROSE figures of record stay
                // directly comparable and the SYNOPSIS contribution stays separately visible.
                void EmitSeedSubsetLine(string label, IReadOnlyList<int> idx, bool gated)
                {
                    var sMeasured = idx.Count(i => !outcomes[i].SeedError && !outcomes[i].Fault);
                    Emit($"  ({label} subset{(gated ? ", INSIDE the aggregate above" : ", NOT gated, OUTSIDE the aggregate above")}: "
                         + $"cleaned {idx.Count(i => outcomes[i].Cleaned)}/{sMeasured}"
                         + $"  |  over-rewrite {idx.Count(i => outcomes[i].OverRewrite)}"
                         + $"  |  model calls {idx.Sum(i => outcomes[i].ModelCalls)})");
                }
                EmitSeedSubsetLine("ANALYSIS-PROSE", proseSeedIdx, scope.AnalysisProseRepaired);
                EmitSeedSubsetLine("SYNOPSIS", synopsisSeedIdx, scope.SynopsisRepaired);
                Emit("");
                perTier.Add(m);
                armOutcomes[(tierName, armName)] = outcomes;
            }

            // ── ARM A vs ARM B — THE be-c08 GATE (computed here, not eyeballed) ──────────────────────────
            EmitArmComparison(Emit, tierName, seeds, scope, armOutcomes, harvestedLeakWords);
        }

        // ── FIELD-VALUE-SCOPE CONTRAST (blast-radius) — LOCAL only, 2 cases ────────────────────────────────
        // Reuses the existing whole-value RepairAsync (Ollama gemma4:12b, verbatim Hebrew cleanup, NO span
        // marking) to CONTRAST the Stage-2 failure mode: the field-scope model re-flows the whole value, so
        // its output does NOT preserve the byte-identical prefix/suffix the span-scope pass guarantees.
        Emit("---");
        Emit("");
        Emit("## Span-scope vs field-value-scope contrast (LOCAL gemma4:12b, 2 cases)");
        Emit("");
        Emit("Span-scope marks ONE run and splices the replacement by offset → prefix/suffix byte-identical.");
        Emit("Field-scope hands the WHOLE value to the model → free to re-flow everything (the Stage-2 blast radius).");
        Emit("");
        var contrastSeeds = new[] { seeds[0], seeds[2] }; // confusion, ambivalence
        foreach (var (label, leak, value) in contrastSeeds)
        {
            var runs = LatinInHebrewContentDetector.DetectForeignRuns(value, ExpectedScript.Hebrew);
            if (runs.Count != 1) continue;
            var run = runs[0];

            try
            {
                var fieldOut = (await RepairAsync(GemmaModel, value)).Trim();
                var (fieldSpanScoped, _) = SpanScopeCheck(value, run.Start, run.Length, fieldOut);
                Emit($"### {label} (`{leak}`)");
                Emit($"- original: `{value}`");
                Emit($"- field-scope output: `{fieldOut}`");
                Emit($"- field-scope preserved the non-leak prefix+suffix byte-identically? **{(fieldSpanScoped ? "yes" : "NO — reflowed outside the span")}** (len {value.Length}→{fieldOut.Length})");
                Emit("");
            }
            catch (Exception ex)
            {
                Emit($"### {label} (`{leak}`): field-scope ERROR {ex.GetType().Name}: {FirstLine(ex.Message)}");
                Emit("");
            }
        }

        // ── DECISION TABLE (one row per tier × arm) ────────────────────────────────────────────────────────
        Emit("---");
        Emit("");
        Emit($"## Decision table - GATED CORPUS ({gatedSeedIdx.Count} values)");
        Emit("");
        Emit($"Scope: every number in this table is computed over the {gatedSeedIdx.Count} leak values of the gated "
             + $"corpus. {scope.CorpusSentence} The per-subset breakdown is in the subset section below.");
        Emit("");
        Emit("| tier / arm | model | cleaned % | over-rewrite (bar=0) | model calls | latency median / p90 (ms) | status |");
        Emit("|---|---|---|---|---|---|---|");
        foreach (var t in perTier)
        {
            if (t.Blocked)
            {
                Emit($"| {t.Name} | {t.Model} | - | - | - | - | {FirstLine(t.BlockReason ?? "BLOCKED")} |");
                continue;
            }
            var pct = t.Total > 0 ? $"{100.0 * t.Cleaned / t.Total:F0}% ({t.Cleaned}/{t.Total})" : "n/a";
            var status = t.OverRewrite == 0 ? "over-rewrite gate PASS" : "over-rewrite gate FAIL";
            Emit($"| {t.Name} | {t.Model} | {pct} | {t.OverRewrite} | {t.ModelCalls} | {t.MedianMs} / {t.P90Ms} | {status} |");
        }
        Emit("");

        var overRewriteGateHeld = perTier.Where(x => !x.Blocked).All(x => x.OverRewrite == 0);
        Emit($"**Over-rewrite HARD gate over the GATED CORPUS ({gatedSeedIdx.Count} values; must be 0 on every "
             + $"measured tier/arm): {(overRewriteGateHeld ? "HELD" : "VIOLATED")}.**");
        // The same count broken out per LABELLED SUBSET, so a synopsis-only over-rewrite is attributable at a
        // glance. Both subsets feed the gate above under the shipped config.
        var proseOverRewriteTotal = armOutcomes.Sum(kv => proseSeedIdx.Count(i => kv.Value[i].OverRewrite));
        var synOverRewriteTotal = armOutcomes.Sum(kv => synopsisSeedIdx.Count(i => kv.Value[i].OverRewrite));
        Emit($"(By subset, across all measured tier/arms: ANALYSIS-PROSE {proseOverRewriteTotal}"
             + $"{(scope.AnalysisProseRepaired ? "" : " (NOT gated)")}, SYNOPSIS {synOverRewriteTotal}"
             + $"{(scope.SynopsisRepaired ? "" : " (NOT gated)")}. Per-tier breakdown in the subset section.)");
        Emit("");

        // ── THE be-c08 CLEANING VERDICT (ARM B vs ARM A on the measured tier) ─────────────────────────────
        var localA = perTier.FirstOrDefault(x => x.Name == "LOCAL/ARM A" && !x.Blocked);
        var localB = perTier.FirstOrDefault(x => x.Name == "LOCAL/ARM B" && !x.Blocked);
        Emit($"### be-c08 verdict - does the entity lever spare a REAL leak? (GATED CORPUS, {gatedSeedIdx.Count} values)");
        Emit("");
        Emit($"**Scope of this verdict: the cleaned/over-rewrite numbers below cover the {gatedSeedIdx.Count} values of "
             + $"the gated corpus. {scope.CorpusSentence} The entity-spared-leak check above is the one number that is "
             + "deliberately WIDER than the gated corpus: it is deterministic and covers BOTH subsets, because a "
             + "spared leak is a code defect, not a model number.**");
        Emit("");
        if (localA is null || localB is null)
        {
            Emit("**INCONCLUSIVE** — the LOCAL tier did not produce both arms (see BLOCKED above). No verdict.");
        }
        else
        {
            var pctA = localA.Total > 0 ? 100.0 * localA.Cleaned / localA.Total : 0;
            var pctB = localB.Total > 0 ? 100.0 * localB.Cleaned / localB.Total : 0;
            var cleaningHeld = localB.Cleaned >= localA.Cleaned;
            var leverSafe = entitySparedLeaks.Count == 0;
            var pass = cleaningHeld && leverSafe && localB.OverRewrite == 0;

            Emit($"- ARM A (entity-free, the e4 control): cleaned **{pctA:F0}%** ({localA.Cleaned}/{localA.Total}), {localA.ModelCalls} model calls");
            Emit($"- ARM B (REAL provider set, adversarial book): cleaned **{pctB:F0}%** ({localB.Cleaned}/{localB.Total}), {localB.ModelCalls} model calls");
            Emit($"- leaks SPARED by the entity set (deterministic, 0 model calls): **{entitySparedLeaks.Count}**"
                 + (entitySparedLeaks.Count > 0 ? $" — `{string.Join("`, `", entitySparedLeaks)}`" : ""));
            Emit($"- ARM B over-rewrite: **{localB.OverRewrite}** (bar = 0)");
            Emit("");
            Emit($"**d5 CLEANING GATE (GATED CORPUS: {gatedSeedIdx.Count} values = {proseSeedIdx.Count} ANALYSIS-PROSE "
                 + $"{(scope.AnalysisProseRepaired ? "+" : "excluded, +")} {synopsisSeedIdx.Count} SYNOPSIS"
                 + $"{(scope.SynopsisRepaired ? "" : " excluded")}, over the {scope.RepairedTypes.Count} analysis types "
                 + $"the shipped `PerType` map repairs): {(pass ? "PASS" : "HALT")}.** "
                 + (pass
                     ? "ARM B matches ARM A: the per-book entity lever, sourced from the REAL BookEntityProvider over an "
                       + "ADVERSARIAL manuscript that harvested the leak words themselves, spares NO real leak. The be-c04 "
                       + "case-SENSITIVE manuscript tier holds on the production path."
                     : "ARM B cleans FEWER leaks than ARM A (or the lever spared a leak / over-rewrote). The entity lever is "
                       + "eating real leaks on the production path — be-c08 HALTs and the shipped default STAYS `Mode=Glossary`."));
            if (harvestedLeakWords.Count > 0 && pass)
            {
                Emit("");
                Emit($"**The be-c04 fix, end to end:** `{string.Join("`, `", harvestedLeakWords)}` ARE manuscript-tier entities in "
                     + "the provider's set, and their LOWERCASE forms were still classified REPAIR and cleaned. A capitalized "
                     + "observation no longer spares the lowercase leak.");
            }
        }
        Emit("");
        Emit("### Decision (grounded in the numbers above)");
        Emit("- The dynamic span-scoped pass is what CLEANS out-of-glossary leaks the closed glossary cannot reach.");
        Emit("- Span-scope keeps over-rewrite at 0 by construction (prefix/suffix byte-identical) — the field-scope");
        Emit("  contrast shows the Stage-2 blast radius that this design avoids.");
        Emit("- LOCAL (gemma4:12b) is free/offline/private and already the loaded TermRepair model. CLOUD is out of");
        Emit("  scope for be-c08 (user decision): routing-only, skipped when no OpenRouter key is configured.");
        Emit("- RECOMMENDATION carried to the parent + d6: Mode = GlossaryThenDynamic (glossary zero-cost fast-path");
        Emit("  for its ~35 known terms, dynamic for the residual tail) ONLY IF this cleaning gate PASSES **and** d6");
        Emit("  (precision/FP gate on the legitimate-term set) passes. Either one HALTing keeps `Mode=Glossary`.");
        Emit("");

        // ── THE TWO LABELLED SUBSETS OF THE GATED CORPUS ─────────────────────────────────────────────────────
        Emit("---");
        Emit("");
        Emit("## Labelled subsets of the gated corpus (same instrument, same bar)");
        Emit("");
        Emit("Both subsets below are part of the corpus the verdict above was computed over (unless a subset is "
             + "marked NOT gated, which happens only when the shipped `PerType` map stops repairing its type). They "
             + "are broken out so the ANALYSIS-PROSE numbers stay DIRECTLY COMPARABLE to the pre-f2 be-c08 figures "
             + "of record (cleaning 10/10), and so the SYNOPSIS numbers q1/q2 recorded stay quotable on their own.");
        Emit("");
        foreach (var (subsetName, idx) in new (string, IReadOnlyList<int>)[]
                 {
                     ($"ANALYSIS-PROSE subset (the pre-f2 corpus of record; {(scope.AnalysisProseRepaired ? "GATED" : "NOT gated")})", proseSeedIdx),
                     ($"SYNOPSIS subset (q1 fixtures, multi-paragraph; {(scope.SynopsisRepaired ? "GATED since f2" : "NOT gated")})", synopsisSeedIdx),
                 })
        {
            Emit($"### {subsetName} ({idx.Count} value(s))");
            foreach (var (tierName, _, _) in tiers)
            foreach (var (arm, _, _) in arms)
            {
                if (!armOutcomes.TryGetValue((tierName, arm), out var os))
                {
                    Emit($"- {tierName}/ARM {arm}: not measured (tier blocked/skipped).");
                    continue;
                }

                var measured = idx.Count(i => !os[i].SeedError && !os[i].Fault);
                var cleaned = idx.Count(i => os[i].Cleaned);
                var over = idx.Count(i => os[i].OverRewrite);
                var calls = idx.Sum(i => os[i].ModelCalls);
                Emit($"- {tierName}/ARM {arm}: cleaned **{(measured > 0 ? 100.0 * cleaned / measured : 0):F0}%** "
                     + $"({cleaned}/{measured})  |  over-rewrite **{over}** (bar = 0)  |  model calls {calls}");
                foreach (var i in idx.Where(i => !os[i].Cleaned && !os[i].SeedError))
                {
                    Emit($"  - NOT CLEANED [{seeds[i].Label}] `{seeds[i].Leak}` → `{Trunc(os[i].Repaired ?? "", 90)}`");
                }
            }

            Emit("");
        }

        // ── Persist the RULE-0 artifact ──
        var outDir = Environment.GetEnvironmentVariable("DIAG_OUT_DIR");
        if (string.IsNullOrWhiteSpace(outDir)) outDir = Path.GetTempPath();
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, "d5-termrepair-measure.md");
        File.WriteAllText(outPath, report.ToString(), new UTF8Encoding(false));
        _output.WriteLine("REPORT WRITTEN: " + outPath);

        // The ONE hard assertion, and it is DETERMINISTIC (classifier-level, GPU-independent): the per-book
        // entity lever must not spare a REAL leak. A model that cleans poorly is REPORTED, never asserted (a
        // model result must not fail the harness) — but a leak the gate refuses to send to the model at all is
        // a code defect, and it is exactly the be-c04 regression this arm exists to catch.
        Assert.True(entitySparedLeaks.Count == 0,
            $"ENTITY LEVER ATE A REAL LEAK: the adversarial book's REAL BookEntityProvider set spares "
            + $"[{string.Join(", ", entitySparedLeaks)}] — they are classified LEAVE and never reach the repair model. "
            + "This is the be-c04 recall regression (a capitalized manuscript occurrence sparing the lowercase leak). "
            + "See d5-termrepair-measure.md.");
    }

    /// <summary>True when an OpenRouter API key is reachable (env <c>AI_OPENROUTER_APIKEY</c>). Checked BEFORE any
    /// call so the CLOUD tier — out of scope for be-c08 — skips cleanly instead of burning a failing preflight.</summary>
    private static bool CloudKeyAvailable()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AI_OPENROUTER_APIKEY"));

    /// <summary>
    /// THE be-c08 GATE, computed rather than eyeballed: ARM B (the REAL per-book entity set) vs ARM A
    /// (entity-free) on one tier. Emits the cleaning-percentage comparison and a PER-CASE DIFF of every case
    /// where the two arms disagree — on cleaned, on over-rewrite, or on model-call count — so the parent reads a
    /// verdict, not two tables.
    /// <para>
    /// SCOPE: the AGGREGATE comparison (the gate) is computed over <paramref name="scope"/>'s GATED CORPUS,
    /// which is derived from the shipped <c>Ai:AnalysisRepair:PerType</c> map, never from a positional split.
    /// Each labelled subset (ANALYSIS-PROSE, SYNOPSIS) is ALSO summarised on its own line so the pre-f2
    /// figures of record stay comparable. The PER-CASE DIFF stays over ALL seeds (each row is labelled with
    /// its subset), since a per-case arm disagreement is diagnostic, not an aggregate.
    /// </para>
    /// </summary>
    private static void EmitArmComparison(
        Action<string> emit,
        string tierName,
        IReadOnlyList<LeakCase> seeds,
        RepairCorpusScope scope,
        IReadOnlyDictionary<(string tier, string arm), LeakOutcome[]> armOutcomes,
        IReadOnlyList<string> harvestedLeakWords)
    {
        if (!armOutcomes.TryGetValue((tierName, "A"), out var a) ||
            !armOutcomes.TryGetValue((tierName, "B"), out var b))
        {
            return; // a tier that was blocked/skipped produced no arms — nothing to compare.
        }

        var gatedIdx = scope.Gated;
        var proseIdx = scope.AnalysisProse;
        var synopsisIdx = scope.Synopsis;

        emit($"### ARM A vs ARM B on {tierName} - the be-c08 cleaning comparison (GATED CORPUS, {gatedIdx.Count} values)");
        emit("");
        emit($"Scope: the percentages and the delta below cover the {gatedIdx.Count} leak values of the gated corpus. "
             + $"{scope.CorpusSentence}");
        emit("");

        var measuredA = gatedIdx.Count(i => !a[i].SeedError && !a[i].Fault);
        var measuredB = gatedIdx.Count(i => !b[i].SeedError && !b[i].Fault);
        var cleanedA = gatedIdx.Count(i => a[i].Cleaned);
        var cleanedB = gatedIdx.Count(i => b[i].Cleaned);
        var pctA = measuredA > 0 ? 100.0 * cleanedA / measuredA : 0;
        var pctB = measuredB > 0 ? 100.0 * cleanedB / measuredB : 0;

        emit($"- ARM A (entity-free): cleaned **{cleanedA}/{measuredA} ({pctA:F0}%)**, {gatedIdx.Sum(i => a[i].ModelCalls)} model calls");
        emit($"- ARM B (real entity set): cleaned **{cleanedB}/{measuredB} ({pctB:F0}%)**, {gatedIdx.Sum(i => b[i].ModelCalls)} model calls");
        emit($"- delta (B - A): **{pctB - pctA:F0} percentage points** (must be >= 0; ARM B may not clean fewer)");
        foreach (var (label, idx, gated) in new (string, IReadOnlyList<int>, bool)[]
                 {
                     ("ANALYSIS-PROSE", proseIdx, scope.AnalysisProseRepaired),
                     ("SYNOPSIS", synopsisIdx, scope.SynopsisRepaired),
                 })
        {
            if (idx.Count == 0) continue;
            var subMeasuredA = idx.Count(i => !a[i].SeedError && !a[i].Fault);
            var subMeasuredB = idx.Count(i => !b[i].SeedError && !b[i].Fault);
            emit($"- {label} subset ({(gated ? "inside the gate above" : "NOT gated")}): ARM A cleaned "
                 + $"**{idx.Count(i => a[i].Cleaned)}/{subMeasuredA}**, ARM B cleaned "
                 + $"**{idx.Count(i => b[i].Cleaned)}/{subMeasuredB}**");
        }
        emit("");

        var diffs = Enumerable.Range(0, seeds.Count)
            .Where(i => a[i].Cleaned != b[i].Cleaned
                     || a[i].OverRewrite != b[i].OverRewrite
                     || a[i].ModelCalls != b[i].ModelCalls)
            .ToList();

        if (diffs.Count == 0)
        {
            emit("**The two arms AGREE on every case** (same cleaned / over-rewrite / model-call count). The per-book");
            emit("entity set changes NOTHING about the leak set — which is the whole point: it spares the book's own");
            emit("proper nouns without sparing a single real leak.");
        }
        else
        {
            emit("**PER-CASE DIFF - the arms disagree on the following cases (ALL cases, subset-labelled):**");
            emit("");
            emit("| seed | subset | leak | A cleaned | B cleaned | A calls | B calls | entity-spared in B? | reading |");
            emit("|---|---|---|---|---|---|---|---|---|");
            foreach (var i in diffs)
            {
                var reading = a[i].Cleaned && !b[i].Cleaned
                    ? (b[i].EntitySpared
                        ? "**REGRESSION — the ENTITY SET spared a real leak (be-c04 failure mode)**"
                        : "**REGRESSION — B failed to clean a leak A cleaned**")
                    : (!a[i].Cleaned && b[i].Cleaned
                        ? "B cleaned a leak A missed (model variance — not a gate change)"
                        : "same cleaning outcome; model-call / over-rewrite difference only");
                emit($"| {seeds[i].Label} | {scope.SubsetLabel(i)}{(scope.IsGated(i) ? "" : " (NOT gated)")} | `{seeds[i].Leak}` "
                     + $"| {(a[i].Cleaned ? "yes" : "NO")} | {(b[i].Cleaned ? "yes" : "NO")} "
                     + $"| {a[i].ModelCalls} | {b[i].ModelCalls} | {(b[i].EntitySpared ? "**YES**" : "no")} | {reading} |");
            }
        }
        emit("");

        // The GATE is the GATED CORPUS (config-derived). `regressed` is a stochastic model comparison, so it
        // follows that corpus; `leverAte` is the DETERMINISTIC entity-spare check and deliberately stays
        // global (a spared leak is a code defect in any subset, gated or not).
        var regressed = diffs.Where(i => scope.IsGated(i) && a[i].Cleaned && !b[i].Cleaned).ToList();
        var ungatedRegressed = diffs.Where(i => !scope.IsGated(i) && a[i].Cleaned && !b[i].Cleaned).ToList();
        var proseRegressed = regressed.Count(i => !scope.IsSynopsis(i));
        var synopsisRegressed = regressed.Count - proseRegressed;
        var leverAte = Enumerable.Range(0, seeds.Count).Where(i => b[i].EntitySpared).ToList();
        var pass = regressed.Count == 0 && leverAte.Count == 0;
        emit($"**{tierName} cleaning gate (GATED CORPUS, {gatedIdx.Count} values = {proseIdx.Count} ANALYSIS-PROSE "
             + $"{(scope.AnalysisProseRepaired ? "+" : "excluded, +")} {synopsisIdx.Count} SYNOPSIS"
             + $"{(scope.SynopsisRepaired ? "" : " excluded")}): "
             + $"{(pass ? "PASS - ARM B matches ARM A" : "HALT")}.** "
             + (pass
                 ? $"The entity lever is armed ({harvestedLeakWords.Count} leak word(s) harvested as manuscript-tier "
                   + "entities) and STILL spares no leak."
                 : $"{regressed.Count} gated leak(s) regressed ({proseRegressed} ANALYSIS-PROSE, {synopsisRegressed} "
                   + $"SYNOPSIS), {leverAte.Count} spared by the entity set "
                   + "(the entity-spare check spans BOTH subsets, deterministically)."));
        if (ungatedRegressed.Count > 0)
        {
            emit($"  (Outside the gated corpus, reported not gating: {ungatedRegressed.Count} value(s) where ARM B cleaned less than ARM A.)");
        }
        emit("");
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════
    // d6 PRECISION / SAFETY GATE — MeasureLegitimateTermPreservation_LocalVsCloud
    //
    // The d5 gate proved the dynamic span-scoped pass CLEANS out-of-glossary leaks (cleaned% high, over-
    // rewrite 0) but only measured leaks it SHOULD change. d6 measures the OPPOSITE risk (the FALSE-POSITIVE
    // side): does the pipeline WRONGLY alter a LEGITIMATE foreign token that MUST be preserved byte-identical
    // — a proper noun, a brand, an acronym, an intentional foreign phrase, a URL/email, or a Hebrew name in
    // an English book? For every legit value the EXPECTED outcome is UNCHANGED (byte-identical); a FALSE
    // POSITIVE = the repaired value differs from the original in ANY byte.
    //
    // Drives the WHOLE shipped pipeline (DynamicTermRepairService.RepairValueAsync = d1 detect -> d2 classify
    // -> d3 span-repair) over the legit set on BOTH tiers (LOCAL Ollama|gemma4:12b, CLOUD OpenRouter|
    // google/gemma-4-31b-it), reusing the d5 tier-router / preflight / latency helpers.
    //
    // Where the safety comes from is recorded per case (so we know what to trust):
    //   • GATED — the d1 detector allowlist OR the d2 classifier (Title-Case mid-sentence / ALL-CAPS acronym /
    //     URL-email-code / number+unit / known book-entity) kept the token AWAY from the model entirely
    //     (repairRuns.Count == 0 => ZERO model calls). Deterministic + tier-independent.
    //   • MODEL-PRESERVED — the token DID reach the model (a lowercase name particle "van"/"da"/"de", a
    //     lowercase code-switch, or a Hebrew run in a Latin book) and the model's "return a proper noun /
    //     no-equivalent token UNCHANGED" instruction + the IsAcceptableReplacement echo-reject guard preserved
    //     it. This is the tier-SENSITIVE surface d6 stresses.
    //
    // AGREED BAR (parent): a tier is SHIPPABLE iff preservation >= 90% (FP <= 10%) AND over-rewrite == 0.
    // Over-rewrite here = a value changed OUTSIDE the REPAIR-classified span(s) (structure / a non-flagged
    // token) — the span-scoped design makes it 0 by construction; a gated case that changes at all is BOTH an
    // FP and an over-rewrite (a real bug). Report -> DIAG_OUT_DIR/d6-precision-fp-measure.md. Numbers only,
    // never faked; a dead tier is BLOCKED (a fail-safe revert must not read as clean preservation).
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════
    [Fact]
    [Trait("Category", "LiveDiagnostic")]
    public async Task MeasureLegitimateTermPreservation_LocalVsCloud()
    {
        if (LiveDiagnosticsOptedOut()) return;
        if (!await IsOllamaReachableAsync())
        {
            _output.WriteLine("SKIP: Ollama not reachable at " + OllamaBaseUrl);
            return;
        }

        var appSettingsPath = FindUpward(Path.Combine("Pagedraft.Api", "appsettings.json"));
        Assert.True(File.Exists(appSettingsPath), "appsettings.json not found at " + appSettingsPath);

        var report = new StringBuilder();
        void Emit(string s) { _output.WriteLine(s); report.AppendLine(s); }

        Emit("# d6 — Legitimate-term PRESERVATION (false-positive) measure: LOCAL Ollama vs CLOUD OpenRouter");
        Emit("");
        Emit($"Run: {DateTime.Now:yyyy-MM-dd HH:mm}  |  routing base: `{appSettingsPath}`");
        Emit("Instrument: `OutputQualityDiagnostic.MeasureLegitimateTermPreservation_LocalVsCloud` driving the");
        Emit("shipped `DynamicTermRepairService.RepairValueAsync` (d1 detect → d2 classify → d3 span-repair).");
        Emit("Expected outcome for EVERY value = UNCHANGED (byte-identical). FP = any byte changed.");
        Emit("");

        // ── PER-BOOK ENTITY SETS — SOURCED FROM THE REAL BookEntityProvider (be-c07) ────────────────────────
        // This harness used to HAND-AUTHOR two literal HashSets and describe them as "exactly what a
        // deterministic BookEntityProvider WOULD surface". That was an ASSUMPTION, not a measurement: it never
        // constructed the provider, never threaded a bookId, and never touched the DB path that ships — so the
        // SCRIPT-AWARE harvest and the bookId threading (the entire e2/e3 contribution) were precisely the parts
        // this gate did NOT measure, and this gate is what flipped the production default ON.
        //
        // Now the sets come from the SHIPPED provider reading a REAL DbContext over two synthetic books
        // (PreservationFixtureBooks — a Hebrew-native book whose chapters carry the Latin names, and a
        // Latin-native book whose chapters carry the recurring Hebrew names plus a stored CharacterAnalysis).
        // A regression in the harvest therefore surfaces HERE as a GATE FAILURE, not as a silently-still-passing
        // hard-coded set. The seeding itself has deterministic coverage in BookEntityFixtureSeedTests.
        using var fixtureBooks = await PreservationFixtureBooks.CreateAsync();
        var hebrewBookEntities = await fixtureBooks.HebrewBookEntitiesAsync();
        var englishBookEntities = await fixtureBooks.EnglishBookEntitiesAsync();
        // q1: the SYNOPSIS book — a third seeded book, read through the SAME real provider, because a synopsis
        // is generated FOR a book and the entity lever must come from that book, not from a hand-authored set.
        var synopsisBookEntities = await fixtureBooks.SynopsisBookEntitiesAsync();

        // Per-case entities = the entity set of the case's BOOK, straight from the provider. No hand-fed union,
        // no per-case override: if a case needs an entity the provider cannot produce, that is a FINDING and the
        // report says so (see the "Entity-lever provenance" section below).
        IReadOnlySet<string> EntitiesFor(LegitCase c)
            => c.BookKey == PreservationFixtureBooks.SynopsisBookKey ? synopsisBookEntities
             : c.Expected == ExpectedScript.Hebrew ? hebrewBookEntities : englishBookEntities;

        // ── LEGITIMATE-TERM SET ────────────────────────────────────────────────────────────────────────────
        // Each value is realistic Hebrew (or, for the last three, English) analysis prose that contains a
        // foreign token which MUST survive byte-identical. Cls = the class it stresses; Note records the expected
        // gate. The REPAIR/LEAVE expectations are UNCHANGED from the e4 fixture — only the SOURCE of the entity
        // set changed. The set lives in PreservationFixtureBooks.Cases so the deterministic seed test and this
        // live gate measure the SAME cases against the SAME books.

        // q1 appends the SYNOPSIS-shaped values (multi-paragraph Hebrew editorial prose, dense in legitimate
        // proper nouns) to the SAME measured set, driven by the SAME instrument against the SAME bar. They are
        // a SEPARATE fixture list so the analysis-prose set's own pins (BookEntityFixtureSeedTests) stay exact,
        // and so the report can summarise the two subsets independently, see the subset section below.
        //
        // SCOPING, DERIVED FROM THE SHIPPED CONFIG (be-c01, re-derived after f2). The d6 AGGREGATE
        // (tierPreservationPct / tierOverRewrite) and the **d6 VERDICT** are computed over the GATED CORPUS,
        // and membership in that corpus comes from `ShippedRepairCorpus`, i.e. from the real
        // `Ai:AnalysisRepair:PerType` map read through `AnalysisRepairGate.Evaluate` (the SAME predicate the
        // production call sites consult). It is NOT a hardcoded "everything except Synopsis" split. be-c01
        // excluded the SYNOPSIS values while `Synopsis` was unrepaired, which was right then; `f2` enabled it
        // in both appsettings files and the literal split stranded the corpus, so a Synopsis regression could
        // no longer move a verdict about a type the product DOES repair. The fixture still supplies the SUBSET
        // attribution (a case routed to the SYNOPSIS book is a `Synopsis` value); the config decides whether
        // that subset GATES. Pinned deterministically by `ShippedRepairCorpusTests`.
        // Same shared builder rule as the d5 harness: one definition of the corpus and its scope, called by
        // both this harness and the deterministic pin in `ShippedRepairCorpusTests`.
        var (cases, scope) = ShippedRepairCorpus.D6PreservationCorpus();
        // Index lists — every array below (predicted, per-tier outcomes) is PARALLEL to `cases`, so all
        // scoping is done by INDEX against these lists, never by re-filtering a projected sequence.
        var gatedIdx = scope.Gated;
        var proseIdx = scope.AnalysisProse;
        var synopsisIdx = scope.Synopsis;
        bool IsGatedCase(int i) => scope.IsGated(i);

        var hebrewSet = hebrewBookEntities as BookEntitySet;
        var englishSet = englishBookEntities as BookEntitySet;
        var hebrewCaseCount = cases.Count(c => c.Expected == ExpectedScript.Hebrew);
        var gatedHebrewCount = gatedIdx.Count(i => cases[i].Expected == ExpectedScript.Hebrew);
        Emit($"Legitimate-term set: {cases.Count} values ({hebrewCaseCount} Hebrew-native + "
             + $"{cases.Count - hebrewCaseCount} English-native), of which **{gatedIdx.Count} are in the GATED "
             + $"CORPUS** ({gatedHebrewCount} Hebrew-native + {gatedIdx.Count - gatedHebrewCount} English-native): "
             + $"{proseIdx.Count} ANALYSIS-PROSE + {synopsisIdx.Count} q1 SYNOPSIS.");
        Emit($"**SCOPE OF THE VERDICT: the d6 aggregate and the `d6 VERDICT` line below are computed over the "
             + $"{gatedIdx.Count} values of the GATED CORPUS. {scope.CorpusSentence}** Both subsets are also broken "
             + "out separately below, so the pre-f2 figures of record stay quotable off this report.");
        Emit("Per-book entity gate ACTIVE, and sourced from the REAL `BookEntityProvider.GetEntitiesAsync(bookId,");
        Emit("language)` over a REAL DbContext (be-c07 — the e4 run hand-authored these sets, so the harvest logic");
        Emit("and the bookId threading were never on the measured path). The language passed is each case's own");
        Emit("ANALYSIS language, so the harvest direction matches the classifier's expected script BY CONSTRUCTION");
        Emit("(final-r02):");
        Emit($"- HEBREW-native book (`Language=he`, foreign script = Latin): **{hebrewBookEntities.Count}** entities"
             + (hebrewSet is not null
                 ? $" — {hebrewSet.ManuscriptTokens.Count} manuscript-harvested (case-SENSITIVE), {hebrewSet.DeclaredNames.Count} declared (case-insensitive)"
                 : ""));
        Emit($"- LATIN-native book (`Language=en`, foreign script = Hebrew): **{englishBookEntities.Count}** entities"
             + (englishSet is not null
                 ? $" — {englishSet.ManuscriptTokens.Count} manuscript-harvested, {englishSet.DeclaredNames.Count} declared"
                 : ""));
        Emit("Lowercase name particles (van/da/de, and the adjacent pairs der/la/of/the) are NOT harvestable (they are");
        Emit("not Title-Case), so the be-c01 name-particle rule is still the thing being exercised on those cases —");
        Emit("the entity set cannot mask it.");
        Emit("");
        Emit("**The three be-c01 P0 shapes are in the set (be-c08):** `The Lord of the Rings`, `Mies van der Rohe`,");
        Emit("`Charles de la Rue` — the values that CORRUPTED under the un-patched rule, which recognized only a");
        Emit("SINGLE lowercase particle, so two ADJACENT particles disqualified each other and BOTH went to the repair");
        Emit("model (`of`+`the`, `van`+`der`, `de`+`la`), which spliced Hebrew into a book title / surname. NONE of");
        Emit("their tokens is seeded into ANY book's manuscript, so the provider CANNOT harvest them and the entity");
        Emit("lever is inert for them BY CONSTRUCTION: they can only be gated by the deterministic classifier rule (8)");
        Emit("name-span walk, at ZERO model calls. That is the invariant this rollout decision rests on — read it off");
        Emit("the provenance table below (`gated by` = classifier/detector rule) and off the preservation table");
        Emit("(`runs / repair` = N / 0). Deterministically pinned in `BookEntityFixtureSeedTests`.");
        Emit("");

        // ── Deterministic per-case gate prediction (tier-independent, ZERO model calls) ──────────────────────
        // Runs d1 detect + d2 classify OFF-LINE so the report can attribute WHERE the safety comes from, and —
        // new in be-c07 — WHETHER the entity that gated a case was actually HARVESTED by the provider. Each case
        // is classified TWICE: with the provider's set and with NO set. A run that flips REPAIR -> LEAVE is one
        // the ENTITY LEVER spared (it exercises BookEntityProvider and regresses if the harvest breaks); a run
        // that is LEAVE either way was spared by a CLASSIFIER RULE (Title-Case / ALL-CAPS / name-span / quote /
        // URL) and the entity set is inert for it.
        var predicted = new GateAttribution[cases.Count];
        for (var i = 0; i < cases.Count; i++)
        {
            predicted[i] = PreservationFixtureBooks.AttributeGate(cases[i], EntitiesFor(cases[i]));
        }

        // ── ENTITY-LEVER PROVENANCE — the be-c07 measurement (deterministic, no model) ───────────────────────
        Emit("## Entity-lever provenance (which cases the REAL provider actually gates)");
        Emit("");
        Emit("| # | subset | class | token | gated by | provider entity | tier |");
        Emit("|---|---|---|---|---|---|---|");
        for (var i = 0; i < cases.Count; i++)
        {
            var c = cases[i];
            var p = predicted[i];
            var gatedBy = p.ReachesModel
                ? "REACHES MODEL"
                : p.EntityLoadBearing ? "**entity (provider-harvested)**" : "classifier/detector rule";
            var ent = p.EntityLoadBearing ? "`" + string.Join("`, `", p.EntitySparedRuns.Distinct()) + "`" : "—";
            var tier = p.EntityLoadBearing ? string.Join(", ", p.EntitySparedTiers.Distinct()) : "—";
            Emit($"| {i + 1} | {scope.SubsetLabel(i)}{(IsGatedCase(i) ? "" : " (NOT gated)")} | {c.Cls} | `{Trunc(c.Token, 22)}` | {gatedBy} | {ent} | {tier} |");
        }
        Emit("");

        // These counts are consumed by the "Residual weaknesses" section, whose denominators must match the
        // VERDICT's corpus, so they are GATED-CORPUS-scoped. The two labelled subsets are computed and emitted
        // alongside so the composition is explicit rather than implied.
        var entityGatedCount = gatedIdx.Count(i => predicted[i].EntityLoadBearing);
        var ruleGatedCount = gatedIdx.Count(i => !predicted[i].ReachesModel && !predicted[i].EntityLoadBearing);
        var modelReachedCount = gatedIdx.Count(i => predicted[i].ReachesModel);
        var proseEntityGatedCount = proseIdx.Count(i => predicted[i].EntityLoadBearing);
        var proseRuleGatedCount = proseIdx.Count(i => !predicted[i].ReachesModel && !predicted[i].EntityLoadBearing);
        var proseModelReachedCount = proseIdx.Count(i => predicted[i].ReachesModel);
        var synEntityGatedCount = synopsisIdx.Count(i => predicted[i].EntityLoadBearing);
        var synRuleGatedCount = synopsisIdx.Count(i => !predicted[i].ReachesModel && !predicted[i].EntityLoadBearing);
        var synModelReachedCount = synopsisIdx.Count(i => predicted[i].ReachesModel);
        Emit($"**Gate attribution - GATED CORPUS ({gatedIdx.Count} values): {entityGatedCount} case(s) "
             + $"gated by a PROVIDER-HARVESTED entity, {ruleGatedCount} by a classifier/detector rule, "
             + $"{modelReachedCount} reach the model.**");
        Emit($"Gate attribution - ANALYSIS-PROSE subset ({proseIdx.Count} values"
             + $"{(scope.AnalysisProseRepaired ? "" : ", NOT gated")}): {proseEntityGatedCount} entity-gated, "
             + $"{proseRuleGatedCount} rule-gated, {proseModelReachedCount} reach the model.");
        Emit($"Gate attribution - SYNOPSIS subset ({synopsisIdx.Count} values"
             + $"{(scope.SynopsisRepaired ? "" : ", NOT gated")}): {synEntityGatedCount} entity-gated, "
             + $"{synRuleGatedCount} rule-gated, {synModelReachedCount} reach the model.");
        Emit("");

        // FINDINGS: a case that DECLARES a required entity the provider could NOT produce could only ever have
        // passed with a hand-fed entity. Record it plainly — never paper over it with a hard-coded fallback.
        // SCOPE: deliberately GLOBAL, over both subsets whether gated or not. This is a deterministic
        // FIXTURE-INTEGRITY finding (the provider cannot produce a declared entity), not a stochastic quality
        // number, so it is reported for BOTH subsets, each line labelled with the subset it came from.
        var missingEntityCases = Enumerable.Range(0, cases.Count).Where(i => predicted[i].RequiredEntityMissing).ToList();
        if (missingEntityCases.Count == 0)
        {
            Emit("FINDINGS (both subsets): none — every case that requires a per-book entity is gated by an entity the "
                 + "REAL `BookEntityProvider` actually harvested. No hand-fed entity is needed anywhere in this fixture.");
        }
        else
        {
            Emit("**FINDINGS (both subsets) — cases that can ONLY pass with a HAND-FED entity the provider cannot produce:**");
            foreach (var i in missingEntityCases)
            {
                Emit($"- [{scope.SubsetLabel(i)}] [{cases[i].Cls}] `{cases[i].Token}` requires "
                     + $"entity `{cases[i].GatingEntity}` — NOT harvested. "
                     + "The provider cannot gate this case; it reaches the repair model in production.");
            }
        }
        Emit("");
        Emit("Recorded, not a finding: the ALL-CAPS acronyms (`NASA`, `PDF`) are present in the seeded manuscript but");
        Emit("are NOT harvestable — the manuscript scan records only TITLE-CASE Latin tokens. They do not need the");
        Emit("entity lever: classifier rule (6) (ALL-CAPS) gates them with zero model calls. The e4 hand-built set");
        Emit("listed them as entities, which OVERSTATED what the provider can produce.");
        Emit("");

        // ── Tiers (reuse the d5 tier router + preflight) ─────────────────────────────────────────────────────
        var tiers = new (string name, string provider, string model)[]
        {
            ("LOCAL",  "Ollama",     "gemma4:12b"),
            ("CLOUD",  "OpenRouter", "google/gemma-4-31b-it"),
        };

        var outcomes = new Dictionary<string, LegitOutcome[]>();
        var blocked = new Dictionary<string, string?>();

        foreach (var (tierName, provider, model) in tiers)
        {
            Emit("---");
            Emit("");
            Emit($"## Tier {tierName}: `{provider}|{model}`");
            Emit("");

            // CLOUD is OUT OF SCOPE for be-c08 (the user scoped the re-measure to LOCAL). Skip it BEFORE any call
            // when no OpenRouter key is configured, so it costs nothing and never fails the run.
            if (provider == "OpenRouter" && !CloudKeyAvailable())
            {
                Emit("SKIPPED: no OpenRouter API key configured (env `AI_OPENROUTER_APIKEY`). be-c08 is scoped to the");
                Emit("LOCAL tier; the cloud tier stays routing-only and is not measured here.");
                Emit("");
                blocked[tierName] = "SKIPPED (no OpenRouter key; be-c08 is LOCAL-only)";
                continue;
            }

            var router = BuildTermRepairTierRouter(appSettingsPath, provider, model);
            var service = new DynamicTermRepairService(router, NullLogger<DynamicTermRepairService>.Instance);

            var (ok, preInfo) = await PreflightTierAsync(router);
            if (!ok)
            {
                Emit($"BLOCKED: tier `{tierName}` preflight failed → {preInfo}");
                Emit("(No preservation numbers for this tier — a fail-safe revert must NOT read as clean preservation.)");
                Emit("");
                blocked[tierName] = preInfo;
                continue;
            }
            Emit($"preflight OK → served by `{preInfo}`");
            Emit("");

            var tierOutcomes = new LegitOutcome[cases.Count];
            for (var i = 0; i < cases.Count; i++)
            {
                var c = cases[i];
                var runs = LatinInHebrewContentDetector.DetectForeignRuns(c.Value, c.Expected);
                var repairRuns = ForeignRunClassifier.RunsToRepair(runs, c.Value, c.Expected, EntitiesFor(c));

                var result = await service.RepairValueAsync(c.Value, c.Expected, c.Language, EntitiesFor(c));
                var preserved = string.Equals(c.Value, result.Value, StringComparison.Ordinal);
                var overRewrite = !preserved && !ChangeConfinedToRepairSpans(c.Value, repairRuns, result.Value);

                tierOutcomes[i] = new LegitOutcome
                {
                    Preserved = preserved,
                    OverRewrite = overRewrite,
                    Repaired = result.Value,
                    LatencyMs = result.LatencyMs,
                    Fault = result.Fault is not null,
                    ReachedModel = repairRuns.Count > 0
                };
            }
            outcomes[tierName] = tierOutcomes;
        }

        // ── FP TABLE (both tiers side by side) ───────────────────────────────────────────────────────────────
        Emit("---");
        Emit("");
        Emit("## Preservation table (per case, both tiers, BOTH subsets; the `subset` column says which)");
        Emit("");
        Emit("| # | subset | class | token | runs / repair | predicted gate | LOCAL | CLOUD |");
        Emit("|---|---|---|---|---|---|---|---|");
        for (var i = 0; i < cases.Count; i++)
        {
            var c = cases[i];
            var p = predicted[i];
            Emit($"| {i + 1} | {scope.SubsetLabel(i)}{(IsGatedCase(i) ? "" : " (NOT gated)")} | {c.Cls} | `{Trunc(c.Token, 22)}` | {p.Runs} / {p.RepairRuns} | {p.Gate} | {CellFor("LOCAL", outcomes, blocked, i)} | {CellFor("CLOUD", outcomes, blocked, i)} |");
        }
        Emit("");

        // ── Per-tier summaries + per-class breakdown + concrete FPs ──────────────────────────────────────────
        var tierPreservationPct = new Dictionary<string, double>();
        var tierOverRewrite = new Dictionary<string, int>();
        var tierBlocked = new Dictionary<string, bool>();
        // EVERY number in this loop — and therefore tierPreservationPct / tierOverRewrite, the two dictionaries
        // the `Shippable()` bar and the d6 VERDICT read — is scoped to `gatedIdx`, the config-derived gated
        // corpus. Each labelled subset is broken out on its own line at the end of the loop.
        foreach (var (tierName, _, _) in tiers)
        {
            Emit($"### {tierName} summary: GATED CORPUS ({gatedIdx.Count} values = {proseIdx.Count} ANALYSIS-PROSE "
                 + $"{(scope.AnalysisProseRepaired ? "+" : "excluded, +")} {synopsisIdx.Count} SYNOPSIS"
                 + $"{(scope.SynopsisRepaired ? "" : " excluded")}; per-subset lines below)");
            if (blocked.ContainsKey(tierName))
            {
                Emit($"BLOCKED: {FirstLine(blocked[tierName] ?? "")}");
                Emit("");
                tierBlocked[tierName] = true;
                continue;
            }
            tierBlocked[tierName] = false;

            var os = outcomes[tierName];
            var total = gatedIdx.Count;
            var preserved = gatedIdx.Count(i => os[i].Preserved);
            var fp = total - preserved;
            var over = gatedIdx.Count(i => os[i].OverRewrite);
            var faults = gatedIdx.Count(i => os[i].Fault);
            var pct = total > 0 ? 100.0 * preserved / total : 0;
            tierPreservationPct[tierName] = pct;
            tierOverRewrite[tierName] = over;

            // Where the safety came from.
            var gated = gatedIdx.Count(i => !os[i].ReachedModel);
            var gatedPreserved = gatedIdx.Count(i => !os[i].ReachedModel && os[i].Preserved);
            var reached = gatedIdx.Count(i => os[i].ReachedModel);
            var reachedPreserved = gatedIdx.Count(i => os[i].ReachedModel && os[i].Preserved);

            var reachedLatencies = gatedIdx.Where(i => os[i].ReachedModel).Select(i => os[i].LatencyMs).ToList();
            var (med, p90) = LatencyStats(reachedLatencies);

            Emit($"- PRESERVED {preserved}/{total} (**{pct:F0}%**)  |  FALSE-POSITIVE {fp}  |  over-rewrite {over}"
                 + (faults > 0 ? $"  |  per-call fault(s) {faults}" : ""));
            Emit($"- safety source: classifier/detector-GATED {gatedPreserved}/{gated} preserved (0 model calls); MODEL-preserved {reachedPreserved}/{reached} reached-model preserved");
            Emit($"- reached-model latency median {med} ms / p90 {p90} ms ({reached} value(s) hit the model)");

            // Per-class breakdown (gated vs model-preserved), so the report shows where the safety sits.
            Emit("");
            Emit($"  per-class ({tierName}, GATED CORPUS): preserved / total  [gated | model]");
            foreach (var grp in gatedIdx.GroupBy(i => cases[i].Cls))
            {
                var idxs = grp.ToList();
                var cPres = idxs.Count(i => os[i].Preserved);
                var cGate = idxs.Count(i => !os[i].ReachedModel);
                var cModel = idxs.Count(i => os[i].ReachedModel);
                Emit($"  - {grp.Key}: {cPres}/{idxs.Count}  [{cGate} gated | {cModel} model]");
            }

            // Concrete false positives (the legit token that was wrongly altered).
            var fps = gatedIdx.Where(i => !os[i].Preserved).ToList();
            Emit("");
            if (fps.Count == 0)
            {
                Emit($"  concrete false positives ({tierName}, GATED CORPUS): NONE — every legitimate token preserved byte-identical.");
            }
            else
            {
                Emit($"  concrete false positives ({tierName}, GATED CORPUS):");
                foreach (var i in fps)
                    Emit($"  - [{scope.SubsetLabel(i)}] [{cases[i].Cls}] `{Trunc(cases[i].Token, 24)}`  |  before: `{Trunc(cases[i].Value, 70)}`  →  after: `{Trunc(os[i].Repaired, 70)}`  ({(os[i].OverRewrite ? "OVER-REWRITE" : "token-only")})");
            }
            // The same aggregate split by LABELLED SUBSET, so the pre-f2 ANALYSIS-PROSE figures of record stay
            // directly comparable and a synopsis FP is attributable at a glance. Full per-FP detail below.
            void EmitCaseSubsetLine(string label, IReadOnlyList<int> idx, bool isGated)
            {
                Emit($"  ({label} subset{(isGated ? ", INSIDE the aggregate above" : ", NOT gated, OUTSIDE the aggregate above")}: "
                     + $"preserved {idx.Count(i => os[i].Preserved)}/{idx.Count}"
                     + $"  |  over-rewrite {idx.Count(i => os[i].OverRewrite)})");
            }
            EmitCaseSubsetLine("ANALYSIS-PROSE", proseIdx, scope.AnalysisProseRepaired);
            EmitCaseSubsetLine("SYNOPSIS", synopsisIdx, scope.SynopsisRepaired);
            Emit("");
        }

        // ── THE TWO LABELLED SUBSETS OF THE GATED CORPUS ─────────────────────────────────────────────────────
        // The aggregate above is GATED-CORPUS-scoped, so this section is where its two labelled subsets sit
        // side by side against the SAME bar (preservation >= 90% AND over-rewrite == 0). Keeping them broken
        // out is what lets the pre-f2 ANALYSIS-PROSE figures of record (21/21) and the q1/q2 SYNOPSIS numbers
        // stay quotable off the artifact even though both now feed one verdict. Gate ATTRIBUTION is reported
        // too: a 100% that comes from everything being deterministically gated is a property of the GATE, not
        // of the model.
        Emit("---");
        Emit("");
        Emit("## Labelled subsets of the gated corpus (same instrument, same bar)");
        Emit("");
        Emit($"The LOADED set is {cases.Count} values: **{proseIdx.Count} ANALYSIS-PROSE** (the pre-f2 corpus of "
             + $"record) + **{synopsisIdx.Count} SYNOPSIS** (multi-paragraph Hebrew editorial prose of the shape "
             + "`SynopsisHe` asks for, PromptFactory.cs:998-1001, dense in legitimate proper nouns). "
             + $"{scope.CorpusSentence} A subset marked NOT gated is measured and reported but cannot move the "
             + "verdict; under the shipped config both subsets DO move it.");
        Emit("");

        void EmitSubset(string subsetName, IReadOnlyList<int> idx)
        {
            var entityGated = idx.Count(i => predicted[i].EntityLoadBearing);
            var ruleGated = idx.Count(i => !predicted[i].ReachesModel && !predicted[i].EntityLoadBearing);
            var reachesModel = idx.Count(i => predicted[i].ReachesModel);

            Emit($"### {subsetName} ({idx.Count} value(s))");
            Emit("");
            Emit($"- gate attribution (deterministic, tier-independent): **{entityGated} entity-gated** "
                 + $"(provider-harvested), **{ruleGated} classifier/detector-rule-gated**, "
                 + $"**{reachesModel} REACH THE MODEL**.");

            foreach (var (tierName, provider, model) in tiers)
            {
                if (tierBlocked.GetValueOrDefault(tierName, true))
                {
                    Emit($"- {tierName} (`{provider}|{model}`): BLOCKED — no numbers.");
                    continue;
                }

                var os = outcomes[tierName];
                var total = idx.Count;
                var preserved = idx.Count(i => os[i].Preserved);
                var over = idx.Count(i => os[i].OverRewrite);
                var pct = total > 0 ? 100.0 * preserved / total : 0;
                var meets = pct >= 90.0 && over == 0;
                Emit($"- {tierName} (`{provider}|{model}`): preservation **{pct:F0}%** ({preserved}/{total})  |  "
                     + $"false positives **{total - preserved}**  |  over-rewrite **{over}** (bar = 0)  |  "
                     + $"**{(meets ? "MEETS THE BAR" : "DOES NOT MEET THE BAR")}**");

                var fps = idx.Where(i => !os[i].Preserved).ToList();
                if (fps.Count > 0)
                {
                    foreach (var i in fps)
                    {
                        // The FIRST DIFFERING REGION, not the head of the value: a synopsis-length value's
                        // change can sit hundreds of chars in, and a truncated head shows an identical prefix
                        // twice and reads as "nothing changed" (RULE 0 — show the artifact where it differs).
                        var (beforeCtx, afterCtx) = FirstDiffContext(cases[i].Value, os[i].Repaired ?? "");
                        Emit($"  - FP [{cases[i].Cls}] `{Trunc(cases[i].Token, 30)}` "
                             + $"({(os[i].OverRewrite ? "**OVER-REWRITE**" : "token-only")})");
                        Emit($"    - before: …`{beforeCtx}`…");
                        Emit($"    - after:  …`{afterCtx}`…");
                    }
                }

                var reached = idx.Where(i => os[i].ReachedModel).ToList();
                Emit($"  - values that reached the model on this tier: **{reached.Count}** "
                     + (reached.Count == 0
                         ? "(so this tier's number is a property of the DETERMINISTIC GATE, not of the model)"
                         : $"— {string.Join(", ", reached.Select(i => $"`{Trunc(cases[i].Token, 24)}` ({(os[i].Preserved ? "preserved" : "**ALTERED**")})"))}"));
            }

            Emit("");
        }

        EmitSubset($"ANALYSIS-PROSE subset (the pre-f2 corpus of record; {(scope.AnalysisProseRepaired ? "INSIDE the gated corpus" : "NOT gated")})", proseIdx);
        EmitSubset($"SYNOPSIS subset (q1 fixtures; {(scope.SynopsisRepaired ? "INSIDE the gated corpus since f2 enabled `Synopsis`" : "NOT gated")})", synopsisIdx);

        // ── NON-REGRESSION vs the shipped glossary under GlossaryThenDynamic (deterministic, no GPU) ──────────
        // Confirms the closed glossary STILL cleans its known terms under GlossaryThenDynamic: the glossary
        // fast-path replaces the term, and the dynamic pass over the glossary-cleaned residual is a byte-
        // identical no-op (the cleaned value has NO foreign runs, so d1 detects nothing → ZERO model calls →
        // dynamic cannot undo the glossary). Deterministic: the glossary + the detector gate are pure.
        Emit("---");
        Emit("");
        Emit("## Non-regression: shipped glossary still cleans under GlossaryThenDynamic (deterministic)");
        Emit("");
        var glossaryProbes = new (string term, string hebrew, string value)[]
        {
            ("narrator", "מספר",     "המבנה מסתמך על narrator יודע-כל לאורך כל הרומן."),
            ("tension",  "מתח",      "הסצנה בונה tension רב עד לרגע ההתרה."),
            ("irony",    "אירוניה",  "הסיום טעון irony מרירה כלפי הגיבור."),
        };
        var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var dynamicForNonReg = new DynamicTermRepairService(
            BuildTermRepairTierRouter(appSettingsPath, "Ollama", "gemma4:12b"),
            NullLogger<DynamicTermRepairService>.Instance);
        var nonRegressionOk = true;
        Emit("| term | glossary cleaned? | Hebrew equiv present? | Latin residual after glossary | dynamic (GlossaryThenDynamic) undoes it? |");
        Emit("|---|---|---|---|---|");
        foreach (var (term, hebrew, value) in glossaryProbes)
        {
            // Stage 1: glossary fast-path (Summarization = whole cleanContent is the prose).
            var g = GlossaryRepairPass.Apply(AnalysisType.Summarization, null, value, "he-IL", jsonOpts);
            var cleaned = g.CleanContent;
            var glossaryCleaned = !string.Equals(cleaned, value, StringComparison.Ordinal);
            var hasHebrew = cleaned.Contains(hebrew, StringComparison.Ordinal);
            var latinResidual = LatinInHebrewContentDetector.HasForeignRuns(cleaned, ExpectedScript.Hebrew);

            // Stage 2: dynamic over the glossary-cleaned residual — must be a byte-identical no-op.
            var afterDynamic = await dynamicForNonReg.RepairValueAsync(cleaned, ExpectedScript.Hebrew, "he-IL");
            var dynamicUndid = !string.Equals(afterDynamic.Value, cleaned, StringComparison.Ordinal);

            var ok = glossaryCleaned && hasHebrew && !latinResidual && !dynamicUndid;
            nonRegressionOk &= ok;
            Emit($"| `{term}` | {(glossaryCleaned ? "yes" : "**NO**")} | {(hasHebrew ? "yes" : "**NO**")} | {(latinResidual ? "**yes (leak!)**" : "none")} | {(dynamicUndid ? "**YES (regression!)**" : "no")} |");
        }
        Emit("");
        Emit($"**Non-regression: {(nonRegressionOk ? "PASS" : "FAIL")}** — glossary terms still clean under GlossaryThenDynamic and the dynamic stage does not undo them.");
        Emit("");

        // ── DECISION vs the AGREED BAR (preservation >= 90% AND over-rewrite == 0) ───────────────────────────
        Emit("---");
        Emit("");
        Emit($"## Decision vs agreed bar: GATED CORPUS ({gatedIdx.Count} values) "
             + "(preservation >= 90% AND over-rewrite == 0)");
        Emit("");
        Emit($"**Corpus of this decision: {scope.CorpusSentence} Each subset is also summarised on its own above, so "
             + "the pre-f2 figures of record stay directly comparable.**");
        Emit("");
        Emit("| tier | model | preservation % | over-rewrite (bar=0) | meets bar? |");
        Emit("|---|---|---|---|---|");
        bool Shippable(string t) => !tierBlocked.GetValueOrDefault(t, true)
            && tierPreservationPct.GetValueOrDefault(t, 0) >= 90.0
            && tierOverRewrite.GetValueOrDefault(t, int.MaxValue) == 0;
        foreach (var (tierName, provider, model) in tiers)
        {
            if (tierBlocked.GetValueOrDefault(tierName, true))
            {
                Emit($"| {tierName} | {provider}|{model} | BLOCKED | BLOCKED | no (BLOCKED) |");
                continue;
            }
            var pct = tierPreservationPct[tierName];
            var over = tierOverRewrite[tierName];
            Emit($"| {tierName} | {provider}|{model} | {pct:F0}% | {over} | {(Shippable(tierName) ? "**YES**" : "no")} |");
        }
        Emit("");

        var localShip = Shippable("LOCAL");
        var cloudShip = Shippable("CLOUD");
        string verdict;
        string recommendation;
        if (localShip)
        {
            verdict = "PASS";
            recommendation = "SHIP LOCAL (`Ollama|gemma4:12b`) + Mode=GlossaryThenDynamic — cheapest tier meets the bar.";
        }
        else if (cloudShip)
        {
            verdict = "PASS";
            recommendation = "SHIP CLOUD (`OpenRouter|google/gemma-4-31b-it`) + Mode=GlossaryThenDynamic — LOCAL missed the bar; CLOUD met it (d5 anticipated this).";
        }
        else
        {
            verdict = "HALT";
            recommendation = "HALT — NEITHER tier met the bar. Keep the SHIPPED default Mode=Glossary (dynamic stays available but OFF).";
        }
        Emit($"**d6 VERDICT (scoped to the GATED CORPUS: {gatedIdx.Count} values = {proseIdx.Count} ANALYSIS-PROSE "
             + $"{(scope.AnalysisProseRepaired ? "+" : "excluded, +")} {synopsisIdx.Count} SYNOPSIS"
             + $"{(scope.SynopsisRepaired ? "" : " excluded")}, covering the {scope.RepairedTypes.Count} analysis "
             + $"types the shipped `Ai:AnalysisRepair:PerType` map repairs): {verdict}.**  {recommendation}");
        Emit("(d6 records the precision-gated recommendation only; d7 owns the documented rollout / appsettings default.)");
        Emit("");

        // ── RESIDUAL WEAKNESSES / DEFERRALS (honest limits of this measure) ──────────────────────────────────
        Emit("## Residual weaknesses / deferrals");
        Emit("");
        Emit($"(Denominators in this section are the GATED CORPUS, {gatedIdx.Count} values, matching the verdict above. "
             + $"Per labelled subset: ANALYSIS-PROSE {proseEntityGatedCount} entity-gated, {proseRuleGatedCount} "
             + $"rule-gated, {proseModelReachedCount} reach the model of {proseIdx.Count}; SYNOPSIS "
             + $"{synEntityGatedCount} entity-gated, {synRuleGatedCount} rule-gated, {synModelReachedCount} reach the model "
             + $"of {synopsisIdx.Count}.)");
        Emit("");
        Emit($"- **Safety is carried by the DETERMINISTIC GATE, not by the tier.** {gatedIdx.Count - modelReachedCount}/{gatedIdx.Count} gated values never");
        Emit($"  reach the model (detector allowlist + d2 classifier LEAVE + the per-book entity lever), so they are");
        Emit($"  preserved identically on EVERY tier and cost 0 model calls. Only {modelReachedCount} gated value(s) are tier-sensitive.");
        Emit("  Read the preservation % accordingly: when the gate catches everything it is a property of the GATE,");
        Emit("  and this measure stops discriminating between LOCAL and CLOUD (that is the intended invariant — but");
        Emit("  it also means d6 no longer stresses the MODEL's preserve-a-proper-noun behaviour).");
        var entityGatedLatinCount = gatedIdx.Count(i => predicted[i].EntityLoadBearing && cases[i].Expected == ExpectedScript.Latin);
        Emit($"- **The entity lever load-bears on {entityGatedCount} of {gatedIdx.Count} gated cases "
             + $"({entityGatedLatinCount} Latin-native, {entityGatedCount - entityGatedLatinCount} Hebrew-native).** In the");
        Emit("  ANALYSIS-PROSE subset's HEBREW-native book every legit token is spared by a CLASSIFIER RULE (Title-Case");
        Emit("  mid-sentence, ALL-CAPS, the name-span walk, the be-c05 quote pair, URL/email), so the Latin harvest is");
        Emit("  belt-and-braces there, not load-bearing. In the LATIN-native direction there is NO case signal at");
        Emit("  all, so the provider's set is the ONLY thing standing between a Hebrew name and the repair model —");
        Emit("  which is exactly why hand-authoring it (as e4 did) hid the only place it actually matters. The");
        Emit("  SYNOPSIS subset adds the fixture's one HEBREW-native case authored to need the lever: a VALUE-INITIAL");
        Emit("  place name, the position classifier rules (7) and (7b) deliberately do not claim.");
        Emit("- **Hebrew-in-English direction is UNDER-MEASURED (deferral, unchanged).** Only 3 Latin-native values,");
        Emit("  all synthetic, and it was not stress-tested with Hebrew common-concept words (where 'translate' is");
        Emit("  arguably correct) — treat its preservation as indicative, not proven.");
        Emit("- **Lowercase foreign IDIOM is shape-indistinguishable from a leak.** `carpe diem` is now spared by the");
        Emit("  be-c05 matched-quote-pair rule, but an UNQUOTED lowercase idiom still looks EXACTLY like the");
        Emit("  lowercase out-of-glossary leaks d5 wants cleaned, and neither the classifier nor the model reliably");
        Emit("  spares it. An inherent precision floor of the dynamic pass; the do-not-translate allowlist for");
        Emit("  intentional foreign phrases remains a plan deferral.");
        Emit("- **The entity set is SYNTHETIC-book-sourced.** It is the REAL provider over a REAL DbContext (be-c07),");
        Emit("  but the books are seeded fixtures, not a real manuscript. What is now measured is the harvest LOGIC");
        Emit("  and the two-tier matching; the DENSITY of a real book's harvest is still unmeasured here.");
        Emit("");

        // ── Persist the RULE-0 artifact ──
        var outDir = Environment.GetEnvironmentVariable("DIAG_OUT_DIR");
        if (string.IsNullOrWhiteSpace(outDir)) outDir = Path.GetTempPath();
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, "d6-precision-fp-measure.md");
        File.WriteAllText(outPath, report.ToString(), new UTF8Encoding(false));
        _output.WriteLine("REPORT WRITTEN: " + outPath);

        // Non-regression is the ONE hard assertion (deterministic, GPU-independent): the shipped glossary
        // fast-path must keep cleaning under GlossaryThenDynamic and the dynamic stage must not undo it. The FP
        // numbers themselves are reported (not asserted) so a cloud/precision result never fails the harness.
        Assert.True(nonRegressionOk,
            "Non-regression FAILED: the closed glossary no longer cleans a known term under GlossaryThenDynamic, " +
            "or the dynamic stage undid a glossary substitution. See d6-precision-fp-measure.md.");
    }

    // ═══════════════════════════════════════════════════════════════════════════════════════════════════
    // q2 SCOPE (i) — THE c2 PROFILE PATH: MeasureProfilePathTermRepair_Local
    //
    // WHAT c2 CHANGED. `BookIntelligenceService.RepairStructuredProfileJsonAsync` used to call
    // `GlossaryRepairPass.RepairFields` and NOTHING else, so the PERSISTED `BookProfile.CharactersJson` /
    // `StoryStructureJson` received the closed 23-term glossary and 0% of the span-scoped dynamic stage. c2
    // made that hook two-stage. d5/d6 measure the dynamic stage at the `RepairValueAsync` seam; NEITHER of
    // them touches the profile path, because that path never reaches `UnifiedAnalysisService.RunAsync`.
    //
    // WHY A NEW HARNESS AND NOT A d5/d6 FIXTURE ROW. RULE 0: the artifact scope (i) is about is the string
    // that lands in the `BookProfile.CharactersJson` COLUMN. So this drives the REAL
    // `BookIntelligenceService.BuildBookProfileAsync` end to end and reads the measurement OFF THE PERSISTED
    // JSON — through the real `RepairableFields` accessors, the real reserialize, the real fail-safes.
    // d5's and d6's SCORING is untouched (their config-derived gated corpus, their verdicts): this is
    // an additional instrument for a path they cannot reach, not a change to theirs.
    //
    // WHAT IS CONTROLLED AND WHAT IS MEASURED. The four profile prompts are answered by a SCRIPTED router so
    // the corpus is deterministic (the same reason d5/d6 feed fixed values); ONLY `AiTaskType.TermRepair` is
    // forwarded to the REAL `Ollama|gemma4:12b` router that d5/d6 use. So the model is on exactly the surface
    // being measured and nowhere else, and the TermRepair call COUNT is an exact probe of the dynamic stage.
    //
    // THE ENTITY SET IS THE REAL ONE. The DI graph registers the production
    // `AddSingleton<IBookEntityProvider, BookEntityProvider>()` over the SAME in-memory `AppDbContext` the
    // service reads, seeded with `PreservationFixtureBooks`' HEBREW-native manuscript VERBATIM — so the hook's
    // own `GetEntitiesAsync(bookId, language, ct)` call produces a genuinely harvested set, never a hand-fed
    // one (the failure that invalidated the e4 tables).
    //
    // TWO CORPORA, TWO BOOKS (never the same book twice — a second build would harvest the FIRST build's
    // persisted CharactersJson, per be-c03's invalidation, and pollute the entity set):
    //   • CLEANING  — the 10 `PreservationFixtureBooks.LeakCases`, embedded in whitelisted prose fields.
    //     MEASURED: is the Latin leak run GONE from the PERSISTED value (d1 detector re-run on the output)?
    //   • PRESERVATION — the 18 HEBREW-expected `PreservationFixtureBooks.Cases`. (The 3 Latin-expected cases
    //     are structurally unreachable here: the hook derives ExpectedScript from the ANALYSIS language, so a
    //     Hebrew profile build cannot exercise the Hebrew-in-English direction. Stated, not hidden.)
    //     MEASURED: is the persisted value BYTE-IDENTICAL?
    // Both also measure OVER-REWRITE the same way d5/d6 do (`ChangeConfinedToRepairSpans` against THIS value's
    // classifier-predicted repair spans) and report model calls, so a 100% that comes from full deterministic
    // gating is visible AS a property of the gate.
    // Report -> DIAG_OUT_DIR/q2-scope-i-profile-path.md
    // ═══════════════════════════════════════════════════════════════════════════════════════════════════
    [Fact]
    [Trait("Category", "LiveDiagnostic")]
    public async Task MeasureProfilePathTermRepair_Local()
    {
        if (LiveDiagnosticsOptedOut()) return;
        if (!await IsOllamaReachableAsync())
        {
            _output.WriteLine("SKIP: Ollama not reachable at " + OllamaBaseUrl);
            return;
        }

        var appSettingsPath = FindUpward(Path.Combine("Pagedraft.Api", "appsettings.json"));
        Assert.True(File.Exists(appSettingsPath), "appsettings.json not found at " + appSettingsPath);

        var report = new StringBuilder();
        void Emit(string s) { _output.WriteLine(s); report.AppendLine(s); }

        Emit("# q2 scope (i) — c2 PROFILE PATH: is the PERSISTED `CharactersJson` / `StoryStructureJson` cleaned, and does preservation hold?");
        Emit("");
        Emit($"Run: {DateTime.Now:yyyy-MM-dd HH:mm}  |  routing base: `{appSettingsPath}`");
        Emit("Instrument: `OutputQualityDiagnostic.MeasureProfilePathTermRepair_Local` driving the REAL");
        Emit("`BookIntelligenceService.BuildBookProfileAsync` and reading every number OFF the PERSISTED");
        Emit("`BookProfile.CharactersJson` / `BookProfile.StoryStructureJson` (RULE 0 — the artifact, not the internals).");
        Emit("Bar (docs 18.2): preservation >= 90% AND over-rewrite EXACTLY 0.");
        Emit("");

        var termRepairRouter = BuildTermRepairTierRouter(appSettingsPath, "Ollama", "gemma4:12b");
        var (ok, preInfo) = await PreflightTierAsync(termRepairRouter);
        if (!ok)
        {
            Emit($"BLOCKED: LOCAL tier preflight failed -> {preInfo}");
            Emit("(No numbers recorded — a fail-safe revert must NOT read as a clean pass.)");
            WriteScopeIReport(report);
            return;
        }
        Emit($"preflight OK -> served by `{preInfo}` (TermRepair is the ONLY task routed to the real model here)");
        Emit("");

        // ── CORPUS 1: CLEANING (the d5 leak set, on the profile path) ─────────────────────────────────────
        var leakItems = PreservationFixtureBooks.LeakCases
            .Select(l => (l.Label, l.Value)).ToList();
        var (cleanChars, cleanStory, cleanSlots) = BuildProfileFixture(leakItems);
        var cleanRun = await RunProfileBuildAsync(termRepairRouter, cleanChars, cleanStory);

        // ── CORPUS 2: PRESERVATION (the d6 legit set, Hebrew direction, on the profile path) ──────────────
        var legitCases = PreservationFixtureBooks.Cases
            .Where(c => c.Expected == ExpectedScript.Hebrew).ToList();
        var legitSkipped = PreservationFixtureBooks.Cases.Count - legitCases.Count;
        var legitItems = legitCases.Select(c => (Label: c.Cls + " / " + c.Token, c.Value)).ToList();
        var (legitChars, legitStory, legitSlots) = BuildProfileFixture(legitItems);
        var legitRun = await RunProfileBuildAsync(termRepairRouter, legitChars, legitStory);

        // ── The REAL entity set both builds' hooks used ───────────────────────────────────────────────────
        Emit("## The entity lever (REAL `BookEntityProvider.GetEntitiesAsync`, not hand-fed)");
        Emit("");
        foreach (var (name, run) in new[] { ("CLEANING book", cleanRun), ("PRESERVATION book", legitRun) })
        {
            var set = run.Entities as BookEntitySet;
            Emit($"- {name} (`{run.BookId}`): **{run.Entities.Count}** entities"
                 + (set is not null
                     ? $" — {set.ManuscriptTokens.Count} manuscript-harvested (case-SENSITIVE), {set.DeclaredNames.Count} declared"
                     : " (NOT a BookEntitySet — tiers unavailable)"));
        }
        var harvested = PreservationFixtureBooks.HebrewBookLatinNames
            .Where(n => legitRun.Entities.Contains(n)).ToList();
        Emit($"- Latin names the fixture DEPENDS on that the provider actually harvested: "
             + $"**{harvested.Count}/{PreservationFixtureBooks.HebrewBookLatinNames.Count}** "
             + $"(`{string.Join("`, `", harvested)}`)");
        Emit("");

        // ── CLEANING table ────────────────────────────────────────────────────────────────────────────────
        Emit("---");
        Emit("");
        Emit($"## (i-a) CLEANING on the PERSISTED profile JSON — {cleanSlots.Count} values (`PreservationFixtureBooks.LeakCases`)");
        Emit("");
        Emit("Every value is out-of-glossary BY CONSTRUCTION, so the pre-c2 hook (glossary only) cleans 0/10 here.");
        Emit("`glossary alone?` re-runs the deterministic glossary over the ORIGINAL so a glossary hit could never be");
        Emit("mis-credited to the dynamic stage.");
        Emit("");
        Emit("| # | leak | field | model calls | glossary alone? | cleaned? | over-rewrite? | before -> after (span) |");
        Emit("|---|---|---|---|---|---|---|---|");
        var cleanCleaned = 0; var cleanOver = 0; var cleanCalls = 0; var cleanReached = 0;
        for (var i = 0; i < cleanSlots.Count; i++)
        {
            var slot = cleanSlots[i];
            var leak = PreservationFixtureBooks.LeakCases[i].Leak;
            var actual = slot.Read(cleanRun.Characters, cleanRun.Story);
            var runs = LatinInHebrewContentDetector.DetectForeignRuns(slot.Original, ExpectedScript.Hebrew);
            var repairRuns = ForeignRunClassifier.RunsToRepair(runs, slot.Original, ExpectedScript.Hebrew, cleanRun.Entities).ToList();
            var glossaryOnly = GlossaryAltersValue(slot.Original);
            var cleaned = !LatinInHebrewContentDetector.HasForeignRuns(actual, ExpectedScript.Hebrew);
            var changed = !string.Equals(slot.Original, actual, StringComparison.Ordinal);
            var overRewrite = changed && !ChangeConfinedToRepairSpans(slot.Original, repairRuns, actual);
            var span = runs.Count == 1
                ? SpanScopeCheck(slot.Original, runs[0].Start, runs[0].Length, actual) is var (sc, rep) && sc
                    ? $"`{leak}` -> `{rep.Trim()}`"
                    : $"`{leak}` -> NOT span-scoped: {Trunc(actual, 60)}"
                : $"(expected 1 Latin run, found {runs.Count})";

            if (cleaned) cleanCleaned++;
            if (overRewrite) cleanOver++;
            cleanCalls += repairRuns.Count;
            if (repairRuns.Count > 0) cleanReached++;

            Emit($"| {i + 1} | `{leak}` | {slot.Where} | {repairRuns.Count} | {(glossaryOnly ? "**yes**" : "no")} "
                 + $"| {(cleaned ? "yes" : "**NO**")} | {(overRewrite ? "**YES**" : "no")} | {span} |");
        }
        Emit("");
        Emit($"**(i-a) summary: cleaned {cleanCleaned}/{cleanSlots.Count} "
             + $"({100.0 * cleanCleaned / cleanSlots.Count:F0}%)  |  over-rewrite {cleanOver} (bar = 0)  |  "
             + $"values reaching the model {cleanReached}/{cleanSlots.Count}  |  model calls {cleanCalls} "
             + $"(router observed {cleanRun.TermRepairCalls}).**");
        Emit("");

        // ── PRESERVATION table ────────────────────────────────────────────────────────────────────────────
        Emit("---");
        Emit("");
        Emit($"## (i-b) PRESERVATION on the PERSISTED profile JSON — {legitSlots.Count} values (Hebrew-direction `PreservationFixtureBooks.Cases`)");
        Emit("");
        Emit($"{legitSkipped} Latin-expected (Hebrew-in-English) case(s) of the shipped 21 are NOT measurable on this path:");
        Emit("the hook derives ExpectedScript from the ANALYSIS language, so a Hebrew profile build cannot exercise that");
        Emit("direction at all. Recorded as a LIMIT of scope (i), not as a pass.");
        Emit("");
        Emit("| # | class | token | field | runs / repair | gated by | preserved? | over-rewrite? |");
        Emit("|---|---|---|---|---|---|---|---|");
        var legitPreserved = 0; var legitOver = 0; var legitCalls = 0; var legitReached = 0;
        var legitEntityGated = 0; var legitRuleGated = 0;
        var legitFps = new List<string>();
        for (var i = 0; i < legitSlots.Count; i++)
        {
            var slot = legitSlots[i];
            var c = legitCases[i];
            var actual = slot.Read(legitRun.Characters, legitRun.Story);
            var attribution = PreservationFixtureBooks.AttributeGate(c, legitRun.Entities);
            var runs = LatinInHebrewContentDetector.DetectForeignRuns(slot.Original, ExpectedScript.Hebrew);
            var repairRuns = ForeignRunClassifier.RunsToRepair(runs, slot.Original, ExpectedScript.Hebrew, legitRun.Entities).ToList();
            var preserved = string.Equals(slot.Original, actual, StringComparison.Ordinal);
            var overRewrite = !preserved && !ChangeConfinedToRepairSpans(slot.Original, repairRuns, actual);

            if (preserved) legitPreserved++; else legitFps.Add($"[{c.Cls}] `{c.Token}` -> `{Trunc(actual, 80)}`");
            if (overRewrite) legitOver++;
            legitCalls += repairRuns.Count;
            if (repairRuns.Count > 0) legitReached++;
            if (attribution.EntityLoadBearing) legitEntityGated++;
            else if (!attribution.ReachesModel) legitRuleGated++;

            var gatedBy = attribution.ReachesModel
                ? "REACHES MODEL"
                : attribution.EntityLoadBearing ? "**entity (provider-harvested)**" : "classifier/detector rule";
            Emit($"| {i + 1} | {c.Cls} | `{Trunc(c.Token, 22)}` | {slot.Where} | {runs.Count} / {repairRuns.Count} "
                 + $"| {gatedBy} | {(preserved ? "yes" : "**NO**")} | {(overRewrite ? "**YES**" : "no")} |");
        }
        Emit("");
        var legitPct = 100.0 * legitPreserved / legitSlots.Count;
        Emit($"**(i-b) summary: preserved {legitPreserved}/{legitSlots.Count} (**{legitPct:F0}%**)  |  "
             + $"false positives {legitSlots.Count - legitPreserved}  |  over-rewrite {legitOver} (bar = 0)  |  "
             + $"values reaching the model {legitReached}/{legitSlots.Count}  |  model calls {legitCalls} "
             + $"(router observed {legitRun.TermRepairCalls}).**");
        Emit($"**Gate attribution: {legitEntityGated} entity-gated (provider-harvested), {legitRuleGated} "
             + $"classifier/detector-rule-gated, {legitSlots.Count - legitEntityGated - legitRuleGated} reach the model.**"
             + (legitReached == 0
                 ? "  With ZERO values reaching the model this number is a property of the DETERMINISTIC GATE, not of gemma4:12b."
                 : ""));
        if (legitFps.Count > 0)
        {
            Emit("");
            Emit("Concrete false positives:");
            foreach (var f in legitFps) Emit("- " + f);
        }
        Emit("");

        // ── MUST-NOT-TOUCH (the non-repairable fields the hook must never reach) ──────────────────────────
        Emit("---");
        Emit("");
        Emit("## Must-not-touch fields (non-repairable by `RepairableFields`, and LATIN — so a whole-value pass would eat them)");
        Emit("");
        foreach (var (name, run) in new[] { ("CLEANING", cleanRun), ("PRESERVATION", legitRun) })
        {
            var roles = run.Characters.Characters.Select(x => x.Role).Distinct().ToList();
            var types = run.Story.Conflicts.Select(x => x.Type).Distinct().ToList();
            var statuses = run.Story.Conflicts.Select(x => x.Status).Distinct().ToList();
            var intact = roles.All(r => r is "protagonist" or "supporting")
                         && types.All(t => t == "external") && statuses.All(s => s == "ongoing");
            Emit($"- {name} book: roles `{string.Join(", ", roles)}` | conflict types `{string.Join(", ", types)}` "
                 + $"| statuses `{string.Join(", ", statuses)}` -> **{(intact ? "INTACT" : "ALTERED")}**");
        }
        Emit("");
        Emit("FE-parseability (the reserialize strips the model's ```json fence): "
             + $"CLEANING `{(cleanRun.CharactersJson.Contains("```") ? "FENCE PRESENT (regression)" : "clean")}`, "
             + $"PRESERVATION `{(legitRun.CharactersJson.Contains("```") ? "FENCE PRESENT (regression)" : "clean")}`.");
        Emit("");

        // ── VERDICT ───────────────────────────────────────────────────────────────────────────────────────
        Emit("---");
        Emit("");
        Emit("## Scope (i) verdict vs the shipped bar (preservation >= 90% AND over-rewrite EXACTLY 0)");
        Emit("");
        var scopeIPass = legitPct >= 90.0 && legitOver == 0 && cleanOver == 0;
        Emit($"| metric | value | bar |");
        Emit($"|---|---|---|");
        Emit($"| (i-a) cleaned on the PERSISTED JSON | {cleanCleaned}/{cleanSlots.Count} | pre-c2 baseline was 0/{cleanSlots.Count} (glossary-only) |");
        Emit($"| (i-a) over-rewrite | {cleanOver} | 0 |");
        Emit($"| (i-b) preservation | {legitPct:F0}% ({legitPreserved}/{legitSlots.Count}) | >= 90% |");
        Emit($"| (i-b) over-rewrite | {legitOver} | 0 |");
        Emit("");
        Emit($"**SCOPE (i) VERDICT: {(scopeIPass ? "PASS" : "HALT")}.**");
        Emit("");

        WriteScopeIReport(report);

        // The ONE hard assertion is DETERMINISTIC and about FIXTURE INTEGRITY, matching this file's idiom: if the
        // REAL provider harvested nothing, the entity lever was never on the measured path and the preservation
        // number would be meaningless. Model quality is REPORTED, never asserted.
        Assert.True(harvested.Count > 0,
            "FIXTURE INTEGRITY: the REAL BookEntityProvider harvested NONE of the Latin names the preservation "
            + "corpus depends on, so scope (i) measured a path with no entity lever on it. See q2-scope-i-profile-path.md.");
    }

    private void WriteScopeIReport(StringBuilder report)
    {
        var outDir = Environment.GetEnvironmentVariable("DIAG_OUT_DIR");
        if (string.IsNullOrWhiteSpace(outDir)) outDir = Path.GetTempPath();
        Directory.CreateDirectory(outDir);
        var outPath = Path.Combine(outDir, "q2-scope-i-profile-path.md");
        File.WriteAllText(outPath, report.ToString(), new UTF8Encoding(false));
        _output.WriteLine("REPORT WRITTEN: " + outPath);
    }

    /// <summary>True when the DETERMINISTIC glossary alone would alter this value — so a change observed on the
    /// profile path can never be mis-credited to the dynamic stage (the glossary ran on this path before c2).</summary>
    private static bool GlossaryAltersValue(string value)
    {
        var g = GlossaryRepairPass.Apply(
            AnalysisType.Summarization, null, value, "he-IL",
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        return !string.Equals(g.CleanContent, value, StringComparison.Ordinal);
    }

    /// <summary>Where ONE fixture value was planted in the profile DTOs, and how to read it back OUT of the
    /// PERSISTED JSON — so every number in scope (i) is measured from the stored artifact.</summary>
    private sealed class ProfileSlot
    {
        public string Original = "";
        public string Where = "";
        public Func<CharacterAnalysisResult, StoryAnalysisResult, string> Read = (_, _) => "";
    }

    /// <summary>The persisted outcome of ONE `BuildBookProfileAsync` run, plus the REAL entity set its hook used
    /// and the number of TermRepair calls the dynamic stage actually made.</summary>
    private sealed class ProfileRunResult
    {
        public Guid BookId;
        public string CharactersJson = "";
        public string StoryJson = "";
        public CharacterAnalysisResult Characters = new();
        public StoryAnalysisResult Story = new();
        public IReadOnlySet<string> Entities = new HashSet<string>();
        public int TermRepairCalls;
    }

    /// <summary>
    /// Plants the given values into the WHITELISTED prose fields of a CharacterAnalysis + StoryAnalysis payload
    /// (first half into `characters[i].description`, the rest into the StoryAnalysis prose fields in a fixed
    /// order), and returns the fenced JSON a model would emit plus the read-back accessors. Every OTHER field is
    /// inert Hebrew, and the non-repairable fields (`role` / conflict `type` / `status`) are deliberately LATIN
    /// so a pass that escaped its span scope would be visible on them.
    /// </summary>
    private static (string charactersJson, string storyJson, List<ProfileSlot> slots) BuildProfileFixture(
        IReadOnlyList<(string Label, string Value)> items)
    {
        var half = (items.Count + 1) / 2;
        var charItems = items.Take(half).ToList();
        var storyItems = items.Skip(half).ToList();
        var slots = new List<ProfileSlot>();

        var chars = new CharacterAnalysisResult
        {
            Summary = "סיכום מערך הדמויות של הרומן, ללא מונחים לועזיים.",
            Relationships = new List<CharacterRelationship>(),
            Characters = charItems.Select((it, i) => new CharacterEntry
            {
                Name = $"דמות מספר {i + 1}",
                Role = i == 0 ? "protagonist" : "supporting",
                Description = it.Value,
                Arc = "התפתחות פנימית לאורך הספר.",
                FirstAppearanceChapter = i + 1,
            }).ToList(),
        };
        for (var i = 0; i < charItems.Count; i++)
        {
            var idx = i;
            slots.Add(new ProfileSlot
            {
                Original = charItems[idx].Value,
                Where = $"characters[{idx}].description",
                Read = (c, _) => idx < c.Characters.Count ? c.Characters[idx].Description : "(MISSING)",
            });
        }

        var plot = new PlotStructure
        {
            Setup = "הצגת המצב ההתחלתי.",
            RisingAction = "העלייה בעימות המרכזי.",
            Climax = "שיא העלילה.",
            FallingAction = "האירועים שלאחר השיא.",
            Resolution = "הסיום.",
        };
        var story = new StoryAnalysisResult
        {
            PlotStructure = plot,
            Pacing = "קצב מתון ועקבי.",
            Summary = "סיכום מבנה הסיפור.",
            Conflicts = new List<ConflictEntry>(),
        };

        // Fixed slot order: the 5 plot prose subfields, pacing, summary, then one conflict description each.
        var storyWriters = new List<(string Where, Action<string> Write, Func<StoryAnalysisResult, string> Read)>
        {
            ("plotStructure.setup",         v => plot.Setup = v,         s => s.PlotStructure.Setup),
            ("plotStructure.risingAction",  v => plot.RisingAction = v,  s => s.PlotStructure.RisingAction),
            ("plotStructure.climax",        v => plot.Climax = v,        s => s.PlotStructure.Climax),
            ("plotStructure.fallingAction", v => plot.FallingAction = v, s => s.PlotStructure.FallingAction),
            ("plotStructure.resolution",    v => plot.Resolution = v,    s => s.PlotStructure.Resolution),
            ("pacing",                      v => story.Pacing = v,       s => s.Pacing),
            ("summary",                     v => story.Summary = v,      s => s.Summary),
        };
        for (var i = 0; i < storyItems.Count; i++)
        {
            if (i < storyWriters.Count)
            {
                var w = storyWriters[i];
                w.Write(storyItems[i].Value);
                slots.Add(new ProfileSlot
                {
                    Original = storyItems[i].Value,
                    Where = w.Where,
                    Read = (_, s) => w.Read(s),
                });
            }
            else
            {
                var conflictIdx = i - storyWriters.Count;
                story.Conflicts.Add(new ConflictEntry
                {
                    Type = "external",
                    Description = storyItems[i].Value,
                    Status = "ongoing",
                });
                slots.Add(new ProfileSlot
                {
                    Original = storyItems[i].Value,
                    Where = $"conflicts[{conflictIdx}].description",
                    Read = (_, s) => conflictIdx < s.Conflicts.Count ? s.Conflicts[conflictIdx].Description : "(MISSING)",
                });
            }
        }

        // Always carry at least one conflict so the must-not-touch `type` / `status` probe exists.
        if (story.Conflicts.Count == 0)
        {
            story.Conflicts.Add(new ConflictEntry
            {
                Type = "external",
                Description = "עימות חיצוני מרכזי בין הגיבורה לסביבתה.",
                Status = "ongoing",
            });
        }

        var opts = new JsonSerializerOptions { WriteIndented = false };
        return ("```json\n" + JsonSerializer.Serialize(chars, opts) + "\n```",
                "```json\n" + JsonSerializer.Serialize(story, opts) + "\n```",
                slots);
    }

    /// <summary>
    /// Routes the four PROFILE prompts to fixed fixture payloads and forwards ONLY `AiTaskType.TermRepair` to the
    /// real model router — so the corpus is deterministic and the model sits on exactly the surface under
    /// measurement. The call counter is the model-free probe of whether the dynamic stage ran at all.
    /// Prompt keying mirrors `BookIntelligenceProfileRepairTests` / `BookIntelligenceProfileDynamicRepairTests`
    /// (the distinctive JSON keys each Hebrew profile prompt requests).
    /// </summary>
    private sealed class ProfileScriptedRouter : IAiRouter
    {
        private readonly IAiRouter _termRepair;
        private readonly string _charactersJson;
        private readonly string _storyJson;
        private readonly object _lock = new();

        public ProfileScriptedRouter(IAiRouter termRepair, string charactersJson, string storyJson)
        {
            _termRepair = termRepair;
            _charactersJson = charactersJson;
            _storyJson = storyJson;
        }

        public int TermRepairCalls { get; private set; }

        public Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            if (request.TaskType == AiTaskType.TermRepair)
            {
                lock (_lock) { TermRepairCalls++; }
                return _termRepair.CompleteAsync(request, cancellationToken);
            }

            var instr = request.Instruction ?? string.Empty;
            var content =
                instr.Contains("plotStructure") ? _storyJson :
                instr.Contains("\"characters\"") ? _charactersJson :
                instr.Contains("\"genre\"")
                    ? "{ \"genre\": \"היסטורי\", \"subGenre\": \"דרמה\", \"targetAudience\": \"מבוגרים\", \"literatureLevel\": 3, \"estimatedReadingTimeMinutes\": 120, \"languageRegister\": \"ספרותי\", \"summary\": \"סקירה כללית.\" }"
                    : "תקציר קצר של הספר בגוף שלישי.";
            return Task.FromResult(new AiResponse { Content = content, Model = "scripted-fixture", Provider = "test" });
        }

        public IAsyncEnumerable<string> StreamCompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("The profile build never streams.");
    }

    /// <summary>
    /// Builds a PRODUCTION-shaped DI graph (the same one `BookIntelligenceProfileDynamicRepairTests` uses, plus
    /// the REAL `BookEntityProvider` over the SAME DbContext), seeds `PreservationFixtureBooks`' Hebrew-native
    /// manuscript under a FRESH book id, runs the real `BuildBookProfileAsync`, and returns the PERSISTED JSON
    /// together with the entity set the hook itself resolved. A fresh graph + book per corpus, because be-c03's
    /// invalidation makes a second build on the SAME book harvest the FIRST build's persisted CharactersJson.
    /// </summary>
    private static async Task<ProfileRunResult> RunProfileBuildAsync(
        IAiRouter termRepairRouter, string charactersJson, string storyJson)
    {
        var router = new ProfileScriptedRouter(termRepairRouter, charactersJson, storyJson);
        var dbName = "q2-scope-i-" + Guid.NewGuid();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        services.AddSingleton<IAiRouter>(router);
        services.Configure<AiOptions>(o =>
        {
            o.BookContextTokenBudget = 1_000_000;
            // The SHIPPED default: Enabled + Mode=GlossaryThenDynamic, no PerType narrowing.
            o.AnalysisRepair = new AnalysisRepairOptions
            {
                Enabled = true,
                GuardOnly = true,
                Mode = AnalysisRepairMode.GlossaryThenDynamic,
            };
        });
        services.Configure<Pagedraft.Api.Services.Analysis.Hebrew.HebrewStyleOptions>(_ => { });
        services.AddSingleton<SfdtConversionService>();
        services.AddSingleton<PromptFactory>();
        services.AddSingleton<AnalysisProgressTracker>();
        services.AddSingleton<SuggestionDiffService>();
        services.AddSingleton<BookSummaryBuildRegistry>();
        services.AddSingleton<Pagedraft.Api.Services.Analysis.Hebrew.KtivMaleChecker>();
        services.AddScoped<ChapterBriefService>();
        services.AddScoped<BookSummaryService>();
        services.AddScoped<BookContextAssembler>();
        services.AddScoped<IAnalysisContextService, AnalysisContextService>();
        services.AddScoped<AnalysisRepairService>();
        services.AddScoped<DynamicTermRepairService>();
        services.AddSingleton<IBookEntityProvider, BookEntityProvider>();   // THE REAL PROVIDER (production registration)
        services.AddScoped<UnifiedAnalysisService>();
        services.AddScoped<BookIntelligenceService>();

        await using var sp = services.BuildServiceProvider();
        var db = sp.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        PreservationFixtureBooks.SeedHebrewNativeBookInto(db, bookId);
        await db.SaveChangesAsync();
        foreach (var ch in db.Chapters.Where(c => c.BookId == bookId).ToList())
        {
            db.ChunkSummaries.Add(new ChunkSummary
            {
                BookId = bookId,
                ChapterId = ch.Id,
                Language = "he",
                SummaryText = "סיכום הפרק: הגיבורה יוצאת למסע ומתמודדת עם עימות מרכזי.",
                StructuredJson = null,
            });
        }
        await db.SaveChangesAsync();

        var profile = await sp.GetRequiredService<BookIntelligenceService>()
            .BuildBookProfileAsync(bookId, "he", CancellationToken.None);

        // The entity set the hook itself resolved (same provider instance, same bookId, same language).
        var entities = await sp.GetRequiredService<IBookEntityProvider>().GetEntitiesAsync(bookId, "he");

        var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return new ProfileRunResult
        {
            BookId = bookId,
            CharactersJson = profile.CharactersJson ?? "",
            StoryJson = profile.StoryStructureJson ?? "",
            Characters = JsonSerializer.Deserialize<CharacterAnalysisResult>(profile.CharactersJson ?? "{}", jsonOpts) ?? new(),
            Story = JsonSerializer.Deserialize<StoryAnalysisResult>(profile.StoryStructureJson ?? "{}", jsonOpts) ?? new(),
            Entities = entities,
            TermRepairCalls = router.TermRepairCalls,
        };
    }

    // NOTE (be-c07): the LegitCase record and the legit-term fixture itself now live in
    // PreservationFixtureBooks, alongside the two synthetic books whose entity sets the REAL
    // BookEntityProvider harvests. They are shared with the DETERMINISTIC BookEntityFixtureSeedTests, so the
    // seeding and the gate attribution this live gate depends on are pinned without a GPU.

    /// <summary>Per-case, per-tier preservation outcome for the d6 measure.</summary>
    private sealed class LegitOutcome
    {
        public bool Preserved;
        public bool OverRewrite;
        public bool Fault;
        public bool ReachedModel;
        public string Repaired = "";
        public long LatencyMs;
    }

    /// <summary>Renders one FP-table cell for <paramref name="tier"/> at case <paramref name="i"/>: BLOCKED
    /// when the tier's preflight failed, else "preserved" or "**FP**" (with an over-rewrite marker).</summary>
    private static string CellFor(string tier, IReadOnlyDictionary<string, LegitOutcome[]> outcomes,
        IReadOnlyDictionary<string, string?> blocked, int i)
    {
        if (blocked.ContainsKey(tier)) return "BLOCKED";
        var o = outcomes[tier][i];
        if (o.Preserved) return "preserved";
        return o.OverRewrite ? "**FP (over-rewrite)**" : "**FP**";
    }

    /// <summary>
    /// The FIRST region where <paramref name="before"/> and <paramref name="after"/> differ, with a little
    /// context on each side, so a report line about a LONG value points at the change instead of at an
    /// identical prefix. Returns two windows anchored on the common prefix/suffix boundary.
    /// </summary>
    private static (string before, string after) FirstDiffContext(string before, string after, int context = 45)
    {
        var p = 0;
        while (p < before.Length && p < after.Length && before[p] == after[p]) p++;

        var s = 0;
        while (s < before.Length - p && s < after.Length - p
               && before[before.Length - 1 - s] == after[after.Length - 1 - s]) s++;

        var start = Math.Max(0, p - context);
        string Window(string v, int tailKeptFromEnd)
        {
            var end = Math.Min(v.Length, v.Length - tailKeptFromEnd + context);
            if (end <= start) end = Math.Min(v.Length, start + context);
            return v.Substring(start, end - start).Replace("\n", "\\n");
        }

        return (Window(before, s), Window(after, s));
    }

    /// <summary>
    /// OVER-REWRITE check generalised to N repair spans (the single-run case reduces to the d5
    /// prefix/suffix SpanScopeCheck): given the ORIGINAL value and its REPAIR-classified runs, the change is
    /// CONFINED to those spans iff the repaired value still carries every NON-span (fixed) segment of the
    /// original — the leading prefix, each inter-run gap, and the trailing suffix — in order and
    /// non-overlapping (only the marked runs may differ). A value with ZERO repair runs that nevertheless
    /// changed is NOT confined (returns false) — nothing was allowed to change, so any change is an
    /// over-rewrite. Heuristic but sufficient for these short controlled values; the primary FP metric is the
    /// whole-value byte-identity check, this only sub-classifies a detected change as token-only vs structural.
    /// </summary>
    private static bool ChangeConfinedToRepairSpans(string original, IReadOnlyList<ForeignRun> repairRuns, string repaired)
    {
        if (repairRuns is null || repairRuns.Count == 0)
        {
            return false; // no span was allowed to change, yet the value changed => over-rewrite
        }

        var ordered = repairRuns.OrderBy(r => r.Start).ToList();
        var segs = new List<string>();
        var cursor = 0;
        foreach (var r in ordered)
        {
            if (r.Start < cursor || r.Start + r.Length > original.Length)
            {
                return false; // overlapping / out-of-range (defensive) — treat as not-confined
            }
            segs.Add(original.Substring(cursor, r.Start - cursor));
            cursor = r.Start + r.Length;
        }
        segs.Add(original.Substring(cursor));

        // The fixed skeleton must survive: repaired starts with the prefix, ends with the suffix, and every
        // interior gap appears in order. Only the (variable) replacements sit between them.
        if (!repaired.StartsWith(segs[0], StringComparison.Ordinal)) return false;
        if (!repaired.EndsWith(segs[^1], StringComparison.Ordinal)) return false;

        var pos = segs[0].Length;
        for (var k = 1; k < segs.Count - 1; k++)
        {
            var seg = segs[k];
            if (seg.Length == 0) continue; // adjacent runs — no fixed text to anchor
            var idx = repaired.IndexOf(seg, pos, StringComparison.Ordinal);
            if (idx < 0) return false;
            pos = idx + seg.Length;
        }

        // Interior matches must not have consumed into the trailing-suffix region.
        return pos <= repaired.Length - segs[^1].Length;
    }

    /// <summary>Per-tier accumulator for the d5 measurement.</summary>
    private sealed class TierMeasurement
    {
        public string Name = "";
        public string Model = "";
        public bool Blocked;
        public string? BlockReason;
        public int Total;
        public int Cleaned;
        public int OverRewrite;
        public int Faults;

        /// <summary>Marked-span model calls this tier/arm made (the service makes ONE per REPAIR-classified run).
        /// For a LEAK set, a call count BELOW the case count means the gate kept a leak away from the model —
        /// the be-c08 regression signal, not an efficiency win.</summary>
        public int ModelCalls;

        public long MedianMs;
        public long P90Ms;
        public List<long> Latencies { get; } = new();
    }

    /// <summary>Per-case, per-ARM cleaning outcome for the d5 measure (be-c08). <see cref="EntitySpared"/> is the
    /// one that matters: the value has ZERO repair spans in this arm but WOULD have had them entity-free — i.e.
    /// the per-book entity set ate a real leak.</summary>
    private sealed class LeakOutcome
    {
        public bool Cleaned;
        public bool OverRewrite;
        public bool Fault;
        public bool SeedError;
        public bool EntitySpared;
        public int ModelCalls;
        public string Repaired = "";
        public long LatencyMs;
    }

    /// <summary>
    /// Builds the PROD appsettings router (all providers + Ollama/OpenRouter configs + tuning blocks) with ONLY
    /// Ai:FeatureModels:TermRepair overridden to the given tier — mirrors <see cref="ResolveRouterForTask"/>'s
    /// in-memory-override idiom, keyed directly at the TermRepair task (which has no AnalysisType, so it cannot
    /// be routed via the AnalysisType-keyed DIAG_MODELS). Every other appsettings value is preserved.
    /// </summary>
    private static IAiRouter BuildTermRepairTierRouter(string appSettingsPath, string provider, string model)
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile(appSettingsPath)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:FeatureModels:TermRepair:Provider"] = provider,
                ["Ai:FeatureModels:TermRepair:Model"] = model,
            })
            .Build();
        return BuildRouter(config);
    }

    /// <summary>
    /// One direct TermRepair round-trip to confirm a tier is reachable BEFORE the sweep, so a total outage
    /// (cloud 401 / network) is reported after ONE call rather than N per-seed faults (the service's fail-safe
    /// swallows exceptions into a silent revert, which would otherwise mask a dead tier). Returns (reachable,
    /// "Provider:Model" on success or the exception summary on failure). Never throws.
    /// </summary>
    private static async Task<(bool ok, string info)> PreflightTierAsync(IAiRouter router)
    {
        try
        {
            var req = new AiRequest
            {
                InputText = "בדיקה קצרה עם המילה «fear» באמצע.",
                Instruction = "החלף אך ורק את המילה המסומנת «...» במונח העברי המתאים. החזר JSON {\"replacement\":\"<מילה>\"}.",
                TaskType = AiTaskType.TermRepair,
                Language = "he-IL",
                SourceId = "diag-preflight",
                JsonMode = true
            };
            var resp = await router.CompleteAsync(req);
            return (true, $"{resp.Provider}:{resp.Model}");
        }
        catch (Exception ex)
        {
            return (false, $"{ex.GetType().Name}: {FirstLine(ex.Message)}");
        }
    }

    /// <summary>
    /// OVER-REWRITE / blast-radius check from the OUTPUT (not the service internals): given the ORIGINAL value
    /// and the single flagged run at [start,len], the repaired value is SPAN-SCOPED iff it still begins with
    /// the exact prefix (original[0..start]) AND ends with the exact suffix (original[start+len..]) — i.e. only
    /// the one marked run may differ; anything else changing is an over-rewrite. When span-scoped, the extracted
    /// replacement is the middle slice. When not, returns the whole repaired value so the caller can show it.
    /// </summary>
    private static (bool spanScoped, string replacement) SpanScopeCheck(string original, int start, int len, string repaired)
    {
        var prefix = original.Substring(0, start);
        var suffix = original.Substring(start + len);
        var spanScoped = repaired.Length >= prefix.Length + suffix.Length
            && repaired.StartsWith(prefix, StringComparison.Ordinal)
            && repaired.EndsWith(suffix, StringComparison.Ordinal);
        var replacement = spanScoped
            ? repaired.Substring(prefix.Length, repaired.Length - prefix.Length - suffix.Length)
            : repaired;
        return (spanScoped, replacement);
    }

    private static (long median, long p90) LatencyStats(List<long> xs)
    {
        if (xs == null || xs.Count == 0) return (0, 0);
        var sorted = xs.OrderBy(x => x).ToList();
        long Pct(double p)
        {
            var idx = (int)Math.Ceiling(p * sorted.Count) - 1;
            if (idx < 0) idx = 0;
            if (idx > sorted.Count - 1) idx = sorted.Count - 1;
            return sorted[idx];
        }
        return (Pct(0.5), Pct(0.9));
    }

    private static string Trunc(string s, int max)
    {
        s = (s ?? "").Replace("\n", " ").Replace("\r", " ").Replace("|", "/").Trim();
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }

    // ─── Task runner: mirrors UnifiedAnalysisService.RunWithInputAsync request construction ───
    private static async Task<(string model, string raw, string clean)> RunTaskAsync(
        IAiRouter router, AnalysisType type, string input, string language)
    {
        var request = new AiRequest
        {
            InputText = input,
            Instruction = _promptFactory.GetAnalysisPrompt(type, language),
            TaskType = AnalysisTaskMapping.ToAiTaskType(type),
            Language = language,
            SourceId = "diag",
            JsonMode = type is AnalysisType.LineEdit or AnalysisType.LinguisticAnalysis
        };
        var response = await router.CompleteAsync(request);
        var raw = response.Content ?? string.Empty;
        var clean = UnifiedAnalysisService.SanitizeResponse(raw);
        return ($"{response.Provider}:{response.Model}", raw, clean);
    }

    // ─── BookReview capture: reproduces BookReviewService.RunCombinedCallAsync's request verbatim ───
    //
    // SHORTCUT (documented, build-only): a REAL whole-book review run stands up the DB + BookContextAssembler
    // to assemble a [BOOK_CONTEXT] from the BookBrief + ChapterBriefs (and windows it for big books). That is
    // disproportionate for this skip-gated diagnostic, so instead we reproduce the EXACT combined-dimension
    // call BookReviewService issues — the same BuildBookReviewCombinedPrompt body, a [BOOK_CONTEXT] wrapper,
    // TaskType=BookReview, JsonMode=true, empty InputText — with the diagnostic Hebrew passage standing in for
    // the assembled book context (a single-chapter "book"). This exercises the REAL review prompt + BookReview
    // model routing + BookReviewResult parse path, so the rationale/suggestedAction PROSE it returns is
    // representative for the leak scan; ONLY the context ASSEMBLY (briefs/windowing) is stubbed.
    private static async Task<(string model, string raw, string clean)> RunBookReviewCombinedAsync(
        IAiRouter router, string input, string language)
    {
        var bookContext = "[BOOK_CONTEXT]\n" + input.Trim() + "\n[/BOOK_CONTEXT]\n\n";
        var instruction = bookContext + _promptFactory.BuildBookReviewCombinedPrompt(language);

        var request = new AiRequest
        {
            InputText = string.Empty, // whole-book context lives in the instruction's [BOOK_CONTEXT]
            Instruction = instruction,
            TaskType = AiTaskType.BookReview,
            Language = language,
            SourceId = "diag",
            JsonMode = true
        };
        var response = await router.CompleteAsync(request);
        var raw = response.Content ?? string.Empty;
        var clean = UnifiedAnalysisService.SanitizeResponse(raw);
        return ($"{response.Provider}:{response.Model}", raw, clean);
    }

    // ─── CONTENT-vs-STRUCTURAL leak split for structured JSON outputs ───
    // Case-insensitive + camelCase to match BookReviewService/UnifiedAnalysisService deserialize options.
    private static readonly JsonSerializerOptions DiagJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Extracts the JSON body from a raw model response (shared ExtractJson) and deserializes it.
    /// Returns false on no-JSON / parse failure so the caller can flag an unusable split rather than report a
    /// false "none".</summary>
    private static bool TryParse<T>(string raw, out T? result) where T : class
    {
        result = null;
        var json = UnifiedAnalysisService.ExtractJson(raw);
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            result = JsonSerializer.Deserialize<T>(json, DiagJsonOpts);
            return result != null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Emits the two-line split: the CONTENT scan over the extracted PROSE values (the real leak metric) and
    /// the STRUCTURAL scan over the remaining schema English (keys + prompt-defined enum values), computed by
    /// subtracting the content values from the extracted JSON. When the JSON could not be parsed, falls back to
    /// a single whole-output scan and says so (a bad parse must not read as a clean CONTENT scan).
    /// </summary>
    private static void EmitContentStructuralSplit(
        string raw, string clean, bool parsed, IReadOnlyList<string> contentValues, Action<string> emit)
    {
        if (!parsed)
        {
            emit($"PARSE: could not extract structured JSON; split unavailable; whole-output scan: {ScanForLatin(clean)}");
            return;
        }

        var content = string.Join("\n", contentValues.Where(v => !string.IsNullOrWhiteSpace(v)));
        emit($"CONTENT leak scan (prose values, the real leak metric): {ScanForLatin(content)}");

        // STRUCTURAL = the JSON with every CONTENT value removed -> keys + enum values + numbers + punctuation.
        var structural = UnifiedAnalysisService.ExtractJson(raw) ?? clean ?? string.Empty;
        foreach (var v in contentValues)
            if (!string.IsNullOrEmpty(v))
                structural = structural.Replace(v, " ");
        emit($"STRUCTURAL scan (schema keys + English enums, expected NOT a leak): {ScanForLatin(structural)}");
    }

    // ─── Per-task model override (DIAG_MODELS) so the SAME diagnostic runs local-small vs a cloud tier ───
    //
    // FORMAT: DIAG_MODELS = "Task=Provider|Model" entries separated by ';'. Task is the AnalysisType enum name
    // (case-insensitive): BookReview, BookOverview, CharacterAnalysis, StoryAnalysis, QA, Summarization,
    // LiteraryAnalysis, LinguisticAnalysis, Proofread, LineEdit. Provider|Model uses '|' (NOT ':', because
    // Ollama model ids contain ':' e.g. "gemma4:12b" / "hf.co/dicta-il/...:latest"); when '|' is absent the
    // Provider defaults to "Ollama" and the whole value is the Model. Any task WITHOUT an entry keeps the prod
    // appsettings FeatureModels model (current behavior).
    //   Local-small (unset): DIAG_MODELS unset → every task uses appsettings routing.
    //   Point a task at a cloud tier: DIAG_MODELS="BookReview=OpenAI|gpt-4o-mini;QA=Anthropic|claude-3-5-sonnet-latest"
    // NOTE for f4: an override changes only Ai:FeatureModels:{AiTaskType}:{Provider,Model}. Cloud providers also
    // need their credentials/base-url in appsettings.json (the router is built from that file); Ollama BaseUrl
    // already comes from appsettings. The Ollama-reachability skip-gate is unchanged, so a run still needs a
    // reachable Ollama even when a task is pointed at the cloud.
    private static Dictionary<AnalysisType, (string Provider, string Model)> ParseDiagModels()
    {
        var result = new Dictionary<AnalysisType, (string, string)>();
        var raw = Environment.GetEnvironmentVariable("DIAG_MODELS");
        if (string.IsNullOrWhiteSpace(raw)) return result;

        foreach (var entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = entry.IndexOf('=');
            if (eq <= 0) continue;
            var taskName = entry[..eq].Trim();
            var spec = entry[(eq + 1)..].Trim();
            if (!Enum.TryParse<AnalysisType>(taskName, ignoreCase: true, out var type) || spec.Length == 0)
                continue;

            var bar = spec.IndexOf('|');
            var (provider, model) = bar >= 0
                ? (spec[..bar].Trim(), spec[(bar + 1)..].Trim())
                : ("Ollama", spec);
            if (model.Length == 0) continue;
            result[type] = (provider, model);
        }
        return result;
    }

    /// <summary>
    /// Returns the router to use for <paramref name="type"/>: the shared prod router (appsettings FeatureModels)
    /// unless DIAG_MODELS overrides this task, in which case a per-task router is built with
    /// Ai:FeatureModels:{AiTaskType}:{Provider,Model} pointed at the override (both keys set so the router's
    /// both-non-empty predicate takes it). Every other appsettings value (Ollama BaseUrl, provider configs,
    /// tuning blocks) is preserved. <paramref name="note"/> records which model source was used for the report.
    /// </summary>
    private static IAiRouter ResolveRouterForTask(
        string appSettingsPath, IAiRouter prodRouter, AnalysisType type, out string note)
    {
        var overrides = ParseDiagModels();
        if (!overrides.TryGetValue(type, out var spec))
        {
            note = "prod-config";
            return prodRouter;
        }

        var taskKey = AnalysisTaskMapping.ToAiTaskType(type).ToString();
        var config = new ConfigurationBuilder()
            .AddJsonFile(appSettingsPath)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"Ai:FeatureModels:{taskKey}:Provider"] = spec.Provider,
                [$"Ai:FeatureModels:{taskKey}:Model"] = spec.Model,
            })
            .Build();
        note = $"DIAG_MODELS override -> {spec.Provider}|{spec.Model}";
        return BuildRouter(config);
    }

    // ─── Repair pass: force `model`, use the LinguisticAnalysis task key so the router uses our custom
    //     instruction VERBATIM (ShouldUseUnifiedInstructionVerbatim) under the Hebrew analysis system frame ───
    private static async Task<string> RepairAsync(string model, string text)
    {
        var router = BuildRouter(new ConfigurationBuilder()
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
            .Build());

        const string repairInstruction =
            "אתה עורך לשוני מקצועי. לפניך טקסט ניתוח ספרותי בעברית שהופק על ידי מודל שפה ועלול להכיל " +
            "מילים או מונחים באנגלית, שגיאות כתיב או ניסוח לא תקין. משימתך: " +
            "1) החלף כל מילה או מונח שאינם בעברית במונח העברי הנכון והמקובל בשדה הספרות " +
            "(לדוגמה: narrator->מספר, tone->טון, foreshadowing->רמיזה מקדימה, imagery->דימויים, " +
            "mood->מצב רוח, tension->מתח, climax->שיא). " +
            "2) תקן שגיאות כתיב, דקדוק ותחביר ושפר את זרימת העברית. " +
            "3) שמור בדיוק על המשמעות, על התובנות ועל המבנה של הטקסט. אל תוסיף ואל תסיר תוכן או תובנות. " +
            "החזר אך ורק את הטקסט המתוקן בעברית, בלי הקדמה ובלי הסברים.";

        var request = new AiRequest
        {
            InputText = text,
            Instruction = repairInstruction,
            TaskType = AiTaskType.LinguisticAnalysis, // verbatim-instruction task key (not a real linguistic run)
            Language = "he-IL",
            SourceId = "repair",
            JsonMode = false
        };
        var response = await router.CompleteAsync(request);
        return UnifiedAnalysisService.SanitizeResponse(response.Content ?? string.Empty);
    }

    // ─── Router DI (mirrors the quality harnesses' CreateRouter) ───
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

    // ─── Helpers ───
    private static string LoadInputText(out string path)
    {
        path = FindUpward(Path.Combine("docs", "test-text.txt"));
        return File.ReadAllText(path);
    }

    /// <summary>
    /// Resolves the passages to sweep. DIAG_INPUTS = a ';'-separated list of ABSOLUTE Hebrew-passage file
    /// paths; each existing file becomes one (path, text) passage (read as UTF-8) and the sweep loops them
    /// as the OUTER loop so models stay warm across passages. Missing paths are skipped with a WARN. When
    /// DIAG_INPUTS is unset (or names no readable file), falls back to the single default docs/test-text.txt
    /// so behavior is EXACTLY the pre-sweep single-passage run. <paramref name="usingDiagInputs"/> reports
    /// whether the multi-passage override actually took effect.
    /// </summary>
    private static List<(string path, string text)> LoadPassages(Action<string> emit, out bool usingDiagInputs)
    {
        var raw = Environment.GetEnvironmentVariable("DIAG_INPUTS");
        usingDiagInputs = !string.IsNullOrWhiteSpace(raw);
        if (!usingDiagInputs)
        {
            var text = LoadInputText(out var path);
            return new List<(string path, string text)> { (path, text) };
        }

        var list = new List<(string path, string text)>();
        foreach (var p in raw!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (File.Exists(p)) list.Add((p, File.ReadAllText(p)));
            else emit($"WARN: DIAG_INPUTS path not found, skipping: `{p}`");
        }

        if (list.Count == 0)
        {
            emit("WARN: DIAG_INPUTS set but no readable files found; falling back to default `docs/test-text.txt`.");
            var text = LoadInputText(out var path);
            list.Add((path, text));
            usingDiagInputs = false;
        }
        return list;
    }

    /// <summary>
    /// Robustly extracts QAResult.answer prose for the CONTENT leak scan. QA routes to GenericChat and is
    /// NOT run in JsonMode, so its output frequently breaks strict JSON — citation excerpts quote the source
    /// with unescaped double-quotes, which trips the shared brace-matching ExtractJson / deserialize path and
    /// would otherwise collapse QA into a whole-output scan (structural terms polluting the read). Route 1 is
    /// the strict structured parse; Route 2 is a targeted "answer"-field regex that survives a malformed body
    /// (the answer itself is usually well-formed prose, so it captures cleanly even when a LATER citation is
    /// broken). Returns false only when neither route yields an answer, so the caller emits an explicit
    /// QA-PARSE-FALLBACK instead of silently mis-reporting. <paramref name="route"/> records which path won.
    /// </summary>
    private static bool TryExtractQaAnswer(string raw, out string answer, out string route)
    {
        raw ??= string.Empty;
        if (TryParse<QAResult>(raw, out var qa) && qa != null && !string.IsNullOrWhiteSpace(qa.Answer))
        {
            answer = qa.Answer;
            route = "structured-json parse";
            return true;
        }

        // Targeted, malformed-tolerant capture of the JSON "answer" string value: matches the value's own
        // escaped-char / non-quote body, so a broken excerpt later in the object does not derail it.
        var m = Regex.Match(raw,
            "\"answer\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.Singleline);
        if (m.Success && m.Groups[1].Value.Trim().Length > 0)
        {
            var capturedValue = m.Groups[1].Value;
            try { answer = Regex.Unescape(capturedValue); }
            catch { answer = capturedValue; }
            route = "targeted answer-field regex (strict JSON parse failed)";
            return true;
        }

        answer = string.Empty;
        route = string.IsNullOrWhiteSpace(UnifiedAnalysisService.ExtractJson(raw))
            ? "no extractable JSON and no \"answer\" field found"
            : "\"answer\" field not found in extracted JSON";
        return false;
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

    // Reports Latin-letter runs (>=2 letters) as an English-leak signal, with the distinct tokens found.
    private static string ScanForLatin(string text)
    {
        var matches = Regex.Matches(text ?? "", "[A-Za-z]{2,}");
        if (matches.Count == 0) return "none";
        var distinct = matches.Select(m => m.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
        return $"{matches.Count} latin run(s): {string.Join(", ", distinct)}";
    }

    private static int WordCount(string s) => Regex.Matches(s ?? "", "\\S+").Count;
    private static string FirstLine(string s) => (s ?? "").Split('\n')[0].Trim();

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
