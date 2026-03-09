using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;

namespace Pagedraft.Api.Controllers;

[ApiController]
[Route("api/books/{bookId:guid}/chapters/{chapterId:guid}")]
public class AnalysisController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly UnifiedAnalysisService _unifiedAnalysis;
    private readonly AnalysisProgressTracker _progress;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AnalysisController> _logger;

    public AnalysisController(
        AppDbContext db,
        UnifiedAnalysisService unifiedAnalysis,
        AnalysisProgressTracker progress,
        IServiceScopeFactory scopeFactory,
        ILogger<AnalysisController> logger)
    {
        _db = db;
        _unifiedAnalysis = unifiedAnalysis;
        _progress = progress;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    [HttpPost("analyze")]
    public async Task<ActionResult<AnalysisResultDto>> RunAnalysis(Guid bookId, Guid chapterId, [FromQuery] Guid? sceneId, [FromBody] RunAnalysisRequest req, CancellationToken ct)
    {
        var chapter = await _db.Chapters.Include(c => c.Book).FirstOrDefaultAsync(c => c.BookId == bookId && c.Id == chapterId, ct);
        if (chapter == null) return NotFound();

        var (analysisType, customPrompt, language) = await ResolveAnalysisParamsAsync(chapterId, req, chapter, ct);
        var scope = sceneId.HasValue ? AnalysisScope.Scene : AnalysisScope.Chapter;
        var targetId = sceneId ?? chapterId;

        try
        {
            if (req.Stream)
            {
                Response.ContentType = "text/event-stream";
                await foreach (var token in _unifiedAnalysis.RunStreamingAsync(scope, analysisType, targetId, customPrompt, language, ct))
                    await Response.WriteAsync($"data: {{\"token\":\"{EscapeJson(token)}\"}}\n\n", ct);
                await Response.WriteAsync("data: {\"token\":\"\",\"done\":true}\n\n", ct);
                return new EmptyResult();
            }
            var result = await _unifiedAnalysis.RunAsync(scope, analysisType, targetId, customPrompt, language, null, ct);
            return Ok(ToDto(result));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("No chapter text") || ex.Message.Contains("no content") || ex.Message.Contains("not found"))
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("too long", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(502, new { error = ex.Message });
        }
    }

    private static AnalysisResultDto ToDto(AnalysisResult a, List<SuggestionOutcomeDto>? suggestionOutcomes = null) =>
        new(a.Id, a.ChapterId, a.JobId, a.Type, a.ResultText, a.ModelName, a.CreatedAt,
            StructuredResult: a.StructuredResult,
            Scope: a.Scope.ToString(),
            AnalysisType: a.AnalysisType.ToString(),
            SceneId: a.SceneId,
            BookId: a.BookId,
            Language: a.Language,
            ProofreadNoChangesHint: a.ProofreadNoChangesHint,
            SuggestionOutcomes: suggestionOutcomes);

    private async Task<(AnalysisType analysisType, string? customPrompt, string language)> ResolveAnalysisParamsAsync(Guid chapterId, RunAnalysisRequest req, Chapter? chapter, CancellationToken ct)
    {
        // Prefer request language; when not set, use the book's language so English books get "en" and the right model/prompts.
        var language = !string.IsNullOrEmpty(req.Language)
            ? req.Language
            : NormalizeLanguageForAnalysis(chapter?.Book?.Language) ?? "he";

        // Type picker: client sends AnalysisType string (e.g. "LinguisticAnalysis")
        if (!string.IsNullOrWhiteSpace(req.AnalysisType) && TryParseAnalysisType(req.AnalysisType, out var parsedType))
            return (parsedType, req.CustomPrompt, language);

        if (req.CustomPrompt != null && !req.TemplateId.HasValue)
            return (AnalysisType.Custom, req.CustomPrompt, language);

        if (req.TemplateId.HasValue)
        {
            var template = await _db.PromptTemplates.FindAsync(new object[] { req.TemplateId.Value }, ct);
            if (template != null)
            {
                var analysisType = TemplateTypeToAnalysisType(template.Type);
                var lang = string.IsNullOrEmpty(template.Language) || template.Language == "he" ? "he" : template.Language;
                return (analysisType, req.CustomPrompt, lang);
            }
        }

        return (AnalysisType.Proofread, null, language);
    }

    private static bool TryParseAnalysisType(string? value, out AnalysisType analysisType)
    {
        analysisType = AnalysisType.Custom;
        if (string.IsNullOrWhiteSpace(value)) return false;
        return Enum.TryParse(value, ignoreCase: true, out analysisType);
    }

    private static AnalysisType TemplateTypeToAnalysisType(string? templateType)
    {
        if (string.IsNullOrEmpty(templateType)) return AnalysisType.Proofread;
        return templateType.Trim() switch
        {
            "Proofreading" => AnalysisType.Proofread,
            "Literary" => AnalysisType.LiteraryAnalysis,
            "Linguistic" => AnalysisType.LinguisticAnalysis,
            "Custom" => AnalysisType.Custom,
            _ => AnalysisType.Proofread
        };
    }

    /// <summary>Normalize book language (e.g. "en-US") to analysis code "en" or "he" for prompts and model selection.</summary>
    private static string? NormalizeLanguageForAnalysis(string? bookLanguage)
    {
        if (string.IsNullOrWhiteSpace(bookLanguage)) return null;
        var lang = bookLanguage.Trim();
        if (lang.StartsWith("he", StringComparison.OrdinalIgnoreCase)) return "he";
        if (lang.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "en";
        return lang.Length <= 5 ? lang : lang[..2];
    }

    private static string EscapeJson(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    [HttpGet("analyses")]
    public async Task<ActionResult<List<AnalysisResultDto>>> GetAnalyses(Guid bookId, Guid chapterId, [FromQuery] Guid? sceneId = null, [FromQuery] string? analysisType = null, CancellationToken ct = default)
    {
        var query = _db.AnalysisResults.AsNoTracking().Where(a => a.ChapterId == chapterId);
        if (sceneId.HasValue)
            query = query.Where(a => a.SceneId == sceneId);

        if (!string.IsNullOrWhiteSpace(analysisType) && Enum.TryParse<AnalysisType>(analysisType, ignoreCase: true, out var type))
            query = query.Where(a => a.AnalysisType == type);

        var items = (await query.ToListAsync(ct)).OrderByDescending(a => a.CreatedAt).ToList();
        var ids = items.Select(a => a.Id).ToList();

        Dictionary<Guid, List<SuggestionOutcomeDto>> grouped;
        if (ids.Count == 0)
        {
            grouped = new Dictionary<Guid, List<SuggestionOutcomeDto>>();
        }
        else
        {
            var outcomesByAnalysis = await _db.SuggestionOutcomeRecords.AsNoTracking()
                .Where(o => ids.Contains(o.AnalysisResultId))
                .Select(o => new SuggestionOutcomeDto(o.AnalysisResultId, o.OriginalText, o.SuggestedText, o.Outcome.ToString()))
                .ToListAsync(ct);

            grouped = outcomesByAnalysis
                .GroupBy(o => o.AnalysisResultId)
                .ToDictionary(g => g.Key, g => g.ToList());
        }

        var dtos = items.Select(a =>
        {
            var outcomes = grouped.TryGetValue(a.Id, out var list) ? list : null;
            return ToDto(a, outcomes);
        }).ToList();

        return Ok(dtos);
    }

    [HttpGet("analyses/{id:guid}")]
    public async Task<ActionResult<AnalysisResultDto>> GetAnalysisById(Guid bookId, Guid chapterId, Guid id, CancellationToken ct)
    {
        var a = await _db.AnalysisResults.FirstOrDefaultAsync(x => x.ChapterId == chapterId && x.Id == id, ct);
        if (a == null) return NotFound();
        return Ok(ToDto(a));
    }

    /// <summary>Save or update the outcome (Accepted/Dismissed) for one suggestion of an analysis run.</summary>
    [HttpPost("analyses/{analysisId:guid}/suggestion-outcomes")]
    public async Task<ActionResult> SaveSuggestionOutcome(
        Guid bookId,
        Guid chapterId,
        Guid analysisId,
        [FromBody] CreateSuggestionOutcomeRequest request,
        CancellationToken ct = default)
    {
        var analysis = await _db.AnalysisResults.FindAsync(new object[] { analysisId }, ct);
        if (analysis == null || analysis.ChapterId != chapterId)
            return NotFound();

        if (string.IsNullOrEmpty(request?.OriginalText) || string.IsNullOrEmpty(request?.SuggestedText))
            return BadRequest("OriginalText and SuggestedText are required.");

        var originalText = request!.OriginalText ?? string.Empty;
        var suggestedText = request.SuggestedText ?? string.Empty;
        var outcomeText = (request.Outcome ?? string.Empty).Trim();
        SuggestionOutcome outcome = outcomeText switch
        {
            _ when outcomeText.Equals("Dismissed", StringComparison.OrdinalIgnoreCase) => SuggestionOutcome.Dismissed,
            _ when outcomeText.Equals("Reverted", StringComparison.OrdinalIgnoreCase) => SuggestionOutcome.Reverted,
            _ => SuggestionOutcome.Accepted
        };

        var existing = await _db.SuggestionOutcomeRecords
            .FirstOrDefaultAsync(
                x => x.AnalysisResultId == analysisId && x.OriginalText == originalText && x.SuggestedText == suggestedText,
                ct);

        if (existing != null)
        {
            existing.Outcome = outcome;
        }
        else
        {
            _db.SuggestionOutcomeRecords.Add(new SuggestionOutcomeRecord
            {
                AnalysisResultId = analysisId,
                OriginalText = originalText,
                SuggestedText = suggestedText,
                Outcome = outcome
            });
        }

        await _db.SaveChangesAsync(ct);
        return Ok();
    }

    /// <summary>Get all suggestion outcomes for analyses in this chapter (and optionally scene). Used to restore Accepted/Dismissed state in the History tab.</summary>
    [HttpGet("suggestion-outcomes")]
    public async Task<ActionResult<List<SuggestionOutcomeDto>>> GetSuggestionOutcomes(
        Guid bookId,
        Guid chapterId,
        [FromQuery] Guid? sceneId,
        CancellationToken ct = default)
    {
        var query = _db.SuggestionOutcomeRecords.AsNoTracking()
            .Where(o => o.AnalysisResult.ChapterId == chapterId);

        if (sceneId.HasValue)
            query = query.Where(o => o.AnalysisResult.SceneId == sceneId);
        else
            query = query.Where(o => o.AnalysisResult.SceneId == null);

        var list = await query
            .Select(o => new SuggestionOutcomeDto(o.AnalysisResultId, o.OriginalText, o.SuggestedText, o.Outcome.ToString()))
            .ToListAsync(ct);

        return Ok(list);
    }

    [HttpGet("analysis-progress/{jobId:guid}")]
    public ActionResult<AnalysisProgressDto> GetAnalysisProgress(Guid bookId, Guid chapterId, Guid jobId)
    {
        if (!_progress.TryGet(jobId, out var snapshot) || snapshot == null)
            return NotFound();

        if (snapshot.ChapterId.HasValue && snapshot.ChapterId != chapterId)
            return NotFound();
        if (snapshot.BookId.HasValue && snapshot.BookId != bookId)
            return NotFound();

        var dto = new AnalysisProgressDto(
            snapshot.JobId,
            snapshot.AnalysisType.ToString(),
            snapshot.Scope.ToString(),
            snapshot.BookId,
            snapshot.ChapterId,
            snapshot.SceneId,
            snapshot.Status.ToString(),
            snapshot.CurrentChunkIndex,
            snapshot.TotalChunks,
            snapshot.Message,
            snapshot.EstimatedCompletionPercent);

        return Ok(dto);
    }

    /// <summary>
    /// Start an async analysis job (currently only supports Proofread). Returns immediately with a jobId,
    /// while the actual work runs in the background and can be tracked via analysis-progress and analysis-jobs endpoints.
    /// </summary>
    [HttpPost("analysis-jobs")]
    public async Task<ActionResult<StartAnalysisJobResponse>> StartAnalysisJob(
        Guid bookId,
        Guid chapterId,
        [FromQuery] Guid? sceneId,
        [FromBody] RunAnalysisRequest req,
        CancellationToken ct)
    {
        var chapter = await _db.Chapters.Include(c => c.Book)
            .FirstOrDefaultAsync(c => c.BookId == bookId && c.Id == chapterId, ct);
        if (chapter == null) return NotFound();

        var (analysisType, customPrompt, language) = await ResolveAnalysisParamsAsync(chapterId, req, chapter, ct);
        var scope = sceneId.HasValue ? AnalysisScope.Scene : AnalysisScope.Chapter;
        var targetId = sceneId ?? chapterId;

        // For now the async job path is focused on long-running Proofread (chunked).
        if (analysisType != AnalysisType.Proofread)
        {
            return BadRequest(new { error = "Async analysis jobs are currently supported only for Proofread type." });
        }

        var jobId = Guid.NewGuid();

        // Initialize progress so the client can start polling immediately.
        _progress.StartJob(
            jobId,
            scope,
            analysisType,
            bookId,
            chapterId,
            sceneId,
            "Queued proofread job…");

        // Fire-and-forget background task that runs the actual analysis using a new DI scope.
        _ = Task.Run(async () =>
        {
            try
            {
                using var serviceScope = _scopeFactory.CreateScope();
                var services = serviceScope.ServiceProvider;
                var unified = services.GetRequiredService<UnifiedAnalysisService>();
                var progress = services.GetRequiredService<AnalysisProgressTracker>();
                var logger = services.GetRequiredService<ILogger<AnalysisController>>();

                try
                {
                    await unified.RunAsync(scope, analysisType, targetId, customPrompt, language, jobId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Async analysis job {JobId} failed for {Scope}/{Type}", jobId, scope, analysisType);
                    progress.SetStatus(jobId, AnalysisProgressStatus.Failed, ex.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute async analysis job {JobId}", jobId);
                _progress.SetStatus(jobId, AnalysisProgressStatus.Failed, "Async analysis job failed to start.");
            }
        }, CancellationToken.None);

        var response = new StartAnalysisJobResponse(jobId, analysisType.ToString(), scope.ToString());
        return Ok(response);
    }

    /// <summary>
    /// Resolve the final AnalysisResult by jobId once an async job has completed.
    /// Used by the frontend after progress status becomes Succeeded.
    /// </summary>
    [HttpGet("analysis-jobs/{jobId:guid}")]
    public async Task<ActionResult<AnalysisResultDto>> GetAnalysisByJobId(
        Guid bookId,
        Guid chapterId,
        Guid jobId,
        CancellationToken ct)
    {
        var analysis = await _db.AnalysisResults
            .FirstOrDefaultAsync(a => a.ChapterId == chapterId && a.BookId == bookId && a.JobId == jobId, ct);

        if (analysis == null)
            return NotFound();

        return Ok(ToDto(analysis));
    }
}
