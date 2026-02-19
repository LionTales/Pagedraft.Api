using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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

    public AnalysisController(AppDbContext db, UnifiedAnalysisService unifiedAnalysis)
    {
        _db = db;
        _unifiedAnalysis = unifiedAnalysis;
    }

    [HttpPost("analyze")]
    public async Task<ActionResult<AnalysisResultDto>> RunAnalysis(Guid bookId, Guid chapterId, [FromQuery] Guid? sceneId, [FromBody] RunAnalysisRequest req, CancellationToken ct)
    {
        var chapter = await _db.Chapters.FirstOrDefaultAsync(c => c.BookId == bookId && c.Id == chapterId, ct);
        if (chapter == null) return NotFound();

        var (analysisType, customPrompt, language) = await ResolveAnalysisParamsAsync(chapterId, req, ct);
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
            var result = await _unifiedAnalysis.RunAsync(scope, analysisType, targetId, customPrompt, language, ct);
            return Ok(ToDto(result));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("No chapter text") || ex.Message.Contains("no content") || ex.Message.Contains("not found"))
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(502, new { error = ex.Message });
        }
    }

    private static AnalysisResultDto ToDto(AnalysisResult a) =>
        new(a.Id, a.ChapterId, a.Type, a.ResultText, a.ModelName, a.CreatedAt,
            StructuredResult: a.StructuredResult,
            Scope: a.Scope.ToString(),
            AnalysisType: a.AnalysisType.ToString(),
            SceneId: a.SceneId,
            BookId: a.BookId,
            Language: a.Language);

    private async Task<(AnalysisType analysisType, string? customPrompt, string language)> ResolveAnalysisParamsAsync(Guid chapterId, RunAnalysisRequest req, CancellationToken ct)
    {
        var language = string.IsNullOrEmpty(req.Language) ? "he" : req.Language;

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

    private static string EscapeJson(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    [HttpGet("analyses")]
    public async Task<ActionResult<List<AnalysisResultDto>>> GetAnalyses(Guid bookId, Guid chapterId, [FromQuery] Guid? sceneId = null, [FromQuery] string? analysisType = null, CancellationToken ct = default)
    {
        var query = _db.AnalysisResults.AsNoTracking().Where(a => a.ChapterId == chapterId);
        if (sceneId.HasValue)
            query = query.Where(a => a.SceneId == sceneId);

        if (!string.IsNullOrWhiteSpace(analysisType) && Enum.TryParse<AnalysisType>(analysisType, ignoreCase: true, out var type))
            query = query.Where(a => a.AnalysisType == type);

        var items = await query.ToListAsync(ct);
        var ordered = items.OrderByDescending(a => a.CreatedAt).ToList();
        return Ok(ordered.Select(ToDto).ToList());
    }

    [HttpGet("analyses/{id:guid}")]
    public async Task<ActionResult<AnalysisResultDto>> GetAnalysisById(Guid bookId, Guid chapterId, Guid id, CancellationToken ct)
    {
        var a = await _db.AnalysisResults.FirstOrDefaultAsync(x => x.ChapterId == chapterId && x.Id == id, ct);
        if (a == null) return NotFound();
        return Ok(ToDto(a));
    }
}
