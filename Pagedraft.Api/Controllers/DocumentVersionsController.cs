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
            .Select(v => new DocumentVersionDto(v.Id, v.BookId, v.ChapterId, v.SceneId, v.CreatedAt, v.Label, v.AnalysisResultId, v.OriginalText, v.SuggestedText))
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
            OriginalText = request.OriginalText?.Trim().Length > 0 ? request.OriginalText!.Trim() : null,
            SuggestedText = request.SuggestedText?.Trim().Length > 0 ? request.SuggestedText!.Trim() : null
        };
        _db.DocumentVersions.Add(version);
        await _db.SaveChangesAsync(ct);

        return Ok(new DocumentVersionDto(
            version.Id, version.BookId, version.ChapterId, version.SceneId,
            version.CreatedAt, version.Label, version.AnalysisResultId, version.OriginalText, version.SuggestedText));
    }

    [HttpGet("versions/{id:guid}")]
    public async Task<ActionResult<DocumentVersionDetailDto>> Get(
        Guid bookId,
        Guid chapterId,
        Guid id,
        CancellationToken ct = default)
    {
        var v = await _db.DocumentVersions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.BookId == bookId && x.ChapterId == chapterId, ct);
        if (v == null) return NotFound();

        return Ok(new DocumentVersionDetailDto(
            v.Id, v.BookId, v.ChapterId, v.SceneId, v.CreatedAt, v.Label, v.ContentSfdt, v.AnalysisResultId, v.OriginalText, v.SuggestedText));
    }
}
