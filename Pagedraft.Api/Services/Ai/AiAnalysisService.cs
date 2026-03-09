using System.Text;
using Microsoft.EntityFrameworkCore;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.LanguageEngine.Contracts;

namespace Pagedraft.Api.Services.Ai;

public class AiAnalysisService
{
    private readonly AppDbContext _db;
    private readonly IAiRouter _router;
    private readonly ILanguageEngine? _languageEngine;

    public AiAnalysisService(AppDbContext db, IAiRouter router, ILanguageEngine? languageEngine = null)
    {
        _db = db;
        _router = router;
        _languageEngine = languageEngine;
    }

    private static string StripCjkFromResponse(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var stripped = System.Text.RegularExpressions.Regex.Replace(text, @"[\u4e00-\u9fff\u3000-\u303f]+", " ");
        stripped = System.Text.RegularExpressions.Regex.Replace(stripped, @"[ \t\r\n]+", " ").Trim();
        return stripped;
    }

    public async Task<AnalysisResult> RunAsync(Guid chapterId, Guid? templateId, string? customPrompt, CancellationToken ct = default)
    {
        var chapter = await _db.Chapters.Include(c => c.Book).FirstOrDefaultAsync(c => c.Id == chapterId, ct);
        if (chapter == null) throw new InvalidOperationException("Chapter not found");

        var (inputText, instruction, taskType, language, typeLabel) = await BuildRequestFromChapterAsync(chapterId, templateId, customPrompt, ct);
        var request = new AiRequest
        {
            InputText = inputText,
            Instruction = instruction,
            TaskType = taskType,
            Language = language,
            SourceId = chapterId.ToString()
        };

        var response = await _router.CompleteAsync(request, ct);
        var template = templateId.HasValue ? await _db.PromptTemplates.FindAsync(new object[] { templateId.Value }, ct) : null;

        var result = new AnalysisResult
        {
            ChapterId = chapterId,
            TemplateId = templateId,
            Type = template?.Type ?? typeLabel,
            PromptUsed = "", // Optional: could store truncated prompt for audit
            ResultText = StripCjkFromResponse(response.Content),
            ModelName = $"{response.Provider}:{response.Model}"
        };
        _db.AnalysisResults.Add(result);
        await _db.SaveChangesAsync(ct);
        return result;
    }

    public async IAsyncEnumerable<string> RunStreamingAsync(Guid chapterId, Guid? templateId, string? customPrompt, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var chapter = await _db.Chapters.Include(c => c.Book).FirstOrDefaultAsync(c => c.Id == chapterId, ct);
        if (chapter == null) yield break;

        var (inputText, instruction, taskType, language, typeLabel) = await BuildRequestFromChapterAsync(chapterId, templateId, customPrompt, ct);
        var request = new AiRequest
        {
            InputText = inputText,
            Instruction = instruction,
            TaskType = taskType,
            Language = language,
            SourceId = chapterId.ToString()
        };

        var sb = new StringBuilder();
        await foreach (var token in _router.StreamCompleteAsync(request, ct))
        {
            if (ct.IsCancellationRequested) yield break;
            sb.Append(token);
            yield return token;
        }

        var template = templateId.HasValue ? await _db.PromptTemplates.FindAsync(new object[] { templateId.Value }, ct) : null;
        var result = new AnalysisResult
        {
            ChapterId = chapterId,
            TemplateId = templateId,
            Type = template?.Type ?? typeLabel,
            PromptUsed = "",
            ResultText = StripCjkFromResponse(sb.ToString()),
            ModelName = "stream"
        };
        _db.AnalysisResults.Add(result);
        await _db.SaveChangesAsync(ct);
    }

    private static string SubstituteChapterPlaceholders(string template, string chapterText, string chapterTitle)
    {
        return template
            .Replace("{chapter_text}", chapterText)
            .Replace("{chapter_title}", chapterTitle)
            .Replace("{{פרק}}", chapterText)
            .Replace("{{כותרת}}", chapterTitle)
            .Replace("{{chapter}}", chapterText)
            .Replace("{{Chapter}}", chapterText)
            .Replace("{{title}}", chapterTitle)
            .Replace("{{Title}}", chapterTitle);
    }

    private static AiTaskType TemplateTypeToTaskType(string? templateType)
    {
        if (string.IsNullOrEmpty(templateType)) return AiTaskType.Proofread;
        return templateType.Trim() switch
        {
            "Proofreading" => AiTaskType.Proofread,
            "Linguistic" => AiTaskType.LinguisticAnalysis,
            "Summarization" => AiTaskType.Summarization,
            "Translation" => AiTaskType.Translation,
            _ => AiTaskType.Proofread
        };
    }

    private async Task<(string InputText, string? Instruction, AiTaskType TaskType, string Language, string TypeLabel)> BuildRequestFromChapterAsync(Guid chapterId, Guid? templateId, string? customPrompt, CancellationToken ct)
    {
        var chapter = await _db.Chapters.FirstOrDefaultAsync(c => c.Id == chapterId, ct);
        if (chapter == null) throw new InvalidOperationException("Chapter not found");

        var chapterText = SyncfusionWatermarkStripper.StripSyncfusionWatermark(chapter.ContentText ?? "");
        if (string.IsNullOrWhiteSpace(chapterText))
            throw new InvalidOperationException("No chapter text to analyze. Save the chapter first so the analysis has content.");

        string? instruction = null;
        string typeLabel = "Custom";
        string language = "he-IL";

        if (customPrompt != null)
        {
            instruction = SubstituteChapterPlaceholders(customPrompt, chapterText, chapter.Title);
        }
        else if (templateId.HasValue)
        {
            var t = await _db.PromptTemplates.FindAsync(new object[] { templateId.Value }, ct);
            if (t != null)
            {
                instruction = SubstituteChapterPlaceholders(t.TemplateText, chapterText, chapter.Title);
                typeLabel = t.Type;
                language = string.IsNullOrEmpty(t.Language) ? "he-IL" : (t.Language == "he" ? "he-IL" : t.Language);
            }
        }

        var taskType = TemplateTypeToTaskType(typeLabel);
        return (chapterText, instruction, taskType, language, typeLabel);
    }

    /// <summary>Run full language engine pipeline on a chapter.</summary>
    public async Task<LanguageEngineResult> RunLanguageEngineAsync(
        Guid chapterId,
        LanguageEngineOptions? options = null,
        CancellationToken ct = default)
    {
        if (_languageEngine == null)
            throw new InvalidOperationException("Language engine is not configured");

        var chapter = await _db.Chapters.Include(c => c.Book).FirstOrDefaultAsync(c => c.Id == chapterId, ct);
        if (chapter == null) throw new InvalidOperationException("Chapter not found");

        var inputText = SyncfusionWatermarkStripper.StripSyncfusionWatermark(chapter.ContentText ?? "");
        if (string.IsNullOrWhiteSpace(inputText))
            throw new InvalidOperationException("Chapter has no content text");

        var request = new LanguageEngineRequest
        {
            InputText = inputText,
            Language = chapter.Book?.Language ?? "he-IL",
            Options = options ?? new LanguageEngineOptions
            {
                EnableNormalize = true,
                EnableDetect = true,
                EnableRewrite = false,
                EnableAnalyze = false
            }
        };

        return await _languageEngine.ProcessAsync(request, ct);
    }

    /// <summary>Detect issues in a chapter.</summary>
    public async Task<List<LanguageIssue>> DetectIssuesAsync(Guid chapterId, CancellationToken ct = default)
    {
        var result = await RunLanguageEngineAsync(chapterId, new LanguageEngineOptions
        {
            EnableNormalize = true,
            EnableDetect = true,
            EnableRewrite = false,
            EnableAnalyze = false
        }, ct);
        return result.Issues;
    }

    /// <summary>Rewrite text in a chapter.</summary>
    public async Task<string> RewriteTextAsync(
        Guid chapterId,
        string? preferredModel = null,
        CancellationToken ct = default)
    {
        var result = await RunLanguageEngineAsync(chapterId, new LanguageEngineOptions
        {
            EnableNormalize = true,
            EnableDetect = true,
            EnableRewrite = true,
            EnableAnalyze = false,
            PreferredModel = preferredModel
        }, ct);
        return result.RewrittenText ?? result.NormalizedText;
    }
}
