using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;

namespace Pagedraft.Api.Controllers;

[ApiController]
[Route("api/books/{bookId:guid}/chapters/{chapterId:guid}")]
public class DocumentVersionsController : ControllerBase
{
    private readonly AppDbContext _db;

    public DocumentVersionsController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet("versions")]
    public async Task<ActionResult<List<DocumentVersionDto>>> List(
        Guid bookId,
        Guid chapterId,
        [FromQuery] Guid? sceneId,
        CancellationToken ct = default)
    {
        var query = _db.DocumentVersions.AsNoTracking()
            .Where(v => v.BookId == bookId && v.ChapterId == chapterId);

        if (sceneId.HasValue)
            query = query.Where(v => v.SceneId == sceneId);
        else
            query = query.Where(v => v.SceneId == null);

        var list = await query
            .GroupJoin(
                _db.AnalysisResults.AsNoTracking(),
                v => v.AnalysisResultId,
                a => a.Id,
                (v, analyses) => new { Version = v, Analysis = analyses.FirstOrDefault() })
            .Select(x => new DocumentVersionDto(
                x.Version.Id,
                x.Version.BookId,
                x.Version.ChapterId,
                x.Version.SceneId,
                x.Version.CreatedAt,
                x.Version.Label,
                x.Version.AnalysisResultId,
                x.Version.SuggestionId,
                x.Version.OriginalText,
                x.Version.SuggestedText,
                x.Analysis != null ? x.Analysis.Status.ToString() : null))
            .ToListAsync(ct);

        return Ok(list.OrderByDescending(v => v.CreatedAt).ToList());
    }

    [HttpPost("versions")]
    public async Task<ActionResult<DocumentVersionDto>> Create(
        Guid bookId,
        Guid chapterId,
        [FromQuery] Guid? sceneId,
        [FromBody] CreateDocumentVersionRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(request?.ContentSfdt))
            return BadRequest("ContentSfdt is required.");

        var version = new DocumentVersion
        {
            BookId = bookId,
            ChapterId = chapterId,
            SceneId = sceneId,
            ContentSfdt = request.ContentSfdt,
            Label = request.Label?.Trim().Length > 0 ? request.Label!.Trim() : null,
            AnalysisResultId = request.AnalysisId,
            SuggestionId = request.SuggestionId,
            OriginalText = request.OriginalText?.Trim().Length > 0 ? request.OriginalText!.Trim() : null,
            SuggestedText = request.SuggestedText?.Trim().Length > 0 ? request.SuggestedText!.Trim() : null
        };
        _db.DocumentVersions.Add(version);
        await _db.SaveChangesAsync(ct);

        string? analysisStatus = null;
        if (version.AnalysisResultId.HasValue)
        {
            analysisStatus = await _db.AnalysisResults.AsNoTracking()
                .Where(a => a.Id == version.AnalysisResultId.Value)
                .Select(a => a.Status.ToString())
                .FirstOrDefaultAsync(ct);
        }

        return Ok(new DocumentVersionDto(
            version.Id,
            version.BookId,
            version.ChapterId,
            version.SceneId,
            version.CreatedAt,
            version.Label,
            version.AnalysisResultId,
            version.SuggestionId,
            version.OriginalText,
            version.SuggestedText,
            analysisStatus));
    }

    [HttpGet("versions/{id:guid}")]
    public async Task<ActionResult<DocumentVersionDetailDto>> Get(
        Guid bookId,
        Guid chapterId,
        Guid id,
        CancellationToken ct = default)
    {
        var v = await _db.DocumentVersions.AsNoTracking()
            .Where(x => x.Id == id && x.BookId == bookId && x.ChapterId == chapterId)
            .GroupJoin(
                _db.AnalysisResults.AsNoTracking(),
                dv => dv.AnalysisResultId,
                a => a.Id,
                (dv, analyses) => new { Version = dv, Analysis = analyses.FirstOrDefault() })
            .FirstOrDefaultAsync(ct);
        if (v == null) return NotFound();

        return Ok(new DocumentVersionDetailDto(
            v.Version.Id,
            v.Version.BookId,
            v.Version.ChapterId,
            v.Version.SceneId,
            v.Version.CreatedAt,
            v.Version.Label,
            v.Version.ContentSfdt,
            v.Version.AnalysisResultId,
            v.Version.SuggestionId,
            v.Version.OriginalText,
            v.Version.SuggestedText,
            v.Analysis != null ? v.Analysis.Status.ToString() : null));
    }
}
