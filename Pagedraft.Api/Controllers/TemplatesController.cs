using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;

namespace Pagedraft.Api.Controllers;

[ApiController]
[Route("api/templates")]
public class TemplatesController : ControllerBase
{
    private readonly AppDbContext _db;

    public TemplatesController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<ActionResult<List<PromptTemplateDto>>> GetAll(CancellationToken ct)
    {
        var list = await _db.PromptTemplates.ToListAsync(ct);
        return Ok(list.Select(t => new PromptTemplateDto(t.Id, t.Name, t.Type, t.TemplateText, t.IsBuiltIn, t.Language)).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<PromptTemplateDto>> Create([FromBody] CreateTemplateRequest req, CancellationToken ct)
    {
        var t = new PromptTemplate
        {
            Name = req.Name,
            Type = req.Type,
            TemplateText = req.TemplateText,
            Language = req.Language ?? "he",
            IsBuiltIn = false
        };
        _db.PromptTemplates.Add(t);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(GetById), new { id = t.Id }, new PromptTemplateDto(t.Id, t.Name, t.Type, t.TemplateText, t.IsBuiltIn, t.Language));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<PromptTemplateDto>> GetById(Guid id, CancellationToken ct)
    {
        var t = await _db.PromptTemplates.FindAsync(new object[] { id }, ct);
        if (t == null) return NotFound();
        return Ok(new PromptTemplateDto(t.Id, t.Name, t.Type, t.TemplateText, t.IsBuiltIn, t.Language));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<PromptTemplateDto>> Update(Guid id, [FromBody] UpdateTemplateRequest req, CancellationToken ct)
    {
        var t = await _db.PromptTemplates.FindAsync(new object[] { id }, ct);
        if (t == null) return NotFound();
        if (t.IsBuiltIn) return Forbid();
        if (req.Name != null) t.Name = req.Name;
        if (req.Type != null) t.Type = req.Type;
        if (req.TemplateText != null) t.TemplateText = req.TemplateText;
        if (req.Language != null) t.Language = req.Language;
        await _db.SaveChangesAsync(ct);
        return Ok(new PromptTemplateDto(t.Id, t.Name, t.Type, t.TemplateText, t.IsBuiltIn, t.Language));
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult> Delete(Guid id, CancellationToken ct)
    {
        var t = await _db.PromptTemplates.FindAsync(new object[] { id }, ct);
        if (t == null) return NotFound();
        if (t.IsBuiltIn) return Forbid();
        _db.PromptTemplates.Remove(t);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
