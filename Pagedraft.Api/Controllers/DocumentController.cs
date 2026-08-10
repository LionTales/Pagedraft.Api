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
    /// no chapters yet (<c>noChapters</c>) or has chapters with nothing written in any of them
    /// (<c>nothingWritten</c>). Before Wave 3 / w1 the first two both reached the assembler's empty-document
    /// path and answered <c>500</c>, indistinguishable from each other and from a real fault; the third
    /// answered <c>200</c> with an empty file, which was worse than either.
    ///
    /// A <c>200</c> also carries <see cref="BookExportService.SkippedCountHeader"/> and, when it is not zero,
    /// <see cref="BookExportService.SkippedChaptersHeader"/>.
    /// </summary>
    [HttpGet("export/book/{bookId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ExportUnavailableDto), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ExportBook(Guid bookId, CancellationToken ct)
        => ToFileResult(await _bookExport.ExportBookAsync(bookId, ct));

    /// <summary>
    /// Downloads one chapter as a DOCX, named after the chapter. <c>404</c> when the book or the chapter does
    /// not exist, and <c>409</c> <c>nothingWritten</c> when the chapter exists but has nothing written in it -
    /// the same answer the book path gives when none of its chapters do.
    /// </summary>
    [HttpGet("export/chapter/{bookId:guid}/{chapterId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ExportUnavailableDto), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ExportChapter(Guid bookId, Guid chapterId, CancellationToken ct)
        => ToFileResult(await _bookExport.ExportChapterAsync(bookId, chapterId, ct));

    /// <summary>
    /// The one place an export outcome becomes an HTTP answer, so the book path and the chapter path cannot
    /// disagree about what "there is nothing to download" looks like on the wire.
    ///
    /// The 409 body carries a REASON TOKEN, not a sentence: the client is he/en bilingual and owns the copy,
    /// exactly as the tier and readiness payloads do it.
    /// </summary>
    private IActionResult ToFileResult(BookExportResult result)
    {
        switch (result.Outcome)
        {
            case BookExportOutcome.Ok:
                WriteSkippedChapterHeaders(result.SkippedChapters);
                return File(result.Content!, BookExportService.DocxContentType, result.FileName!);
            case BookExportOutcome.NothingToExport:
                return Conflict(new ExportUnavailableDto(ExportUnavailableDto.NoChapters));
            case BookExportOutcome.NothingWritten:
                return Conflict(new ExportUnavailableDto(ExportUnavailableDto.NothingWritten));
            default:
                return NotFound();
        }
    }

    /// <summary>
    /// How many chapter titles the skipped-chapters header will name at most. The author needs to recognize
    /// the gap, not to read the whole list out of a header; past this the count carries the rest.
    /// </summary>
    private const int MaxNamedSkippedChapters = 20;

    /// <summary>
    /// Ceiling on the encoded length of the skipped-chapters header. Response-header limits live in proxies
    /// and are typically a few kilobytes for the WHOLE header block, which this header shares with
    /// <c>Content-Disposition</c> and the rest. A Hebrew title costs about six characters per letter once
    /// percent-encoded, so an unbounded list on a long book could run to tens of kilobytes and get the
    /// response truncated or rejected - losing the filename too.
    /// </summary>
    private const int MaxSkippedChaptersHeaderLength = 3000;

    /// <summary>Cap on a single title inside the header, for the same budget reason.</summary>
    private const int MaxSkippedChapterTitleLength = 60;

    /// <summary>JSON for the header payload: <c>JsonSerializerDefaults.Web</c> is camelCase, like every other body on this API.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions SkippedChapterJson =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>
    /// Tells the caller what the file it is about to receive does NOT contain.
    ///
    /// The count is written on EVERY successful export, zero included, so its absence means "this server or
    /// this proxy did not tell me" rather than "nothing was skipped" - the client must be able to tell those
    /// apart. The list is dropped entry by entry until it fits the budget; the count never shrinks with it,
    /// which is why the count is the authoritative figure and the list is a courtesy.
    /// </summary>
    private void WriteSkippedChapterHeaders(IReadOnlyList<ExportSkippedChapter> skipped)
    {
        Response.Headers[BookExportService.SkippedCountHeader] =
            skipped.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (skipped.Count == 0) return;

        for (var take = Math.Min(skipped.Count, MaxNamedSkippedChapters); take > 0; take--)
        {
            var named = skipped
                .Take(take)
                .Select(s => new ExportSkippedChapter(s.Order, Truncate(s.Title, MaxSkippedChapterTitleLength)))
                .ToList();
            var encoded = Uri.EscapeDataString(System.Text.Json.JsonSerializer.Serialize(named, SkippedChapterJson));
            if (encoded.Length > MaxSkippedChaptersHeaderLength) continue;

            Response.Headers[BookExportService.SkippedChaptersHeader] = encoded;
            return;
        }
    }

    /// <summary>Shortens a title for the header without ever cutting a surrogate pair in half.</summary>
    private static string Truncate(string value, int max)
    {
        if (value.Length <= max) return value;
        var end = max;
        if (char.IsHighSurrogate(value[end - 1])) end--;
        return value[..end];
    }
}
