using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Services.Feedback;

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
    private readonly AiTierStatusService _aiTierStatus;
    private readonly BookProfileBuildCoordinator _profileBuilds;
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
        AiTierStatusService aiTierStatus,
        BookProfileBuildCoordinator profileBuilds,
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime appLifetime,
        ILogger<BooksController> logger)
    {
        _aiTierStatus = aiTierStatus;
        _profileBuilds = profileBuilds;
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
        // A book is created empty, so the M1 counts are 0/0 by construction - stated rather than re-queried.
        return CreatedAtAction(nameof(GetById), new { bookId = book.Id }, ToDto(book, chapterCount: 0, chaptersWithTextCount: 0));
    }

    /// <summary>
    /// The books list. Since Wave 3 / M1 each row carries <c>chapterCount</c> and
    /// <c>chaptersWithTextCount</c> so the stage spine can compute the Import stage HERE, on the one surface
    /// where importing is the next action. Both are projected inside this single query (a correlated count per
    /// row, not a request per book) - the cost of the list does not scale with the size of the manuscripts.
    /// Uses the same <see cref="WithCounts"/> projection as <see cref="Update"/>, so the two are symmetric:
    /// both compute both counts as SQL aggregates, never a re-query of the chapter rows.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<BookDto>>> GetAll(CancellationToken ct)
    {
        var orderedBooks = await WithCounts(_db.Books.AsNoTracking().OrderBy(b => b.UpdatedAt)).ToListAsync(ct);

        return Ok(orderedBooks
            .Select(row => ToDto(row.Book, row.ChapterCount, row.ChaptersWithTextCount))
            .ToList());
    }

    /// <summary>
    /// Projects a books query into (book, chapterCount, chaptersWithTextCount) using ONE SQL query per
    /// caller: <c>ChapterCount</c> and <c>ChaptersWithTextCount</c> (<see cref="ChapterTextPredicate.HasText"/>)
    /// are both correlated COUNT subqueries, not a materialization of the chapter rows. The shared shape is
    /// what keeps <see cref="GetAll"/> and <see cref="Update"/> symmetric - see <see cref="BooksControllerQueryShapeTests"/>
    /// for the assertion that this stays translated to SQL rather than silently regressing to a per-row fetch.
    /// </summary>
    internal static IQueryable<BookWithCountsRow> WithCounts(IQueryable<Book> books) =>
        books.Select(b => new BookWithCountsRow(
            b,
            b.Chapters.Count(),
            b.Chapters.AsQueryable().Count(ChapterTextPredicate.HasText)));

    internal sealed record BookWithCountsRow(Book Book, int ChapterCount, int ChaptersWithTextCount);

    /// <summary>
    /// One book with its chapter list.
    ///
    /// <c>exportableChapterCount</c> (w8 / F2) is answered by <see cref="BookExportService"/> itself rather
    /// than counted here: the stage spine's Export stage and the export endpoints have to agree about whether a
    /// file can be produced, and they disagreed - the spine read <c>WordCount &gt; 0</c> and rendered `ready`
    /// on a book whose export answered 409 <c>nothingWritten</c>. See <see cref="BookDetailDto"/> for why the
    /// count is on THIS payload and not on the books list.
    ///
    /// The chapter rows this endpoint already materializes are handed straight to that helper, so the count
    /// costs one extra query (the scenes) and no second read of the manuscript.
    /// </summary>
    [HttpGet("{bookId:guid}")]
    public async Task<ActionResult<BookDetailDto>> GetById(Guid bookId, CancellationToken ct)
    {
        var book = await _db.Books.Include(b => b.Chapters.OrderBy(c => c.Order)).FirstOrDefaultAsync(b => b.Id == bookId, ct);
        if (book == null) return NotFound();
        var orderedChapters = book.Chapters.ToList();
        var chapters = orderedChapters.Select(c => new ChapterSummaryDto(c.Id, c.Title, c.PartName, c.Order, c.WordCount, c.UpdatedAt)).ToList();
        var exportableChapterCount = await BookExportService.CountExportableChaptersAsync(_db, orderedChapters, ct);
        return Ok(new BookDetailDto(
            book.Id, book.Title, book.Author, book.Language, book.CreatedAt, book.UpdatedAt,
            AiTierPolicy.ToStoredValue(AiTierPolicy.Parse(book.AiTier)),
            chapters,
            exportableChapterCount));
    }

    /// <summary>
    /// GET the book's model tier plus everything needed to describe it honestly (model-tier-fast-thinking
    /// plan, p3-4): the stored tier, whether the thinking tier is usable on this deployment at all, whether
    /// the book is currently FALLING BACK (stored "thinking" but running local), and per user-facing task the
    /// stored/effective tier, where its text is processed, and why "thinking" is unavailable if it is.
    ///
    /// The answers still come from <see cref="Services.Ai.LinguisticModelResolver"/>, the same function
    /// <see cref="Services.Ai.AiRouter"/> resolves through, so "the tier the UI said would run" and "the tier
    /// that ran" are one computation rather than two that agree today. What CHANGED in tier-ux-rework c2 is
    /// what survives onto the wire: the resolved (provider, model) is reduced to a local/cloud token inside
    /// <see cref="Services.Ai.AiTierStatusService"/> and NO field on the response names a provider, a model or
    /// a version. That is an IP boundary, not a cosmetic one - see <see cref="BookAiTierDto"/>.
    /// </summary>
    [HttpGet("{bookId:guid}/ai-tier")]
    public async Task<ActionResult<BookAiTierDto>> GetAiTier(Guid bookId, CancellationToken ct)
    {
        var book = await _db.Books.AsNoTracking().FirstOrDefaultAsync(b => b.Id == bookId, ct);
        if (book == null) return NotFound();
        return Ok(await BuildAiTierDtoAsync(book, ct));
    }

    /// <summary>
    /// PUT a model tier. Opt-in only: choosing "thinking" means this unpublished manuscript's text is sent to
    /// a third-party provider for the allowlisted tasks.
    ///
    /// SCOPE (tier-ux-rework c1). With <c>task</c> set, this writes THAT task's override. With <c>task</c>
    /// absent it writes the BOOK DEFAULT, which is a seed for tasks that have not been decided and therefore
    /// leaves existing per-task overrides ALONE - clearing them is <see cref="ClearAiTierTask"/>, an explicit
    /// verb. The alternative (a default write that wipes overrides) makes an unrelated control silently
    /// discard a choice the user made deliberately, with no undo.
    ///
    /// THREE REJECTIONS, all deliberate:
    ///   • an unrecognised tier token is a 400, NOT a defensive parse to "fast". <c>AiTierPolicy.Parse</c> is
    ///     fail-safe for READS (a legacy row must not throw), but silently storing "fast" because a caller
    ///     typed "thinkng" would make the UI and the database disagree with no signal anywhere.
    ///   • an unrecognised TASK token is a 400 for the same reason: storing it as the book default because
    ///     the name did not parse would move a setting the caller never asked to move.
    ///   • "thinking" when the tier is not usable is a 409 carrying the readiness reason, evaluated PER TASK
    ///     when a task is named and deployment-wide otherwise. Accepting it would let a book advertise a tier
    ///     that provably cannot route, which is the exact silent-lie this endpoint exists to close - and per
    ///     task it is also what refuses "thinking" for a non-allowlisted task and for an English book's
    ///     Proofread (the p2-4 NO-GO, enforced by the language rung outranking the tier rung). Switching BACK
    ///     to "fast" is always allowed.
    /// </summary>
    [HttpPut("{bookId:guid}/ai-tier")]
    public async Task<ActionResult<BookAiTierDto>> UpdateAiTier(
        Guid bookId, [FromBody] UpdateBookAiTierRequest req, CancellationToken ct)
    {
        var book = await _db.Books.FindAsync(new object[] { bookId }, ct);
        if (book == null) return NotFound();

        var requested = req?.Tier?.Trim() ?? "";
        AiTier tier;
        if (string.Equals(requested, AiTierPolicy.ThinkingStoredValue, StringComparison.OrdinalIgnoreCase))
            tier = AiTier.Thinking;
        else if (string.Equals(requested, AiTierPolicy.FastStoredValue, StringComparison.OrdinalIgnoreCase))
            tier = AiTier.Fast;
        else
            return BadRequest(new { error = "unrecognizedTier", allowed = new[] { AiTierPolicy.FastStoredValue, AiTierPolicy.ThinkingStoredValue } });

        AiTaskType? task = null;
        if (!string.IsNullOrWhiteSpace(req!.Task))
        {
            if (!AiTierPolicy.TryParseTaskKey(req.Task, out var parsedTask))
                return BadRequest(new
                {
                    error = "unrecognizedTask",
                    allowed = AiTierPolicy.UserFacingTasks.Select(t => t.ToString()).ToArray()
                });
            task = parsedTask;
        }

        if (tier == AiTier.Thinking)
        {
            var readiness = task is null
                ? _aiTierStatus.EvaluateThinkingReadiness(book.Language)
                : _aiTierStatus.EvaluateThinkingReadiness(book.Language, task.Value);
            if (readiness != AiTierReadiness.Ready)
                return Conflict(new
                {
                    error = "thinkingTierUnavailable",
                    reason = ToCamelCase(readiness.ToString()),
                    task = task?.ToString()
                });
        }

        if (task is null)
        {
            book.AiTier = AiTierPolicy.ToStoredValue(tier);
            book.UpdatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            var taskKey = AiTierPolicy.TaskKeyFor(task.Value);
            var existing = await _db.BookAiTaskTiers
                .FirstOrDefaultAsync(t => t.BookId == bookId && t.TaskKey == taskKey, ct);
            if (existing == null)
                _db.BookAiTaskTiers.Add(new BookAiTaskTier
                {
                    BookId = bookId,
                    TaskKey = taskKey,
                    Tier = AiTierPolicy.ToStoredValue(tier)
                });
            else
                existing.Tier = AiTierPolicy.ToStoredValue(tier);
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Book {BookId} model tier for {Task} set to {Tier}. Thinking means chapter text for the allowlisted " +
            "tasks leaves this machine.",
            bookId, task?.ToString() ?? "(book default)", AiTierPolicy.ToStoredValue(tier));

        // CancellationToken.None, not ct (be-f03's rule): the tier write is already committed above, and
        // this read only assembles the response from it. A client abort here would report a failed tier
        // change that in fact persisted, leaving the caller's idea of the tier and the tier that will
        // actually route disagreeing until it re-reads.
        return Ok(await BuildAiTierDtoAsync(book, CancellationToken.None));
    }

    /// <summary>
    /// DELETE one task's tier override, so the task INHERITS the book default again (tier-ux-rework c1).
    ///
    /// It exists as its own verb because the PUT deliberately does not clear overrides: "set the default" and
    /// "forget what I chose for this task" are different intents, and collapsing them into one call means the
    /// user cannot express the first without risking the second. Idempotent - clearing an override that is not
    /// there is a 200 with the unchanged state, not a 404, because the caller's desired end state holds.
    /// </summary>
    [HttpDelete("{bookId:guid}/ai-tier/{task}")]
    public async Task<ActionResult<BookAiTierDto>> ClearAiTierTask(Guid bookId, string task, CancellationToken ct)
    {
        var book = await _db.Books.FindAsync(new object[] { bookId }, ct);
        if (book == null) return NotFound();

        if (!AiTierPolicy.TryParseTaskKey(task, out var parsedTask))
            return BadRequest(new
            {
                error = "unrecognizedTask",
                allowed = AiTierPolicy.UserFacingTasks.Select(t => t.ToString()).ToArray()
            });

        var taskKey = AiTierPolicy.TaskKeyFor(parsedTask);
        var existing = await _db.BookAiTaskTiers
            .FirstOrDefaultAsync(t => t.BookId == bookId && t.TaskKey == taskKey, ct);
        var cleared = existing != null;
        if (cleared)
        {
            _db.BookAiTaskTiers.Remove(existing!);
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation(
                "Book {BookId} model tier override for {Task} cleared; the task follows the book default again.",
                bookId, taskKey);
        }

        // The token depends on whether anything COMMITTED. When an override was removed this read runs after
        // that write and must not be able to fail it (be-f03), so it drops the request token. On the
        // idempotent no-op path nothing was written, the read IS the whole request, and the caller's token
        // still governs its own observation - the be-c03 distinction between bounding a wait and bounding
        // committed work.
        return Ok(await BuildAiTierDtoAsync(book, cleared ? CancellationToken.None : ct));
    }

    /// <summary>
    /// Builds the tier read model. The per-task tiers come from <see cref="BookAiTierResolver"/> - the SAME
    /// lookup the analysis run uses - rather than from a second copy of the precedence here, so "the tier the
    /// UI showed" and "the tier that ran" are one computation. That is the same structural argument as
    /// <see cref="AiTierStatusService"/> resolving routes through <see cref="LinguisticModelResolver"/>.
    ///
    /// WHAT THE RESOLVER ANSWERS IS THE STORAGE QUESTION (be-c01) - the override, else the book default - and
    /// it knows nothing about task eligibility or the language rung. Turning that into the tier that will
    /// actually route is <see cref="AiTierStatusService.DescribeTask"/>'s job, which is why every per-task
    /// value on the wire comes back out of that call rather than out of this dictionary.
    /// </summary>
    private async Task<BookAiTierDto> BuildAiTierDtoAsync(Book book, CancellationToken ct)
    {
        var resolvedByTask = new Dictionary<AiTaskType, AiTier>();
        foreach (var task in AiTierPolicy.UserFacingTasks)
            resolvedByTask[task] = await BookAiTierResolver.ResolveAsync(_db, book.Id, task, _logger, ct);

        var bookDefault = AiTierPolicy.Parse(book.AiTier);
        var status = _aiTierStatus.Describe(
            t => resolvedByTask.TryGetValue(t, out var resolved) ? resolved : bookDefault,
            bookDefault,
            book.Language);

        var storedByTask = await _db.BookAiTaskTiers
            .AsNoTracking()
            .Where(t => t.BookId == book.Id)
            .ToDictionaryAsync(t => t.TaskKey, t => t.Tier, StringComparer.Ordinal, ct);

        var tasks = AiTierPolicy.UserFacingTasks.Select(task =>
        {
            var key = AiTierPolicy.TaskKeyFor(task);
            // A stored row is reported through the SAME defensive parse as the resolver, so the wire value is
            // always a clean token and a client never has to own a second copy of that parse. An absent row
            // stays null, which is the "inherits the default" state and is NOT the same as "fast".
            AiTier? stored = storedByTask.TryGetValue(key, out var raw) ? AiTierPolicy.Parse(raw) : null;
            // The per-task read model arrives already DE-IDENTIFIED (tier-ux-rework c2, total since be-c03):
            // the service resolves the real route and hands back only judgements about it, so this method
            // never holds a provider, a model or a topology token it could project onto the wire.
            //
            // It also arrives CLAMPED (be-c01): what goes in is the resolver's storage answer, what comes back
            // on EffectiveTier is the tier that will actually route. The stored override goes in beside it
            // because the service needs it to tell an ignored per-task opt-in apart from a book default that
            // never applied to this task - see AiTierTaskStatus.FallbackActive.
            var taskStatus = _aiTierStatus.DescribeTask(task, resolvedByTask[task], stored, book.Language);
            return new BookAiTierTaskDto(
                key,
                stored is { } storedValue ? AiTierPolicy.ToStoredValue(storedValue) : null,
                AiTierPolicy.ToStoredValue(taskStatus.EffectiveTier),
                ToCamelCase(taskStatus.ThinkingReadiness.ToString()),
                taskStatus.FallbackActive);
        }).ToList();

        return new BookAiTierDto(
            book.Id,
            AiTierPolicy.ToStoredValue(status.Tier),
            ToCamelCase(status.ThinkingReadiness.ToString()),
            status.FallbackActive,
            _aiTierStatus.ConsentRequired,
            tasks);
    }

    private static string ToCamelCase(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value[1..];

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

    /// <summary>
    /// Rebuild the book profile: re-summarize stale chapters, then rebuild the profile from them.
    ///
    /// THE REQUEST TOKEN GOVERNS THE WAIT, NEVER THE WORK (be-c03, review finding 6). This endpoint used
    /// to await <see cref="BookIntelligenceService.RefreshProfileAsync"/> inline with <paramref name="ct"/>,
    /// so an ordinary client teardown - closing the dashboard panel, entering focus mode, switching the
    /// assistant to Edit help, reloading mid-build - did not merely stop OBSERVING a whole-book build that
    /// costs minutes, it CANCELED it, and the book profile was silently never built while the row read
    /// ready. The build now runs on its own DI scope under the SERVER's lifetime token, and
    /// <c>WaitAsync(ct)</c> bounds only how long this request waits for it: an abandoned request returns,
    /// the build commits.
    ///
    /// CONCURRENT CALLERS ARE DEDUPLICATED SERVER-SIDE (review finding 24). Two reattached tabs, or the
    /// import-handoff card racing the dashboard's status row, join ONE build - see
    /// <see cref="BookProfileBuildCoordinator"/> for why running it twice is not merely wasteful.
    ///
    /// NOT A TRACKED JOB. Unlike the briefs / review / style-baseline builds this returns no jobId and has
    /// no progress route, so the response shape is unchanged (<see cref="BookProfileDto"/>) and the client
    /// has nothing to reattach to. That was a costed decision, recorded in the be-c03 investigation
    /// findings; do not read the absence as an oversight.
    /// </summary>
    [HttpPost("{bookId:guid}/profile/refresh")]
    public async Task<ActionResult<BookProfileDto>> RefreshProfile(Guid bookId, [FromBody] RefreshProfileRequest req, CancellationToken ct)
    {
        if (await _db.Books.FindAsync(new object[] { bookId }, ct) == null) return NotFound();
        // The RAW request value is what reaches the build, unchanged: the persistence layer normalizes for
        // itself (SummarizeChaptersCoreAsync) and the prompts see the caller's value. Normalizing here
        // would quietly change what the model is asked for, which be-c03 is not allowed to do.
        var language = req.Language ?? "he";

        var shutdownToken = _appLifetime.ApplicationStopping;
        if (shutdownToken.IsCancellationRequested)
            return StatusCode(503, new { error = "Server is shutting down; cannot start new build." });

        var build = _profileBuilds.StartOrJoin(bookId, language, shutdownToken);
        if (build.Joined)
        {
            _logger.LogInformation(
                "Book profile refresh for {BookId} joined the build already in flight (requested {RequestedLanguage}, building {BuildLanguage}).",
                bookId, language, build.BuildLanguage);
        }

        // ct is the WAIT bound only. If it fires, this throws and the response is abandoned - which is
        // correct, because the only thing that cancels it is the caller going away - while the build keeps
        // running on its own scope and commits.
        await build.Completion.WaitAsync(ct);

        // Re-read from THIS request's DbContext rather than returning the entity the build produced: the
        // build ran in another scope, and a joining caller never had one at all. Both callers therefore
        // return committed state, read the same way.
        //
        // Uses CancellationToken.None, not ct - the SAME rule as the post-commit counts re-query in
        // <see cref="Update"/> (be-f03). ct bounds the WAIT above, which the caller is entitled to abandon;
        // it must not bound this read, which only runs once the build has already COMMITTED. A client abort
        // landing in that window would fail a response whose work succeeded, and the caller would be told
        // its profile was never built while the row sits in the database.
        var profile = await _db.Set<BookProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.BookId == bookId, CancellationToken.None);
        if (profile == null) return NotFound();
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

        // wb4-c06: carry the transient review build-shape the BookReview build stamps at its terminal, so the FE
        // can render the "N windows[, continuity pass]" detail + the "N windows failed" partial warning right
        // after a build (the persisted status probe reports these as 0/false, so this LIVE poll is their channel).
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
            snapshot.EstimatedCompletionPercent,
            snapshot.BookReviewWindowCount,
            snapshot.BookReviewRanContinuityReduce,
            snapshot.BookReviewFailedWindows);

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
    /// Update the workflow status of a single whole-book finding. The accepted body values and the persisted
    /// value each one maps to are declared by <see cref="FindingStatusPartition"/> (imperative verbs and the
    /// stored adjectives are both accepted); this endpoint does not spell them, and neither should its
    /// callers' docs. MIRRORS AnalysisController.UpdateSuggestionOutcome: validate the value (BadRequest on invalid),
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

        if (!FindingStatusPartition.TryParse(request.Status, out var status))
            return BadRequest(
                "Invalid status. Must be one of: " + string.Join(", ", FindingStatusPartition.AcceptedInputs) + ".");

        var finding = await _db.BookFindings.FirstOrDefaultAsync(f => f.Id == id && f.BookId == bookId, ct);
        if (finding == null) return NotFound();

        finding.Status = status; // idempotent: re-setting the same value + SaveChanges is a no-op success
        await _db.SaveChangesAsync(ct);

        return Ok(ToFindingDto(finding));
    }

    private static BookReviewStatusDto ToReviewStatusDto(BookReviewStatus s) => new(
        s.BookId,
        s.Language,
        s.HasReview,
        s.FindingCount,
        s.LastUpdatedAt,
        // BuiltWithModel / ActiveModel stay on the internal status record; only the VERDICT crosses the wire.
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
        s.IsReady,
        // Wave 3 / M3: working-through progress without downloading the ledger.
        s.OpenFindingCount,
        s.ResolvedFindingCount);

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
        // CancellationToken.None, not ct (be-f03's rule): the author's summary edit is already committed, so
        // a client abort in this window must not report a failed save for text that is in the database. The
        // clobber guard makes that worse than cosmetic - the edit counts as the user's from now on.
        var saved = await _db.ChunkSummaries.AsNoTracking()
            .FirstAsync(cs => cs.BookId == bookId && cs.ChapterId == chapterId, CancellationToken.None);
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
            StructuredBrief: StructuredChunkSummaryParser.Parse(row.StructuredJson));
    }

    // ─── Active chapter analysis jobs (rf-b01) ────────────────────────────────────────────────────

    /// <summary>
    /// GET all non-terminal (Pending or Running), non-expired in-flight chapter analysis jobs for a
    /// book. Allows a refreshed web client to rediscover an async Proofread or LineEdit job and
    /// reattach to its progress without losing the jobId on a browser refresh.
    ///
    /// READ-ONLY: no side effects, no DB queries. Reads the existing singleton
    /// <see cref="AnalysisProgressTracker"/> (in-memory, 30-min TTL). Returns an empty list when no
    /// active jobs exist. Semantics: survives a BROWSER refresh (server process stays up) but NOT an
    /// API restart — identical to how the book-level builds (style-baseline, summary, review) already
    /// behave with their <c>activeBuildJobId</c> fields.
    ///
    /// Covers Proofread and LineEdit chapter/scene jobs only (started via POST .../analysis-jobs).
    /// Book-level jobs (style-baseline, summary, review) are surfaced on their own status endpoints.
    /// </summary>
    // SECURITY DEBT (be-c01): this action performs NO ownership check on {bookId}. Any caller who knows
    // (or guesses) a bookId gets back that book's reattachable jobIds, chapter/scene GUIDs, and free-text
    // progress messages. This is acceptable under the current SINGLE-USER posture (one operator, no auth,
    // local/trusted deployment). When the multi-user production service + authentication land, this MUST
    // be scoped to the caller's own books (verify the authenticated user owns {bookId} before returning).
    // Out of scope for this change — do NOT add auth here.
    [HttpGet("{bookId:guid}/active-analysis-jobs")]
    public ActionResult<List<AnalysisJobSummaryDto>> GetActiveAnalysisJobs(Guid bookId)
    {
        var snapshots = _progress.GetActiveJobsByBook(bookId);
        var dtos = snapshots.Select(s => new AnalysisJobSummaryDto(
            JobId: s.JobId,
            AnalysisType: s.AnalysisType.ToString(),
            Scope: s.Scope.ToString(),
            ChapterId: s.ChapterId,
            SceneId: s.SceneId,
            Status: s.Status.ToString(),
            EstimatedCompletionPercent: s.EstimatedCompletionPercent,
            Message: s.Message,
            LastUpdatedUtc: s.LastUpdatedUtc)).ToList();
        return Ok(dtos);
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

        // The M1 counts are part of the BookDto contract, so the update response has to carry the REAL ones -
        // a renamed book that came back reporting 0 chapters would be a typed lie about that contract, even
        // though no current client caller re-renders from this response today. Symmetric with GetAll: one SQL
        // query aggregating both counts server-side via the same WithCounts projection (ChapterCount and
        // ChaptersWithTextCount are both correlated COUNT subqueries), not a fetch of the chapter rows into
        // memory to count them here.
        //
        // Uses CancellationToken.None, not the request token ct: this read runs AFTER SaveChangesAsync has
        // already committed the rename, so a client abort (or a DB blip that trips the request token) in this
        // window must not be able to fail a response whose write already succeeded - that would show the
        // caller a failed rename that in fact persisted, leaving the UI and DB disagreeing until a reload.
        var counts = await WithCounts(_db.Books.AsNoTracking().Where(b => b.Id == bookId)).FirstAsync(CancellationToken.None);

        return Ok(ToDto(book, counts.ChapterCount, counts.ChaptersWithTextCount));
    }

    [HttpDelete("{bookId:guid}")]
    public async Task<ActionResult> Delete(Guid bookId, CancellationToken ct)
    {
        var book = await _db.Books.FindAsync(new object[] { bookId }, ct);
        if (book == null) return NotFound();

        // Per-task tier overrides. The FK is Cascade (Book is this table's only relationship, so there is no
        // multiple-cascade-paths problem), but removing them explicitly keeps this method the one readable
        // list of everything a book owns, and keeps the in-memory provider - which only cascades what it has
        // tracked - behaving like SQL Server in the tests.
        var taskTiers = await _db.BookAiTaskTiers.Where(t => t.BookId == bookId).ToListAsync(ct);
        if (taskTiers.Count > 0)
            _db.BookAiTaskTiers.RemoveRange(taskTiers);

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

        // Show C1's conversations and their turns, and with them Show C2's tombstone. Both FKs cascade
        // (Conversation.BookId and ConversationMessage.ConversationId - AppDbContext :329 / :347, and the
        // AddShowConversationHistory migration's own ON DELETE CASCADE on both), so these rows would go
        // even if this block did not exist. They are enumerated anyway for the reason every other table
        // above is, plus one that is theirs alone: the FEEDBACK rows pointing at these turns have to be
        // STAMPED before their target disappears, and a database cascade offers no server-side step to
        // stamp from. Deleting a book is the second arrival path of "a target that disappears is
        // tombstoned"; ConversationsController.Delete is the first.
        //
        // Only KEYS are read, never the message rows themselves. ConversationMessage.Text is the full
        // untruncated turn and GroundingJson is a whole grounding snapshot, both nvarchar(max), so
        // materialising a busy book's chat history just to delete it would be paid in memory for nothing:
        // the stamp needs ids, and EF deletes by key. The FOREIGN key is projected alongside the primary
        // one on purpose - it is what tells EF's save pipeline that a turn depends on its conversation and
        // a conversation on its book, so the three DELETEs are ordered child-first. Without it the message
        // DELETE could be emitted after the conversation's, by which time SQL Server's own ON DELETE
        // CASCADE has already removed the row and the statement would affect zero rows, which EF reports as
        // a concurrency failure. A keyed stub carrying its FK is, to the save pipeline, indistinguishable
        // from a loaded row - which is the same ordering ConversationsController.Delete already depends on
        // one level down, where it removes a conversation and its turns in a single save.
        var conversationIds = await _db.Conversations
            .Where(c => c.BookId == bookId)
            .Select(c => c.Id)
            .ToListAsync(ct);

        // COUNTED HERE, REPORTED AFTER THE SAVE. The log at the bottom of this method asserts a DURABLE
        // outcome - that feedback rows "were KEPT and tombstoned" - and that sentence is only true once
        // SaveChangesAsync has committed it. Emitted inside the block below it would stay in the record
        // even when the save meant to make it true rolled back, which is the same class of lie as a
        // fail-safe that swallows its fault: the log becomes evidence for something that did not happen.
        // ConversationsController.Delete has always reported this event after its own save; these three
        // counters are what let the second path follow the same rule instead of nearly matching it.
        var tombstonedFeedback = 0;
        var deletedConversations = 0;
        var deletedTurns = 0;

        if (conversationIds.Count > 0)
        {
            var messageKeys = await _db.ConversationMessages
                .Where(m => conversationIds.Contains(m.ConversationId))
                .Select(m => new { m.Id, m.ConversationId })
                .ToListAsync(ct);

            var messageIds = messageKeys.Select(m => m.Id).ToList();

            // Marks without saving, so the stamp commits or fails WITH the delete below - a second save
            // could commit the removal and then fail, leaving feedback rows silently pointing at nothing
            // and no record that anything happened. Reused rather than re-queried here: one place owns
            // "already stamped rows keep their original date" and "the target type is part of the match".
            tombstonedFeedback = await FeedbackTombstone.StampAsync(
                _db, FeedbackTargetTypes.ConversationMessage, messageIds, DateTimeOffset.UtcNow, ct);

            if (messageIds.Count > 0)
            {
                _db.ConversationMessages.RemoveRange(
                    messageKeys.Select(m => new ConversationMessage { Id = m.Id, ConversationId = m.ConversationId }));
            }
            _db.Conversations.RemoveRange(
                conversationIds.Select(id => new Conversation { Id = id, BookId = bookId }));

            deletedConversations = conversationIds.Count;
            deletedTurns = messageIds.Count;
        }

        _db.Books.Remove(book);
        await _db.SaveChangesAsync(ct);

        // PAST THE SAVE, so the sentence is true when it is written. A throw from SaveChangesAsync above
        // propagates and this never runs, which is the whole point: no log line survives a rolled-back
        // delete.
        if (tombstonedFeedback > 0)
        {
            _logger.LogInformation(
                "Book {BookId} was deleted with {ConversationCount} conversation(s) and {MessageCount} " +
                "turn(s); {TombstonedCount} feedback row(s) pointing at those turns were KEPT and " +
                "tombstoned. Their triage detail now renders the stored vote-time context instead of " +
                "the transcript.",
                bookId, deletedConversations, deletedTurns, tombstonedFeedback);
        }

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
        // BuiltWithModel / ActiveModel stay on the internal status record; only the VERDICT crosses the wire.
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
        // BuiltWithModel / ActiveModel stay on the internal status record; only the VERDICT crosses the wire.
        s.BuiltWithDifferentModel,
        s.ActiveBuildJobId,
        s.ChaptersToBuild,
        s.EstimatedSeconds,
        s.EstimatedUsd,
        // Wave 3 / w1: the third not-ready reason, computed all along and previously dropped on the floor.
        s.SummaryCoversBuiltChapters);

    // Normalized on the way out (p3-4): the stored column is a nullable free string so a legacy or
    // hand-edited row degrades to the local tier instead of throwing, but the wire value is always exactly
    // "fast" or "thinking". Doing the defensive parse HERE means no client has to own a second copy of it.
    private static BookDto ToDto(Book b, int chapterCount, int chaptersWithTextCount) => new(
        b.Id, b.Title, b.Author, b.Language, b.CreatedAt, b.UpdatedAt,
        AiTierPolicy.ToStoredValue(AiTierPolicy.Parse(b.AiTier)),
        chapterCount,
        chaptersWithTextCount);

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
