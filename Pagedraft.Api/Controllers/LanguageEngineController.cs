using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pagedraft.Api.Data;
using Pagedraft.Api.Services.LanguageEngine.Contracts;

namespace Pagedraft.Api.Controllers;

[ApiController]
[Route("api/books/{bookId:guid}/chapters/{chapterId:guid}/language-engine")]
public class LanguageEngineController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILanguageEngine _languageEngine;

    public LanguageEngineController(AppDbContext db, ILanguageEngine languageEngine)
    {
        _db = db;
        _languageEngine = languageEngine;
    }

    /// <summary>Run full language engine pipeline (Normalize → Detect → Rewrite → Analyze).</summary>
    [HttpPost("full")]
    public async Task<ActionResult<LanguageEngineResult>> RunFullPipeline(
        Guid bookId,
        Guid chapterId,
        [FromBody] LanguageEngineRequestDto? requestDto,
        CancellationToken ct = default)
    {
        var chapter = await _db.Chapters.Include(c => c.Book).FirstOrDefaultAsync(c => c.BookId == bookId && c.Id == chapterId, ct);
        if (chapter == null) return NotFound();

        var inputText = chapter.ContentText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(inputText))
            return BadRequest(new { error = "Chapter has no content text" });

        var request = new LanguageEngineRequest
        {
            InputText = inputText,
            Language = requestDto?.Language ?? chapter.Book?.Language ?? "en-US",
            Options = requestDto?.Options ?? new LanguageEngineOptions
            {
                EnableNormalize = true,
                EnableDetect = true,
                EnableRewrite = requestDto?.Options?.EnableRewrite ?? false,
                EnableAnalyze = requestDto?.Options?.EnableAnalyze ?? false
            }
        };

        try
        {
            var result = await _languageEngine.ProcessAsync(request, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>Detect issues only (Normalize → Detect).</summary>
    [HttpPost("detect")]
    public async Task<ActionResult<LanguageEngineResult>> DetectIssues(
        Guid bookId,
        Guid chapterId,
        [FromBody] LanguageEngineRequestDto? requestDto,
        CancellationToken ct = default)
    {
        var chapter = await _db.Chapters.Include(c => c.Book).FirstOrDefaultAsync(c => c.BookId == bookId && c.Id == chapterId, ct);
        if (chapter == null) return NotFound();

        var inputText = chapter.ContentText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(inputText))
            return BadRequest(new { error = "Chapter has no content text" });

        var request = new LanguageEngineRequest
        {
            InputText = inputText,
            Language = requestDto?.Language ?? chapter.Book?.Language ?? "en-US",
            Options = new LanguageEngineOptions
            {
                EnableNormalize = true,
                EnableDetect = true,
                EnableRewrite = false,
                EnableAnalyze = false
            }
        };

        try
        {
            var result = await _languageEngine.ProcessAsync(request, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>Rewrite text only (Normalize → Detect → Rewrite).</summary>
    [HttpPost("rewrite")]
    public async Task<ActionResult<LanguageEngineResult>> RewriteText(
        Guid bookId,
        Guid chapterId,
        [FromBody] LanguageEngineRequestDto? requestDto,
        CancellationToken ct = default)
    {
        var chapter = await _db.Chapters.Include(c => c.Book).FirstOrDefaultAsync(c => c.BookId == bookId && c.Id == chapterId, ct);
        if (chapter == null) return NotFound();

        var inputText = chapter.ContentText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(inputText))
            return BadRequest(new { error = "Chapter has no content text" });

        var request = new LanguageEngineRequest
        {
            InputText = inputText,
            Language = requestDto?.Language ?? chapter.Book?.Language ?? "en-US",
            Options = new LanguageEngineOptions
            {
                EnableNormalize = true,
                EnableDetect = true,
                EnableRewrite = true,
                EnableAnalyze = false,
                PreferredModel = requestDto?.Options?.PreferredModel
            }
        };

        try
        {
            var result = await _languageEngine.ProcessAsync(request, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>Get detected issues for a chapter.</summary>
    [HttpGet("issues")]
    public async Task<ActionResult<IssuesResponse>> GetIssues(
        Guid bookId,
        Guid chapterId,
        CancellationToken ct = default)
    {
        var chapter = await _db.Chapters.Include(c => c.Book).FirstOrDefaultAsync(c => c.BookId == bookId && c.Id == chapterId, ct);
        if (chapter == null) return NotFound();

        var inputText = chapter.ContentText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(inputText))
            return Ok(new IssuesResponse { Issues = [] });

        var request = new LanguageEngineRequest
        {
            InputText = inputText,
            Language = chapter.Book?.Language ?? "en-US",
            Options = new LanguageEngineOptions
            {
                EnableNormalize = true,
                EnableDetect = true,
                EnableRewrite = false,
                EnableAnalyze = false
            }
        };

        try
        {
            var result = await _languageEngine.ProcessAsync(request, ct);
            var unav = result.Metadata.TryGetValue("languageToolUnavailable", out var unavObj) && unavObj is true;
            var msg = result.Metadata.TryGetValue("languageToolMessage", out var msgObj) ? msgObj?.ToString() : null;
            return Ok(new IssuesResponse
            {
                Issues = result.Issues,
                LanguageToolUnavailable = unav ? true : null,
                LanguageToolMessage = msg
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
}

/// <summary>Response for GET issues; includes optional message when LanguageTool is unavailable.</summary>
public class IssuesResponse
{
    public List<LanguageIssue> Issues { get; set; } = new();
    public bool? LanguageToolUnavailable { get; set; }
    public string? LanguageToolMessage { get; set; }
}

/// <summary>DTO for language engine requests.</summary>
public class LanguageEngineRequestDto
{
    public string? Language { get; set; }
    public LanguageEngineOptions? Options { get; set; }
}
