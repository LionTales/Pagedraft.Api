using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// Single entry-point for all analysis: replaces both AiAnalysisService.RunAsync and the
/// pipeline's IAnalyzeEngine. Handles prompt selection, LLM invocation, structured parsing,
/// and persistence for every (Scope × Type) combination.
/// </summary>
public class UnifiedAnalysisService
{
    private readonly AppDbContext _db;
    private readonly IAiRouter _router;
    private readonly PromptFactory _promptFactory;
    private readonly SfdtConversionService _sfdtConversion;
    private readonly ILogger<UnifiedAnalysisService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public UnifiedAnalysisService(
        AppDbContext db,
        IAiRouter router,
        PromptFactory promptFactory,
        SfdtConversionService sfdtConversion,
        ILogger<UnifiedAnalysisService> logger)
    {
        _db = db;
        _router = router;
        _promptFactory = promptFactory;
        _sfdtConversion = sfdtConversion;
        _logger = logger;
    }

    /// <summary>Run an analysis and persist the result.</summary>
    public async Task<AnalysisResult> RunAsync(
        AnalysisScope scope,
        AnalysisType analysisType,
        Guid targetId,
        string? customPrompt,
        string language,
        CancellationToken ct = default)
    {
        var (inputText, bookId, chapterId, sceneId) = await ResolveTarget(scope, targetId, ct);
        var taskType = MapToTaskType(analysisType);
        var instruction = customPrompt ?? _promptFactory.GetAnalysisPrompt(analysisType, language);

        var request = new AiRequest
        {
            InputText = inputText,
            Instruction = instruction,
            TaskType = taskType,
            Language = language,
            SourceId = targetId.ToString()
        };

        _logger.LogInformation("Running {Scope}/{Type} analysis on {TargetId}", scope, analysisType, targetId);
        var response = await _router.CompleteAsync(request, ct);

        var cleanContent = SanitizeResponse(response.Content);
        var structuredJson = TryParseStructured(analysisType, cleanContent);

        var result = new AnalysisResult
        {
            ChapterId = chapterId ?? Guid.Empty,
            BookId = bookId,
            SceneId = sceneId,
            Scope = scope,
            AnalysisType = analysisType,
            Type = analysisType.ToString(),
            PromptUsed = TruncateForAudit(instruction),
            ResultText = cleanContent,
            StructuredResult = structuredJson,
            Language = language,
            ModelName = $"{response.Provider}:{response.Model}"
        };

        _db.AnalysisResults.Add(result);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Analysis {Id} persisted ({Scope}/{Type})", result.Id, scope, analysisType);
        return result;
    }

    /// <summary>
    /// Run analysis with explicit input text and persist. Used for Book-scope Q&A where
    /// input is concatenated chapter summaries + question, not resolved from a target.
    /// </summary>
    public async Task<AnalysisResult> RunWithInputAsync(
        AnalysisScope scope,
        AnalysisType analysisType,
        Guid? bookId,
        Guid? chapterId,
        Guid? sceneId,
        string inputText,
        string language,
        CancellationToken ct = default)
    {
        var taskType = MapToTaskType(analysisType);
        var instruction = _promptFactory.GetAnalysisPrompt(analysisType, language);

        var request = new AiRequest
        {
            InputText = inputText,
            Instruction = instruction,
            TaskType = taskType,
            Language = language,
            SourceId = bookId?.ToString() ?? chapterId?.ToString() ?? sceneId?.ToString() ?? ""
        };

        _logger.LogInformation("Running {Scope}/{Type} with provided input", scope, analysisType);
        var response = await _router.CompleteAsync(request, ct);

        var cleanContent = SanitizeResponse(response.Content);
        var structuredJson = TryParseStructured(analysisType, cleanContent);

        var result = new AnalysisResult
        {
            ChapterId = chapterId ?? Guid.Empty,
            BookId = bookId,
            SceneId = sceneId,
            Scope = scope,
            AnalysisType = analysisType,
            Type = analysisType.ToString(),
            PromptUsed = TruncateForAudit(instruction),
            ResultText = cleanContent,
            StructuredResult = structuredJson,
            Language = language,
            ModelName = $"{response.Provider}:{response.Model}"
        };

        _db.AnalysisResults.Add(result);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Analysis {Id} persisted ({Scope}/{Type})", result.Id, scope, analysisType);
        return result;
    }

    /// <summary>Stream an analysis, accumulate tokens, then persist.</summary>
    public async IAsyncEnumerable<string> RunStreamingAsync(
        AnalysisScope scope,
        AnalysisType analysisType,
        Guid targetId,
        string? customPrompt,
        string language,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var (inputText, bookId, chapterId, sceneId) = await ResolveTarget(scope, targetId, ct);
        var taskType = MapToTaskType(analysisType);
        var instruction = customPrompt ?? _promptFactory.GetAnalysisPrompt(analysisType, language);

        var request = new AiRequest
        {
            InputText = inputText,
            Instruction = instruction,
            TaskType = taskType,
            Language = language,
            SourceId = targetId.ToString()
        };

        var sb = new StringBuilder();
        await foreach (var token in _router.StreamCompleteAsync(request, ct))
        {
            if (ct.IsCancellationRequested) yield break;
            sb.Append(token);
            yield return token;
        }

        var cleanContent = SanitizeResponse(sb.ToString());
        var structuredJson = TryParseStructured(analysisType, cleanContent);

        var result = new AnalysisResult
        {
            ChapterId = chapterId ?? Guid.Empty,
            BookId = bookId,
            SceneId = sceneId,
            Scope = scope,
            AnalysisType = analysisType,
            Type = analysisType.ToString(),
            PromptUsed = TruncateForAudit(instruction),
            ResultText = cleanContent,
            StructuredResult = structuredJson,
            Language = language,
            ModelName = "stream"
        };

        _db.AnalysisResults.Add(result);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Run analysis without persistence — used internally by BookIntelligenceService
    /// for chapter summarization where the result feeds into a larger pipeline.
    /// </summary>
    public async Task<string> RunRawAsync(
        string inputText,
        AnalysisType analysisType,
        string? instruction,
        string language,
        CancellationToken ct = default)
    {
        var taskType = MapToTaskType(analysisType);
        var prompt = instruction ?? _promptFactory.GetAnalysisPrompt(analysisType, language);

        var request = new AiRequest
        {
            InputText = inputText,
            Instruction = prompt,
            TaskType = taskType,
            Language = language
        };

        var response = await _router.CompleteAsync(request, ct);
        return SanitizeResponse(response.Content);
    }

    // ─── Target Resolution ──────────────────────────────────────────

    private async Task<(string InputText, Guid? BookId, Guid? ChapterId, Guid? SceneId)> ResolveTarget(
        AnalysisScope scope, Guid targetId, CancellationToken ct)
    {
        return scope switch
        {
            AnalysisScope.Chapter => await ResolveChapter(targetId, ct),
            AnalysisScope.Scene => await ResolveScene(targetId, ct),
            AnalysisScope.Book => await ResolveBook(targetId, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(scope))
        };
    }

    private async Task<(string, Guid?, Guid?, Guid?)> ResolveChapter(Guid chapterId, CancellationToken ct)
    {
        var chapter = await _db.Chapters.FirstOrDefaultAsync(c => c.Id == chapterId, ct)
            ?? throw new InvalidOperationException("Chapter not found");

        var text = StripSyncfusionWatermark(chapter.ContentText ?? "");
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("No chapter text to analyze. Save the chapter first so the analysis has content.");

        return (text, chapter.BookId, chapterId, null);
    }

    private async Task<(string, Guid?, Guid?, Guid?)> ResolveScene(Guid sceneId, CancellationToken ct)
    {
        var scene = await _db.Scenes.Include(s => s.Chapter).FirstOrDefaultAsync(s => s.Id == sceneId, ct)
            ?? throw new InvalidOperationException("Scene not found");

        var sfdt = scene.ContentSfdt ?? "{}";
        var (plainText, _) = _sfdtConversion.GetTextFromSfdt(sfdt);
        var text = StripSyncfusionWatermark(plainText);
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Scene has no content to analyze. Edit the scene and save first.");

        return (text, scene.Chapter.BookId, scene.ChapterId, sceneId);
    }

    private async Task<(string, Guid?, Guid?, Guid?)> ResolveBook(Guid bookId, CancellationToken ct)
    {
        var chapters = await _db.Chapters
            .Where(c => c.BookId == bookId)
            .OrderBy(c => c.Order)
            .ToListAsync(ct);

        if (chapters.Count == 0)
            throw new InvalidOperationException("Book has no chapters to analyze.");

        var sb = new StringBuilder();
        foreach (var ch in chapters)
        {
            var text = StripSyncfusionWatermark(ch.ContentText ?? "");
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.AppendLine($"## {ch.Title}");
                sb.AppendLine(text);
                sb.AppendLine();
            }
        }

        return (sb.ToString(), bookId, null, null);
    }

    // ─── Structured Output Parsing ──────────────────────────────────

    private static string? TryParseStructured(AnalysisType type, string content)
    {
        return type switch
        {
            AnalysisType.LineEdit => TryExtractAndReserialize<LineEditResult>(content),
            AnalysisType.LinguisticAnalysis => TryExtractAndReserialize<LinguisticAnalysisResult>(content),
            AnalysisType.LiteraryAnalysis => TryExtractAndReserialize<LiteraryAnalysisResult>(content),
            AnalysisType.BookOverview => TryExtractAndReserialize<BookOverviewResult>(content),
            AnalysisType.CharacterAnalysis => TryExtractAndReserialize<CharacterAnalysisResult>(content),
            AnalysisType.StoryAnalysis => TryExtractAndReserialize<StoryAnalysisResult>(content),
            AnalysisType.QA => TryExtractAndReserialize<QAResult>(content),
            _ => null
        };
    }

    private static string? TryExtractAndReserialize<T>(string content) where T : class
    {
        try
        {
            var json = ExtractJson(content);
            if (json == null) return null;

            var parsed = JsonSerializer.Deserialize<T>(json, JsonOpts);
            if (parsed == null) return null;

            return JsonSerializer.Serialize(parsed, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extract the first top-level JSON object or array from LLM output,
    /// which may contain markdown fences or surrounding text.
    /// </summary>
    private static string? ExtractJson(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return null;

        var fenceMatch = Regex.Match(content, @"```(?:json)?\s*\n?([\s\S]*?)```", RegexOptions.IgnoreCase);
        if (fenceMatch.Success)
        {
            var inner = fenceMatch.Groups[1].Value.Trim();
            if (inner.Length > 0 && (inner[0] == '{' || inner[0] == '['))
                return inner;
        }

        var start = content.IndexOfAny(new[] { '{', '[' });
        if (start < 0) return null;

        char open = content[start];
        char close = open == '{' ? '}' : ']';
        int depth = 0;
        bool inString = false;
        bool escape = false;

        for (int i = start; i < content.Length; i++)
        {
            char c = content[i];
            if (escape) { escape = false; continue; }
            if (c == '\\' && inString) { escape = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (c == open) depth++;
            else if (c == close) depth--;
            if (depth == 0)
                return content.Substring(start, i - start + 1);
        }

        return null;
    }

    // ─── Mapping helpers ────────────────────────────────────────────

    private static AiTaskType MapToTaskType(AnalysisType analysisType) => analysisType switch
    {
        AnalysisType.Proofread => AiTaskType.Proofread,
        AnalysisType.LineEdit => AiTaskType.Proofread,
        AnalysisType.LinguisticAnalysis => AiTaskType.LinguisticAnalysis,
        AnalysisType.LiteraryAnalysis => AiTaskType.LinguisticAnalysis,
        AnalysisType.Summarization => AiTaskType.Summarization,
        AnalysisType.BookOverview => AiTaskType.LinguisticAnalysis,
        AnalysisType.Synopsis => AiTaskType.Summarization,
        AnalysisType.CharacterAnalysis => AiTaskType.LinguisticAnalysis,
        AnalysisType.StoryAnalysis => AiTaskType.LinguisticAnalysis,
        AnalysisType.QA => AiTaskType.GenericChat,
        AnalysisType.Custom => AiTaskType.GenericChat,
        _ => AiTaskType.GenericChat
    };

    // ─── Sanitization ───────────────────────────────────────────────

    private static string SanitizeResponse(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = StripSyncfusionWatermark(text);
        text = StripCjk(text);
        return text;
    }

    private static string StripSyncfusionWatermark(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        const StringComparison ci = StringComparison.OrdinalIgnoreCase;
        var result = text;
        int searchStart = 0;
        while (true)
        {
            var startIdx = result.IndexOf("Created with a trial version of Syncfusion", searchStart, ci);
            if (startIdx < 0) break;
            const string keyPhrase = "to obtain the valid key.";
            var keyEnd = result.IndexOf(keyPhrase, startIdx, ci);
            int endIdx = keyEnd >= 0 ? keyEnd + keyPhrase.Length : result.Length;
            result = result.Remove(startIdx, endIdx - startIdx);
            searchStart = startIdx;
        }
        result = Regex.Replace(result, @"[\r\n]+", "\n");
        result = Regex.Replace(result, @"[ \t]+", " ").Trim();
        return result;
    }

    private static string StripCjk(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var stripped = Regex.Replace(text, @"[\u4e00-\u9fff\u3000-\u303f]+", " ");
        return Regex.Replace(stripped, @"[ \t\r\n]+", " ").Trim();
    }

    private static string TruncateForAudit(string? prompt, int max = 500) =>
        string.IsNullOrEmpty(prompt) ? "" : prompt.Length <= max ? prompt : prompt[..max] + "…";
}
