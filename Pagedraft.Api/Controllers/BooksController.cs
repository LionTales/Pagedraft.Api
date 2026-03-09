using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Analysis;

namespace Pagedraft.Api.Controllers;

[ApiController]
[Route("api/books")]
public class BooksController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly BookIntelligenceService _bookIntelligence;

    public BooksController(AppDbContext db, BookIntelligenceService bookIntelligence)
    {
        _db = db;
        _bookIntelligence = bookIntelligence;
    }

    [HttpPost]
    public async Task<ActionResult<BookDto>> Create([FromBody] CreateBookRequest req, CancellationToken ct)
    {
        var book = new Book
        {
            Title = req.Title,
            Author = req.Author,
            Language = req.Language ?? "he"
        };
        _db.Books.Add(book);
        await _db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(GetById), new { bookId = book.Id }, ToDto(book));
    }

    [HttpGet]
    public async Task<ActionResult<List<BookDto>>> GetAll(CancellationToken ct)
    {
        // SQLite does not support ordering by DateTimeOffset on the server,
        // so we materialize first and then order in memory.
        var list = await _db.Books.AsNoTracking().ToListAsync(ct);
        var ordered = list.OrderBy(b => b.UpdatedAt).ToList();
        return Ok(ordered.Select(ToDto).ToList());
    }

    [HttpGet("{bookId:guid}")]
    public async Task<ActionResult<BookDetailDto>> GetById(Guid bookId, CancellationToken ct)
    {
        var book = await _db.Books.Include(b => b.Chapters.OrderBy(c => c.Order)).FirstOrDefaultAsync(b => b.Id == bookId, ct);
        if (book == null) return NotFound();
        var chapters = book.Chapters.Select(c => new ChapterSummaryDto(c.Id, c.Title, c.PartName, c.Order, c.WordCount, c.UpdatedAt)).ToList();
        return Ok(new BookDetailDto(book.Id, book.Title, book.Author, book.Language, book.CreatedAt, book.UpdatedAt, chapters));
    }

    [HttpGet("{bookId:guid}/profile")]
    public async Task<ActionResult<BookProfileDto>> GetProfile(Guid bookId, CancellationToken ct)
    {
        if (await _db.Books.FindAsync(new object[] { bookId }, ct) == null) return NotFound();
        var profile = await _db.Set<BookProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.BookId == bookId, ct);
        if (profile == null) return NotFound();
        return Ok(ToProfileDto(profile));
    }

    [HttpPost("{bookId:guid}/summarize")]
    public async Task<ActionResult> Summarize(Guid bookId, [FromBody] SummarizeBookRequest req, CancellationToken ct)
    {
        if (await _db.Books.FindAsync(new object[] { bookId }, ct) == null) return NotFound();
        var language = req.Language ?? "he";
        await _bookIntelligence.SummarizeChaptersAsync(bookId, language, ct);
        return NoContent();
    }

    [HttpPost("{bookId:guid}/profile/refresh")]
    public async Task<ActionResult<BookProfileDto>> RefreshProfile(Guid bookId, [FromBody] RefreshProfileRequest req, CancellationToken ct)
    {
        if (await _db.Books.FindAsync(new object[] { bookId }, ct) == null) return NotFound();
        var language = req.Language ?? "he";
        var profile = await _bookIntelligence.RefreshProfileAsync(bookId, language, ct);
        return Ok(ToProfileDto(profile));
    }

    [HttpPost("{bookId:guid}/ask")]
    public async Task<ActionResult<AnalysisResultDto>> Ask(Guid bookId, [FromBody] AskBookRequest req, CancellationToken ct)
    {
        if (await _db.Books.FindAsync(new object[] { bookId }, ct) == null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Question)) return BadRequest("Question is required.");
        var language = req.Language ?? "he";
        var result = await _bookIntelligence.AskAsync(bookId, req.Question.Trim(), language, ct);
        return Ok(AnalysisController.ToDto(result));
    }

    [HttpPut("{bookId:guid}")]
    public async Task<ActionResult<BookDto>> Update(Guid bookId, [FromBody] CreateBookRequest req, CancellationToken ct)
    {
        var book = await _db.Books.FindAsync(new object[] { bookId }, ct);
        if (book == null) return NotFound();
        book.Title = req.Title;
        book.Author = req.Author;
        book.Language = req.Language ?? book.Language;
        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(book));
    }

    [HttpDelete("{bookId:guid}")]
    public async Task<ActionResult> Delete(Guid bookId, CancellationToken ct)
    {
        var book = await _db.Books.FindAsync(new object[] { bookId }, ct);
        if (book == null) return NotFound();

        // Explicitly remove dependent ChunkSummaries to satisfy the Restrict FK on BookId.
        var chunkSummaries = await _db.ChunkSummaries.Where(cs => cs.BookId == bookId).ToListAsync(ct);
        if (chunkSummaries.Count > 0)
            _db.ChunkSummaries.RemoveRange(chunkSummaries);

        // Clean up document history snapshots for this book to avoid orphaned versions.
        var documentVersions = await _db.DocumentVersions.Where(dv => dv.BookId == bookId).ToListAsync(ct);
        if (documentVersions.Count > 0)
            _db.DocumentVersions.RemoveRange(documentVersions);

        _db.Books.Remove(book);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static BookDto ToDto(Book b) => new(b.Id, b.Title, b.Author, b.Language, b.CreatedAt, b.UpdatedAt);

    private static BookProfileDto ToProfileDto(BookProfile p) => new(
        p.Id,
        p.BookId,
        p.Genre,
        p.SubGenre,
        p.Synopsis,
        p.TargetAudience,
        p.LiteratureLevel,
        p.LanguageRegister,
        p.CharactersJson,
        p.StoryStructureJson,
        p.Language,
        p.CreatedAt,
        p.UpdatedAt);

}
