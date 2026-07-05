using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
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

        var input = LoadInputText(out var inputPath);
        var report = new StringBuilder();
        void Emit(string s) { _output.WriteLine(s); report.AppendLine(s); }

        Emit("# PageDraft analysis-output diagnostic");
        Emit("");
        Emit($"Input: real Hebrew first-person passage `{inputPath}` ({input.Length} chars, ~{WordCount(input)} words)");
        Emit("");

        var appSettingsPath = FindUpward(Path.Combine("Pagedraft.Api", "appsettings.json"));
        Assert.True(File.Exists(appSettingsPath), "appsettings.json not found at " + appSettingsPath);
        var prodRouter = BuildRouter(new ConfigurationBuilder().AddJsonFile(appSettingsPath).Build());
        Emit($"Routing loaded from: `{appSettingsPath}`");
        Emit("");

        // ── Part 1: real chapter-level analysis tasks, production routing ──
        Emit("## Part 1 — real task outputs (production routing)");
        Emit("");

        var tasks = new[]
        {
            AnalysisType.Summarization,
            AnalysisType.LiteraryAnalysis,
            AnalysisType.LinguisticAnalysis,
            AnalysisType.Proofread,
            AnalysisType.LineEdit,
        };

        var captured = new Dictionary<AnalysisType, string>();
        foreach (var t in tasks)
        {
            Emit($"### TASK: {t}");
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var (model, raw, clean) = await RunTaskAsync(prodRouter, t, input, "he-IL");
                sw.Stop();
                captured[t] = clean;
                Emit($"model: `{model}`  |  {sw.Elapsed.TotalSeconds:F0}s  |  raw {raw.Length} chars -> sanitized {clean.Length} chars");
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

        // ── Part 2: second-pass repair prototype ──
        Emit("## Part 2 — repair-pass prototype (self-critique / cleanup layer)");
        Emit("");
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
