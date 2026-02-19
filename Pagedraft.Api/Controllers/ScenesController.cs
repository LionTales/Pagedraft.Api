using Microsoft.AspNetCore.Mvc;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services;

namespace Pagedraft.Api.Controllers;

[ApiController]
[Route("api/books/{bookId:guid}/chapters/{chapterId:guid}/scenes")]
public class ScenesController : ControllerBase
{
    private readonly SceneService _sceneService;

    public ScenesController(SceneService sceneService) => _sceneService = sceneService;

    [HttpGet]
    public async Task<ActionResult<List<SceneSummaryDto>>> GetAll(Guid bookId, Guid chapterId, CancellationToken ct)
    {
        try
        {
            var list = await _sceneService.GetAllByChapterAsync(bookId, chapterId, ct);
            return Ok(list.Select(s => new SceneSummaryDto(s.Id, s.ChapterId, s.Title, s.Order, s.UpdatedAt)).ToList());
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
    }

    [HttpGet("{sceneId:guid}")]
    public async Task<ActionResult<SceneDto>> GetById(Guid bookId, Guid chapterId, Guid sceneId, CancellationToken ct)
    {
        try
        {
            var scene = await _sceneService.GetByIdAsync(bookId, chapterId, sceneId, ct);
            if (scene == null) return NotFound();
            return Ok(ToDto(scene));
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
    }

    [HttpPost]
    public async Task<ActionResult<SceneDto>> Create(Guid bookId, Guid chapterId, [FromBody] CreateSceneDto req, CancellationToken ct)
    {
        var scene = await _sceneService.CreateAsync(bookId, chapterId, req.Title, req.Order, req.ContentSfdt, ct);
        if (scene == null) return NotFound();
        return CreatedAtAction(nameof(GetById), new { bookId, chapterId, sceneId = scene.Id }, ToDto(scene));
    }

    [HttpPatch("{sceneId:guid}")]
    public async Task<ActionResult<SceneDto>> Update(Guid bookId, Guid chapterId, Guid sceneId, [FromBody] UpdateSceneDto req, CancellationToken ct)
    {
        var scene = await _sceneService.UpdateAsync(bookId, chapterId, sceneId, req.Title, req.Order, req.ContentSfdt, ct);
        if (scene == null) return NotFound();
        return Ok(ToDto(scene));
    }

    [HttpDelete("{sceneId:guid}")]
    public async Task<ActionResult> Delete(Guid bookId, Guid chapterId, Guid sceneId, CancellationToken ct)
    {
        if (!await _sceneService.DeleteAsync(bookId, chapterId, sceneId, ct)) return NotFound();
        return NoContent();
    }

    [HttpPut("reorder")]
    public async Task<ActionResult<List<SceneSummaryDto>>> Reorder(Guid bookId, Guid chapterId, [FromBody] ReorderScenesRequest req, CancellationToken ct)
    {
        var newOrder = req.Scenes.Select(x => (x.SceneId, x.Order)).ToList();
        var list = await _sceneService.ReorderAsync(bookId, chapterId, newOrder, ct);
        if (list == null) return NotFound();
        return Ok(list.Select(s => new SceneSummaryDto(s.Id, s.ChapterId, s.Title, s.Order, s.UpdatedAt)).ToList());
    }

    [HttpPost("split-scenes")]
    public async Task<ActionResult<List<SceneSummaryDto>>> SplitScenes(Guid bookId, Guid chapterId, CancellationToken ct)
    {
        try
        {
            var list = await _sceneService.SplitScenesFromChapterAsync(bookId, chapterId, ct);
            return Ok(list.Select(s => new SceneSummaryDto(s.Id, s.ChapterId, s.Title, s.Order, s.UpdatedAt)).ToList());
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not found"))
        {
            return NotFound();
        }
    }

    private static SceneDto ToDto(Scene s) => new(s.Id, s.ChapterId, s.Title, s.Order, s.ContentSfdt, s.CreatedAt, s.UpdatedAt);
}
