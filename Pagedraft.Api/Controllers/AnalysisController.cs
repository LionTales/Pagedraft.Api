using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Ai;
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
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly IAiRouter _router;
    private readonly PromptFactory _promptFactory;

    public AnalysisController(
        AppDbContext db,
        UnifiedAnalysisService unifiedAnalysis,
        AnalysisProgressTracker progress,
        IServiceScopeFactory scopeFactory,
        ILogger<AnalysisController> logger,
        IHostApplicationLifetime appLifetime,
        IAiRouter router,
        PromptFactory promptFactory)
    {
        _db = db;
        _unifiedAnalysis = unifiedAnalysis;
        _progress = progress;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _appLifetime = appLifetime;
        _router = router;
        _promptFactory = promptFactory;
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
                {
                    var payload = System.Text.Json.JsonSerializer.Serialize(new { token });
                    await Response.WriteAsync($"data: {payload}\n\n", ct);
                }
                var donePayload = System.Text.Json.JsonSerializer.Serialize(new { token = string.Empty, done = true });
                await Response.WriteAsync($"data: {donePayload}\n\n", ct);
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

    internal static AnalysisResultDto ToDto(AnalysisResult a)
    {
        var suggestions = a.Suggestions
            .OrderBy(s => s.OrderIndex)
            .Select(s => new AnalysisSuggestionDto(
                s.Id,
                s.AnalysisResultId,
                s.OriginalText,
                s.SuggestedText,
                s.StartOffset,
                s.EndOffset,
                s.Reason,
                s.Category,
                s.Explanation,
                s.Outcome?.ToString(),
                s.OrderIndex))
            .ToList();

        return new AnalysisResultDto(
            a.Id,
            a.ChapterId,
            a.JobId,
            a.Type,
            a.ResultText,
            a.ModelName,
            a.CreatedAt,
            StructuredResult: a.StructuredResult,
            Scope: a.Scope.ToString(),
            AnalysisType: a.AnalysisType.ToString(),
            SceneId: a.SceneId,
            BookId: a.BookId,
            Language: a.Language,
            Status: a.Status.ToString(),
            ProofreadNoChangesHint: a.ProofreadNoChangesHint,
            Suggestions: suggestions);
    }

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

    [HttpGet("analyses")]
    public async Task<ActionResult<List<AnalysisResultDto>>> GetAnalyses(Guid bookId, Guid chapterId, [FromQuery] Guid? sceneId = null, [FromQuery] string? analysisType = null, CancellationToken ct = default)
    {
        var query = _db.AnalysisResults
            .AsNoTracking()
            .Include(a => a.Suggestions)
            .Where(a => a.ChapterId == chapterId);
        if (sceneId.HasValue)
            query = query.Where(a => a.SceneId == sceneId);

        if (!string.IsNullOrWhiteSpace(analysisType) && Enum.TryParse<AnalysisType>(analysisType, ignoreCase: true, out var type))
            query = query.Where(a => a.AnalysisType == type);

        var items = (await query.ToListAsync(ct)).OrderByDescending(a => a.CreatedAt).ToList();
        var dtos = items.Select(a => ToDto(a)).ToList();

        return Ok(dtos);
    }

    [HttpGet("analyses/{id:guid}")]
    public async Task<ActionResult<AnalysisResultDto>> GetAnalysisById(Guid bookId, Guid chapterId, Guid id, CancellationToken ct)
    {
        var a = await _db.AnalysisResults
            .Include(x => x.Suggestions)
            .FirstOrDefaultAsync(x => x.ChapterId == chapterId && x.Id == id, ct);
        if (a == null) return NotFound();

        return Ok(ToDto(a));
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

        // If the application is already stopping, don't enqueue new jobs that can never run.
        var shutdownToken = _appLifetime.ApplicationStopping;
        if (shutdownToken.IsCancellationRequested)
        {
            return StatusCode(503, new { error = "Server is shutting down; cannot start new analysis job." });
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
        // Tie the inner work to the host's shutdown token, but always start the task so we can
        // transition the job out of "Queued" even if shutdown happens right after StartJob.
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
                    await unified.RunAsync(scope, analysisType, targetId, customPrompt, language, jobId, shutdownToken);
                }
                catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
                {
                    // Mark the job as canceled when the host is shutting down.
                    progress.SetStatus(jobId, AnalysisProgressStatus.Canceled, "Analysis job canceled due to application shutdown.");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Async analysis job {JobId} failed for {Scope}/{Type}", jobId, scope, analysisType);
                    progress.SetStatus(jobId, AnalysisProgressStatus.Failed, ex.Message);
                }
            }
            catch (Exception ex)
            {
                // Use the same AnalysisProgressTracker to mark the job as failed if the scoped execution setup fails.
                try
                {
                    _progress.SetStatus(jobId, AnalysisProgressStatus.Failed, "Async analysis job failed to start.");
                }
                catch
                {
                    // Swallow to avoid crashing the background task if progress tracking is unavailable.
                }

                try
                {
                    _logger.LogError(ex, "Failed to execute async analysis job {JobId}", jobId);
                }
                catch
                {
                    // Logging should not crash the background task.
                }
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
            .Include(a => a.Suggestions)
            .FirstOrDefaultAsync(a => a.ChapterId == chapterId && a.BookId == bookId && a.JobId == jobId, ct);

        if (analysis == null)
            return NotFound();

        return Ok(ToDto(analysis));
    }

    /// <summary>Get all persisted suggestions for a given analysis result.</summary>
    [HttpGet("analyses/{analysisId:guid}/suggestions")]
    public async Task<ActionResult<List<AnalysisSuggestionDto>>> GetSuggestionsForAnalysis(
        Guid bookId,
        Guid chapterId,
        Guid analysisId,
        CancellationToken ct = default)
    {
        var analysis = await _db.AnalysisResults
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == analysisId && a.ChapterId == chapterId && a.BookId == bookId, ct);
        if (analysis == null) return NotFound();

        var items = await _db.AnalysisSuggestions.AsNoTracking()
            .Where(s => s.AnalysisResultId == analysisId)
            .OrderBy(s => s.OrderIndex)
            .Select(s => new AnalysisSuggestionDto(
                s.Id,
                s.AnalysisResultId,
                s.OriginalText,
                s.SuggestedText,
                s.StartOffset,
                s.EndOffset,
                s.Reason,
                s.Category,
                s.Explanation,
                s.Outcome == null ? (string?)null : s.Outcome.ToString(),
                s.OrderIndex))
            .ToListAsync(ct);

        return Ok(items);
    }

    /// <summary>Update the outcome for a single suggestion (Accepted, Dismissed, Reverted, Superseded).</summary>
    [HttpPatch("suggestions/{suggestionId:guid}/outcome")]
    public async Task<ActionResult> UpdateSuggestionOutcome(
        Guid bookId,
        Guid chapterId,
        Guid suggestionId,
        [FromBody] UpdateSuggestionOutcomeRequest request,
        CancellationToken ct = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Outcome))
            return BadRequest("Outcome is required.");

        var suggestion = await _db.AnalysisSuggestions
            .Include(s => s.AnalysisResult)
            .FirstOrDefaultAsync(s => s.Id == suggestionId, ct);
        if (suggestion == null || suggestion.AnalysisResult.ChapterId != chapterId || suggestion.AnalysisResult.BookId != bookId)
            return NotFound();

        if (!Enum.TryParse<SuggestionOutcome>(request.Outcome, ignoreCase: true, out var outcome))
            return BadRequest("Invalid outcome. Must be Accepted, Dismissed, Reverted, or Superseded.");

        suggestion.Outcome = outcome;
        await _db.SaveChangesAsync(ct);
        return Ok();
    }

    /// <summary>
    /// Explain why a specific suggestion was made. Uses LLM with a focused prompt and caches the explanation on the suggestion row.
    /// </summary>
    [HttpPost("suggestions/{suggestionId:guid}/explain")]
    public async Task<ActionResult<ExplainSuggestionResponse>> ExplainSuggestion(
        Guid bookId,
        Guid chapterId,
        Guid suggestionId,
        CancellationToken ct = default)
    {
        var suggestion = await _db.AnalysisSuggestions
            .Include(s => s.AnalysisResult)
            .FirstOrDefaultAsync(s => s.Id == suggestionId, ct);
        if (suggestion == null || suggestion.AnalysisResult.ChapterId != chapterId || suggestion.AnalysisResult.BookId != bookId)
            return NotFound();

        if (!string.IsNullOrWhiteSpace(suggestion.Explanation))
            return Ok(new ExplainSuggestionResponse(suggestion.Explanation));

        var language = string.IsNullOrWhiteSpace(suggestion.AnalysisResult.Language)
            ? "he"
            : suggestion.AnalysisResult.Language;

        var prompt = _promptFactory.GetExplainSuggestionPrompt(
            suggestion.OriginalText,
            suggestion.SuggestedText,
            suggestion.Reason,
            language);

        var request = new AiRequest
        {
            InputText = suggestion.OriginalText,
            Instruction = prompt,
            TaskType = AiTaskType.GenericChat,
            Language = language,
            SourceId = suggestion.Id.ToString()
        };

        var response = await _router.CompleteAsync(request, ct);
        var explanation = (response.Content ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(explanation))
            explanation = "No explanation could be generated for this suggestion.";

        suggestion.Explanation = explanation;
        await _db.SaveChangesAsync(ct);

        return Ok(new ExplainSuggestionResponse(explanation));
    }
}
