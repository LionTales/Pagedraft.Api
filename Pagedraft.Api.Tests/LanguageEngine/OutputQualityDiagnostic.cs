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
/// SKIP-BY-DEFAULT + GPU: needs a live local Ollama. Probes the endpoint first and returns (passes)
/// if unreachable, so CI stays green. When it runs it drives the 8-12 GB local models sequentially.
/// Output is written both to ITestOutputHelper and to a UTF-8 markdown file (env DIAG_OUT_DIR or temp).
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

    [Fact]
    public async Task DumpRealAnalysisOutputs_AndPrototypeRepairPass()
    {
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
