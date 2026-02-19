using Microsoft.AspNetCore.Mvc;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services;

namespace Pagedraft.Api.Controllers;

[ApiController]
[Route("api/document")]
public class DocumentController : ControllerBase
{
    private readonly DocxParserService _docxParser;
    private readonly SfdtConversionService _sfdtConversion;
    private readonly ChapterService _chapterService;
    private readonly BookAssemblyService _bookAssembly;

    public DocumentController(DocxParserService docxParser, SfdtConversionService sfdtConversion, ChapterService chapterService, BookAssemblyService bookAssembly)
    {
        _docxParser = docxParser;
        _sfdtConversion = sfdtConversion;
        _chapterService = chapterService;
        _bookAssembly = bookAssembly;
    }

    /// <summary>
    /// Parses a DOCX file and returns a preview of detected chapters without persisting any changes.
    /// </summary>
    [HttpPost("import/{bookId:guid}")]
    public async Task<ActionResult<ImportPreviewResponseDto>> Import(Guid bookId, IFormFile file, CancellationToken ct)
    {
        if (file == null || file.Length == 0)
        {
            return BadRequest("No file");
        }

        if (!file.FileName.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("Only DOCX is supported");
        }

        await using var stream = file.OpenReadStream();
        var segments = _docxParser.SplitIntoChapters(stream);
        if (segments.Count == 0)
        {
            return BadRequest("No chapters detected in the document");
        }

        var chapters = new List<ImportPreviewChapterDto>();
        try
        {
            foreach (var seg in segments.OrderBy(s => s.Order))
            {
                var result = _sfdtConversion.ConvertToSfdt(seg.BodyElements);
                var snippet = result.PlainText.Length <= 240
                    ? result.PlainText
                    : result.PlainText.Substring(0, 240) + "…";

                chapters.Add(new ImportPreviewChapterDto(
                    TempId: Guid.NewGuid(),
                    Order: seg.Order,
                    Title: seg.Title,
                    PartName: seg.PartName,
                    WordCount: result.WordCount,
                    Snippet: snippet,
                    SfdtJson: result.SfdtJson
                ));
            }
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { detail = ex.Message });
        }

        var response = new ImportPreviewResponseDto(
            BookId: bookId,
            FileName: file.FileName,
            FileSize: file.Length,
            PageCount: null,
            Chapters: chapters
        );

        return Ok(response);
    }

    /// <summary>
    /// Confirms an import preview and persists selected chapters with append/overwrite behavior.
    /// </summary>
    [HttpPost("import/{bookId:guid}/confirm")]
    public async Task<ActionResult<ImportConfirmationResultDto>> Confirm(Guid bookId, [FromBody] ImportConfirmationRequest request, CancellationToken ct)
    {
        if (request == null)
        {
            return BadRequest("Request body is required");
        }

        if (string.IsNullOrWhiteSpace(request.Mode))
        {
            return BadRequest("Mode is required");
        }

        var mode = request.Mode.ToLowerInvariant();
        if (mode != "append" && mode != "overwrite")
        {
            return BadRequest("Mode must be either 'append' or 'overwrite'");
        }

        if (request.Chapters == null || request.Chapters.Count == 0)
        {
            return BadRequest("At least one chapter must be provided");
        }

        var selected = request.Chapters
            .Where(c => c.Include)
            .ToList();

        if (selected.Count == 0)
        {
            return BadRequest("No chapters selected for import");
        }

        var tuples = selected
            .Select(c => (c.Title, c.PartName, c.Order, c.SfdtJson))
            .ToList();

        var created = await _chapterService.ImportFromPreviewAsync(bookId, mode, tuples, ct);

        var summaries = created
            .OrderBy(c => c.Order)
            .Select(c => new ChapterSummaryDto(c.Id, c.Title, c.PartName, c.Order, c.WordCount, c.UpdatedAt))
            .ToList();

        var result = new ImportConfirmationResultDto(
            BookId: bookId,
            ImportedCount: created.Count,
            SkippedCount: request.Chapters.Count - created.Count,
            TotalChapters: request.Chapters.Count,
            Chapters: summaries
        );

        return Ok(result);
    }

    [HttpGet("export/book/{bookId:guid}")]
    public async Task<IActionResult> ExportBook(Guid bookId, CancellationToken ct)
    {
        var chapters = await _chapterService.GetAllByBookAsync(bookId, ct);
        var buffers = new List<byte[]>();
        foreach (var ch in chapters)
            buffers.Add(_sfdtConversion.ConvertSfdtToDocx(ch.ContentSfdt));
        var docx = _bookAssembly.AssembleDocx(buffers);
        return File(docx, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "book.docx");
    }

    [HttpGet("export/chapter/{bookId:guid}/{chapterId:guid}")]
    public async Task<IActionResult> ExportChapter(Guid bookId, Guid chapterId, CancellationToken ct)
    {
        var ch = await _chapterService.GetByIdAsync(bookId, chapterId, ct);
        if (ch == null) return NotFound();
        var docx = _sfdtConversion.ConvertSfdtToDocx(ch.ContentSfdt);
        return File(docx, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", $"{ch.Title}.docx");
    }
}
