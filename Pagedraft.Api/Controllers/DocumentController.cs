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
    private readonly BookExportService _bookExport;

    public DocumentController(
        DocxParserService docxParser,
        SfdtConversionService sfdtConversion,
        ChapterService chapterService,
        BookAssemblyService bookAssembly,
        BookExportService bookExport)
    {
        _docxParser = docxParser;
        _sfdtConversion = sfdtConversion;
        _chapterService = chapterService;
        _bookAssembly = bookAssembly;
        _bookExport = bookExport;
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

    /// <summary>
    /// Downloads the whole book as one DOCX, chapters in order, named after the book.
    ///
    /// <c>404</c> when the book does not exist and <c>409</c> <see cref="ExportUnavailableDto"/> when it has
    /// no chapters yet. Before Wave 3 / w1 both reached the assembler's empty-document path and answered
    /// <c>500</c>, indistinguishable from each other and from a real fault.
    /// </summary>
    [HttpGet("export/book/{bookId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ExportUnavailableDto), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ExportBook(Guid bookId, CancellationToken ct)
        => ToFileResult(await _bookExport.ExportBookAsync(bookId, ct));

    /// <summary>
    /// Downloads one chapter as a DOCX, named after the chapter. <c>404</c> when the book or the chapter does
    /// not exist.
    /// </summary>
    [HttpGet("export/chapter/{bookId:guid}/{chapterId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportChapter(Guid bookId, Guid chapterId, CancellationToken ct)
        => ToFileResult(await _bookExport.ExportChapterAsync(bookId, chapterId, ct));

    /// <summary>
    /// The one place an export outcome becomes an HTTP answer, so the book path and the chapter path cannot
    /// disagree about what "there is nothing to download" looks like on the wire.
    ///
    /// The 409 body carries a REASON TOKEN, not a sentence: the client is he/en bilingual and owns the copy,
    /// exactly as the tier and readiness payloads do it.
    /// </summary>
    private IActionResult ToFileResult(BookExportResult result) => result.Outcome switch
    {
        BookExportOutcome.Ok => File(result.Content!, BookExportService.DocxContentType, result.FileName!),
        BookExportOutcome.NothingToExport => Conflict(new ExportUnavailableDto(ExportUnavailableDto.NoChapters)),
        _ => NotFound()
    };
}
