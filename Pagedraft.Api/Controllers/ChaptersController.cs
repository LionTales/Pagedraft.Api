using Microsoft.AspNetCore.Mvc;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services;

namespace Pagedraft.Api.Controllers;

[ApiController]
[Route("api/books/{bookId:guid}/chapters")]
public class ChaptersController : ControllerBase
{
    private readonly ChapterService _chapterService;

    public ChaptersController(ChapterService chapterService) => _chapterService = chapterService;

    [HttpGet]
    public async Task<ActionResult<List<ChapterSummaryDto>>> GetAll(Guid bookId, CancellationToken ct)
    {
        var list = await _chapterService.GetAllByBookAsync(bookId, ct);
        return Ok(list.Select(c => new ChapterSummaryDto(c.Id, c.Title, c.PartName, c.Order, c.WordCount, c.UpdatedAt)).ToList());
    }

    [HttpGet("{chapterId:guid}")]
    public async Task<ActionResult<ChapterDto>> GetById(Guid bookId, Guid chapterId, CancellationToken ct)
    {
        var c = await _chapterService.GetByIdAsync(bookId, chapterId, ct);
        if (c == null) return NotFound();
        return Ok(new ChapterDto(c.Id, c.Title, c.PartName, c.Order, c.WordCount, c.UpdatedAt, c.ContentSfdt));
    }

    [HttpPost]
    public async Task<ActionResult<ChapterDto>> Create(Guid bookId, [FromBody] CreateChapterRequest req, CancellationToken ct)
    {
        var emptySfdt = "{\"sections\":[{\"blocks\":[]}]}";
        var c = await _chapterService.CreateAsync(bookId, req.Title, req.PartName, req.Order, emptySfdt, ct);
        return CreatedAtAction(nameof(GetById), new { bookId, chapterId = c.Id }, new ChapterDto(c.Id, c.Title, c.PartName, c.Order, c.WordCount, c.UpdatedAt, c.ContentSfdt));
    }

    [HttpPatch("{chapterId:guid}")]
    public async Task<ActionResult<ChapterSummaryDto>> Update(Guid bookId, Guid chapterId, [FromBody] UpdateChapterRequest req, CancellationToken ct)
    {
        var c = await _chapterService.SaveAsync(bookId, chapterId, req.ContentSfdt, req.Title, req.PartName, req.Order, ct);
        if (c == null) return NotFound();
        return Ok(new ChapterSummaryDto(c.Id, c.Title, c.PartName, c.Order, c.WordCount, c.UpdatedAt));
    }

    [HttpDelete("{chapterId:guid}")]
    public async Task<ActionResult> Delete(Guid bookId, Guid chapterId, CancellationToken ct)
    {
        if (!await _chapterService.DeleteAsync(bookId, chapterId, ct)) return NotFound();
        return NoContent();
    }

    [HttpPut("reorder")]
    public async Task<ActionResult<List<ChapterSummaryDto>>> Reorder(Guid bookId, [FromBody] ReorderChaptersRequest req, CancellationToken ct)
    {
        var newOrder = req.Chapters.Select(x => (x.ChapterId, x.Order)).ToList();
        await _chapterService.ReorderAsync(bookId, newOrder, ct);
        var list = await _chapterService.GetAllByBookAsync(bookId, ct);
        return Ok(list.Select(c => new ChapterSummaryDto(c.Id, c.Title, c.PartName, c.Order, c.WordCount, c.UpdatedAt)).ToList());
    }
}
