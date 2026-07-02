using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;

namespace Pagedraft.Api.Controllers;

[ApiController]
[Route("api/books")]
public class BooksController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly BookIntelligenceService _bookIntelligence;
    private readonly StyleBaselineService _styleBaseline;
    private readonly BookSummaryService _bookSummary;
    private readonly BookReviewService _bookReview;
    private readonly ChapterBriefService _chapterBrief;
    private readonly AnalysisProgressTracker _progress;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly ILogger<BooksController> _logger;

    public BooksController(
        AppDbContext db,
        BookIntelligenceService bookIntelligence,
        StyleBaselineService styleBaseline,
        BookSummaryService bookSummary,
        BookReviewService bookReview,
        ChapterBriefService chapterBrief,
        AnalysisProgressTracker progress,
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime appLifetime,
        ILogger<BooksController> logger)
    {
        _db = db;
        _bookIntelligence = bookIntelligence;
        _styleBaseline = styleBaseline;
        _bookSummary = bookSummary;
        _bookReview = bookReview;
        _chapterBrief = chapterBrief;
        _progress = progress;
        _scopeFactory = scopeFactory;
        _appLifetime = appLifetime;
        _logger = logger;
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
        var orderedBooks = await _db.Books
            .AsNoTracking()
            .OrderBy(b => b.UpdatedAt)
            .ToListAsync(ct);

        return Ok(orderedBooks.Select(ToDto).ToList());
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

    /// <summary>
    /// GET coverage + freshness of the cached book-wide style baseline for a language.
    /// Mirrors the analysis status-poll contract so the FE can decide whether a (re)build is needed.
    /// Status DTO (camelCase JSON): totalChapters, builtChapters, staleCount, hasBaseline, ready,
    /// lastUpdatedAt, builtWithModel, activeModel, builtWithDifferentModel, activeBuildJobId,
    /// chaptersToBuild, estimatedSeconds, estimatedUsd.
    /// </summary>
    [HttpGet("{bookId:guid}/style-baseline")]
    public async Task<ActionResult<BookStyleBaselineStatusDto>> GetStyleBaselineStatus(
        Guid bookId,
        [FromQuery] string? language,
        CancellationToken ct)
    {
        if (await _db.Books.FindAsync(new object[] { bookId }, ct) == null) return NotFound();

        var lang = await ResolveBaselineLanguageAsync(bookId, language, ct);
        var status = await _styleBaseline.GetStatusAsync(bookId, lang, ct);
        return Ok(ToStatusDto(status));
    }

    /// <summary>
    /// Start a book-wide style-baseline build. Mirrors AnalysisController.StartAnalysisJob's async-job
    /// background-execution + AnalysisProgressTracker pattern so the FE reuses analysis-progress.service.
    /// Returns a jobId pollable via GET style-baseline/progress/{jobId}. IDEMPOTENT: when everything is
    /// already fresh the build runs synchronously as a no-op and returns ready with no jobId.
    /// </summary>
    [HttpPost("{bookId:guid}/style-baseline/build")]
    public async Task<ActionResult<StartStyleBaselineBuildResponse>> BuildStyleBaseline(
        Guid bookId,
        [FromBody] BuildStyleBaselineRequest? req,
        CancellationToken ct)
    {
        if (await _db.Books.FindAsync(new object[] { bookId }, ct) == null) return NotFound();

        var lang = await ResolveBaselineLanguageAsync(bookId, req?.Language, ct);

        // Idempotent fast path: nothing stale/missing, a usable cached average already exists, AND that
        // average was built under the ACTIVE model → no-op. When the cached baseline was built under a
        // different model (BuiltWithDifferentModel), it is NOT up to date even if every chapter profile
        // is fresh, so we fall through and start a build to recompute + restamp it under the active model.
        var status = await _styleBaseline.GetStatusAsync(bookId, lang, ct);
        if (status.StaleCount == 0 && status.HasBaseline && !status.BuiltWithDifferentModel)
        {
            return Ok(new StartStyleBaselineBuildResponse(
                JobId: null,
                Language: lang,
                NoOp: true,
                Ready: true,
                BuiltChapters: status.BuiltChapters,
                TotalChapters: status.TotalChapters,
                StaleCount: status.StaleCount));
        }

        // Dedup guard: a build for this (bookId, language) is already running. GetStatusAsync only
        // surfaces ActiveBuildJobId for a job the progress tracker still reports as live (it self-heals a
        // lingering/terminal registry entry to null), so handing back the existing jobId is safe — the FE
        // reattaches to that live job instead of us minting a SECOND build that re-issues the same paid
        // LinguisticAnalysis LLM calls. Checked AFTER the no-op fast path, BEFORE starting a new build.
        if (status.ActiveBuildJobId is Guid activeJobId)
        {
            return Ok(new StartStyleBaselineBuildResponse(
                JobId: activeJobId,
                Language: lang,
                NoOp: false,
                Ready: false,
                BuiltChapters: status.BuiltChapters,
                TotalChapters: status.TotalChapters,
                StaleCount: status.StaleCount));
        }

        var shutdownToken = _appLifetime.ApplicationStopping;
        if (shutdownToken.IsCancellationRequested)
            return StatusCode(503, new { error = "Server is shutting down; cannot start new build." });

        var jobId = Guid.NewGuid();
        // Pre-register the snapshot BEFORE returning jobId so an immediate FE poll does not 404.
        // BuildBookStyleBaselineAsync will call StartJob again to refresh the message and set total
        // chunks once the chapter count is known — that second call is intentional (see below).
        _progress.StartJob(jobId, AnalysisScope.Book, Services.Ai.Contracts.AnalysisType.LinguisticAnalysis,
            bookId, null, null, "Queued style baseline build…");

        // Fire-and-forget background task on a fresh DI scope (mirrors AnalysisController.StartAnalysisJob).
        _ = Task.Run(async () =>
        {
            try
            {
                using var serviceScope = _scopeFactory.CreateScope();
                var services = serviceScope.ServiceProvider;
                var baseline = services.GetRequiredService<StyleBaselineService>();
                var progress = services.GetRequiredService<AnalysisProgressTracker>();
                var logger = services.GetRequiredService<ILogger<BooksController>>();
                try
                {
                    await baseline.BuildBookStyleBaselineAsync(bookId, lang, jobId, shutdownToken);
                }
                catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
                {
                    progress.SetStatus(jobId, AnalysisProgressStatus.Canceled, "Style baseline build canceled due to application shutdown.");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Style baseline build job {JobId} failed for book {BookId}", jobId, bookId);
                    progress.SetStatus(jobId, AnalysisProgressStatus.Failed, ex.Message);
                }
            }
            catch (Exception ex)
            {
                try { _progress.SetStatus(jobId, AnalysisProgressStatus.Failed, "Style baseline build failed to start."); } catch { }
                try { _logger.LogError(ex, "Failed to execute style baseline build job {JobId}", jobId); } catch { }
            }
        }, CancellationToken.None);

        return Ok(new StartStyleBaselineBuildResponse(
            JobId: jobId,
            Language: lang,
            NoOp: false,
            Ready: false,
            BuiltChapters: status.BuiltChapters,
            TotalChapters: status.TotalChapters,
            StaleCount: status.StaleCount));
    }

    /// <summary>
    /// Poll progress of a book-wide style-baseline build job. Book-scoped sibling of
    /// AnalysisController.GetAnalysisProgress (which requires a chapterId path segment); returns the SAME
    /// AnalysisProgressDto shape so the FE reuses analysis-progress.service.
    /// </summary>
    [HttpGet("{bookId:guid}/style-baseline/progress/{jobId:guid}")]
    public ActionResult<AnalysisProgressDto> GetStyleBaselineProgress(Guid bookId, Guid jobId)
    {
        if (!_progress.TryGet(jobId, out var snapshot) || snapshot == null)
            return NotFound();

        if (snapshot.BookId.HasValue && snapshot.BookId != bookId)
            return NotFound();

        // Guard: this endpoint serves ONLY book-wide style-baseline jobs.
        // A chapter Proofread (or any other) job that happens to share the same bookId
        // must not be leaked through the book-scoped progress route.
        if (snapshot.Scope != AnalysisScope.Book || snapshot.AnalysisType != Services.Ai.Contracts.AnalysisType.LinguisticAnalysis)
            return NotFound();

        var dto = new AnalysisProgressDto(
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
            snapshot.EstimatedCompletionPercent);

        return Ok(dto);
    }

    /// <summary>
    /// GET coverage + freshness of the cached L2 book summary (BookBrief rollup) for a language. Mirrors
    /// GET style-baseline so the FE reuses the same status/progress UI. Status DTO (camelCase JSON):
    /// totalChapters, builtChapters, staleCount, hasSummary, ready, lastUpdatedAt, builtWithModel,
    /// activeModel, builtWithDifferentModel, activeBuildJobId, chaptersToBuild, estimatedSeconds, estimatedUsd.
    /// </summary>
    [HttpGet("{bookId:guid}/summary")]
    public async Task<ActionResult<BookSummaryStatusDto>> GetBookSummaryStatus(
        Guid bookId,
        [FromQuery] string? language,
        CancellationToken ct)
    {
        if (await _db.Books.FindAsync(new object[] { bookId }, ct) == null) return NotFound();

        var lang = await ResolveBaselineLanguageAsync(bookId, language, ct);
        var status = await _bookSummary.GetStatusAsync(bookId, lang, ct);
        return Ok(ToSummaryStatusDto(status));
    }

    /// <summary>
    /// Start a book-wide L2 summary build (L0 → L1 → L2 rollup). MIRRORS BuildStyleBaseline's async-job
    /// background-execution + AnalysisProgressTracker pattern so the FE reuses analysis-progress.service.
    /// Returns a jobId pollable via GET summary/progress/{jobId}. IDEMPOTENT: when everything is already
    /// fresh the build runs synchronously as a no-op and returns ready with no jobId.
    /// </summary>
    [HttpPost("{bookId:guid}/summary/build")]
    public async Task<ActionResult<StartBookSummaryBuildResponse>> BuildBookSummary(
        Guid bookId,
        [FromBody] BuildBookSummaryRequest? req,
        CancellationToken ct)
    {
        if (await _db.Books.FindAsync(new object[] { bookId }, ct) == null) return NotFound();

        var lang = await ResolveBaselineLanguageAsync(bookId, req?.Language, ct);

        // Idempotent fast path: nothing stale/missing, a usable cached rollup exists, it was built under the
        // ACTIVE model, AND that rollup still covers every built chapter → no-op. Use status.IsReady so this
        // endpoint applies the SAME gate as GET summary and BuildBookSummaryAsync (single source of truth):
        // re-deriving a subset here let a partial rollup (a chapter that gained a brief outside a full build —
        // !SummaryCoversBuiltChapters) return NoOp/Ready and never refresh the stale BookBriefJson, even though
        // GET summary already reported not-ready. A cross-model rollup likewise falls through to rebuild.
        var status = await _bookSummary.GetStatusAsync(bookId, lang, ct);
        if (status.IsReady)
        {
            return Ok(new StartBookSummaryBuildResponse(
                JobId: null,
                Language: lang,
                NoOp: true,
                Ready: true,
                BuiltChapters: status.BuiltChapters,
                TotalChapters: status.TotalChapters,
                StaleCount: status.StaleCount));
        }

        // Dedup guard: a build for this (bookId, language) is already running. GetStatusAsync only surfaces
        // ActiveBuildJobId for a job the progress tracker still reports as live, so handing back the existing
        // jobId is safe — the FE reattaches instead of minting a SECOND build. Checked AFTER the no-op fast
        // path, BEFORE starting a new build.
        if (status.ActiveBuildJobId is Guid activeJobId)
        {
            return Ok(new StartBookSummaryBuildResponse(
                JobId: activeJobId,
                Language: lang,
                NoOp: false,
                Ready: false,
                BuiltChapters: status.BuiltChapters,
                TotalChapters: status.TotalChapters,
                StaleCount: status.StaleCount));
        }

        var shutdownToken = _appLifetime.ApplicationStopping;
        if (shutdownToken.IsCancellationRequested)
            return StatusCode(503, new { error = "Server is shutting down; cannot start new build." });

        var jobId = Guid.NewGuid();
        // Pre-register the snapshot BEFORE returning jobId so an immediate FE poll does not 404.
        // BuildBookSummaryAsync calls StartJob again to refresh the message + set total chunks.
        _progress.StartJob(jobId, AnalysisScope.Book, Services.Ai.Contracts.AnalysisType.Summarization,
            bookId, null, null, "Queued book summary build…");

        // Fire-and-forget background task on a fresh DI scope (mirrors BuildStyleBaseline).
        _ = Task.Run(async () =>
        {
            try
            {
                using var serviceScope = _scopeFactory.CreateScope();
                var services = serviceScope.ServiceProvider;
                var summary = services.GetRequiredService<BookSummaryService>();
                var progress = services.GetRequiredService<AnalysisProgressTracker>();
                var logger = services.GetRequiredService<ILogger<BooksController>>();
                try
                {
                    await summary.BuildBookSummaryAsync(bookId, lang, jobId, shutdownToken);
                }
                catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
                {
                    progress.SetStatus(jobId, AnalysisProgressStatus.Canceled, "Book summary build canceled due to application shutdown.");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Book summary build job {JobId} failed for book {BookId}", jobId, bookId);
                    progress.SetStatus(jobId, AnalysisProgressStatus.Failed, ex.Message);
                }
            }
            catch (Exception ex)
            {
                try { _progress.SetStatus(jobId, AnalysisProgressStatus.Failed, "Book summary build failed to start."); } catch { }
                try { _logger.LogError(ex, "Failed to execute book summary build job {JobId}", jobId); } catch { }
            }
        }, CancellationToken.None);

        return Ok(new StartBookSummaryBuildResponse(
            JobId: jobId,
            Language: lang,
            NoOp: false,
            Ready: false,
            BuiltChapters: status.BuiltChapters,
            TotalChapters: status.TotalChapters,
            StaleCount: status.StaleCount));
    }

    /// <summary>
    /// Poll progress of a book-wide summary build job. Book-scoped sibling of GetStyleBaselineProgress;
    /// returns the SAME AnalysisProgressDto shape so the FE reuses analysis-progress.service. Guarded to
    /// serve ONLY book-wide Summarization jobs (distinct AnalysisType from the LinguisticAnalysis style
    /// baseline, so the two book-scoped progress routes never cross-serve).
    /// </summary>
    [HttpGet("{bookId:guid}/summary/progress/{jobId:guid}")]
    public ActionResult<AnalysisProgressDto> GetBookSummaryProgress(Guid bookId, Guid jobId)
    {
        if (!_progress.TryGet(jobId, out var snapshot) || snapshot == null)
            return NotFound();

        if (snapshot.BookId.HasValue && snapshot.BookId != bookId)
            return NotFound();

        if (snapshot.Scope != AnalysisScope.Book || snapshot.AnalysisType != Services.Ai.Contracts.AnalysisType.Summarization)
            return NotFound();

        var dto = new AnalysisProgressDto(
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
            snapshot.EstimatedCompletionPercent);

        return Ok(dto);
    }

    // ─── Whole-book review (wb2-c03) ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Start a consented whole-book developmental review build. MIRRORS BuildBookSummary's async-job
    /// background-execution + AnalysisProgressTracker pattern so the FE reuses analysis-progress.service.
    /// Returns a jobId pollable via GET review/progress/{jobId}.
    ///
    /// IDEMPOTENT: when the review is already fresh (built under the active model, not stale vs briefs) the
    /// build runs synchronously as a no-op and returns ready with no jobId.
    ///
    /// BRIEFS-MISSING GUARD: the review reads the structured chapter briefs (the book summary). When the book
    /// has no usable briefs yet, NO model calls are spent and the response carries BriefsMissing=true plus a
    /// guidance message ("build the book summary first") so the FE can route the user there. Surfaced as a
    /// 200 (not an error) carrying the structured flag the FE localizes.
    /// </summary>
    [HttpPost("{bookId:guid}/review")]
    public async Task<ActionResult<StartBookReviewBuildResponse>> BuildBookReview(
        Guid bookId,
        [FromBody] BuildBookReviewRequest? req,
        CancellationToken ct)
    {
        if (await _db.Books.FindAsync(new object[] { bookId }, ct) == null) return NotFound();

        var lang = await ResolveBaselineLanguageAsync(bookId, req?.Language, ct);

        var status = await _bookReview.GetStatusAsync(bookId, lang, ct);

        // Briefs-missing guard: the book has no usable structured briefs to review. Spend NO model calls;
        // surface the structured BriefsMissing flag + guidance so the FE prompts to build the summary first.
        if (!status.HasBriefs)
        {
            return Ok(new StartBookReviewBuildResponse(
                JobId: null,
                Language: lang,
                NoOp: false,
                Ready: false,
                BriefsMissing: true,
                FindingCount: status.FindingCount,
                Message: "Build the book summary first; the whole-book review reads the chapter briefs."));
        }

        // Idempotent fast path: a usable review exists, built under the ACTIVE model, not stale vs briefs →
        // no-op. Uses status.IsReady so this endpoint applies the SAME gate as GET review/status and
        // BuildBookReviewAsync (single source of truth).
        if (status.IsReady)
        {
            return Ok(new StartBookReviewBuildResponse(
                JobId: null,
                Language: lang,
                NoOp: true,
                Ready: true,
                BriefsMissing: false,
                FindingCount: status.FindingCount,
                Message: "Whole-book review already up to date."));
        }

        // Dedup guard: a review build for this (bookId, language) is already running. GetStatusAsync only
        // surfaces ActiveBuildJobId for a job the progress tracker still reports as live (self-healing a
        // lingering/terminal entry to null), so handing back the existing jobId is safe — the FE reattaches
        // instead of minting a SECOND build that re-issues the same paid per-dimension LLM calls. Checked
        // AFTER the no-op fast path, BEFORE starting a new build.
        if (status.ActiveBuildJobId is Guid activeJobId)
        {
            return Ok(new StartBookReviewBuildResponse(
                JobId: activeJobId,
                Language: lang,
                NoOp: false,
                Ready: false,
                BriefsMissing: false,
                FindingCount: status.FindingCount,
                Message: "A whole-book review build is already in progress for this book."));
        }

        var shutdownToken = _appLifetime.ApplicationStopping;
        if (shutdownToken.IsCancellationRequested)
            return StatusCode(503, new { error = "Server is shutting down; cannot start new build." });

        var jobId = Guid.NewGuid();
        // Pre-register the snapshot BEFORE returning jobId so an immediate FE poll does not 404.
        // BuildBookReviewAsync calls StartJob again to refresh the message + set total chunks.
        _progress.StartJob(jobId, AnalysisScope.Book, Services.Ai.Contracts.AnalysisType.BookReview,
            bookId, null, null, "Queued whole-book review build…");

        // Fire-and-forget background task on a fresh DI scope (mirrors BuildBookSummary).
        _ = Task.Run(async () =>
        {
            try
            {
                using var serviceScope = _scopeFactory.CreateScope();
                var services = serviceScope.ServiceProvider;
                var review = services.GetRequiredService<BookReviewService>();
                var progress = services.GetRequiredService<AnalysisProgressTracker>();
                var logger = services.GetRequiredService<ILogger<BooksController>>();
                try
                {
                    await review.BuildBookReviewAsync(bookId, lang, jobId, shutdownToken);
                }
                catch (OperationCanceledException) when (shutdownToken.IsCancellationRequested)
                {
                    progress.SetStatus(jobId, AnalysisProgressStatus.Canceled, "Whole-book review build canceled due to application shutdown.");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Whole-book review build job {JobId} failed for book {BookId}", jobId, bookId);
                    progress.SetStatus(jobId, AnalysisProgressStatus.Failed, ex.Message);
                }
            }
            catch (Exception ex)
            {
                try { _progress.SetStatus(jobId, AnalysisProgressStatus.Failed, "Whole-book review build failed to start."); } catch { }
                try { _logger.LogError(ex, "Failed to execute whole-book review build job {JobId}", jobId); } catch { }
            }
        }, CancellationToken.None);

        return Ok(new StartBookReviewBuildResponse(
            JobId: jobId,
            Language: lang,
            NoOp: false,
            Ready: false,
            BriefsMissing: false,
            FindingCount: status.FindingCount,
            Message: "Whole-book review build started."));
    }

    /// <summary>
    /// Poll progress of a whole-book review build job. Book-scoped sibling of GetBookSummaryProgress; returns
    /// the SAME AnalysisProgressDto shape so the FE reuses analysis-progress.service. Guarded to serve ONLY
    /// book-wide BookReview jobs (distinct AnalysisType from the Summarization summary build and the
    /// LinguisticAnalysis style baseline, so the three book-scoped progress routes never cross-serve).
    /// </summary>
    [HttpGet("{bookId:guid}/review/progress/{jobId:guid}")]
    public ActionResult<AnalysisProgressDto> GetBookReviewProgress(Guid bookId, Guid jobId)
    {
        if (!_progress.TryGet(jobId, out var snapshot) || snapshot == null)
            return NotFound();

        if (snapshot.BookId.HasValue && snapshot.BookId != bookId)
            return NotFound();

        if (snapshot.Scope != AnalysisScope.Book || snapshot.AnalysisType != Services.Ai.Contracts.AnalysisType.BookReview)
            return NotFound();

        var dto = new AnalysisProgressDto(
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
            snapshot.EstimatedCompletionPercent);

        return Ok(dto);
    }

    /// <summary>
    /// GET coverage + freshness of the cached whole-book review for a language. Mirrors GET summary so the FE
    /// reuses the same status/progress UI. Status DTO (camelCase JSON): hasReview, findingCount, lastUpdatedAt,
    /// builtWithModel, activeModel, builtWithDifferentModel, staleVsBriefs, hasBriefs, activeBuildJobId, ready.
    /// </summary>
    [HttpGet("{bookId:guid}/review/status")]
    public async Task<ActionResult<BookReviewStatusDto>> GetBookReviewStatus(
        Guid bookId,
        [FromQuery] string? language,
        CancellationToken ct)
    {
        if (await _db.Books.FindAsync(new object[] { bookId }, ct) == null) return NotFound();

        var lang = await ResolveBaselineLanguageAsync(bookId, language, ct);
        var status = await _bookReview.GetStatusAsync(bookId, lang, ct);
        return Ok(ToReviewStatusDto(status));
    }

    /// <summary>
    /// GET the persisted whole-book findings + per-dimension rollup scores for a language. The findings are
    /// the single source of truth the FE renders; the scores summarise keep/improve/cut per dimension.
    /// </summary>
    [HttpGet("{bookId:guid}/review/findings")]
    public async Task<ActionResult<BookReviewFindingsDto>> GetBookReviewFindings(
        Guid bookId,
        [FromQuery] string? language,
        CancellationToken ct)
    {
        if (await _db.Books.FindAsync(new object[] { bookId }, ct) == null) return NotFound();

        var lang = await ResolveBaselineLanguageAsync(bookId, language, ct);
        var findings = await _bookReview.GetFindingsAsync(bookId, lang, ct);
        return Ok(ToReviewFindingsDto(findings));
    }

    /// <summary>
    /// Update the workflow status of a single whole-book finding. Body { status: acknowledge | dismiss | done
    /// | open }; the imperative verbs map to the BookFinding.Status set (acknowledged | dismissed | done |
    /// open). MIRRORS AnalysisController.UpdateSuggestionOutcome: validate the value (BadRequest on invalid),
    /// set + SaveChanges, return the updated finding. IDEMPOTENT: PATCH-ing the same status twice is a no-op
    /// success (setting a field to its current value + SaveChanges changes nothing).
    /// </summary>
    [HttpPatch("{bookId:guid}/review/findings/{id:guid}/status")]
    public async Task<ActionResult<BookFindingDto>> UpdateFindingStatus(
        Guid bookId,
        Guid id,
        [FromBody] UpdateFindingStatusRequest request,
        CancellationToken ct)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Status))
            return BadRequest("Status is required.");

        if (!TryMapFindingStatus(request.Status, out var status))
            return BadRequest("Invalid status. Must be acknowledge, dismiss, done, or open.");

        var finding = await _db.BookFindings.FirstOrDefaultAsync(f => f.Id == id && f.BookId == bookId, ct);
        if (finding == null) return NotFound();

        finding.Status = status; // idempotent: re-setting the same value + SaveChanges is a no-op success
        await _db.SaveChangesAsync(ct);

        return Ok(ToFindingDto(finding));
    }

    /// <summary>
    /// Maps the FE's imperative status verb to the persisted BookFinding.Status value (case-insensitive).
    /// Accepts both the verb form (acknowledge/dismiss) and the stored adjective form
    /// (acknowledged/dismissed) so the endpoint is tolerant of either; "done" and "open" are identical in
    /// both forms.
    /// </summary>
    private static bool TryMapFindingStatus(string raw, out string status)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "acknowledge":
            case "acknowledged":
                status = "acknowledged";
                return true;
            case "dismiss":
            case "dismissed":
                status = "dismissed";
                return true;
            case "done":
                status = "done";
                return true;
            case "open":
                status = "open";
                return true;
            default:
                status = string.Empty;
                return false;
        }
    }

    private static BookReviewStatusDto ToReviewStatusDto(BookReviewStatus s) => new(
        s.BookId,
        s.Language,
        s.HasReview,
        s.FindingCount,
        s.LastUpdatedAt,
        s.BuiltWithModel,
        s.ActiveModel,
        s.BuiltWithDifferentModel,
        s.StaleVsBriefs,
        s.HasBriefs,
        s.ChaptersReviewed,
        s.ChaptersTotal,
        s.WindowCount,
        s.RanSynthesis,
        s.RanContinuityReduce,
        s.FailedWindows,
        s.ActiveBuildJobId,
        s.IsReady);

    private static BookReviewFindingsDto ToReviewFindingsDto(BookReviewFindings f) => new(
        f.BookId,
        f.Language,
        f.Findings.Select(ToFindingDto).ToList(),
        f.Scores.Select(s => new BookReviewDimensionScoreDto(
            s.Dimension, s.Score, s.KeepCount, s.ImproveCount, s.CutCount)).ToList());

    private static readonly System.Text.Json.JsonSerializerOptions FindingJsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static BookFindingDto ToFindingDto(BookFinding f)
    {
        var anchors = DeserializeAnchors(f.ChapterAnchorsJson);
        var evidence = DeserializeEvidence(f.EvidenceJson);
        return new BookFindingDto(
            f.Id,
            f.Dimension,
            f.Verdict,
            f.Severity,
            f.Rationale,
            evidence,
            anchors,
            f.SuggestedAction,
            f.Status,
            f.BuiltWithModel,
            f.CreatedAt,
            f.UpdatedAt);
    }

    private static IReadOnlyList<FindingChapterAnchorDto> DeserializeAnchors(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<FindingChapterAnchorDto>();
        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<List<FindingChapterAnchor>>(json, FindingJsonOpts);
            return parsed == null
                ? Array.Empty<FindingChapterAnchorDto>()
                : parsed.Select(a => new FindingChapterAnchorDto(a.ChapterId, a.Order, a.Title)).ToList();
        }
        catch (System.Text.Json.JsonException)
        {
            return Array.Empty<FindingChapterAnchorDto>();
        }
    }

    private static IReadOnlyList<FindingEvidenceDto> DeserializeEvidence(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<FindingEvidenceDto>();
        try
        {
            var parsed = System.Text.Json.JsonSerializer.Deserialize<List<FindingEvidence>>(json, FindingJsonOpts);
            return parsed == null
                ? Array.Empty<FindingEvidenceDto>()
                : parsed.Select(e => new FindingEvidenceDto(e.ChapterId, e.ChapterOrder, e.Excerpt)).ToList();
        }
        catch (System.Text.Json.JsonException)
        {
            return Array.Empty<FindingEvidenceDto>();
        }
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

    // ─── Chapter summary view + edit (wb3-c04) ────────────────────────────────────────────────────

    /// <summary>
    /// GET one chapter's cached summary: the flat (user-authoritative, editable) <c>summaryText</c> plus a
    /// structured-present indicator and BOTH freshness stamps + the user-edited flag (dual-surface trap).
    /// Returns a ChapterSummaryViewDto even when no ChunkSummary row exists yet (empty summary, no structured
    /// brief) so the FE can render an editable-but-empty state.
    /// </summary>
    [HttpGet("{bookId:guid}/chapters/{chapterId:guid}/summary")]
    public async Task<ActionResult<ChapterSummaryViewDto>> GetChapterSummary(
        Guid bookId,
        Guid chapterId,
        [FromQuery] string? language,
        CancellationToken ct)
    {
        if (await _db.Books.FindAsync(new object[] { bookId }, ct) == null) return NotFound();
        var chapter = await _db.Chapters.AsNoTracking().FirstOrDefaultAsync(c => c.Id == chapterId && c.BookId == bookId, ct);
        if (chapter == null) return NotFound();

        var lang = await ResolveBaselineLanguageAsync(bookId, language, ct);
        var row = await _db.ChunkSummaries.AsNoTracking()
            .FirstOrDefaultAsync(cs => cs.BookId == bookId && cs.ChapterId == chapterId, ct);

        return Ok(ToChapterSummaryViewDto(bookId, chapterId, lang, row));
    }

    /// <summary>
    /// PUT a chapter's edited flat summary. The flat <c>summaryText</c> is the user's OWN authoritative
    /// understanding of the chapter. Saving sets <see cref="ChunkSummary.SummaryUserEdited"/> = true and
    /// stamps <see cref="ChunkSummary.SummaryUserEditedAt"/> so a later automatic re-summary skips this row
    /// (clobber guard). DUAL-SURFACE: it writes ONLY the flat surface (text + flat stamps + Language) and does
    /// NOT touch the structured surface's StructuredJson / StructuredBuiltAt / BuiltWithModel — so the
    /// structured brief is not orphaned and keeps its own freshness. Creates the row if the chapter has none
    /// yet. After saving, the FE OFFERS the explicit re-derive so the review reflects the edit.
    /// </summary>
    [HttpPut("{bookId:guid}/chapters/{chapterId:guid}/summary")]
    public async Task<ActionResult<ChapterSummaryViewDto>> UpdateChapterSummary(
        Guid bookId,
        Guid chapterId,
        [FromBody] UpdateChapterSummaryRequest req,
        CancellationToken ct)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.SummaryText))
            return BadRequest("summaryText is required.");
        if (await _db.Books.FindAsync(new object[] { bookId }, ct) == null) return NotFound();
        var chapter = await _db.Chapters.AsNoTracking().FirstOrDefaultAsync(c => c.Id == chapterId && c.BookId == bookId, ct);
        if (chapter == null) return NotFound();

        var lang = await ResolveBaselineLanguageAsync(bookId, req.Language, ct);
        var text = req.SummaryText.Trim();
        var now = DateTimeOffset.UtcNow;

        var row = await _db.ChunkSummaries
            .FirstOrDefaultAsync(cs => cs.BookId == bookId && cs.ChapterId == chapterId, ct);
        if (row == null)
        {
            // No row yet (chapter never summarized): create one carrying ONLY the user's flat edit. The
            // structured surface stays empty (a re-derive builds it). CreatedAt is stamped by the SaveChanges
            // override on Add; we also stamp the user-edit freshness explicitly.
            row = new ChunkSummary
            {
                BookId = bookId,
                ChapterId = chapterId,
                Language = lang,
                SummaryText = text,
                SummaryUserEdited = true,
                SummaryUserEditedAt = now
            };
            _db.ChunkSummaries.Add(row);
        }
        else
        {
            // be-c02 (language-flip guard): the row's Language is the SINGLE identity for BOTH surfaces. The
            // incoming flat text IS in `lang` (the user just wrote it), so it is never stale; but if this PUT
            // flips the row's Language, the EXISTING structured brief (StructuredJson, built under the OLD
            // language) would masquerade as the new locale's brief once Language flips. Mirror the load path
            // (LoadOrBuildChapterBriefAsync, which clears the surface that no longer matches the new locale):
            // clear the now-stale structured surface so it cannot leak the wrong language; a re-derive rebuilds
            // it for the new locale when the user asks. On the SAME-language path the dual-surface contract is
            // unchanged — StructuredJson / StructuredBuiltAt / BuiltWithModel are left untouched and not orphaned.
            // NOTE: a flip is normally unreachable (the FE always passes the book language); this only fires if
            // the book language changed after the structured brief was built.
            if (!string.Equals(row.Language, lang, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(row.StructuredJson))
            {
                row.StructuredJson = null;
                row.StructuredBuiltAt = null;
                row.BuiltWithModel = null;
            }

            row.SummaryText = text;
            row.SummaryUserEdited = true;
            row.SummaryUserEditedAt = now;
            row.Language = lang;
        }

        await _db.SaveChangesAsync(ct);

        // Re-read AsNoTracking so the DTO reflects any SaveChanges-override stamps (CreatedAt on Add).
        var saved = await _db.ChunkSummaries.AsNoTracking()
            .FirstAsync(cs => cs.BookId == bookId && cs.ChapterId == chapterId, ct);
        return Ok(ToChapterSummaryViewDto(bookId, chapterId, lang, saved));
    }

    /// <summary>
    /// POST re-derive: the user-triggered action that rebuilds the STRUCTURED brief for one chapter SEEDED
    /// with the user's edited flat summary, so the whole-book review (which reads the structured brief, not
    /// the flat text) reflects the edit. Synchronous (one chapter, one model call) — mirrors how the
    /// per-chapter structured build is exposed elsewhere, and degrades gracefully: if the model cannot
    /// produce a brief the edit is still saved + clobber-guarded and the response carries rederived=false.
    /// Requires a user-edited flat summary to seed from (else 409, since a re-derive with nothing to seed is
    /// just the ordinary AI build).
    /// </summary>
    [HttpPost("{bookId:guid}/chapters/{chapterId:guid}/summary/rederive")]
    public async Task<ActionResult<RederiveChapterSummaryResponse>> RederiveChapterSummary(
        Guid bookId,
        Guid chapterId,
        [FromBody] RederiveChapterSummaryRequest? req,
        CancellationToken ct)
    {
        if (await _db.Books.FindAsync(new object[] { bookId }, ct) == null) return NotFound();
        var chapter = await _db.Chapters.AsNoTracking().FirstOrDefaultAsync(c => c.Id == chapterId && c.BookId == bookId, ct);
        if (chapter == null) return NotFound();

        var lang = await ResolveBaselineLanguageAsync(bookId, req?.Language, ct);
        var row = await _db.ChunkSummaries.AsNoTracking()
            .FirstOrDefaultAsync(cs => cs.BookId == bookId && cs.ChapterId == chapterId, ct);

        if (row == null || !row.SummaryUserEdited || string.IsNullOrWhiteSpace(row.SummaryText))
            // Nothing authoritative to seed from: re-derive is only meaningful after a user edit. 409 rather
            // than silently running the ordinary AI build (which the summary/build endpoints already cover).
            return Conflict(new { error = "No user-edited summary to re-derive from. Edit and save the summary first." });

        var brief = await _chapterBrief.RederiveChapterBriefFromUserSummaryAsync(bookId, chapterId, lang, ct);

        // Re-read so the response reflects the persisted structured surface (or its absence on a graceful miss).
        var after = await _db.ChunkSummaries.AsNoTracking()
            .FirstOrDefaultAsync(cs => cs.BookId == bookId && cs.ChapterId == chapterId, ct);
        var hasStructured = after != null && !string.IsNullOrWhiteSpace(after.StructuredJson);
        var rederived = brief != null;

        return Ok(new RederiveChapterSummaryResponse(
            BookId: bookId,
            ChapterId: chapterId,
            Language: lang,
            Rederived: rederived,
            HasStructuredBrief: hasStructured,
            StructuredBuiltAt: after?.StructuredBuiltAt,
            BuiltWithModel: after?.BuiltWithModel,
            Message: rederived
                ? "Structured brief re-derived from your edited summary; rebuild the whole-book review to reflect it."
                : "Could not re-derive the structured brief from your summary right now; your edit was saved."));
    }

    private static ChapterSummaryViewDto ToChapterSummaryViewDto(Guid bookId, Guid chapterId, string lang, ChunkSummary? row)
    {
        if (row == null)
            return new ChapterSummaryViewDto(
                bookId, chapterId, lang,
                SummaryText: string.Empty,
                HasSummary: false,
                HasStructuredBrief: false,
                SummaryUserEdited: false,
                CreatedAt: null,
                SummaryUserEditedAt: null,
                StructuredBuiltAt: null,
                BuiltWithModel: null,
                StructuredBrief: null);

        // READ-only enrichment (wb3-c04 fallback): expose the PARSED structured-brief facts so the FE can
        // render a human-readable digest when the flat summary is empty. Defensive parse via the single
        // source of truth (StructuredChunkSummaryParser): null/blank/unparseable StructuredJson -> null, so a
        // malformed brief never throws and is simply omitted. HasStructuredBrief stays the cheap presence
        // check (unchanged contract); StructuredBrief is null when the JSON is present but not usable.
        return new ChapterSummaryViewDto(
            row.BookId,
            row.ChapterId,
            row.Language,
            row.SummaryText ?? string.Empty,
            HasSummary: !string.IsNullOrWhiteSpace(row.SummaryText),
            HasStructuredBrief: !string.IsNullOrWhiteSpace(row.StructuredJson),
            row.SummaryUserEdited,
            row.CreatedAt,
            row.SummaryUserEditedAt,
            row.StructuredBuiltAt,
            row.BuiltWithModel,
            StructuredBrief: StructuredChunkSummaryParser.Parse(row.StructuredJson));
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

        // Explicitly remove cached ChapterStyleProfiles to satisfy the Restrict FK on BookId.
        var styleProfiles = await _db.ChapterStyleProfiles.Where(p => p.BookId == bookId).ToListAsync(ct);
        if (styleProfiles.Count > 0)
            _db.ChapterStyleProfiles.RemoveRange(styleProfiles);

        // Explicitly remove cached BookStyleBaselines to satisfy the Restrict FK on BookId.
        var styleBaselines = await _db.BookStyleBaselines.Where(b => b.BookId == bookId).ToListAsync(ct);
        if (styleBaselines.Count > 0)
            _db.BookStyleBaselines.RemoveRange(styleBaselines);

        // Explicitly remove cached BookSummaryBaselines to satisfy the Restrict FK on BookId.
        var summaryBaselines = await _db.BookSummaryBaselines.Where(b => b.BookId == bookId).ToListAsync(ct);
        if (summaryBaselines.Count > 0)
            _db.BookSummaryBaselines.RemoveRange(summaryBaselines);

        // Explicitly remove cached BookFindings to satisfy the Restrict FK on BookId.
        var bookFindings = await _db.BookFindings.Where(f => f.BookId == bookId).ToListAsync(ct);
        if (bookFindings.Count > 0)
            _db.BookFindings.RemoveRange(bookFindings);

        // Explicitly remove persisted BookReviewCoverages (data-c01) to satisfy the Restrict FK on BookId.
        var reviewCoverages = await _db.BookReviewCoverages.Where(c => c.BookId == bookId).ToListAsync(ct);
        if (reviewCoverages.Count > 0)
            _db.BookReviewCoverages.RemoveRange(reviewCoverages);

        // Clean up document history snapshots for this book to avoid orphaned versions.
        var documentVersions = await _db.DocumentVersions.Where(dv => dv.BookId == bookId).ToListAsync(ct);
        if (documentVersions.Count > 0)
            _db.DocumentVersions.RemoveRange(documentVersions);

        _db.Books.Remove(book);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Resolves the style-baseline language: explicit request language if supplied, else the book's
    /// normalized language ("he"/"en"), defaulting to "he". Keeps the cache key aligned with how
    /// LinguisticAnalysis resolves language elsewhere.
    /// </summary>
    private async Task<string> ResolveBaselineLanguageAsync(Guid bookId, string? requestLanguage, CancellationToken ct)
    {
        // An explicit request language must be normalized with the SAME rules as the book language, or
        // the two entry points (GET status, POST build) key the cache differently. A request for
        // "en-US" would otherwise target the "en-US" slot while profiles/baselines are persisted under
        // the normalized "en" - so status would understate coverage and a build would write the wrong
        // cache slot.
        // Normalize BOTH the explicit request value and the book value with the SAME shared rule the
        // inline LinguisticAnalysis path and the background builder use, so every entry point keys the
        // baseline cache identically (e.g. "en-US" → "en").
        if (!string.IsNullOrWhiteSpace(requestLanguage)) return BaselineLanguageResolver.Normalize(requestLanguage);
        var book = await _db.Books.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookId, ct);
        return BaselineLanguageResolver.Normalize(book?.Language);
    }

    private static BookStyleBaselineStatusDto ToStatusDto(BookStyleBaselineStatus s) => new(
        s.BookId,
        s.Language,
        s.TotalChapters,
        s.BuiltChapters,
        s.StaleCount,
        s.HasBaseline,
        s.IsReady,
        s.LastUpdatedAt,
        s.BuiltWithModel,
        s.ActiveModel,
        s.BuiltWithDifferentModel,
        s.ActiveBuildJobId,
        s.ChaptersToBuild,
        s.EstimatedSeconds,
        s.EstimatedUsd);

    private static BookSummaryStatusDto ToSummaryStatusDto(BookSummaryStatus s) => new(
        s.BookId,
        s.Language,
        s.TotalChapters,
        s.BuiltChapters,
        s.StaleCount,
        s.HasSummary,
        s.IsReady,
        s.LastUpdatedAt,
        s.BuiltWithModel,
        s.ActiveModel,
        s.BuiltWithDifferentModel,
        s.ActiveBuildJobId,
        s.ChaptersToBuild,
        s.EstimatedSeconds,
        s.EstimatedUsd);

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
