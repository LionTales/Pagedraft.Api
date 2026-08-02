using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// Coverage status for a book's cached style baseline. Surfaced by the GET status endpoint so the FE
/// can decide whether a (re)build is needed and render progress.
/// </summary>
public sealed class BookStyleBaselineStatus
{
    public Guid BookId { get; init; }
    public string Language { get; init; } = "he";

    /// <summary>Total chapters in the book.</summary>
    public int TotalChapters { get; init; }

    /// <summary>Chapters that currently have a FRESH (non-stale, non-missing) ChapterStyleProfile.</summary>
    public int BuiltChapters { get; init; }

    /// <summary>Chapters whose profile is MISSING or STALE (chapter edited after the profile was built).</summary>
    public int StaleCount { get; init; }

    /// <summary>True when a usable cached average exists for (BookId, Language).</summary>
    public bool HasBaseline { get; init; }

    /// <summary>When the cached average was last (re)built; null when no baseline exists.</summary>
    public DateTimeOffset? LastUpdatedAt { get; init; }

    /// <summary>LinguisticAnalysis model id that built the cached average; null when no baseline exists.</summary>
    public string? BuiltWithModel { get; init; }

    /// <summary>
    /// The resolved active LinguisticAnalysis model id (same resolution AiRouter uses). Surfaced so the FE
    /// can render a "built with a different model" warning and show which model is now active.
    /// </summary>
    public string? ActiveModel { get; init; }

    /// <summary>
    /// True when a cached baseline exists AND its model differs from <see cref="ActiveModel"/> (cross-model
    /// signal). The chapter profiles self-heal on next access, but the cached average row is only refreshed
    /// by a build, so this warns the user a rebuild is advisable.
    /// </summary>
    public bool BuiltWithDifferentModel { get; init; }

    /// <summary>
    /// The jobId of an in-progress build for (BookId, Language), or null when none is running. Lets the FE
    /// reattach to a build started in another tab/session after a reload (DEF-2).
    /// </summary>
    public Guid? ActiveBuildJobId { get; init; }

    /// <summary>
    /// True when a build would be a genuine no-op: nothing missing/stale AND a usable cached average
    /// exists that was built under the ACTIVE LinguisticAnalysis model. Mirrors the no-op gate in
    /// <see cref="StyleBaselineService.BuildBookStyleBaselineAsync"/> and BooksController exactly - so a
    /// fresh-chapters book whose cached average is cross-model (<see cref="BuiltWithDifferentModel"/>)
    /// correctly reports NOT ready, because a rebuild is still required to restamp the average.
    /// </summary>
    public bool IsReady => StaleCount == 0 && HasBaseline && !BuiltWithDifferentModel;

    // ─── Build estimate (a4) ───────────────────────────────────────────────────────────────────────

    /// <summary>Chapters that a build would (re)process: missing + stale. Same as StaleCount; surfaced
    /// explicitly so the FE consent prompt does not need to alias it.</summary>
    public int ChaptersToBuild { get; init; }

    /// <summary>Rough estimate of wall-clock seconds a build would take, accounting for limited
    /// parallelism. Null if not computed (should always be set by GetStatusAsync).</summary>
    public int EstimatedSeconds { get; init; }

    /// <summary>Rough estimate of USD cost. Null for local providers (Ollama); a coarse figure for
    /// paid providers (e.g. OpenRouter).</summary>
    public decimal? EstimatedUsd { get; init; }
}

/// <summary>
/// Result of a baseline build job.
/// </summary>
public sealed class BookStyleBaselineBuildResult
{
    /// <summary>True when the build (or a no-op fresh build) ended with a usable cached average.</summary>
    public bool Ready { get; init; }

    /// <summary>True when nothing needed (re)building - every chapter was already fresh.</summary>
    public bool NoOp { get; init; }

    /// <summary>Chapters that contributed a usable profile to the average.</summary>
    public int BuiltChapters { get; init; }

    public int TotalChapters { get; init; }

    /// <summary>
    /// Chapters that did NOT end up with a fresh profile after the build attempt (graceful-null build
    /// failures, thrown failures, or empty content). The job is never aborted by these; they stay stale.
    /// </summary>
    public int FailedChapters { get; init; }

    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Heavy "fill coverage" book-wide style baseline builder.
///
/// CONTRAST with <see cref="AnalysisContextService.BuildBookStyleAverageProfileAsync"/> (a1):
///   • a1 is READ-ONLY: it AGGREGATES ONLY the already-FRESH, same-model chapter profiles (it never
///     builds or refreshes anything, and excludes stale/cross-model rows) and returns a SYNTHETIC,
///     unpersisted average - it is the cheap read path used inline during analysis.
///   • THIS service (re)builds EVERY chapter via
///     <see cref="IAnalysisContextService.LoadOrBuildChapterStyleProfileAsync"/> (idempotent: only
///     stale/missing chapters trigger an LLM call), THEN calls a1's averaging method and PERSISTS the
///     result to <see cref="BookStyleBaseline"/>. It is the heavy, explicitly-triggered coverage path.
///
/// REUSE INTENT: this establishes the async-job + progress-polling contract for book-level cached
/// builds. Future builders (BookBible, Literary baseline) should extend this shape - per-unit progress
/// through <see cref="AnalysisProgressTracker"/>, limited parallelism, idempotent skip-when-fresh,
/// a (BookId, Language) cache row carrying the model id (see docs/LINGUISTIC_SCALE_AND_REUSE.md).
/// </summary>
public class StyleBaselineService
{
    private readonly AppDbContext _db;
    private readonly IAnalysisContextService _contextService;
    private readonly AnalysisProgressTracker _progress;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly StyleBaselineBuildRegistry _buildRegistry;
    private readonly ILogger<StyleBaselineService> _logger;

    public StyleBaselineService(
        AppDbContext db,
        IAnalysisContextService contextService,
        AnalysisProgressTracker progress,
        IServiceScopeFactory scopeFactory,
        IOptions<AiOptions> aiOptions,
        StyleBaselineBuildRegistry buildRegistry,
        ILogger<StyleBaselineService> logger)
    {
        _db = db;
        _contextService = contextService;
        _progress = progress;
        _scopeFactory = scopeFactory;
        _aiOptions = aiOptions;
        _buildRegistry = buildRegistry;
        _logger = logger;
    }

    /// <summary>
    /// Read-only coverage status for (bookId, language). Reuses the SAME staleness predicate as
    /// <see cref="AnalysisContextService.LoadOrBuildChapterStyleProfileAsync"/> via the shared
    /// <see cref="ChapterStyleProfileFreshness.IsFresh"/>: a chapter is fresh when it has a profile that is
    /// at/after the chapter's UpdatedAt AND was built under the active LinguisticAnalysis model; missing,
    /// timestamp-older, OR built under a different model (incl. legacy null) = stale.
    /// Also surfaces the cross-model warning (DEF-1) and the in-progress build jobId (DEF-2).
    /// </summary>
    public async Task<BookStyleBaselineStatus> GetStatusAsync(
        Guid bookId,
        string language,
        CancellationToken ct = default)
        => await GetStatusAsync(bookId, language, await ResolveBookTierAsync(bookId, ct), ct);

    /// <summary>
    /// <see cref="GetStatusAsync(Guid, string, CancellationToken)"/> with the book's tier already resolved,
    /// so a build that calls status twice (pre + post) does not re-query <c>Book.AiTier</c> each time and -
    /// more importantly - cannot observe a DIFFERENT tier half-way through its own build.
    /// </summary>
    private async Task<BookStyleBaselineStatus> GetStatusAsync(
        Guid bookId,
        string language,
        AiTier tier,
        CancellationToken ct)
    {
        var lang = NormalizeLanguage(language);

        var chapters = await _db.Chapters
            .AsNoTracking()
            .Where(c => c.BookId == bookId)
            .Select(c => new { c.Id, c.UpdatedAt })
            .ToListAsync(ct);

        var profiles = await _db.ChapterStyleProfiles
            .AsNoTracking()
            .Where(p => p.BookId == bookId && p.Language == lang)
            .Select(p => new { p.ChapterId, p.UpdatedAt, p.BuiltWithModel })
            .ToListAsync(ct);

        // Keep the NEWEST profile per chapter (its UpdatedAt + the model it carries) for the freshness gate.
        var profileByChapter = profiles
            .GroupBy(p => p.ChapterId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var newest = g.OrderByDescending(x => x.UpdatedAt).First();
                    return (newest.UpdatedAt, newest.BuiltWithModel);
                });

        // The active LinguisticAnalysis model the freshness gate compares against (config-resolved, shared
        // resolver, at THIS BOOK'S TIER - p3-3). A profile built under a different model is stale, exactly
        // like a timestamp mismatch, which is how a tier change invalidates this book and only this book.
        // The PROVIDER moves with the tier too, so the USD estimate below is the estimate for the tier the
        // build would actually run on rather than always the local (free) one.
        var (provider, activeModel) = ResolveLinguisticProviderAndModel(tier);

        var built = 0;
        var stale = 0;
        foreach (var chapter in chapters)
        {
            // SAME predicate as LoadOrBuildChapterStyleProfileAsync step 3 (timestamp AND model), via the
            // single shared definition so the two can never drift.
            if (profileByChapter.TryGetValue(chapter.Id, out var profile)
                && ChapterStyleProfileFreshness.IsFresh(profile.UpdatedAt, profile.BuiltWithModel, chapter.UpdatedAt, activeModel))
                built++;
            else
                stale++; // missing OR timestamp-stale OR model-mismatched
        }

        var baseline = await _db.BookStyleBaselines
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.BookId == bookId && b.Language == lang, ct);

        var hasBaseline = baseline != null && !string.IsNullOrWhiteSpace(baseline.MetricsJson);

        // a4: coarse build estimate — same staleness count as StaleCount; resolved via AiRouter-mirror.
        var maxParallel = Math.Max(1, _aiOptions.Value.MaxParallelStyleBaselineChapters);
        var (estimatedSeconds, estimatedUsd) = ComputeEstimate(stale, provider, maxParallel);

        return new BookStyleBaselineStatus
        {
            BookId = bookId,
            Language = lang,
            TotalChapters = chapters.Count,
            BuiltChapters = built,
            StaleCount = stale,
            HasBaseline = hasBaseline,
            LastUpdatedAt = baseline?.UpdatedAt,
            BuiltWithModel = baseline?.BuiltWithModel,
            ActiveModel = activeModel,
            // Cross-model warning: a cached average exists but was built under a different model than the
            // one now active. Ordinal compare; a null cached model also counts as "different" when active
            // is set, so a legacy baseline surfaces the warning until rebuilt.
            BuiltWithDifferentModel = hasBaseline
                && !string.Equals(baseline!.BuiltWithModel, activeModel, StringComparison.Ordinal),
            ActiveBuildJobId = ResolveActiveBuildJobId(bookId, lang),
            ChaptersToBuild = stale,
            EstimatedSeconds = estimatedSeconds,
            EstimatedUsd = estimatedUsd
        };
    }

    /// <summary>
    /// Resolves the in-progress build jobId for (bookId, lang) for the status payload, but NEVER reports a
    /// job that is already finished (DEF-2 hardening). The registry entry can linger if a build crashed
    /// before its finally-block cleared it, or in a race where the progress tracker already advanced to a
    /// terminal status; advertising such a jobId as in-progress would make the FE reattach to a dead job
    /// forever. So we cross-check the progress tracker: if the registry holds a jobId but the tracker has
    /// NO entry for it (pruned/unknown) OR its Status is terminal (Succeeded/Failed/Canceled), we treat the
    /// build as finished - clear the lingering registry entry to self-heal and return null. Only a job the
    /// tracker still reports as running (Pending/Running) is surfaced.
    /// </summary>
    private Guid? ResolveActiveBuildJobId(Guid bookId, string lang)
    {
        var jobId = _buildRegistry.TryGetActive(bookId, lang);
        if (jobId == null)
            return null;

        var present = _progress.TryGet(jobId.Value, out var snapshot);
        if (!present || snapshot == null || IsTerminal(snapshot.Status))
        {
            // The build is finished/unknown but the registry entry lingered → self-heal and do not
            // advertise a dead job as in-progress.
            _buildRegistry.Complete(bookId, lang);
            return null;
        }

        return jobId;
    }

    /// <summary>True when a progress status is terminal (the job is no longer running).</summary>
    private static bool IsTerminal(AnalysisProgressStatus status) =>
        status is AnalysisProgressStatus.Succeeded
            or AnalysisProgressStatus.Failed
            or AnalysisProgressStatus.Canceled;

    /// <summary>
    /// Build (or refresh) the cached book-wide style baseline for (bookId, language).
    /// Enumerates the book's chapters, (re)builds only stale/missing chapter profiles via the idempotent
    /// read-or-build primitive (limited parallelism), then computes a1's per-metric average and persists
    /// it to <see cref="BookStyleBaseline"/>. Per-chapter failures are caught so one bad chapter never
    /// aborts the job. Reports per-chapter progress through <paramref name="jobId"/> when supplied.
    ///
    /// IDEMPOTENT: when nothing is missing/stale and a baseline already exists, this is a no-op that
    /// returns ready without any LLM call.
    /// </summary>
    public async Task<BookStyleBaselineBuildResult> BuildBookStyleBaselineAsync(
        Guid bookId,
        string language,
        Guid? jobId = null,
        CancellationToken ct = default)
    {
        var lang = NormalizeLanguage(language);

        // p3-3: resolve the book's tier ONCE for the whole build, and THREAD that one value as an explicit
        // argument to every site that needs it - the pre-status, the per-chapter rebuilds, the book average,
        // the post-status and the BookStyleBaseline stamp - so all five agree on which model is active.
        //
        // be-c02: two of those five USED to re-read Book.AiTier for themselves
        // (LoadOrBuildChapterStyleProfileAsync and BuildBookStyleAverageProfileAsync), which made the
        // "resolve once" claim false at exactly the two sites where a mid-build flip does damage. Both now
        // take the resolved tier; their `tier` parameter defaults to null (= resolve from the DB) only for
        // single-shot callers that have none. Pinned by
        // AiTierStalenessTests.ATierFlipMidBuild_StampsEveryChapterUnderOneModel_AndStillPersistsTheBaseline.
        var tier = await ResolveBookTierAsync(bookId, ct);

        var chapters = await _db.Chapters
            .AsNoTracking()
            .Where(c => c.BookId == bookId)
            .OrderBy(c => c.Order)
            .Select(c => c.Id)
            .ToListAsync(ct);

        var existingBaseline = await _db.BookStyleBaselines
            .FirstOrDefaultAsync(b => b.BookId == bookId && b.Language == lang, ct);

        // Pre-build coverage check (same staleness predicate as GetStatusAsync) to decide idempotency.
        var preStatus = await GetStatusAsync(bookId, lang, tier, ct);

        if (jobId.HasValue)
        {
            // Intentional second StartJob: refreshes the "Queued…" placeholder the controller
            // pre-registered (to prevent an immediate-poll 404) with the real chapter-count message.
            _progress.StartJob(jobId.Value, AnalysisScope.Book, AnalysisType.LinguisticAnalysis,
                bookId, null, null, "Starting style baseline build…");
            _progress.SetTotalChunks(jobId.Value, chapters.Count, $"Building style baseline for {chapters.Count} chapters");
        }

        // IDEMPOTENT no-op: every chapter already fresh, a usable cached average exists, AND that cached
        // average was itself built under the ACTIVE model. The chapter freshness count (StaleCount)
        // does NOT cover the persisted BookStyleBaseline row's own model: a baseline built under a
        // DIFFERENT model is out of date even when every chapter profile matches the active model
        // (GetStatusAsync flags this via BuiltWithDifferentModel). Skipping the rebuild there would leave
        // the cross-model average persisted forever, so we fall through to recompute + restamp it under
        // the active model. No LLM call only when all three conditions hold.
        if (preStatus.StaleCount == 0 && preStatus.HasBaseline && !preStatus.BuiltWithDifferentModel)
        {
            if (jobId.HasValue)
                _progress.SetStatus(jobId.Value, AnalysisProgressStatus.Succeeded,
                    "Style baseline already up to date.");

            return new BookStyleBaselineBuildResult
            {
                Ready = true,
                NoOp = true,
                BuiltChapters = preStatus.BuiltChapters,
                TotalChapters = preStatus.TotalChapters,
                FailedChapters = 0,
                Message = "Style baseline already up to date."
            };
        }

        // DEF-2: register this REAL (non-no-op) build as in-progress so a reload/second tab can reattach
        // (GetStatusAsync surfaces it as ActiveBuildJobId). Only the async job path carries a jobId; a
        // jobId-less synchronous call is not reattachable, so it is not registered. The whole build body is
        // wrapped in try/finally so a crash still clears the registration. Registered here (after the
        // no-op early return) so a no-op does not register a build that never runs.
        var registered = jobId.HasValue && _buildRegistry.TryStart(bookId, lang, jobId.Value);

        // be-c02: dedup the residual controller-guard race. TryStart is the atomic compare-and-set that
        // truly enforces "one active build per (bookId, language)". If this is the async job path
        // (jobId.HasValue) but TryStart lost the race to a concurrent build already holding the slot,
        // BAIL instead of running RunBuildAsync — otherwise the loser re-issues the same paid
        // LinguisticAnalysis LLM calls. NOTE the jobId == null synchronous path is never registered and
        // must STILL run (it is the un-reattachable direct/inline build path), so this bail is gated on
        // jobId.HasValue AND !registered, never on !registered alone.
        if (jobId.HasValue && !registered)
        {
            _logger.LogInformation(
                "Style baseline build {JobId} for book {BookId} ({Lang}) skipped: a build is already in progress.",
                jobId, bookId, lang);
            // be-c03: drive this bailed (loser) jobId to a TERMINAL status so the FE progress poll
            // resolves instead of hanging in "BUILDING" forever. The controller + this service both
            // StartJob'd it, leaving it Pending/Running; without a terminal status the losing tab
            // polls a jobId that never completes. Use Canceled (NOT Succeeded — Succeeded would flash a
            // false "done/ready"). The FE's pollStyleBaselineBuild treats succeeded/failed/canceled
            // identically (building=false, mark jobId handled, then loadStyleBaselineStatus reattaches
            // to the winner's still-running activeBuildJobId), so Canceling the loser cleanly hands the
            // losing tab over to the winning build's progress.
            _progress.SetStatus(jobId.Value, AnalysisProgressStatus.Canceled,
                "A style baseline build is already in progress for this book; reattaching.");
            // This jobId never owned the slot, so do NOT clear it here (the active build owns cleanup).
            return new BookStyleBaselineBuildResult
            {
                Ready = preStatus.HasBaseline,
                NoOp = true,
                BuiltChapters = preStatus.BuiltChapters,
                TotalChapters = preStatus.TotalChapters,
                FailedChapters = preStatus.StaleCount,
                Message = "A style baseline build is already in progress for this book and language."
            };
        }

        try
        {
            return await RunBuildAsync(bookId, lang, tier, jobId, chapters, existingBaseline, ct);
        }
        finally
        {
            if (registered)
                _buildRegistry.Complete(bookId, lang);
        }
    }

    /// <summary>
    /// The real (non-no-op) build body, extracted so <see cref="BuildBookStyleBaselineAsync"/> can wrap it
    /// in a try/finally that clears the in-progress registry even on a crash (DEF-2).
    /// </summary>
    private async Task<BookStyleBaselineBuildResult> RunBuildAsync(
        Guid bookId,
        string lang,
        AiTier tier,
        Guid? jobId,
        List<Guid> chapters,
        BookStyleBaseline? existingBaseline,
        CancellationToken ct)
    {
        // (Re)build every chapter profile with LIMITED parallelism. LoadOrBuildChapterStyleProfileAsync
        // is idempotent: a fresh chapter returns its cached row (no LLM call); only missing/stale
        // chapters incur a model call. Mirrors the proofread chunk-parallelism cap idiom (SemaphoreSlim),
        // default cap 2. Each chapter build runs on its OWN DI scope (fresh DbContext + scoped
        // AnalysisContextService) because EF Core's DbContext is not thread-safe and these run
        // concurrently; the controller already runs the whole job on a background DI scope.
        var maxParallel = Math.Max(1, _aiOptions.Value.MaxParallelStyleBaselineChapters);
        var semaphore = new SemaphoreSlim(maxParallel, maxParallel);
        var completed = 0;
        var totalForProgress = Math.Max(chapters.Count, 1);

        async Task ProcessChapter(int index)
        {
            var chapterId = chapters[index];
            await semaphore.WaitAsync(ct);
            try
            {
                var chapterNumber = index + 1;
                if (jobId.HasValue)
                    _progress.ChunkStarted(jobId.Value, chapterNumber, totalForProgress);

                try
                {
                    // Own DI scope per chapter → own DbContext + scoped IAnalysisContextService, so the
                    // bounded-parallel builds never share a non-thread-safe DbContext.
                    // LoadOrBuildChapterStyleProfileAsync already degrades GRACEFULLY to null on any
                    // per-chapter LLM/parse failure (it does not throw), so a single bad chapter never
                    // aborts the whole job - it simply stays stale and is reflected in the final status
                    // (see FailedChapters derived from postStatus below). The try/catch here is defence
                    // against an unexpected throw escaping that contract; we still log + continue.
                    // be-c02: hand it the tier this build resolved. Left to resolve for itself it would
                    // read Book.AiTier once PER CHAPTER (41 extra queries on a 40-chapter book) and, worse,
                    // a flip landing mid-build would route later chapters to a different model than earlier
                    // ones - two BuiltWithModel values for one build, which the average below then cannot
                    // reconcile.
                    using var scope = _scopeFactory.CreateScope();
                    var chapterContextService = scope.ServiceProvider.GetRequiredService<IAnalysisContextService>();
                    await chapterContextService.LoadOrBuildChapterStyleProfileAsync(bookId, chapterId, lang, ct, tier);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Style baseline: chapter {ChapterId} of book {BookId} threw during build; skipping",
                        chapterId, bookId);
                }

                if (jobId.HasValue)
                {
                    var done = Interlocked.Increment(ref completed);
                    _progress.ChunkCompleted(jobId.Value, done, totalForProgress);
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        var tasks = new List<Task>(chapters.Count);
        for (var i = 0; i < chapters.Count; i++)
            tasks.Add(ProcessChapter(i));
        await Task.WhenAll(tasks);

        ct.ThrowIfCancellationRequested();

        // Recompute the average over the (now-refreshed) chapter profiles via a1's method, then persist.
        // be-c02: at the SAME tier the chapters were just built under. This aggregator never rebuilds - it
        // EXCLUDES rows whose BuiltWithModel is not the active model - so resolving the tier again here
        // would, after a mid-build flip, drop every profile this build just wrote and return null. That
        // null short-circuits the persist below, so the build costs its full time (and, on the thinking
        // tier, real money) and stores nothing, with no exception and no log to say why. p3-3 section 3
        // named this the riskiest of the three active-model consumers for exactly that reason.
        var average = await _contextService.BuildBookStyleAverageProfileAsync(bookId, lang, ct, tier);

        // How many chapters now have a fresh profile (post-build truth, reused predicate, SAME tier the
        // build ran under - re-reading it here could observe a tier flipped mid-build and report every
        // just-built profile as stale).
        var postStatus = await GetStatusAsync(bookId, lang, tier, ct);

        // FailedChapters = chapters that did NOT end up with a fresh profile after the build attempt.
        // Derived from the reused staleness predicate so it captures BOTH thrown failures AND the
        // graceful-null path (LoadOrBuildChapterStyleProfileAsync returns null without throwing on an
        // LLM/parse failure or empty content). This is the honest, single-source count.
        var failedChapters = postStatus.StaleCount;

        if (average == null || string.IsNullOrWhiteSpace(average.MetricsJson))
        {
            // Fewer than two usable chapter profiles → no meaningful book average. We do NOT clear an
            // existing baseline (it may still be a useful older snapshot); we just report not-ready.
            var msg = "Not enough chapter profiles to build a book style baseline (need at least two).";
            if (jobId.HasValue)
                _progress.SetStatus(jobId.Value, AnalysisProgressStatus.Succeeded, msg);

            return new BookStyleBaselineBuildResult
            {
                Ready = existingBaseline != null && !string.IsNullOrWhiteSpace(existingBaseline.MetricsJson),
                NoOp = false,
                BuiltChapters = postStatus.BuiltChapters,
                TotalChapters = postStatus.TotalChapters,
                FailedChapters = failedChapters,
                Message = msg
            };
        }

        var builtWithModel = ResolveLinguisticModelId(tier);
        var metricsJson = average.MetricsJson;

        if (existingBaseline == null)
        {
            existingBaseline = new BookStyleBaseline
            {
                BookId = bookId,
                Language = lang,
                MetricsJson = metricsJson,
                BuiltChapterCount = postStatus.BuiltChapters,
                BuiltWithModel = builtWithModel
                // CreatedAt/UpdatedAt stamped by the SaveChanges override.
            };
            _db.BookStyleBaselines.Add(existingBaseline);
        }
        else
        {
            existingBaseline.MetricsJson = metricsJson;
            existingBaseline.BuiltChapterCount = postStatus.BuiltChapters;
            existingBaseline.BuiltWithModel = builtWithModel;
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // A concurrent build may have inserted the unique (BookId, Language) row first. Detach so the
            // failed insert is not retried, then re-read the winning row.
            _logger.LogWarning(ex, "Failed to persist BookStyleBaseline for book {BookId}; reloading", bookId);
            _db.Entry(existingBaseline).State = EntityState.Detached;
            existingBaseline = await _db.BookStyleBaselines
                .FirstOrDefaultAsync(b => b.BookId == bookId && b.Language == lang, ct);
        }

        var ready = existingBaseline != null && !string.IsNullOrWhiteSpace(existingBaseline.MetricsJson);
        var successMsg = failedChapters > 0
            ? $"Style baseline built from {postStatus.BuiltChapters}/{postStatus.TotalChapters} chapters ({failedChapters} failed)."
            : $"Style baseline built from {postStatus.BuiltChapters}/{postStatus.TotalChapters} chapters.";

        if (jobId.HasValue)
            _progress.SetStatus(jobId.Value, AnalysisProgressStatus.Succeeded, successMsg);

        return new BookStyleBaselineBuildResult
        {
            Ready = ready,
            NoOp = false,
            BuiltChapters = postStatus.BuiltChapters,
            TotalChapters = postStatus.TotalChapters,
            FailedChapters = failedChapters,
            Message = successMsg
        };
    }

    /// <summary>
    /// Resolves the (provider, model) pair for LinguisticAnalysis, using the SAME predicate as
    /// AiRouter.ResolveSelection (line 104-105): the FeatureModels["LinguisticAnalysis"] entry is only
    /// applied when BOTH Provider AND Model are non-empty. A half-configured entry (only one set) falls
    /// back to DefaultProvider / DefaultModel, exactly as the router does, so BuiltWithModel and the
    /// cost estimate cannot diverge from the model the request is actually routed to.
    /// Delegates to <see cref="LinguisticModelResolver"/> so this and AnalysisContextService share one
    /// definition (cross-model staleness must compare against the SAME active-model resolution).
    /// <para>
    /// TIER-AWARE since p3-3. <paramref name="tier"/> is the BOOK's tier, and it is the mechanism by which a
    /// tier change invalidates a baseline: the value returned here is both what
    /// <see cref="BookStyleBaseline.BuiltWithModel"/> is STAMPED with on a build and what the status gate
    /// COMPARES a stored stamp against, so flipping a book's tier makes its persisted baseline read
    /// <c>BuiltWithDifferentModel</c> and its chapter profiles read stale - for that book only. It defaults
    /// to <see cref="AiTier.Fast"/>, which resolves byte-identically to the pre-tier behaviour, so the tests
    /// that call this directly are unaffected.
    /// </para>
    /// </summary>
    internal (string provider, string? model) ResolveLinguisticProviderAndModel(AiTier tier = AiTier.Fast)
        => LinguisticModelResolver.ResolveForTask(_aiOptions.Value, AiTaskType.LinguisticAnalysis, tier);

    private string? ResolveLinguisticModelId(AiTier tier) => ResolveLinguisticProviderAndModel(tier).model;

    /// <summary>
    /// This book's model tier FOR LINGUISTICANALYSIS, through the shared <see cref="BookAiTierResolver"/> so
    /// this service, <see cref="AnalysisContextService"/> and <c>UnifiedAnalysisService</c> cannot disagree
    /// about which tier a book is on. Fail-safe to <see cref="AiTier.Fast"/> on anything unknown.
    /// <para>
    /// tier-ux-rework c1: the task is LinguisticAnalysis and not a parameter, because every use of this value
    /// in this service resolves the LinguisticAnalysis model - it is what
    /// <see cref="BookStyleBaseline.BuiltWithModel"/> is stamped with and what the status gate compares a
    /// stored stamp against. Asking about any other task would gate baselines on a tier that never built them.
    /// </para>
    /// </summary>
    private Task<AiTier> ResolveBookTierAsync(Guid bookId, CancellationToken ct)
        => BookAiTierResolver.ResolveAsync(_db, bookId, AiTaskType.LinguisticAnalysis, _logger, ct);

    // ─── Build estimate helpers (a4) ─────────────────────────────────────────────────────────────

    // Coarse heuristic constants — intentionally rough; revise as production timings improve.
    // One LinguisticAnalysis call on a typical chapter (~800 Hebrew words) on local Ollama gemma4:12b.
    /// <summary>Approximate seconds per chapter build (single chapter, serial). Used with parallelism
    /// cap to estimate wall-clock seconds.</summary>
    internal const int ApproxSecondsPerChapter = 45;

    /// <summary>Approximate input+output tokens consumed per chapter LinguisticAnalysis call.
    /// Feeds the paid-provider USD estimate.</summary>
    internal const int ApproxTokensPerChapter = 3_000;

    // Coarse per-1k-token USD rates keyed by provider name (lower-case). NOT secrets; not prod
    // identifiers — just ballpark public pricing for a rough consent-screen estimate.
    // OpenRouter bills per token; $0.0015/1k is representative of mid-tier open-weight models.
    private static readonly Dictionary<string, decimal> ProviderRatePerKToken = new(StringComparer.OrdinalIgnoreCase)
    {
        ["openrouter"] = 0.0015m,
        ["openai"]     = 0.005m,
        ["anthropic"]  = 0.008m,
        ["azure"]      = 0.005m,
    };

    // Local / free providers whose USD cost is null.
    private static readonly HashSet<string> LocalProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "ollama",
    };

    /// <summary>
    /// Computes a coarse build estimate given the number of chapters to build, the active provider name,
    /// and the parallelism cap. Pure/side-effect-free so unit tests call it directly.
    /// <para>
    /// Wall-clock formula: ceil(chaptersToBuild / maxParallel) * ApproxSecondsPerChapter.
    /// This accounts for the SemaphoreSlim-capped concurrency in BuildBookStyleBaselineAsync; a higher
    /// parallelism cap yields a proportionally lower estimate.
    /// </para>
    /// <para>
    /// USD formula (paid providers only): chaptersToBuild * ApproxTokensPerChapter / 1000 * ratePerKToken.
    /// Returns null for local providers (Ollama) or unknown providers (safe default = free).
    /// </para>
    /// </summary>
    internal static (int estimatedSeconds, decimal? estimatedUsd) ComputeEstimate(
        int chaptersToBuild,
        string providerName,
        int maxParallel)
    {
        if (chaptersToBuild <= 0)
            return (0, null);

        var cap = Math.Max(1, maxParallel);
        var waves = (int)Math.Ceiling((double)chaptersToBuild / cap);
        var seconds = waves * ApproxSecondsPerChapter;

        decimal? usd = null;
        if (!LocalProviders.Contains(providerName)
            && ProviderRatePerKToken.TryGetValue(providerName, out var rate))
        {
            usd = chaptersToBuild * (decimal)ApproxTokensPerChapter / 1000m * rate;
        }

        return (seconds, usd);
    }

    // Canonical cache-key normalization, shared with the inline LinguisticAnalysis path and the controller
    // so status/build/profile all key the baseline identically (e.g. "en-US" → "en"). Callers already pass
    // a normalized value today; delegating keeps that guarantee defensively in one place.
    private static string NormalizeLanguage(string language) =>
        BaselineLanguageResolver.Normalize(language);
}
