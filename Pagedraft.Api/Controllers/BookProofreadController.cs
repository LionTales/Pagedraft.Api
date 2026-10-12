using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;

namespace Pagedraft.Api.Controllers;

/// <summary>
/// The whole-book proofread run, under <c>api/books/{bookId}/proofread</c> beside the other book-scope builds
/// (summary, review, style baseline). Its own controller rather than more lines in the ~1500-line
/// <see cref="BooksController"/>; the routes are what the client contract fixes, not the class.
///
/// | call     | route                                         | answer                               |
/// |----------|-----------------------------------------------|--------------------------------------|
/// | start    | POST .../proofread?language=&amp;force=&amp;onlyFailed= | <see cref="StartBookProofreadResponse"/> |
/// | progress | GET  .../proofread/progress/{jobId}           | <see cref="AnalysisProgressDto"/> (review shape) |
/// | status   | GET  .../proofread/status?language=           | <see cref="BookProofreadStatusDto"/> |
/// | cancel   | POST .../proofread/cancel?jobId={jobId}       | 200, empty body accepted             |
///
/// SECURITY DEBT, same posture as <c>GET active-analysis-jobs</c>: no ownership check on {bookId} under the
/// current single-user deployment.
/// </summary>
[ApiController]
[Route("api/books/{bookId:guid}/proofread")]
public class BookProofreadController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IBookProofreadService _bookProofread;
    private readonly AnalysisProgressTracker _progress;
    private readonly IHostApplicationLifetime _appLifetime;

    public BookProofreadController(
        AppDbContext db,
        IBookProofreadService bookProofread,
        AnalysisProgressTracker progress,
        IHostApplicationLifetime appLifetime)
    {
        _db = db;
        _bookProofread = bookProofread;
        _progress = progress;
        _appLifetime = appLifetime;
    }

    /// <summary>
    /// Start a whole-book proofread. Chapters whose Active proofread result is fresh are skipped unless
    /// <paramref name="force"/>; Started=false (no job) when nothing needs running; an in-flight run for the same
    /// (book, language) comes back as Started=true with its own job id. <paramref name="onlyFailed"/> (c20) plans
    /// exactly the status's FailedChapters instead (force semantics for them, other chapters untouched); it wins
    /// over <paramref name="force"/>.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<StartBookProofreadResponse>> Start(
        Guid bookId,
        [FromQuery] string? language,
        [FromQuery] bool force,
        [FromQuery] bool onlyFailed,
        CancellationToken ct)
    {
        var lang = await ResolveLanguageAsync(bookId, language, ct);
        if (lang == null) return NotFound();

        if (_appLifetime.ApplicationStopping.IsCancellationRequested)
            return StatusCode(503, new { error = "Server is shutting down; cannot start a new proofread run." });

        return Ok(onlyFailed
            ? await _bookProofread.StartFailedAsync(bookId, lang, ct)
            : await _bookProofread.StartAsync(bookId, lang, force, ct));
    }

    /// <summary>
    /// Poll the run's PARENT job. Same <see cref="AnalysisProgressDto"/> shape as the review build; chunks are
    /// planned chapters, while EstimatedCompletionPercent is word-weighted and EstimatedRemainingSeconds carries
    /// the server's ETA (both published by the run itself, never computed here). Serves ONLY Book-scope Proofread
    /// jobs, so no other progress route's ids cross over.
    /// </summary>
    [HttpGet("progress/{jobId:guid}")]
    public ActionResult<AnalysisProgressDto> GetProgress(Guid bookId, Guid jobId)
    {
        if (!_progress.TryGet(jobId, out var snapshot) || snapshot == null)
            return NotFound();
        if (snapshot.BookId != bookId)
            return NotFound();
        if (snapshot.Scope != AnalysisScope.Book || snapshot.AnalysisType != AnalysisType.Proofread)
            return NotFound();

        return Ok(new AnalysisProgressDto(
            snapshot.JobId,
            snapshot.AnalysisType.ToString(),
            snapshot.Scope.ToString(),
            snapshot.BookId,
            snapshot.ChapterId,
            snapshot.SceneId,
            snapshot.Status.ToString(),
            snapshot.CurrentChunkIndex,
            snapshot.TotalChunks,
            snapshot.CompletedChunks,
            snapshot.Message,
            snapshot.EstimatedCompletionPercent,
            EstimatedRemainingSeconds: snapshot.EstimatedRemainingSeconds));
    }

    /// <summary>Coverage, staleness and the in-flight run id for a language (book language when omitted).</summary>
    [HttpGet("status")]
    public async Task<ActionResult<BookProofreadStatusDto>> GetStatus(
        Guid bookId,
        [FromQuery] string? language,
        CancellationToken ct)
    {
        var lang = await ResolveLanguageAsync(bookId, language, ct);
        if (lang == null) return NotFound();
        return Ok(await _bookProofread.GetStatusAsync(bookId, lang, ct));
    }

    /// <summary>
    /// Stop the run before its next chapter; finished chapters keep their results. Idempotent: a job that has
    /// already reached a terminal status answers 200 with canceled=false. 404 when the job is unknown.
    /// </summary>
    [HttpPost("cancel")]
    public IActionResult Cancel(Guid bookId, [FromQuery] Guid jobId)
    {
        if (_bookProofread.Cancel(bookId, jobId))
            return Ok(new { canceled = true });

        if (_progress.TryGet(jobId, out var snapshot) && snapshot != null
            && snapshot.BookId == bookId && snapshot.Scope == AnalysisScope.Book
            && snapshot.AnalysisType == AnalysisType.Proofread)
            return Ok(new { canceled = false, status = snapshot.Status.ToString() });

        return NotFound();
    }

    /// <summary>The request language, else the book's, normalized like every book-scope build; null when no book.</summary>
    private async Task<string?> ResolveLanguageAsync(Guid bookId, string? requestLanguage, CancellationToken ct)
    {
        var bookLanguage = await _db.Books.AsNoTracking()
            .Where(b => b.Id == bookId)
            .Select(b => new { b.Language })
            .FirstOrDefaultAsync(ct);
        if (bookLanguage == null) return null;
        return BaselineLanguageResolver.Normalize(
            string.IsNullOrWhiteSpace(requestLanguage) ? bookLanguage.Language : requestLanguage);
    }
}
