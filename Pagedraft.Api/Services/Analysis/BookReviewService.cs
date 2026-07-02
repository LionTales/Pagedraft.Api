using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// Read-only coverage status for a book's cached whole-book review (BookFinding rows). Surfaced by the
/// GET status endpoint (wired by wb2-c03) so the FE can decide whether a (re)build is needed and render
/// progress. MIRRORS <see cref="BookSummaryStatus"/> so the FE reuses the same status/progress UI shape.
/// </summary>
public sealed class BookReviewStatus
{
    public Guid BookId { get; init; }
    public string Language { get; init; } = "he";

    /// <summary>True when at least one BookFinding row exists for (BookId, Language).</summary>
    public bool HasReview { get; init; }

    /// <summary>Total persisted findings for (BookId, Language).</summary>
    public int FindingCount { get; init; }

    /// <summary>When the review was last (re)built (max UpdatedAt over the findings); null when none.</summary>
    public DateTimeOffset? LastUpdatedAt { get; init; }

    /// <summary>BookReview model id that produced the cached findings; null when none exist.</summary>
    public string? BuiltWithModel { get; init; }

    /// <summary>The resolved active BookReview model id (same resolution AiRouter uses). Surfaced so the FE
    /// can warn the review was built with a different model and show which model is now active.</summary>
    public string? ActiveModel { get; init; }

    /// <summary>True when cached findings exist AND their model differs from <see cref="ActiveModel"/>.</summary>
    public bool BuiltWithDifferentModel { get; init; }

    /// <summary>
    /// True when the review is STALE versus the briefs it was built over: the book summary (the L2 BookBrief
    /// + L1 ChapterBriefs the review reads) has been (re)built MORE RECENTLY than the newest finding, so the
    /// review reflects an older view of the book. Computed by comparing the latest finding UpdatedAt to the
    /// cached BookSummaryBaseline.UpdatedAt for (BookId, Language). False when no review or no summary.
    /// </summary>
    public bool StaleVsBriefs { get; init; }

    /// <summary>True when the book has usable structured briefs to review at all (BookBrief or ChapterBriefs).
    /// When false, a build cannot produce findings and the FE should prompt to build the summary first.</summary>
    public bool HasBriefs { get; init; }

    // ─── Coverage provenance (wb4-c06) ────────────────────────────────────────────────────────────────
    // Honest end-to-end coverage of the multi-pass (windowed MAP + reduce passes) build, so the FE can show
    // "N/N chapters" rather than trusting a silent-truncation-prone single call. The STATUS probe derives
    // what persisted state allows (ChaptersTotal from the book's chapter count; ChaptersReviewed == N/N once a
    // review exists, since the windowed build covers every chapter as a primary); the build-time-only shape
    // (WindowCount / RanSynthesis / RanContinuityReduce / FailedWindows) is known only during a build, so the
    // status probe reports those as 0/false and the BUILD RESULT carries the precise counts.

    /// <summary>Distinct PRIMARY chapters covered by the cached review. On the status probe this equals
    /// <see cref="ChaptersTotal"/> when a review exists (the windowed build covers every chapter as a
    /// primary) and 0 otherwise.</summary>
    public int ChaptersReviewed { get; init; }

    /// <summary>Total chapters in the book (the coverage denominator). Derived from the book's chapter count.</summary>
    public int ChaptersTotal { get; init; }

    /// <summary>Number of windows the last build mapped over. Known only at build time; 0 on the status probe.</summary>
    public int WindowCount { get; init; }

    /// <summary>True when the synthesis reduce pass ran in the last build. Known only at build time; false on
    /// the status probe. Note: this flag means the pass was ATTEMPTED (a full BookBrief existed), not that it
    /// produced findings -- a synthesis call that errored or returned empty still reports RanSynthesis=true.</summary>
    public bool RanSynthesis { get; init; }

    /// <summary>True when the continuity reduce pass ran in the last build. Known only at build time; false on
    /// the status probe. Note: this flag means the pass was ATTEMPTED (the deterministic continuity plan was
    /// non-null), not that it produced findings -- an errored or empty continuity call still reports true.</summary>
    public bool RanContinuityReduce { get; init; }

    /// <summary>Windows that failed to produce findings in the last build. Known only at build time; 0 on the
    /// status probe.</summary>
    public int FailedWindows { get; init; }

    /// <summary>The jobId of an in-progress review build for (BookId, Language), or null when none.</summary>
    public Guid? ActiveBuildJobId { get; init; }

    /// <summary>
    /// True when a build would be a genuine no-op: usable structured briefs exist (<see cref="HasBriefs"/>), a
    /// usable review exists (<see cref="HasReview"/>), it was built under the ACTIVE model, and it is not stale
    /// versus the briefs. Mirrors the no-op gate in <see cref="BookReviewService.BuildBookReviewAsync"/> exactly.
    ///
    /// HasBriefs is REQUIRED so this never reports ready while the briefs are gone or degraded: in that state a
    /// build would NOT be a no-op — it would hit the briefs-absent guard and return BriefsMissing — so a caller
    /// that trusts IsReady (or the DTO's `ready`) must not treat the cached review as current when
    /// <see cref="HasBriefs"/> is false.
    /// </summary>
    public bool IsReady => HasBriefs && HasReview && !BuiltWithDifferentModel && !StaleVsBriefs;
}

/// <summary>Result of a whole-book review build job.</summary>
public sealed class BookReviewBuildResult
{
    /// <summary>True when the build (or a no-op fresh build) ended with a usable cached review. FALSE on a
    /// TOTAL failure (every dimension failed to produce findings): no fresh review was produced, the job is
    /// surfaced as Failed, and the FE must not treat it as a successful (re)build (wb2-c05).</summary>
    public bool Ready { get; init; }

    /// <summary>True when nothing needed (re)building — the review was already fresh.</summary>
    public bool NoOp { get; init; }

    /// <summary>
    /// True when the build could NOT run because the book has no usable structured briefs yet (the book
    /// summary has not been built). NO model calls are spent; the controller turns this into "build the
    /// book summary first" guidance.
    /// </summary>
    public bool BriefsMissing { get; init; }

    /// <summary>Findings persisted after this build (the full current set for the book+language).</summary>
    public int FindingCount { get; init; }

    /// <summary>Units that failed to parse / errored and contributed ZERO findings — WINDOWS on the default
    /// windowed path, or dimensions on the legacy per-dimension path. The job is never aborted by these; the
    /// other units still persist. RETAINED for back-compat (wb2-c05 reads it); on the windowed path it equals
    /// <see cref="FailedWindows"/>. New code should prefer the clearly-named window/pass fields below.</summary>
    public int FailedDimensions { get; init; }

    // ─── Coverage provenance (wb4-c06) ────────────────────────────────────────────────────────────────
    // The precise multi-pass shape of THIS build, threaded through so the status contract can be HONEST about
    // coverage (closes the silent-truncation gap: the FE shows N/N chapters across the actual window count
    // instead of trusting a single call that may have been truncated).

    /// <summary>Distinct PRIMARY chapters covered across all windows this build (union of each window's
    /// IncludedChapterOrders minus its OverlapChapterOrders). With windowing this equals
    /// <see cref="ChaptersTotal"/> — that equality is the honest-coverage claim.</summary>
    public int ChaptersReviewed { get; init; }

    /// <summary>Total chapters in the book (the coverage denominator: the composed ChapterBriefs count).</summary>
    public int ChaptersTotal { get; init; }

    /// <summary>Number of windows this build mapped over (the windowed MAP fan-out; 1 for a small book).</summary>
    public int WindowCount { get; init; }

    /// <summary>True when the wb4-c04 synthesis reduce pass executed (a full BookBrief existed). Means the pass
    /// was ATTEMPTED, not that it produced findings -- a synthesis call that errored or returned empty still
    /// reports RanSynthesis=true.</summary>
    public bool RanSynthesis { get; init; }

    /// <summary>True when the wb4-c05 continuity reduce pass executed (a full BookBrief AND ordered chapter
    /// briefs existed). Means the pass was ATTEMPTED (the deterministic continuity plan was non-null), not that
    /// it produced findings -- an errored or empty continuity call still reports RanContinuityReduce=true.</summary>
    public bool RanContinuityReduce { get; init; }

    /// <summary>Windows that failed to produce findings (model error / unparseable). Never aborts the build;
    /// equals <see cref="FailedDimensions"/> on the windowed path (kept for the clearer name).</summary>
    public int FailedWindows { get; init; }

    public string Message { get; init; } = string.Empty;
}

/// <summary>Per-dimension rollup score for the FE, mirroring <see cref="DimensionScore"/> (DTO-ready).</summary>
public sealed class BookReviewDimensionScore
{
    public string Dimension { get; init; } = string.Empty;
    public string Score { get; init; } = "mixed";
    public int KeepCount { get; init; }
    public int ImproveCount { get; init; }
    public int CutCount { get; init; }
}

/// <summary>The findings + per-dimension scores read surface (wb2-c03 wraps this in a REST DTO).</summary>
public sealed class BookReviewFindings
{
    public Guid BookId { get; init; }
    public string Language { get; init; } = "he";
    public IReadOnlyList<BookFinding> Findings { get; init; } = Array.Empty<BookFinding>();
    public IReadOnlyList<BookReviewDimensionScore> Scores { get; init; } = Array.Empty<BookReviewDimensionScore>();
}

/// <summary>
/// Whole-book developmental REVIEW orchestrator (wb2-c02, fan-out strategy wb2-r02). Assembles the budgeted
/// book context ONCE via <see cref="BookContextAssembler"/>, then produces findings via one of two
/// strategies selected by <see cref="AiOptions.BookReviewSingleCombined"/>: DEFAULT (true) runs ONE combined
/// call (<see cref="PromptFactory.BuildBookReviewCombinedPrompt"/>) reviewing all six dimensions in a single
/// pass (cheaper, a measured quality TIE per wb2-c04, and the only path that survives the 8 GB dev GPU on
/// big books per wb2-c06); the FALLBACK (false) fans out the six per-dimension review prompts
/// (<see cref="PromptFactory.BuildBookReviewPrompt"/>) through <see cref="IAiRouter"/> with a parallel cap,
/// kept reversible for a future larger-GPU re-measure. Either way it parses the findings, unions + dedups
/// them, rolls up per-dimension scores, and persists <see cref="BookFinding"/> rows PRESERVING any user-set
/// Status across rebuilds.
///
/// REUSE: this is the REVIEW sibling of <see cref="BookSummaryService"/> and shares its async-job +
/// progress-polling contract verbatim — a consented async build job (dedup via
/// <see cref="BookReviewBuildRegistry"/>, idempotent skip-when-fresh), per-dimension progress through
/// <see cref="AnalysisProgressTracker"/>, limited parallelism, and a status DTO carrying the model id — so
/// the FE reuses the same progress UI. It does NOT fork a divergent job pattern.
///
/// SEAM with wb2-c03: c03 owns AnalysisType.BookReview, the AnalysisTaskMapping entry mapping it to
/// AiTaskType.BookReview, the Ai:FeatureModels:BookReview appsettings key (+ breadcrumb), and the REST
/// endpoints. This service only adds + tags AiTaskType.BookReview on its AiRequest so the router resolves
/// that (future) key and the call is correctly labelled/capped. Until c03 sets the key, routing falls back
/// to the default model — fine (no real model call happens in c02's tests; they mock IAiRouter).
/// </summary>
public class BookReviewService
{
    /// <summary>The six editorial dimensions the review fans out over. Order is stable for deterministic
    /// progress reporting and dedup-iteration; the model is instructed to stamp each finding's dimension.</summary>
    private static readonly string[] Dimensions =
        { "plot", "character", "pacing", "tone", "theme", "continuity" };

    private static readonly JsonSerializerOptions DeserializeOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions SerializeOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly AppDbContext _db;
    private readonly BookContextAssembler _contextAssembler;
    private readonly PromptFactory _promptFactory;
    private readonly IAiRouter _router;
    private readonly AnalysisProgressTracker _progress;
    private readonly BookReviewBuildRegistry _buildRegistry;
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly ILogger<BookReviewService> _logger;

    public BookReviewService(
        AppDbContext db,
        BookContextAssembler contextAssembler,
        PromptFactory promptFactory,
        IAiRouter router,
        AnalysisProgressTracker progress,
        BookReviewBuildRegistry buildRegistry,
        IOptions<AiOptions> aiOptions,
        ILogger<BookReviewService> logger)
    {
        _db = db;
        _contextAssembler = contextAssembler;
        _promptFactory = promptFactory;
        _router = router;
        _progress = progress;
        _buildRegistry = buildRegistry;
        _aiOptions = aiOptions;
        _logger = logger;
    }

    /// <summary>The resolved active BookReview model id — the cross-model staleness target and the row's
    /// BuiltWithModel stamp. Until wb2-c03 sets Ai:FeatureModels:BookReview this resolves to the default
    /// model (same fallback the router applies).</summary>
    internal string? ActiveBookReviewModel =>
        LinguisticModelResolver.ResolveModelForTask(_aiOptions.Value, AiTaskType.BookReview);

    // ─── Status ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Read-only review status for (bookId, language): coverage (HasReview/FindingCount), lastUpdatedAt,
    /// the cross-model warning, and staleness versus the briefs the review was built over (the book summary
    /// rebuilt more recently than the newest finding). Surfaces the in-progress build jobId and whether the
    /// book has any usable briefs to review at all.
    /// </summary>
    public async Task<BookReviewStatus> GetStatusAsync(
        Guid bookId,
        string language,
        CancellationToken ct = default)
    {
        var lang = BaselineLanguageResolver.Normalize(language);

        var findings = await _db.BookFindings
            .AsNoTracking()
            .Where(f => f.BookId == bookId && f.Language == lang)
            .Select(f => new { f.UpdatedAt, f.BuiltWithModel })
            .ToListAsync(ct);

        var hasReview = findings.Count > 0;
        var lastUpdatedAt = hasReview ? findings.Max(f => f.UpdatedAt) : (DateTimeOffset?)null;
        // The model that built the review: the most recent finding's stamp (all rows in one build share it).
        var builtWithModel = hasReview
            ? findings.OrderByDescending(f => f.UpdatedAt).First().BuiltWithModel
            : null;

        var activeModel = ActiveBookReviewModel;

        // Staleness vs briefs: the cached book summary (the briefs the review reads) was (re)built after the
        // newest finding → the review reflects an older view of the book.
        var summary = await _db.BookSummaryBaselines
            .AsNoTracking()
            .Where(b => b.BookId == bookId && b.Language == lang)
            .Select(b => new { b.UpdatedAt, b.BookBriefJson })
            .FirstOrDefaultAsync(ct);

        var staleVsBriefs = hasReview
            && summary != null
            && lastUpdatedAt is { } lastFinding
            && summary.UpdatedAt > lastFinding;

        // Does the book have usable structured briefs to review at all? Cheap composition probe (no LLM).
        var hasBriefs = await HasUsableBriefsAsync(bookId, lang, ct);

        // data-c01 HONEST coverage the STATUS probe reads from PERSISTED state so a reload stays honest (never
        // claims N/N after a partial/degraded build):
        //   • When a BookReviewCoverage row exists for (bookId, lang), report its persisted ChaptersReviewed +
        //     ChaptersTotal — the exact honest counts the last persisting build computed (be-c01). A partial
        //     build persists reviewed < total, so the probe now reports the SAME reviewed < total, not N/N.
        //   • When NO row exists (a review built before data-c01, or no review yet), fall back to
        //     (0, reviewable-chapter count) — old reviews keep working and the probe never crashes. The
        //     denominator is the REVIEWABLE (non-empty) chapter count, NOT the raw Chapters row count: a windowed
        //     build persists ChaptersTotal as the distinct reviewable primaries only (a chapter with no brief,
        //     summary, or text never enters a window), so using the raw count here would make the status
        //     denominator JUMP after the first build on any book with empty chapters. CountReviewableChaptersAsync
        //     derives the SAME reviewable set through the assembler's shared per-chapter selection, so the
        //     fallback denominator matches what the first build will persist.
        // The build-time-only shape (WindowCount / RanSynthesis / RanContinuityReduce / FailedWindows) is NOT
        // persisted, so it is left at its default (0/false) here — the precise per-build counts ride on
        // BookReviewBuildResult, not this cached probe.
        var coverage = await _db.BookReviewCoverages
            .AsNoTracking()
            .Where(c => c.BookId == bookId && c.Language == lang)
            .Select(c => new { c.ChaptersReviewed, c.ChaptersTotal })
            .FirstOrDefaultAsync(ct);
        int chaptersReviewed;
        int chaptersTotal;
        if (coverage != null)
        {
            chaptersReviewed = coverage.ChaptersReviewed;
            chaptersTotal = coverage.ChaptersTotal;
        }
        else
        {
            chaptersReviewed = 0;
            chaptersTotal = await _contextAssembler.CountReviewableChaptersAsync(bookId, lang, ct);
        }

        return new BookReviewStatus
        {
            BookId = bookId,
            Language = lang,
            HasReview = hasReview,
            FindingCount = findings.Count,
            LastUpdatedAt = lastUpdatedAt,
            BuiltWithModel = builtWithModel,
            ActiveModel = activeModel,
            BuiltWithDifferentModel = hasReview
                && !string.Equals(builtWithModel, activeModel, StringComparison.Ordinal),
            StaleVsBriefs = staleVsBriefs,
            HasBriefs = hasBriefs,
            ChaptersReviewed = chaptersReviewed,
            ChaptersTotal = chaptersTotal,
            ActiveBuildJobId = ResolveActiveBuildJobId(bookId, lang)
        };
    }

    /// <summary>
    /// Resolves the in-progress review build jobId for (bookId, lang), but NEVER reports a finished job.
    /// Mirrors <see cref="BookSummaryService.ResolveActiveBuildJobId"/>: a lingering registry entry whose
    /// progress is terminal/unknown is self-healed (cleared) and reported as null, so the FE never
    /// reattaches to a dead job.
    /// </summary>
    private Guid? ResolveActiveBuildJobId(Guid bookId, string lang)
    {
        var jobId = _buildRegistry.TryGetActive(bookId, lang);
        if (jobId == null)
            return null;

        var present = _progress.TryGet(jobId.Value, out var snapshot);
        if (!present || snapshot == null || IsTerminal(snapshot.Status))
        {
            _buildRegistry.Complete(bookId, lang);
            return null;
        }

        return jobId;
    }

    private static bool IsTerminal(AnalysisProgressStatus status) =>
        status is AnalysisProgressStatus.Succeeded
            or AnalysisProgressStatus.Failed
            or AnalysisProgressStatus.Canceled;

    // ─── Read findings ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the persisted findings for (bookId, language) ordered by severity desc then dimension, and
    /// rolls up the per-dimension keep/improve/cut scores from those rows (so the read surface is consistent
    /// with what was persisted, not a re-derived model field). DTO-ready for wb2-c03 to wrap.
    /// </summary>
    public async Task<BookReviewFindings> GetFindingsAsync(
        Guid bookId,
        string language,
        CancellationToken ct = default)
    {
        var lang = BaselineLanguageResolver.Normalize(language);

        var findings = await _db.BookFindings
            .AsNoTracking()
            .Where(f => f.BookId == bookId && f.Language == lang)
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Dimension)
            .ToListAsync(ct);

        var scores = RollUpScoresFromFindings(findings);

        return new BookReviewFindings
        {
            BookId = bookId,
            Language = lang,
            Findings = findings,
            Scores = scores
        };
    }

    // ─── Build ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build (or refresh) the whole-book review for (bookId, language). Assembles the budgeted book context
    /// ONCE, GUARDS on briefs being present (no model calls otherwise), fans out the six per-dimension
    /// prompts with a parallel cap (one bad dimension never aborts the job), unions + dedups the findings,
    /// rolls up scores, and persists the BookFinding rows PRESERVING user-set Status. Reports per-dimension
    /// progress through <paramref name="jobId"/> when supplied.
    ///
    /// IDEMPOTENT: when a usable review exists that was built under the active model AND is not stale versus
    /// the briefs, this is a no-op returning ready with no LLM call.
    /// </summary>
    public async Task<BookReviewBuildResult> BuildBookReviewAsync(
        Guid bookId,
        string language,
        Guid? jobId = null,
        CancellationToken ct = default)
    {
        var lang = BaselineLanguageResolver.Normalize(language);

        var preStatus = await GetStatusAsync(bookId, lang, ct);

        if (jobId.HasValue)
            _progress.StartJob(jobId.Value, AnalysisScope.Book, AnalysisType.BookReview,
                bookId, null, null, "Starting whole-book review…");

        // IDEMPOTENT no-op: a usable review exists, built under the active model, not stale vs briefs.
        if (preStatus.IsReady)
        {
            if (jobId.HasValue)
                _progress.SetStatus(jobId.Value, AnalysisProgressStatus.Succeeded,
                    "Whole-book review already up to date.");

            return new BookReviewBuildResult
            {
                Ready = true,
                NoOp = true,
                FindingCount = preStatus.FindingCount,
                FailedDimensions = 0,
                // wb4-c06: a no-op did not re-run the passes, so the build-time shape (windows / reduce passes /
                // failed windows) is unknown here (0/false); carry the persisted coverage (N/N) from the probe.
                ChaptersReviewed = preStatus.ChaptersReviewed,
                ChaptersTotal = preStatus.ChaptersTotal,
                FailedWindows = 0,
                Message = "Whole-book review already up to date."
            };
        }

        // Register this REAL build so a reload/second tab can reattach. Only the async job path carries a
        // jobId; a jobId-less synchronous call is not reattachable so it is not registered. Wrapped in
        // try/finally so a crash still clears the registration.
        var registered = jobId.HasValue && _buildRegistry.TryStart(bookId, lang, jobId.Value);

        // Dedup the residual controller-guard race: if this is the async path but TryStart lost to a
        // concurrent build already holding the slot, BAIL (do not re-issue the same paid LLM calls) and
        // drive THIS jobId to a terminal status so the losing tab's poll resolves and reattaches to the
        // winner. The jobId == null synchronous path is never registered and must STILL run.
        if (jobId.HasValue && !registered)
        {
            _logger.LogInformation(
                "Book review build {JobId} for book {BookId} ({Lang}) skipped: a build is already in progress.",
                jobId, bookId, lang);
            _progress.SetStatus(jobId.Value, AnalysisProgressStatus.Canceled,
                "A whole-book review build is already in progress for this book; reattaching.");
            return new BookReviewBuildResult
            {
                // Report Ready with the SAME IsReady gate as the intentional no-op above (briefs present, a
                // usable review built under the active model, not stale), NOT HasReview alone: a stale,
                // wrong-model, or briefs-gone review must not surface Ready=true while another build is still
                // running, or callers treat an outdated/un-rebuildable cache as fresh.
                Ready = preStatus.IsReady,
                NoOp = true,
                FindingCount = preStatus.FindingCount,
                Message = "A whole-book review build is already in progress for this book and language."
            };
        }

        try
        {
            return await RunBuildAsync(bookId, lang, jobId, ct);
        }
        finally
        {
            if (registered)
                _buildRegistry.Complete(bookId, lang);
        }
    }

    /// <summary>
    /// The real build body, extracted so <see cref="BuildBookReviewAsync"/> can wrap it in a try/finally
    /// that clears the in-progress registry even on a crash. Assembles the budgeted book context into WINDOWS
    /// (wb4-c01 <see cref="BookContextAssembler.AssembleWindowsAsync"/>) then, on the DEFAULT single-combined
    /// path (<see cref="AiOptions.BookReviewSingleCombined"/> true), runs the WINDOWED MAP: each window is
    /// reviewed SEQUENTIALLY by one combined call (wb4-c03 windowed prompt), every window's findings are
    /// ACCUMULATED in memory, the wb4-c04/c05 reduce passes append to that same list, and the whole set is
    /// unioned/deduped and persisted ONCE (persisting per window would let the delete-vanished-open loop wipe
    /// prior windows). A small book yields a single window (equivalent to the old assemble-once combined call).
    /// The legacy per-dimension fan-out (toggle false) still runs over the whole-book context concatenated
    /// from the windows via <see cref="RunPerDimensionFanOutAsync"/>. Both feed the SAME
    /// union/dedup/persist/rollup/reporting tail.
    /// </summary>
    private async Task<BookReviewBuildResult> RunBuildAsync(
        Guid bookId,
        string lang,
        Guid? jobId,
        CancellationToken ct)
    {
        // 1. Assemble the budgeted book context into WINDOWS (wb4-c01) — one BookContextAssembly per window in
        //    narrative order. A small book yields a single window (equivalent to the old assemble-once); a big
        //    book is partitioned so each window fits the model. Each window carries the trimmed BookBrief +
        //    that window's chapters as READY-to-feed Text, plus its metadata (WindowIndex, IncludedChapterOrders,
        //    OverlapChapterOrders). We MAP each window through the combined review call, ACCUMULATE every
        //    window's findings in memory, then (after the wb4-c04/c05 reduce passes) union/dedup + persist ONCE.
        var windows = await _contextAssembler.AssembleWindowsAsync(
            bookId, lang, consumingTasks: new[] { AiTaskType.BookReview }, ct);

        // 2. BRIEFS-ABSENT GUARD (before spending any model calls). The review reads the dense structured
        //    briefs; producing findings from the degraded flat-text fallback would be unanchored noise. We
        //    gate on whether ANY window has usable structured briefs (each window carries the same BookBrief +
        //    its own included chapter briefs, so if no window passes HasUsableBriefs the whole book has none).
        //    No windows at all (an empty/degenerate book) is likewise briefs-absent. In either case surface a
        //    clear "build the book summary first" outcome and spend NO model calls.
        var briefsAbsent = windows.Count == 0 || !windows.Any(BookContextAssembly.HasUsableBriefs);
        if (briefsAbsent)
        {
            const string guidance = "Build the book summary first; the whole-book review reads the chapter briefs.";
            _logger.LogInformation(
                "Book review build for book {BookId} ({Lang}) skipped: no usable structured briefs across any of " +
                "{WindowCount} window(s).",
                bookId, lang, windows.Count);

            // No review was produced (an unmet precondition: the briefs are gone), so this job must NOT report
            // Succeeded — that lets progress polling show a green finish for a build that produced nothing. Use
            // a benign non-success terminal (Canceled), mirroring the registry-race bail above: the FE then
            // shows no error banner and refreshes to the "needs summary" row that carries this guidance. (The
            // controller guards briefs-missing before starting a job; this path only fires when the briefs
            // vanish AFTER that check, mid-flight.)
            if (jobId.HasValue)
                _progress.SetStatus(jobId.Value, AnalysisProgressStatus.Canceled, guidance);

            var existingCount = await _db.BookFindings.CountAsync(
                f => f.BookId == bookId && f.Language == lang, ct);

            // wb4-c06: briefs are gone → no review was produced this build. Coverage is zero-of-total: the book
            // still has chapters (the denominator) but none were reviewed; the windowed shape never ran.
            var briefsMissingChapterTotal = await _db.Chapters.CountAsync(c => c.BookId == bookId, ct);

            return new BookReviewBuildResult
            {
                Ready = false,
                NoOp = false,
                BriefsMissing = true,
                FindingCount = existingCount,
                FailedDimensions = 0,
                ChaptersReviewed = 0,
                ChaptersTotal = briefsMissingChapterTotal,
                FailedWindows = 0,
                Message = guidance
            };
        }

        // Chapter Order → (Id, Title) for backfilling anchors/evidence chapterId (Phase-3 navigation).
        var chaptersByOrder = await LoadChaptersByOrderAsync(bookId, ct);

        var singleCombined = _aiOptions.Value.BookReviewSingleCombined;
        var builtWithModel = ActiveBookReviewModel;

        // 3. PRODUCE the findings. The DEFAULT path is the windowed MAP (single-combined per window); the
        //    legacy per-dimension fan-out (toggle OFF) operates over the whole-book context concatenated back
        //    from the windows (kept reversible for a future larger-GPU re-measure). Both funnel their findings
        //    into the Dimensions-length per-dimension buckets that UnionAndDedup consumes, so the shared
        //    union/dedup/persist/rollup is IDENTICAL.
        List<BookFindingItem>?[] perDimension;
        int failedUnits;   // failed windows (windowed path) or failed dimensions (per-dimension path)
        int totalUnits;    // windows (windowed path) or dimensions (per-dimension path) — for the warning text

        // wb4-c06 coverage provenance for THIS build, threaded into the returned BookReviewBuildResult so the
        // status contract is HONEST about the multi-pass shape (closes the silent-truncation gap). Populated on
        // the windowed path (the default); the legacy per-dimension path leaves them at the whole-book defaults.
        var chaptersReviewedCount = 0;     // distinct PRIMARY chapter orders SUCCESSFULLY reviewed across windows
        var chaptersTotalCount = 0;        // distinct PRIMARY chapter orders across ALL windows (the reviewable set)
        var windowCountForResult = 0;      // number of windows mapped over
        var ranSynthesis = false;          // synthesis reduce pass executed
        var ranContinuityReduce = false;   // continuity reduce pass executed

        // wb4-c06 HONEST COVERAGE — the SUCCESS-only reviewed set. Declared in the OUTER scope (not inside the
        // windowed block) for two reasons: (1) it is populated only on the success branch of each window's model
        // call, so a FAILED window never inflates the numerator; (2) a downstream persist step (be-c02) reuses
        // this exact set at the PersistPreservingStatusAsync call site below, which lives outside the windowed
        // block. Keep it in scope there — do not collapse it to a local count.
        var reviewedPrimaryOrders = new HashSet<int>();

        if (singleCombined)
        {
            // ── WINDOWED MAP (wb4-c04/c05 reduce passes slot in AFTER this loop, before UnionAndDedup) ──
            // reducePassCount reserves a progress chunk per reduce CALL up front so progress reaches 100% for
            // BOTH the single-call and the multi-group cases:
            //   • wb4-c04 SYNTHESIS is exactly ONE call → contributes 1.
            //   • wb4-c05 CONTINUITY makes a VARIABLE, DETERMINISTIC number of calls (see ContinuityPlan):
            //     ONE call when the whole skeleton fits one budget window, else (group calls + a final reduce).
            //     The skeleton + grouping are pure functions of the already-composed ChapterBriefs, so we plan
            //     it HERE, before SetTotalChunks, and reserve exactly that many chunks — never a hardcoded 2.
            var windowCount = windows.Count;
            totalUnits = windowCount;
            windowCountForResult = windowCount;

            // wb4-c06 HONEST COVERAGE — the DENOMINATOR (total reviewable set): union of EVERY window's PRIMARY
            // chapter orders (IncludedChapterOrders minus OverlapChapterOrders), regardless of whether that
            // window's model call later succeeds. This is the full set the build is RESPONSIBLE for reviewing,
            // deduped by construction (a chapter is a primary in exactly one window). It replaces the old
            // orderedChapterBriefs.Count denominator, which counted only chapters with a FRESH structured brief
            // and therefore undercounted flat/raw-fallback chapters — letting the SUCCESS-based numerator EXCEED
            // it (the >100% "Reviewed 64/40" regression). `reviewedPrimaryOrders` (the numerator) is a SUBSET of
            // this set by construction, so the reviewed <= total invariant holds ALWAYS.
            var totalReviewableOrders = new HashSet<int>();

            // The FULL (untrimmed) BookBrief is shared by every window (each charges a TRIMMED copy to its own
            // budget but keeps the untrimmed object). Both reduce passes read it for their [BOOK_CONTEXT].
            var fullBookBrief = windows.Count > 0 ? windows[0].BookBrief : null;

            // Reconstruct the FULL ordered chapter-brief list from the windows: union each window's included
            // briefs and dedup by Order (an overlap chapter appears in two windows). This is the same set
            // ComposeChapterBriefsAsync produced, without a second DB round-trip. Empty when no window carried
            // structured briefs (then the continuity pass is a no-op).
            var chapterBriefsByOrder = new Dictionary<int, ChapterBrief>();
            foreach (var w in windows)
                foreach (var b in w.IncludedChapterBriefs)
                    chapterBriefsByOrder[b.Order] = b;
            var orderedChapterBriefs = chapterBriefsByOrder.Values.OrderBy(b => b.Order).ToList();
            // NOTE: orderedChapterBriefs is STILL used below to build the continuity plan / reduce inputs. Only
            // its COUNT is no longer the coverage denominator (that is now totalReviewableOrders, populated in the
            // window loop and assigned to chaptersTotalCount after it).

            // Plan the continuity reduce DETERMINISTICALLY (no model call) so its call count is known before we
            // reserve progress chunks. A null plan (no brief / no chapter briefs) means the continuity pass is
            // skipped entirely and contributes ZERO reserved chunks.
            var continuityPlan = fullBookBrief != null && orderedChapterBriefs.Count > 0
                ? PlanContinuityReduce(orderedChapterBriefs, fullBookBrief, lang)
                : null;
            var continuityCallCount = continuityPlan?.TotalCallCount ?? 0;

            // reducePassCount = 1 (synthesis) + the deterministic continuity call count.
            var reducePassCount = 1 + continuityCallCount;
            var totalChunks = windowCount + reducePassCount;

            if (jobId.HasValue)
                _progress.SetTotalChunks(jobId.Value, totalChunks,
                    $"Reviewing {windowCount} window(s) across the book");

            // ACCUMULATE every window's findings in ONE in-memory list. We do NOT persist per window: the
            // persist step DELETES existing still-open findings whose DedupKey is not in `incoming`, so a
            // per-window persist would wipe the PRIOR windows' findings. We accumulate the whole set and
            // persist ONCE below.
            var accumulated = new List<BookFindingItem>();
            var failedWindows = 0;

            // SEQUENTIAL window loop — deliberate. Do NOT Task.WhenAll the windows: on the 8 GB dev GPU each
            // combined call carries a full model context, and running them concurrently doubles the KV cache
            // and wedges the runner on the OOM edge (see the Ollama-8GB tuning breadcrumb). One window at a
            // time keeps memory bounded; a per-window try/catch means one bad window never aborts the build.
            for (var wi = 0; wi < windowCount; wi++)
            {
                ct.ThrowIfCancellationRequested();
                var window = windows[wi];
                var windowIndex1Based = wi + 1;

                if (jobId.HasValue)
                    _progress.ChunkStarted(jobId.Value, windowIndex1Based, totalChunks);

                // firstOrder/lastOrder frame the PRIMARY chapters this window is responsible for (overlap
                // chapters excluded), so the model is told exactly which chapters to report on.
                var primaryOrders = window.IncludedChapterOrders.Except(window.OverlapChapterOrders).ToList();
                var firstOrder = primaryOrders.Count > 0 ? primaryOrders.Min() : 0;
                var lastOrder = primaryOrders.Count > 0 ? primaryOrders.Max() : 0;

                // wb4-c06 DENOMINATOR: every window's primaries join the reviewable-total set UNCONDITIONALLY,
                // BEFORE the model call — a window is RESPONSIBLE for its chapters whether or not its call later
                // succeeds. A failed window is reported separately via FailedWindows; it does NOT shrink the
                // denominator. (The reviewed-numerator add is on the SUCCESS branch below, so a failed window
                // lowers reviewed relative to total, never the reverse.)
                foreach (var order in primaryOrders)
                    totalReviewableOrders.Add(order);

                var windowContext = BuildBookContextSection(window.Text);
                var frame = new WindowFrame(windowIndex1Based, windowCount, firstOrder, lastOrder);

                var windowFindings = await RunCombinedCallAsync(lang, windowContext, ct, frame);
                if (windowFindings == null)
                {
                    // A null return is a window-level failure (model error / unparseable). It does NOT abort
                    // the build — the other windows still contribute. An EMPTY (parsed but zero) window is NOT
                    // a failure here: with N windows a legitimately clean window is expected, unlike the single
                    // whole-book combined call where an empty result is the truncation symptom. Overall total
                    // failure is decided below on the fully accumulated + reduced + deduped set.
                    failedWindows++;
                }
                else
                {
                    // wb4-c06 NUMERATOR: only a window whose call SUCCEEDED (windowFindings != null, i.e. the
                    // model returned a parseable result) counts its primaries as REVIEWED. This runs on the
                    // success branch ONLY, so a failed/unparseable window never inflates ChaptersReviewed. An
                    // EMPTY (parsed, zero-finding) window is still a SUCCESS here — those chapters were reviewed
                    // and legitimately clean. reviewedPrimaryOrders is a subset of totalReviewableOrders by
                    // construction (same primaryOrders source), so reviewed <= total holds always.
                    foreach (var order in primaryOrders)
                        reviewedPrimaryOrders.Add(order);

                    foreach (var item in windowFindings)
                        item.Dimension = NormalizeDimension(item.Dimension);
                    accumulated.AddRange(windowFindings);
                }

                if (jobId.HasValue)
                    _progress.ChunkCompleted(jobId.Value, windowIndex1Based, totalChunks);
            }

            // ── wb4-c04/c05 reduce passes append here ──
            // The synthesis (wb4-c04) and continuity (wb4-c05) reduce passes run AFTER the window loop and
            // APPEND their findings to `accumulated` (same in-memory list), then bump reducePassCount above
            // (+1 each) so SetTotalChunks/ChunkStarted/Completed reserve a progress chunk per reduce pass.
            // Keep them BEFORE the bucketing below so their findings flow through the same dedup/rollup.

            // wb4-c04 SYNTHESIS reduce pass: ONE holistic pass over the FULL accumulated set. It ADDS
            // book-level findings the per-window passes could not see AND reconciles/merges the windows'
            // findings. It reports exactly ONE progress chunk (the chunk AFTER the last window). A synthesis
            // failure (null/empty) contributes ZERO findings but must NOT fail an otherwise-good build: the
            // windows already produced coverage. So it is guarded in its own try/catch and appends nothing on
            // failure — the total-failure decision below still keys only on the whole deduped set being empty.
            //
            // The FULL (untrimmed) BookBrief was captured above (fullBookBrief) from the assembled windows.
            // Synthesis needs a BookBrief for its [BOOK_CONTEXT]; when none exists (chapter-briefs-only book)
            // the pass is skipped — windows still gave coverage.
            var synthesisChunkIndex = windowCount + 1; // 1-based: the chunk right after the last window
            if (fullBookBrief != null)
            {
                if (jobId.HasValue)
                    _progress.ChunkStarted(jobId.Value, synthesisChunkIndex, totalChunks);

                try
                {
                    var synthesisFindings = await RunSynthesisAsync(fullBookBrief, accumulated, lang, jobId, ct);
                    if (synthesisFindings is { Count: > 0 })
                    {
                        foreach (var item in synthesisFindings)
                            item.Dimension = NormalizeDimension(item.Dimension);
                        // Append BEFORE UnionAndDedup so a synthesis finding that duplicates a window finding
                        // dedups away by key (dimension + primary order + rationale) — reconciliation is free.
                        accumulated.AddRange(synthesisFindings);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Non-fatal: a synthesis exception must not sink a build the windows already carried.
                    _logger.LogWarning(ex,
                        "Book review (synthesis): reduce pass threw; continuing with the window findings only.");
                }

                if (jobId.HasValue)
                    _progress.ChunkCompleted(jobId.Value, synthesisChunkIndex, totalChunks);
            }
            else if (jobId.HasValue)
            {
                // No BookBrief → synthesis skipped, but the chunk was RESERVED in SetTotalChunks. Report it
                // started+completed so progress still reaches 100% (no orphaned reserved chunk).
                _progress.ChunkStarted(jobId.Value, synthesisChunkIndex, totalChunks);
                _progress.ChunkCompleted(jobId.Value, synthesisChunkIndex, totalChunks);
            }

            // wb4-c05 HIERARCHICAL CONTINUITY reduce pass: runs AFTER synthesis, over the DETERMINISTIC
            // continuity plan built above. It makes `continuityCallCount` calls occupying the chunks right
            // after the synthesis chunk (windowCount + 2 .. windowCount + 1 + continuityCallCount). The whole
            // pass is guarded: a group failure contributes nothing (non-fatal), and the total-failure decision
            // below still keys only on the whole deduped set being empty. Continuity findings are FORCED to
            // dimension='continuity' and appended BEFORE UnionAndDedup so they dedup/bucket with the rest.
            var continuityBaseChunkIndex = synthesisChunkIndex; // continuity chunks start at +1 from here
            if (continuityPlan != null)
            {
                try
                {
                    var continuityFindings = await RunContinuityReduceAsync(
                        fullBookBrief!, continuityPlan, lang, jobId, continuityBaseChunkIndex, totalChunks, ct);
                    if (continuityFindings.Count > 0)
                    {
                        foreach (var item in continuityFindings)
                            item.Dimension = "continuity"; // continuity pass findings are continuity by construction
                        accumulated.AddRange(continuityFindings);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Non-fatal: a continuity-pass exception must not sink a build the windows already carried.
                    // Drain any continuity chunks the throwing pass left unreported by marking the LAST reserved
                    // continuity chunk complete (ChunkCompleted is monotonic, so this only advances progress) so
                    // the job still reaches 100% instead of stalling on an orphaned reserved chunk.
                    if (jobId.HasValue)
                        _progress.ChunkCompleted(jobId.Value, totalChunks, totalChunks);
                    _logger.LogWarning(ex,
                        "Book review (continuity): reduce pass threw; continuing with the window/synthesis findings only.");
                }
            }

            ct.ThrowIfCancellationRequested();

            // Bucket the accumulated (windows + future reduces) findings into the Dimensions-length slots
            // UnionAndDedup consumes — identical to the single-combined bucketing, just over the union set.
            perDimension = BucketByDimension(accumulated);
            failedUnits = failedWindows;

            // wb4-c06 coverage provenance for the returned result — ONE honest universe, invariant reviewed <= total:
            //   • ChaptersTotal    = distinct primaries across ALL windows (the reviewable set — every chapter the
            //     build was responsible for, including flat/raw-fallback chapters with no fresh structured brief).
            //   • ChaptersReviewed = distinct primaries of the windows whose call SUCCEEDED (a strict subset).
            //     A fully-successful build gives reviewed == total (N/N); a failed window gives reviewed < total;
            //     a partial-brief book can NEVER produce reviewed > total.
            //   • RanSynthesis / RanContinuityReduce mirror the exact gates the reduce passes ran under above
            //     (synthesis iff a full BookBrief existed; continuity iff the deterministic plan was non-null,
            //     i.e. a full BookBrief AND ordered chapter briefs existed).
            chaptersTotalCount = totalReviewableOrders.Count;
            chaptersReviewedCount = reviewedPrimaryOrders.Count;
            ranSynthesis = fullBookBrief != null;           // "attempted" gate: true even if the call errored or returned empty
            ranContinuityReduce = continuityPlan != null;  // "attempted" gate: true even if the call errored or returned empty
        }
        else
        {
            // ── LEGACY per-dimension fan-out (toggle OFF). Operates over the whole-book context concatenated
            //    back from the windows (a small book is one window, so this equals the old assemble-once). ──
            var bookContextSection = BuildBookContextSection(
                string.Join("\n\n", windows.Select(w => w.Text)));
            var (fanned, failedDimensions) = await RunPerDimensionFanOutAsync(
                lang, bookContextSection, jobId, ct);
            perDimension = fanned;
            failedUnits = failedDimensions;
            totalUnits = Dimensions.Length;

            // be-c02: the legacy path reviews the WHOLE book in ONE concatenated context (not per-window), so
            // EVERY chapter order is reviewed together. Seed reviewedPrimaryOrders with all chapter orders so the
            // scoped delete-vanished-open in PersistPreservingStatusAsync behaves EXACTLY as before on this path
            // (it deletes any vanished-open finding — the whole book was re-reviewed). Without this the set would
            // be empty here and the delete would never fire, silently preserving regenerated noise.
            foreach (var order in chaptersByOrder.Keys)
                reviewedPrimaryOrders.Add(order);

            // wb4-c06 coverage provenance for the RETURNED result on the legacy path. chaptersReviewedCount /
            // chaptersTotalCount are otherwise assigned ONLY inside the windowed block above, so without this the
            // returned BookReviewBuildResult reports 0/0 even though PersistPreservingStatusAsync writes HONEST
            // coverage (reviewed == total == chapter count, from reviewedPrimaryOrders / chaptersByOrder.Count on
            // this path). Mirror that persisted coverage here so the result agrees with what is stored: the whole
            // book is reviewed in one concatenated context, so every chapter is both reviewable and reviewed.
            // (windowCountForResult stays 0 — the legacy path has no windows; its message uses the dimensions
            // wording, not the windowed coverage string, so the 0 window count never leaks into user-facing text.)
            chaptersReviewedCount = reviewedPrimaryOrders.Count;
            chaptersTotalCount = chaptersByOrder.Count;
        }

        ct.ThrowIfCancellationRequested();

        // 4 + 5. UNION across dimensions and DEDUP by BookFinding.ComputeDedupKey(dimension, primaryOrder,
        //         rationale). First occurrence wins; later duplicates are merged away. This runs ONCE over the
        //         fully accumulated (all windows + reduce passes) set.
        var deduped = UnionAndDedup(perDimension, chaptersByOrder, builtWithModel, lang);

        // 6. TOTAL FAILURE: the ENTIRE accumulated + reduced set deduped to ZERO fresh findings — every window
        //    AND both reduce passes produced nothing usable (errored/unparseable OR all-empty). This is the
        //    same guard as the single-combined path (wb2-c05), now over the whole windowed set: keying on the
        //    produced-finding count catches the errored AND the empty case, and covers findings dropped by the
        //    rationale filter in UnionAndDedup. A total failure must NOT persist (see below).
        var totalFailure = deduped.Count == 0;

        // 7. PERSIST preserving user-set Status across the rebuild — ONLY when fresh findings were produced, and
        //    exactly ONCE over the FULL set. On a total failure we SKIP the persist entirely so the cached
        //    review survives a bad build; running it with an empty incoming set would delete every still-open
        //    cached finding. Persisting once (not per window) is what keeps the delete-vanished-open loop from
        //    wiping earlier windows' findings — the whole set is `incoming` together.
        // data-c01 HONEST coverage to PERSIST alongside the findings so the status probe survives a reload:
        //   • reviewed = reviewedPrimaryOrders.Count — the honest reviewed set on BOTH paths (windowed:
        //     SUCCESS-only primaries; legacy: seeded with every chapter order above, since the whole book is
        //     reviewed in one concatenated context). Equals chaptersReviewedCount on the windowed path.
        //   • total = the reviewable denominator: chaptersTotalCount on the windowed path; the whole-book chapter
        //     count (chaptersByOrder.Count) on the legacy path, where chaptersTotalCount stays 0 (it is only set
        //     inside the singleCombined block). This keeps the legacy path persisting honest full coverage
        //     (reviewed == total == chapter count) rather than 0/0.
        var persistedReviewed = reviewedPrimaryOrders.Count;
        var persistedTotal = singleCombined ? chaptersTotalCount : chaptersByOrder.Count;
        if (!totalFailure)
            await PersistPreservingStatusAsync(
                bookId, lang, deduped, reviewedPrimaryOrders, persistedReviewed, persistedTotal, ct);

        var totalNow = await _db.BookFindings.CountAsync(
            f => f.BookId == bookId && f.Language == lang, ct);

        // wb4-c06 HONEST coverage tail. The windowed (default) path speaks in chapters/windows/passes; the
        // legacy per-dimension path keeps its dimensions wording. The success/partial message leads with the
        // honest "Reviewed N/N chapters across W windows" claim (+ a continuity note and a failed-window count
        // when relevant), so the FE never has to trust a possibly-truncated single call. No em-dash (U+2014)
        // anywhere — a regular hyphen only. `coverage` is the shared HONEST prefix.
        var coverage = $"Reviewed {chaptersReviewedCount}/{chaptersTotalCount} chapters across " +
                       $"{windowCountForResult} window(s)" +
                       (ranContinuityReduce ? " + continuity pass" : string.Empty);

        string msg;
        if (totalFailure)
        {
            msg = singleCombined
                ? "Whole-book review failed: no findings were produced across any window. " +
                  "Try again; if it persists the book may be too large for the model context."
                : $"Whole-book review failed: no findings were produced across any of the {Dimensions.Length} dimensions. " +
                  "Try again; if it persists the book may be too large for the model context.";
        }
        else if (failedUnits > 0)
        {
            // PARTIAL warning: some windows (or, in the legacy path, dimensions) failed but others produced
            // findings. Surfaced as Succeeded-with-warning carrying the failed count. Preserve the "warning"
            // token + the failed count the FE red-banner / warn logic keys on, now on the honest coverage line.
            msg = singleCombined
                ? $"Whole-book review built with warnings: {coverage}, {failedUnits} window(s) failed " +
                  $"({deduped.Count} findings)."
                : $"Whole-book review built with warnings: {deduped.Count} findings across " +
                  $"{Dimensions.Length - failedUnits}/{Dimensions.Length} dimensions ({failedUnits} failed).";
        }
        else
        {
            msg = singleCombined
                ? $"Whole-book review built: {coverage} ({deduped.Count} findings)."
                : $"Whole-book review built: {deduped.Count} findings across {Dimensions.Length} dimensions.";
        }

        if (jobId.HasValue)
        {
            // wb4-c06: stamp the TRANSIENT build-shape onto the job BEFORE the terminal status, so the SAME
            // terminal progress poll that observes Succeeded/Failed also carries the window/continuity/failed-
            // window counts. These are build-time-only (the persisted status probe reports 0/false), so this
            // live progress payload is the FE's channel for the "N windows[, continuity pass]" detail + the
            // "N windows failed" partial warning. Values mirror the returned result exactly (0/false/failedUnits
            // on the legacy per-dimension path, where windowCount is 0 so the FE hides the window detail).
            _progress.SetBookReviewShape(jobId.Value, windowCountForResult, ranContinuityReduce, failedUnits);
            _progress.SetStatus(
                jobId.Value,
                totalFailure ? AnalysisProgressStatus.Failed : AnalysisProgressStatus.Succeeded,
                msg);
        }

        return new BookReviewBuildResult
        {
            // A total failure is NOT a ready review — even if stale acted-on rows linger, no fresh review
            // was produced this build, so the FE must not treat it as a successful (re)build.
            Ready = !totalFailure,
            NoOp = false,
            BriefsMissing = false,
            FindingCount = totalNow,
            FailedDimensions = failedUnits,   // back-compat (wb2-c05): failed WINDOW count on the windowed path
            // wb4-c06 coverage provenance (precise per-build counts; the status probe reports the persisted
            // subset). FailedWindows is the clearly-named twin of FailedDimensions on the windowed path.
            ChaptersReviewed = chaptersReviewedCount,
            ChaptersTotal = chaptersTotalCount,
            WindowCount = windowCountForResult,
            RanSynthesis = ranSynthesis,
            RanContinuityReduce = ranContinuityReduce,
            FailedWindows = failedUnits,
            Message = msg
        };
    }

    /// <summary>
    /// PER-DIMENSION strategy (toggle OFF): fans out the six single-dimension prompts with a PARALLEL CAP,
    /// reporting per-dimension progress. One bad dimension (parse failure / model error) must NOT abort the
    /// whole build: it is logged and treated as zero findings (a null array slot). Returns the
    /// Dimensions-length array of per-dimension findings plus the count of dimensions that produced none.
    /// </summary>
    private async Task<(List<BookFindingItem>?[] PerDimension, int FailedDimensions)> RunPerDimensionFanOutAsync(
        string lang,
        string bookContextSection,
        Guid? jobId,
        CancellationToken ct)
    {
        if (jobId.HasValue)
            _progress.SetTotalChunks(jobId.Value, Dimensions.Length,
                $"Reviewing {Dimensions.Length} dimensions");

        var maxParallel = Math.Max(1, _aiOptions.Value.MaxParallelBookReviewDimensions);
        var semaphore = new SemaphoreSlim(maxParallel, maxParallel);
        var completed = 0;
        var perDimension = new List<BookFindingItem>?[Dimensions.Length];

        async Task ProcessDimension(int index)
        {
            await semaphore.WaitAsync(ct);
            try
            {
                if (jobId.HasValue)
                    _progress.ChunkStarted(jobId.Value, index + 1, Dimensions.Length);

                perDimension[index] = await RunDimensionAsync(
                    Dimensions[index], lang, bookContextSection, ct);

                if (jobId.HasValue)
                {
                    var done = Interlocked.Increment(ref completed);
                    _progress.ChunkCompleted(jobId.Value, done, Dimensions.Length);
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        var tasks = new List<Task>(Dimensions.Length);
        for (var i = 0; i < Dimensions.Length; i++)
            tasks.Add(ProcessDimension(i));
        await Task.WhenAll(tasks);

        var failedDimensions = perDimension.Count(d => d == null);
        return (perDimension, failedDimensions);
    }

    /// <summary>
    /// Buckets a flat list of combined-shape findings (accumulated across every window + the future reduce
    /// passes) into the Dimensions-length array the shared <see cref="UnionAndDedup"/> consumes, so the whole
    /// windowed set flows through the SAME union/dedup/persist/rollup the single-combined path used. Each
    /// finding's self-labelled dimension is normalised to one of the six (an unknown/blank value falls back to
    /// "plot", the per-dimension prompt's own unknown fallback) so a bad self-label never poisons the dedup
    /// key or score rollup. The caller has already normalised each item's Dimension, but this re-normalises
    /// defensively so it is safe to call on any combined-shape list.
    /// </summary>
    private static List<BookFindingItem>?[] BucketByDimension(IReadOnlyList<BookFindingItem> findings)
    {
        var perDimension = new List<BookFindingItem>?[Dimensions.Length];
        var dimensionIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < Dimensions.Length; i++)
            dimensionIndex[Dimensions[i]] = i;

        foreach (var item in findings)
        {
            var normalized = NormalizeDimension(item.Dimension);
            item.Dimension = normalized;
            var slot = dimensionIndex[normalized];
            (perDimension[slot] ??= new List<BookFindingItem>()).Add(item);
        }

        return perDimension;
    }

    /// <summary>
    /// Window framing passed to <see cref="RunCombinedCallAsync"/> to select the WINDOWED prompt
    /// (wb4-c03 <see cref="PromptFactory.BuildBookReviewWindowPrompt"/>) instead of the whole-book combined
    /// prompt. <see cref="WindowIndex"/> is 1-BASED (the frame says "window X of N"); <see cref="FirstOrder"/>
    /// / <see cref="LastOrder"/> are the min/max PRIMARY chapter orders the window is responsible for, so the
    /// frame names exactly the chapters shown (overlap chapters excluded). A null window = the non-windowed
    /// single-combined call, unchanged.
    /// </summary>
    private readonly record struct WindowFrame(int WindowIndex, int WindowCount, int FirstOrder, int LastOrder);

    /// <summary>
    /// Runs ONE combined review call: prepends the shared [BOOK_CONTEXT] to either the whole-book combined
    /// prompt (<paramref name="window"/> null) or, in the windowed MAP path, the wb4-c03
    /// <see cref="PromptFactory.BuildBookReviewWindowPrompt"/> for that window, calls the router with
    /// <see cref="AiTaskType.BookReview"/>, and parses the multi-dimension findings[] via the shared
    /// <see cref="UnifiedAnalysisService.ExtractJson"/> extractor. Returns the parsed findings, or NULL when
    /// the model errors or the output cannot be parsed (the caller treats null as a TOTAL/window failure).
    /// Mirrors <see cref="RunDimensionAsync"/>'s request shape + null-on-failure contract.
    /// </summary>
    private async Task<List<BookFindingItem>?> RunCombinedCallAsync(
        string lang,
        string bookContextSection,
        CancellationToken ct,
        WindowFrame? window = null)
    {
        try
        {
            var promptBody = window is { } w
                ? _promptFactory.BuildBookReviewWindowPrompt(lang, w.WindowIndex, w.WindowCount, w.FirstOrder, w.LastOrder)
                : _promptFactory.BuildBookReviewCombinedPrompt(lang);
            var instruction = bookContextSection + promptBody;

            var request = new AiRequest
            {
                InputText = string.Empty, // the whole-book context lives in the instruction's [BOOK_CONTEXT]
                Instruction = instruction,
                TaskType = AiTaskType.BookReview,
                Language = lang,
                JsonMode = true
            };

            var scope = window is { } wf ? $"window {wf.WindowIndex}/{wf.WindowCount}" : "combined";

            var response = await _router.CompleteAsync(request, ct);
            var raw = response.Content;
            if (string.IsNullOrWhiteSpace(raw))
            {
                _logger.LogWarning("Book review ({Scope}): model returned empty output; treating as failure.", scope);
                return null;
            }

            var json = UnifiedAnalysisService.ExtractJson(raw);
            if (string.IsNullOrWhiteSpace(json))
            {
                _logger.LogWarning("Book review ({Scope}): output had no extractable JSON; treating as failure.", scope);
                return null;
            }

            var parsed = JsonSerializer.Deserialize<BookReviewResult>(json, DeserializeOpts);
            if (parsed?.Findings == null)
            {
                _logger.LogWarning("Book review ({Scope}): JSON had no findings array; treating as failure.", scope);
                return null;
            }

            return parsed.Findings;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Book review: threw during the model call/parse; treating as failure.");
            return null;
        }
    }

    // ─── Synthesis reduce pass (wb4-c04) ──────────────────────────────────────────────────────────

    /// <summary>Max chars of a finding's rationale kept in a digest line (evidence/suggestedAction are dropped
    /// entirely). Terse by design so the whole digest fits the model window even with ~100 accumulated
    /// findings on the local 8192-token budget.</summary>
    private const int SynthesisRationaleDigestChars = 140;

    /// <summary>
    /// SYNTHESIS reduce pass (wb4-c04). Runs ONCE after the window MAP over the FULL accumulated finding set.
    /// Builds a COMPACT digest — one terse line per accumulated finding (<c>dimension | chapterOrder |
    /// rationale[..140]</c>), stripping evidence + suggestedAction so the digest fits the model window even for
    /// ~100 findings — prepends the FULL (untrimmed) <paramref name="bookBrief"/> in [BOOK_CONTEXT], and calls
    /// the model with <see cref="PromptFactory.BuildBookReviewSynthesisPrompt"/> to (1) add holistic book-level
    /// findings the per-window passes could not see and (2) reconcile/merge the accumulated set. Output is the
    /// SAME BookReviewResult/{findings:[BookFindingItem]} shape as the combined call, parsed identically.
    ///
    /// BUDGET: the [WINDOW_FINDINGS] digest is capped to the same token budget the assembler used
    /// (<see cref="BookContextAssembler.ResolveBudgetTokens"/>) minus the brief block. If the full digest would
    /// exceed the budget it is capped by dropping the LOWEST-severity findings first (highest severity kept)
    /// and the cap is LOGGED (no silent truncation).
    ///
    /// FAILURE: mirrors <see cref="RunCombinedCallAsync"/>'s null-on-failure contract. On a model error /
    /// unparseable output this returns NULL, which the caller treats as ZERO synthesis findings — NOT a
    /// total-build failure, since the windows already produced coverage. Reports exactly ONE progress chunk.
    /// </summary>
    private async Task<List<BookFindingItem>?> RunSynthesisAsync(
        BookBrief bookBrief,
        IReadOnlyList<BookFindingItem> accumulatedFindings,
        string lang,
        Guid? jobId,
        CancellationToken ct)
    {
        try
        {
            var briefBlock = BookContextAssembler.FormatBookBrief(bookBrief);
            var digestBlock = BuildSynthesisDigest(accumulatedFindings, lang, briefBlock);

            // Input mirrors the combined call: whole-book context in the instruction's [BOOK_CONTEXT], then the
            // compact [WINDOW_FINDINGS] digest, then the synthesis prompt body. InputText stays empty.
            var bookContextSection = briefBlock + "\n\n" + digestBlock + "\n\n";
            var instruction = bookContextSection + _promptFactory.BuildBookReviewSynthesisPrompt(lang);

            var request = new AiRequest
            {
                InputText = string.Empty, // the whole-book context + digest live in the instruction
                Instruction = instruction,
                TaskType = AiTaskType.BookReview,
                Language = lang,
                JsonMode = true
            };

            var response = await _router.CompleteAsync(request, ct);
            var raw = response.Content;
            if (string.IsNullOrWhiteSpace(raw))
            {
                _logger.LogWarning("Book review (synthesis): model returned empty output; treating as zero synthesis findings.");
                return null;
            }

            var json = UnifiedAnalysisService.ExtractJson(raw);
            if (string.IsNullOrWhiteSpace(json))
            {
                _logger.LogWarning("Book review (synthesis): output had no extractable JSON; treating as zero synthesis findings.");
                return null;
            }

            var parsed = JsonSerializer.Deserialize<BookReviewResult>(json, DeserializeOpts);
            if (parsed?.Findings == null)
            {
                _logger.LogWarning("Book review (synthesis): JSON had no findings array; treating as zero synthesis findings.");
                return null;
            }

            // Self-labelled dimension (plot/pacing/theme for arc-level notes) — normalise defensively so a bad
            // self-label never poisons the dedup key or score rollup, exactly as the window path does.
            foreach (var f in parsed.Findings)
                f.Dimension = NormalizeDimension(f.Dimension);

            return parsed.Findings;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Book review (synthesis): threw during the model call/parse; treating as zero synthesis findings.");
            return null;
        }
    }

    /// <summary>
    /// Builds the COMPACT [WINDOW_FINDINGS] digest the synthesis prompt reads: one terse line per accumulated
    /// finding — <c>dimension | chapterOrder | rationale[..140]</c> — with evidence and suggestedAction
    /// STRIPPED so the block stays small even for ~100 findings. chapterOrder is the first chapter anchor's
    /// Order (else 0), matching the dedup-key derivation. If the full digest's estimated tokens exceed the
    /// resolved book-context budget (minus the brief block already charged), the digest is CAPPED by dropping
    /// the LOWEST-severity findings first (highest severity retained) and the drop is LOGGED (no silent
    /// truncation). The lines keep their original accumulation order; only the over-budget tail is removed.
    /// </summary>
    private string BuildSynthesisDigest(
        IReadOnlyList<BookFindingItem> accumulatedFindings,
        string lang,
        string briefBlock)
    {
        var charsPerToken = BookContextAssembler.CharsPerTokenForLanguage(lang);
        var budget = _contextAssembler.ResolveBudgetTokens(new[] { AiTaskType.BookReview });

        // Reserve the room the FULL brief block already occupies in [BOOK_CONTEXT]; the digest must fit in what
        // remains. Guard a pathological non-positive remainder to a small floor so at least a few lines survive.
        var briefTokens = BookContextAssembler.EstimateTokens(briefBlock, charsPerToken);
        var digestBudget = Math.Max(256, budget - briefTokens);

        // One terse line per finding, in accumulation order, paired with its severity for the cap decision.
        var lines = new List<(int Severity, string Line)>(accumulatedFindings.Count);
        foreach (var f in accumulatedFindings)
        {
            var order = f.ChapterAnchors is { Count: > 0 } anchors ? anchors[0].Order : 0;
            var rationale = (f.Rationale ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (rationale.Length > SynthesisRationaleDigestChars)
                rationale = rationale.Substring(0, SynthesisRationaleDigestChars);
            var dimension = NormalizeDimension(f.Dimension);
            lines.Add((f.Severity, $"{dimension} | {order} | {rationale}"));
        }

        const string openMarker = "[WINDOW_FINDINGS]";
        const string closeMarker = "[/WINDOW_FINDINGS]";
        var markerTokens = BookContextAssembler.EstimateTokens(openMarker + "\n" + closeMarker, charsPerToken);

        // Greedily keep lines in ORIGINAL order until the budget is hit; if that would drop any line, instead
        // keep the HIGHEST-severity lines (stable within a severity) so the most important findings survive the
        // cap, then re-emit those in their original order. This keeps the digest deterministic and severity-first.
        var keptCount = lines.Count;
        var runningTokens = markerTokens;
        for (var i = 0; i < lines.Count; i++)
        {
            var lineTokens = BookContextAssembler.EstimateTokens(lines[i].Line + "\n", charsPerToken);
            if (runningTokens + lineTokens > digestBudget)
            {
                keptCount = i;
                break;
            }
            runningTokens += lineTokens;
        }

        List<(int Severity, string Line)> emitted;
        if (keptCount >= lines.Count)
        {
            emitted = lines; // everything fits
        }
        else
        {
            // Over budget: drop lowest-severity first. Rank by severity DESC (stable), keep as many as fit the
            // digest budget, then restore original order for the kept subset.
            var ranked = lines
                .Select((l, idx) => (l.Severity, l.Line, idx))
                .OrderByDescending(x => x.Severity)
                .ThenBy(x => x.idx)
                .ToList();

            var keepIndices = new HashSet<int>();
            var tokens = markerTokens;
            foreach (var r in ranked)
            {
                var lineTokens = BookContextAssembler.EstimateTokens(r.Line + "\n", charsPerToken);
                if (tokens + lineTokens > digestBudget)
                    continue; // this line does not fit; a later lower-severity line will not either, but keep scanning
                tokens += lineTokens;
                keepIndices.Add(r.idx);
            }

            emitted = lines.Where((_, idx) => keepIndices.Contains(idx)).ToList();

            var dropped = lines.Count - emitted.Count;
            _logger.LogWarning(
                "Book review (synthesis): the accumulated-findings digest ({Total} findings, ~{FullTokens} tokens) " +
                "exceeded the reduce budget ({DigestBudget} tokens after the {BriefTokens}-token brief); capped to " +
                "{Kept} findings (dropped {Dropped}, lowest-severity first) so the synthesis input fits the model window.",
                lines.Count,
                runningTokens,
                digestBudget,
                briefTokens,
                emitted.Count,
                dropped);
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(openMarker);
        foreach (var (_, line) in emitted)
            sb.AppendLine(line);
        sb.Append(closeMarker);
        return sb.ToString();
    }

    // ─── Hierarchical continuity reduce pass (wb4-c05) ───────────────────────────────────────────────

    /// <summary>Max chars of a group finding's rationale kept in the FINAL-reduce digest line (evidence +
    /// suggestedAction dropped), mirroring <see cref="SynthesisRationaleDigestChars"/>. Terse so the union of
    /// every group's continuity findings fits the final reduce's model window.</summary>
    private const int ContinuityRationaleDigestChars = 140;

    /// <summary>Bounded recursion depth for the continuity grouping (wb4-c05). Level 0 = the group calls over
    /// the skeleton; each deeper level regroups the previous level's group-findings union when even THAT would
    /// overflow one budget window. Capped so a pathological book cannot recurse without end; the level count is
    /// LOGGED (never silent). 3 levels of budget-sized fan-in covers any realistically-sized book.</summary>
    private const int MaxContinuityReduceDepth = 3;

    /// <summary>A deterministic plan for the continuity reduce, computed from the already-composed ChapterBriefs
    /// BEFORE any model call so <see cref="RunBuildAsync"/> can reserve exactly the right number of progress
    /// chunks. A SINGLE group (the whole skeleton fits one budget window) collapses to ONE call and NO final
    /// reduce — the auto-collapse a bigger window / cloud model gets for free. More than one group means a
    /// group call each PLUS one final reduce over the union of their findings. <see cref="LevelCount"/> records
    /// how many rounds of deterministic regrouping the plan needed (logged; bounded by
    /// <see cref="MaxContinuityReduceDepth"/>).</summary>
    private sealed record ContinuityPlan(IReadOnlyList<ContinuityGroup> Groups, int LevelCount)
    {
        /// <summary>True when the whole skeleton fits one budget window → exactly ONE continuity call, no final
        /// reduce (this is what a larger window / cloud model auto-collapses to).</summary>
        public bool FitsOneWindow => Groups.Count == 1;

        /// <summary>The DETERMINISTIC number of continuity model calls this plan makes: ONE when it fits one
        /// window, else one per group PLUS the single final reduce that merges the groups' findings.</summary>
        public int TotalCallCount => FitsOneWindow ? 1 : Groups.Count + 1;
    }

    /// <summary>One budget-sized continuity group: a CONTIGUOUS run of chapter briefs (narrative order) whose
    /// skeleton + the full BookBrief fit one budget window, WITH a small leading chapter overlap repeated from
    /// the previous group so a continuity break that straddles a group boundary is visible on both sides.</summary>
    private sealed record ContinuityGroup(IReadOnlyList<ChapterBrief> Briefs);

    /// <summary>
    /// DETERMINISTIC planner (no model call). "Fits one window" = full-BookBrief tokens + whole-skeleton tokens
    /// &lt;= the SAME budget the windows/synthesis use (<see cref="BookContextAssembler.ResolveBudgetTokens"/>
    /// over [BookReview], with <see cref="BookContextAssembler.CharsPerTokenForLanguage"/>). When it fits, the
    /// plan is ONE group over ALL briefs → one call, no final reduce (auto-collapse). When it does NOT fit
    /// (e.g. a 64-chapter book on the local 8192 window), greedily pack CONTIGUOUS briefs into groups so
    /// (fullBriefTokens + groupSkeletonTokens) &lt;= budget, repeating the last K chapters of the previous group
    /// as leading overlap (K = <see cref="AiOptions.BookReviewWindowOverlapChapters"/>, the SAME knob the
    /// windows use).
    ///
    /// BOUNDED-DEPTH REGROUPING: if the greedy pack still yields SO many groups that even one representative
    /// skeleton line PER GROUP (the densest the final reduce's union digest can be) would not fit one budget
    /// window, the group count itself is unmanageable — the plan records another level and re-packs the group
    /// REPRESENTATIVES the same way, up to <see cref="MaxContinuityReduceDepth"/> levels; the level count is
    /// LOGGED (never silent). In practice one level covers any realistically-sized book; the recursion exists so
    /// a pathological book still terminates. Called once up front; the returned plan's
    /// <see cref="ContinuityPlan.TotalCallCount"/> is the exact number of model calls the pass will make, so
    /// progress chunks are reserved correctly.
    /// </summary>
    private ContinuityPlan PlanContinuityReduce(IReadOnlyList<ChapterBrief> orderedBriefs, BookBrief bookBrief, string lang)
    {
        var charsPerToken = BookContextAssembler.CharsPerTokenForLanguage(lang);
        var budget = _contextAssembler.ResolveBudgetTokens(new[] { AiTaskType.BookReview });

        // The full BookBrief block is charged to EVERY group (repeated in each group's [BOOK_CONTEXT]), so the
        // room left for a group's skeleton lines is budget - briefTokens. Estimate it once from the same
        // renderer the run path uses so planning and execution agree. A non-positive remainder floors so at
        // least one chapter always fits (never an empty/looping group).
        var briefTokens = BookContextAssembler.EstimateTokens(
            BookContextAssembler.FormatBookBrief(bookBrief), charsPerToken);

        // Whole-skeleton fits-one-window probe: full brief + the ENTIRE skeleton within the budget → auto-collapse.
        var wholeSkeletonTokens = BookContextAssembler.EstimateTokens(
            BookContextAssembler.FormatContinuitySkeleton(orderedBriefs), charsPerToken);
        if (briefTokens + wholeSkeletonTokens <= budget)
            return new ContinuityPlan(new[] { new ContinuityGroup(orderedBriefs) }, LevelCount: 0);

        // Does NOT fit → HIERARCHY. Greedily pack contiguous briefs into budget-sized groups.
        var perGroupSkeletonBudget = Math.Max(1, budget - briefTokens);
        var overlapK = Math.Max(0, _aiOptions.Value.BookReviewWindowOverlapChapters);
        var markerTokens = BookContextAssembler.EstimateTokens(
            BookContextAssembler.ContinuitySkeletonOpen + "\n" + BookContextAssembler.ContinuitySkeletonClose,
            charsPerToken);

        var groups = PackContiguous(orderedBriefs, perGroupSkeletonBudget, markerTokens, overlapK, charsPerToken);

        // Bounded-depth accounting: with many groups, the final reduce's union digest (one dense line per group
        // finding) can itself exceed one window, which conceptually needs another round of budget-sized fan-in.
        // How many rounds = how many times you must divide the group count by the lines-that-fit-one-window fan-
        // in factor before it collapses to a single window. We cannot know the true finding volume up front, so
        // we bound on the GROUP COUNT with a worst-case (one full-length digest line per group). The final reduce
        // itself CAPS its digest to the budget, so this depth is diagnostic — it never changes the call count —
        // but it is LOGGED (never silent) and bounded by MaxContinuityReduceDepth so a pathological book still
        // terminates. Level 1 = the one flat group→final-reduce round (the common case).
        var digestBudget = Math.Max(256, budget - briefTokens);
        var perGroupDigestLineTokens = Math.Max(1, BookContextAssembler.EstimateTokens(
            "continuity | 0,0 | " + new string('x', ContinuityRationaleDigestChars) + "\n", charsPerToken));
        var linesPerWindow = Math.Max(2, (digestBudget - markerTokens) / perGroupDigestLineTokens); // fan-in factor
        var level = 1;
        var remaining = (long)groups.Count;
        while (remaining > linesPerWindow && level < MaxContinuityReduceDepth)
        {
            remaining = (remaining + linesPerWindow - 1) / linesPerWindow; // ceil-divide by the fan-in factor
            level++;
        }
        if (level > 1)
            _logger.LogInformation(
                "Book review (continuity): {GroupCount} skeleton groups imply {LevelCount} fan-in level(s) " +
                "(bounded at {MaxDepth}) for book-scale continuity reduce; the final reduce caps its union digest.",
                groups.Count, level, MaxContinuityReduceDepth);

        return new ContinuityPlan(groups, LevelCount: level);
    }

    /// <summary>Greedily packs <paramref name="orderedBriefs"/> into CONTIGUOUS budget-sized groups: fill a
    /// group's skeleton until the next chapter would exceed <paramref name="perGroupSkeletonBudget"/>, then
    /// close it and start the next with a leading overlap of the previous group's last
    /// <paramref name="overlapK"/> PRIMARY chapters. Always takes at least one primary per group so it never
    /// loops. Deterministic — the same briefs always yield the same groups.</summary>
    private static List<ContinuityGroup> PackContiguous(
        IReadOnlyList<ChapterBrief> orderedBriefs,
        int perGroupSkeletonBudget,
        int markerTokens,
        int overlapK,
        double charsPerToken)
    {
        var groups = new List<ContinuityGroup>();
        var primaryOfPrevGroup = new List<ChapterBrief>();
        var i = 0;
        while (i < orderedBriefs.Count)
        {
            var current = new List<ChapterBrief>();

            // Leading overlap: repeat the last K PRIMARY chapters of the previous group so a break at the group
            // boundary is visible on both sides. Charged to this group's skeleton budget like any chapter.
            if (groups.Count > 0 && overlapK > 0)
            {
                var take = Math.Min(overlapK, primaryOfPrevGroup.Count);
                for (var k = primaryOfPrevGroup.Count - take; k < primaryOfPrevGroup.Count; k++)
                    current.Add(primaryOfPrevGroup[k]);
            }

            var used = markerTokens + current.Sum(b => BookContextAssembler.EstimateTokens(
                BookContextAssembler.FormatContinuitySkeletonLine(b) + "\n", charsPerToken));

            var primaryThisGroup = new List<ChapterBrief>();
            while (i < orderedBriefs.Count)
            {
                var lineTokens = BookContextAssembler.EstimateTokens(
                    BookContextAssembler.FormatContinuitySkeletonLine(orderedBriefs[i]) + "\n", charsPerToken);
                if (used + lineTokens > perGroupSkeletonBudget && primaryThisGroup.Count > 0)
                    break; // close this group, start a new one with the next chapter (never drop)
                current.Add(orderedBriefs[i]);
                primaryThisGroup.Add(orderedBriefs[i]);
                used += lineTokens;
                i++;
            }

            groups.Add(new ContinuityGroup(current));
            primaryOfPrevGroup = primaryThisGroup;
        }
        return groups;
    }

    /// <summary>
    /// Runs the HIERARCHICAL CONTINUITY reduce over the deterministic <paramref name="plan"/>. Two shapes:
    ///
    ///  • FITS ONE WINDOW (single group): ONE continuity call over the full BookBrief + the WHOLE skeleton,
    ///    reported as the single reserved continuity chunk. NO final reduce — this is the auto-collapse a
    ///    bigger window / cloud model gets for free.
    ///
    ///  • HIERARCHY (multiple groups): each group's continuity prompt runs SEQUENTIALLY (8 GB-GPU KV-cache
    ///    safety — never concurrent, like the window MAP), collecting group-level continuity findings; THEN a
    ///    FINAL reduce over the full BookBrief + a COMPACT union/digest of those group findings merges/dedups
    ///    cross-group continuity issues. The union digest is CAPPED to the budget (lowest-severity dropped
    ///    first, logged) rather than recursing per model output, so the call count stays the deterministic
    ///    <see cref="ContinuityPlan.TotalCallCount"/> the progress reservation used. Deterministic regrouping
    ///    recursion (bounded by <see cref="MaxContinuityReduceDepth"/>) is planned up front, not here.
    ///
    /// A GROUP failure (null parse / model error) contributes ZERO findings and is NON-FATAL — the pass moves
    /// on. Every reserved continuity chunk is reported started+completed (even a failed/empty group) so
    /// progress reaches 100% in both shapes. Returns the (possibly empty) continuity findings; the caller
    /// forces dimension='continuity' and appends them before dedup.
    /// </summary>
    private async Task<List<BookFindingItem>> RunContinuityReduceAsync(
        BookBrief bookBrief,
        ContinuityPlan plan,
        string lang,
        Guid? jobId,
        int baseChunkIndex,
        int totalChunks,
        CancellationToken ct)
    {
        var briefBlock = BookContextAssembler.FormatBookBrief(bookBrief);
        var nextChunk = baseChunkIndex; // continuity chunks start at baseChunkIndex + 1

        // ── Single group: the whole skeleton fits one window → ONE call, no final reduce (auto-collapse). ──
        if (plan.FitsOneWindow)
        {
            nextChunk++;
            if (jobId.HasValue)
                _progress.ChunkStarted(jobId.Value, nextChunk, totalChunks);

            var skeleton = BookContextAssembler.FormatContinuitySkeleton(plan.Groups[0].Briefs);
            var findings = await RunContinuityCallAsync(briefBlock, skeleton, lang, ct) ?? new List<BookFindingItem>();

            if (jobId.HasValue)
                _progress.ChunkCompleted(jobId.Value, nextChunk, totalChunks);
            return findings;
        }

        // ── Hierarchy: SEQUENTIAL group calls, then ONE final reduce over their findings union. ──
        // SEQUENTIAL on purpose: on the 8 GB dev GPU each continuity call carries the full model context;
        // running groups concurrently doubles the KV cache and wedges the runner (Ollama-8GB tuning breadcrumb).
        var groupFindings = new List<BookFindingItem>();
        foreach (var group in plan.Groups)
        {
            ct.ThrowIfCancellationRequested();
            nextChunk++;
            if (jobId.HasValue)
                _progress.ChunkStarted(jobId.Value, nextChunk, totalChunks);

            var skeleton = BookContextAssembler.FormatContinuitySkeleton(group.Briefs);
            var found = await RunContinuityCallAsync(briefBlock, skeleton, lang, ct);
            if (found is { Count: > 0 })
            {
                foreach (var f in found) f.Dimension = "continuity";
                groupFindings.AddRange(found);
            }
            // A null/empty group contributes nothing (non-fatal); its chunk is still reported complete.

            if (jobId.HasValue)
                _progress.ChunkCompleted(jobId.Value, nextChunk, totalChunks);
        }

        // FINAL reduce: full BookBrief + a compact digest of the group findings, back through the continuity
        // prompt so it merges/dedups cross-group continuity issues into the final continuity set. Always ONE
        // call (the deterministic +1 in TotalCallCount), even when the groups produced nothing.
        nextChunk++;
        if (jobId.HasValue)
            _progress.ChunkStarted(jobId.Value, nextChunk, totalChunks);

        List<BookFindingItem> finalFindings;
        if (groupFindings.Count == 0)
        {
            // No group produced anything → nothing to merge; skip a pointless model call but still report the
            // reserved final chunk complete so progress reaches 100%.
            finalFindings = new List<BookFindingItem>();
        }
        else
        {
            // The final reduce reuses the CONTINUITY skeleton marker so the same prompt/parse path applies: the
            // digest of group findings is rendered as skeleton-shaped lines the continuity prompt already reads.
            var digestSkeleton = BuildContinuityFindingsDigest(groupFindings, lang, briefBlock);
            finalFindings = await RunContinuityCallAsync(briefBlock, digestSkeleton, lang, ct)
                ?? new List<BookFindingItem>();
        }

        if (jobId.HasValue)
            _progress.ChunkCompleted(jobId.Value, nextChunk, totalChunks);

        // The final reduce is the authoritative merged set when it produced findings; otherwise fall back to
        // the raw group findings so a final-reduce failure never DROPS the continuity issues the groups found.
        return finalFindings.Count > 0 ? finalFindings : groupFindings;
    }

    /// <summary>
    /// One continuity model call: prepends the full BookBrief [BOOK_CONTEXT] to the
    /// <c>[CONTINUITY_SKELETON]…[/CONTINUITY_SKELETON]</c> block, then the wb4-c03
    /// <see cref="PromptFactory.BuildBookReviewContinuityReducePrompt"/> body, tags the request
    /// <see cref="AiTaskType.BookReview"/>, and parses the findings[] via the shared extractor. Mirrors
    /// <see cref="RunSynthesisAsync"/>'s request/parse/null-on-failure template. Returns NULL on a model error
    /// / unparseable output (the caller treats null as ZERO findings for that group — non-fatal).
    /// </summary>
    private async Task<List<BookFindingItem>?> RunContinuityCallAsync(
        string briefBlock,
        string skeletonBlock,
        string lang,
        CancellationToken ct)
    {
        try
        {
            var bookContextSection = briefBlock + "\n\n" + skeletonBlock + "\n\n";
            var instruction = bookContextSection + _promptFactory.BuildBookReviewContinuityReducePrompt(lang);

            var request = new AiRequest
            {
                InputText = string.Empty, // the full brief + skeleton live in the instruction
                Instruction = instruction,
                TaskType = AiTaskType.BookReview,
                Language = lang,
                JsonMode = true
            };

            var response = await _router.CompleteAsync(request, ct);
            var raw = response.Content;
            if (string.IsNullOrWhiteSpace(raw))
            {
                _logger.LogWarning("Book review (continuity): model returned empty output; treating as zero continuity findings.");
                return null;
            }

            var json = UnifiedAnalysisService.ExtractJson(raw);
            if (string.IsNullOrWhiteSpace(json))
            {
                _logger.LogWarning("Book review (continuity): output had no extractable JSON; treating as zero continuity findings.");
                return null;
            }

            var parsed = JsonSerializer.Deserialize<BookReviewResult>(json, DeserializeOpts);
            if (parsed?.Findings == null)
            {
                _logger.LogWarning("Book review (continuity): JSON had no findings array; treating as zero continuity findings.");
                return null;
            }

            return parsed.Findings;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Book review (continuity): threw during the model call/parse; treating as zero continuity findings.");
            return null;
        }
    }

    /// <summary>
    /// Builds the COMPACT digest of group-level continuity findings for the FINAL reduce, rendered as
    /// skeleton-shaped lines the continuity prompt reads: one line per finding —
    /// <c>continuity | &lt;chapterOrders&gt; | rationale[..140]</c> — wrapped in the
    /// <c>[CONTINUITY_SKELETON]…[/CONTINUITY_SKELETON]</c> markers so the same prompt/parse/mock switch applies.
    /// chapterOrders lists ALL of a finding's anchor orders (a continuity break spans chapters). Capped to the
    /// budget (minus the brief block) by dropping the LOWEST-severity findings first, LOGGED (no silent
    /// truncation), exactly like <see cref="BuildSynthesisDigest"/>.
    /// </summary>
    private string BuildContinuityFindingsDigest(
        IReadOnlyList<BookFindingItem> groupFindings,
        string lang,
        string briefBlock)
    {
        var charsPerToken = BookContextAssembler.CharsPerTokenForLanguage(lang);
        var budget = _contextAssembler.ResolveBudgetTokens(new[] { AiTaskType.BookReview });
        var briefTokens = BookContextAssembler.EstimateTokens(briefBlock, charsPerToken);
        var digestBudget = Math.Max(256, budget - briefTokens);

        var lines = new List<(int Severity, string Line)>(groupFindings.Count);
        foreach (var f in groupFindings)
        {
            var orders = f.ChapterAnchors is { Count: > 0 } anchors
                ? string.Join(",", anchors.Select(a => a.Order))
                : "0";
            var rationale = (f.Rationale ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (rationale.Length > ContinuityRationaleDigestChars)
                rationale = rationale.Substring(0, ContinuityRationaleDigestChars);
            lines.Add((f.Severity, $"continuity | {orders} | {rationale}"));
        }

        var openMarker = BookContextAssembler.ContinuitySkeletonOpen;
        var closeMarker = BookContextAssembler.ContinuitySkeletonClose;
        var markerTokens = BookContextAssembler.EstimateTokens(openMarker + "\n" + closeMarker, charsPerToken);

        var keptCount = lines.Count;
        var runningTokens = markerTokens;
        for (var i = 0; i < lines.Count; i++)
        {
            var lineTokens = BookContextAssembler.EstimateTokens(lines[i].Line + "\n", charsPerToken);
            if (runningTokens + lineTokens > digestBudget)
            {
                keptCount = i;
                break;
            }
            runningTokens += lineTokens;
        }

        List<(int Severity, string Line)> emitted;
        if (keptCount >= lines.Count)
        {
            emitted = lines; // everything fits
        }
        else
        {
            var ranked = lines
                .Select((l, idx) => (l.Severity, l.Line, idx))
                .OrderByDescending(x => x.Severity)
                .ThenBy(x => x.idx)
                .ToList();

            var keepIndices = new HashSet<int>();
            var tokens = markerTokens;
            foreach (var r in ranked)
            {
                var lineTokens = BookContextAssembler.EstimateTokens(r.Line + "\n", charsPerToken);
                if (tokens + lineTokens > digestBudget)
                    continue;
                tokens += lineTokens;
                keepIndices.Add(r.idx);
            }

            emitted = lines.Where((_, idx) => keepIndices.Contains(idx)).ToList();
            _logger.LogWarning(
                "Book review (continuity): the group-findings union digest ({Total} findings, ~{FullTokens} tokens) " +
                "exceeded the reduce budget ({DigestBudget} tokens after the {BriefTokens}-token brief); capped to " +
                "{Kept} findings (dropped {Dropped}, lowest-severity first) so the final reduce input fits the model window.",
                lines.Count, runningTokens, digestBudget, briefTokens, emitted.Count, lines.Count - emitted.Count);
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(openMarker);
        foreach (var (_, line) in emitted)
            sb.AppendLine(line);
        sb.Append(closeMarker);
        return sb.ToString();
    }

    /// <summary>Normalises a model-supplied dimension to one of the six known dimensions (case-insensitive,
    /// trimmed). An unknown or blank value falls back to "plot" — the same unknown-dimension fallback the
    /// per-dimension prompt uses — so a bad self-label never poisons the dedup key or score rollup.</summary>
    private static string NormalizeDimension(string? dimension)
    {
        var d = (dimension ?? string.Empty).Trim().ToLowerInvariant();
        return Array.IndexOf(Dimensions, d) >= 0 ? d : "plot";
    }

    // ─── Per-dimension model call + parse ─────────────────────────────────────────────────────────

    /// <summary>
    /// Runs ONE dimension's review: prepends the shared [BOOK_CONTEXT] section to the dimension prompt,
    /// calls the router with <see cref="AiTaskType.BookReview"/>, parses the single-dimension findings[] via
    /// the shared <see cref="UnifiedAnalysisService.ExtractJson"/> extractor (BOM/bidi-stripping,
    /// balanced-brace). Returns the parsed findings, or NULL when the model errors or the output cannot be
    /// parsed — the caller treats null as zero findings WITHOUT aborting the build.
    /// </summary>
    private async Task<List<BookFindingItem>?> RunDimensionAsync(
        string dimension,
        string lang,
        string bookContextSection,
        CancellationToken ct)
    {
        try
        {
            var instruction = bookContextSection + _promptFactory.BuildBookReviewPrompt(dimension, lang);

            var request = new AiRequest
            {
                InputText = string.Empty, // the whole-book context lives in the instruction's [BOOK_CONTEXT]
                Instruction = instruction,
                // wb2-c03 attaches AnalysisType.BookReview -> AiTaskType.BookReview in AnalysisTaskMapping
                // and the Ai:FeatureModels:BookReview key; tagging the request here labels/caps the call.
                TaskType = AiTaskType.BookReview,
                Language = lang,
                JsonMode = true
            };

            var response = await _router.CompleteAsync(request, ct);
            var raw = response.Content;
            if (string.IsNullOrWhiteSpace(raw))
            {
                _logger.LogWarning("Book review: dimension '{Dimension}' returned empty output; treating as zero findings.", dimension);
                return null;
            }

            var json = UnifiedAnalysisService.ExtractJson(raw);
            if (string.IsNullOrWhiteSpace(json))
            {
                _logger.LogWarning("Book review: dimension '{Dimension}' output had no extractable JSON; treating as zero findings.", dimension);
                return null;
            }

            var parsed = JsonSerializer.Deserialize<BookReviewResult>(json, DeserializeOpts);
            if (parsed?.Findings == null)
            {
                _logger.LogWarning("Book review: dimension '{Dimension}' JSON had no findings array; treating as zero findings.", dimension);
                return null;
            }

            // The model is single-dimension-scoped, but defensively stamp the dimension on every finding so
            // the dedup key + score rollup never key on a mislabelled or blank dimension from the model.
            foreach (var f in parsed.Findings)
                f.Dimension = dimension;

            return parsed.Findings;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Book review: dimension '{Dimension}' threw during the model call/parse; treating as zero findings.",
                dimension);
            return null;
        }
    }

    // ─── Union + dedup ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Unions the per-dimension findings into one set and dedups by
    /// <see cref="BookFinding.ComputeDedupKey"/>(dimension, primaryChapterOrder, rationale) where
    /// primaryChapterOrder = the first chapter anchor's Order, else 0. First occurrence of a dedup key wins;
    /// later duplicates are dropped. Each surviving item is projected to a <see cref="BookFinding"/> with
    /// anchors/evidence chapterId backfilled by Order (Phase-3 navigation), Status defaulted to "open"
    /// (the persist step preserves any prior user Status), and BuiltWithModel stamped.
    /// </summary>
    private static List<BookFinding> UnionAndDedup(
        IReadOnlyList<List<BookFindingItem>?> perDimension,
        IReadOnlyDictionary<int, (Guid Id, string Title)> chaptersByOrder,
        string? builtWithModel,
        string lang)
    {
        var byKey = new Dictionary<string, BookFinding>(StringComparer.Ordinal);

        foreach (var dimensionFindings in perDimension)
        {
            if (dimensionFindings == null)
                continue;

            foreach (var item in dimensionFindings)
            {
                if (string.IsNullOrWhiteSpace(item.Rationale))
                    continue; // a finding with no rationale is not actionable and cannot dedup stably

                // ChapterAnchors/Evidence can arrive null when the model emits "chapterAnchors": null (or a
                // value the deserializer leaves null) — a finding with only rationale + verdict is still valid.
                // Treat missing anchors as empty (primaryOrder 0) instead of letting a NullReferenceException
                // here fail the ENTIRE build (UnionAndDedup runs outside the per-dimension try/catch).
                var primaryOrder = item.ChapterAnchors is { Count: > 0 } ? item.ChapterAnchors[0].Order : 0;
                var dedupKey = BookFinding.ComputeDedupKey(item.Dimension, primaryOrder, item.Rationale);

                if (byKey.ContainsKey(dedupKey))
                    continue; // first occurrence wins

                byKey[dedupKey] = ProjectToEntity(item, dedupKey, chaptersByOrder, builtWithModel, lang);
            }
        }

        return byKey.Values.ToList();
    }

    /// <summary>
    /// Projects a model <see cref="BookFindingItem"/> into a <see cref="BookFinding"/> entity. Anchors and
    /// evidence are CHAPTER-level only (never character offsets); chapterId is backfilled from the book's
    /// chapters by Order where the model gave an Order (for Phase-3 navigation), else left empty/null.
    /// </summary>
    private static BookFinding ProjectToEntity(
        BookFindingItem item,
        string dedupKey,
        IReadOnlyDictionary<int, (Guid Id, string Title)> chaptersByOrder,
        string? builtWithModel,
        string lang)
    {
        // Backfill chapterId on anchors by Order (the model returns order + title, usually not the id).
        // Null-safe: the model can emit "chapterAnchors": null, which the deserializer leaves null; a finding
        // with only rationale/verdict is still valid, so a null list projects to no anchors (not a crash).
        var anchors = item.ChapterAnchors?.Select(a =>
        {
            var resolved = chaptersByOrder.TryGetValue(a.Order, out var ch);
            return new FindingChapterAnchor
            {
                ChapterId = resolved ? ch.Id : a.ChapterId, // keep any id the model supplied if order missed
                Order = a.Order,
                Title = !string.IsNullOrWhiteSpace(a.Title) ? a.Title : (resolved ? ch.Title : string.Empty)
            };
        }).ToList() ?? new List<FindingChapterAnchor>();

        // Backfill chapterId on evidence by chapterOrder where we can (null when the order is unknown).
        // Null-safe for the same reason as anchors above: "evidence": null projects to no evidence.
        var evidence = item.Evidence?.Select(e =>
        {
            Guid? chapterId = e.ChapterId;
            if (chapterId == null && chaptersByOrder.TryGetValue(e.ChapterOrder, out var ch))
                chapterId = ch.Id;
            return new FindingEvidence
            {
                ChapterId = chapterId,
                ChapterOrder = e.ChapterOrder,
                Excerpt = e.Excerpt
            };
        }).ToList() ?? new List<FindingEvidence>();

        var severity = Math.Clamp(item.Severity, 1, 3);

        return new BookFinding
        {
            // Id stamped by the entity default; BookId set by the persist step.
            Language = lang,
            Dimension = item.Dimension,
            Verdict = NormalizeVerdict(item.Verdict),
            Severity = severity,
            Rationale = item.Rationale,
            EvidenceJson = JsonSerializer.Serialize(evidence, SerializeOpts),
            ChapterAnchorsJson = JsonSerializer.Serialize(anchors, SerializeOpts),
            SuggestedAction = string.IsNullOrWhiteSpace(item.SuggestedAction) ? null : item.SuggestedAction,
            Status = "open",
            DedupKey = dedupKey,
            BuiltWithModel = builtWithModel
            // CreatedAt/UpdatedAt stamped by the SaveChanges override.
        };
    }

    private static string NormalizeVerdict(string? verdict)
    {
        var v = (verdict ?? string.Empty).Trim().ToLowerInvariant();
        return v is "keep" or "improve" or "cut" ? v : "improve";
    }

    // ─── Persist preserving user Status across rebuilds ───────────────────────────────────────────

    /// <summary>
    /// Persists the freshly-built findings PRESERVING any user-set Status, mirroring the suggestion
    /// outcome-preservation idiom (match incoming to existing rows by a stable key; never clobber a user
    /// decision on rebuild). Match key = (BookId, DedupKey):
    ///   • MATCH: keep the existing Status (acknowledged/dismissed/done stays; an existing "open" stays
    ///     "open") and refresh the content (verdict/severity/rationale/evidence/anchors/suggestedAction) +
    ///     BuiltWithModel + UpdatedAt — the finding regenerated identically (same key) but its text may have
    ///     shifted slightly.
    ///   • NEW: insert as "open".
    ///   • VANISHED (existing row whose key is NOT in the new set): a user decision must not be lost, so
    ///     DELETE ONLY rows still "open" (pure regenerated noise the model no longer surfaces) and PRESERVE
    ///     any the user acted on (acknowledged/dismissed/done) — they remain as a record of that decision.
    ///     be-c02 SCOPING: the delete is FURTHER gated to only findings whose EVERY anchored chapter order is in
    ///     <paramref name="reviewedChapterOrders"/> (the primaries of windows whose model call SUCCEEDED this
    ///     build). A vanished-open finding anchored to ANY chapter whose window FAILED (or was never covered) is
    ///     PRESERVED — we did not actually re-review that chapter, so its absence from `incoming` is a
    ///     truncation/failure artifact, NOT the model retracting the finding. Checking ALL anchors (not just the
    ///     first) matters for MULTI-chapter continuity findings: one whose first anchor was re-reviewed but a
    ///     later anchored chapter's window failed must survive. Without this scope a partial rebuild (some windows
    ///     fail) would silently wipe the prior still-open findings — and their user Status path was already handled
    ///     above — for the un-reviewed chapters. A parsed-EMPTY window is a SUCCESS per be-c01 (its chapters ARE in
    ///     reviewedChapterOrders and legitimately clean), so a finding anchored ENTIRELY within reviewed chapters
    ///     IS deleted — the window(s) reviewed them and no longer surface it (regenerated noise).
    ///
    /// DECISION (delete-open vs superseded status): we DELETE vanished "open" rows rather than introduce a
    /// "superseded" status. A superseded status would need a migration + widening the status set + FE
    /// handling, with no user value (an open finding the model no longer surfaces is exactly regenerated
    /// noise). Preserving user-acted rows already covers the only case where losing the row would lose
    /// information. So: delete-open / preserve-touched, no schema change.
    /// </summary>
    /// <param name="reviewedChapterOrders">The distinct PRIMARY chapter orders of the windows whose model call
    /// SUCCEEDED this build (be-c01's <c>reviewedPrimaryOrders</c>, passed through verbatim). A vanished-open
    /// finding is deleted ONLY when EVERY chapter order it anchors is in this set; a finding anchored to any
    /// un-reviewed (failed/uncovered) chapter is preserved. On a fully-successful build this covers every reviewed
    /// chapter, so the delete behaves exactly as before (no behavior change).</param>
    /// <param name="chaptersReviewed">data-c01 HONEST coverage numerator to persist (see
    /// <see cref="BookReviewCoverage"/>): chapters actually reviewed this build. Upserted into the (BookId,
    /// Language) coverage row inside this SAME persist step so the status probe stays honest across a reload.</param>
    /// <param name="chaptersTotal">data-c01 HONEST coverage denominator to persist: chapters this build was
    /// responsible for. Always &gt;= <paramref name="chaptersReviewed"/>.</param>
    private async Task PersistPreservingStatusAsync(
        Guid bookId,
        string lang,
        IReadOnlyList<BookFinding> incoming,
        IReadOnlySet<int> reviewedChapterOrders,
        int chaptersReviewed,
        int chaptersTotal,
        CancellationToken ct)
    {
        var existing = await _db.BookFindings
            .Where(f => f.BookId == bookId && f.Language == lang)
            .ToListAsync(ct);

        var existingByKey = existing
            .GroupBy(f => f.DedupKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var incomingKeys = new HashSet<string>(incoming.Select(f => f.DedupKey), StringComparer.Ordinal);

        foreach (var fresh in incoming)
        {
            fresh.BookId = bookId;

            if (existingByKey.TryGetValue(fresh.DedupKey, out var prior))
            {
                // MATCH: preserve the user's Status, refresh content + model + UpdatedAt.
                prior.Dimension = fresh.Dimension;
                prior.Verdict = fresh.Verdict;
                prior.Severity = fresh.Severity;
                prior.Rationale = fresh.Rationale;
                prior.EvidenceJson = fresh.EvidenceJson;
                prior.ChapterAnchorsJson = fresh.ChapterAnchorsJson;
                prior.SuggestedAction = fresh.SuggestedAction;
                prior.BuiltWithModel = fresh.BuiltWithModel;
                // prior.Status intentionally untouched (user decision preserved; "open" stays "open").
                _db.Entry(prior).State = EntityState.Modified; // force UpdatedAt refresh via the override
            }
            else
            {
                // NEW: insert as "open" (already defaulted on the projected entity).
                _db.BookFindings.Add(fresh);
            }
        }

        // VANISHED rows: delete ONLY those still "open" (regenerated noise); preserve user-acted ones. be-c02:
        // AND scope the delete to chapters actually REVIEWED this build — a still-open finding vanishes from the
        // delete ONLY when EVERY chapter order it anchors is in `reviewedChapterOrders` (windows that SUCCEEDED).
        // A finding anchored to ANY chapter whose window FAILED / was uncovered is PRESERVED (its absence from
        // `incoming` is a truncation/failure artifact, not the model retracting it), stopping a partial rebuild
        // from silently wiping prior open findings. The anchor orders of an EXISTING row are derived in memory
        // from its persisted ChapterAnchorsJson (every anchor's Order, else {0}) — the same no-anchor convention
        // UnionAndDedup uses — since the JSON is not SQL-queryable.
        foreach (var stale in existing)
        {
            if (incomingKeys.Contains(stale.DedupKey))
                continue; // still present → handled above
            if (!string.Equals(stale.Status, "open", StringComparison.Ordinal))
                continue; // acknowledged/dismissed/done → preserve the user's decision (keep the row).
            // be-c02 multi-anchor scope: a vanished-open finding is deleted ONLY when EVERY chapter it anchors was
            // reviewed this build. A MULTI-chapter continuity finding (anchors spanning e.g. ch 5 and ch 12) whose
            // FIRST anchor was re-reviewed but another anchored chapter's window FAILED / was uncovered must be
            // PRESERVED — its absence from `incoming` is a truncation/failure artifact for the un-reviewed anchor,
            // not the model retracting the finding. Requiring ALL anchor orders (not just the first, as the old
            // PrimaryChapterOrderOf did) closes that gap. A no-anchor finding maps to {0}, preserving the prior
            // order-0 convention: deletable only when 0 is itself a reviewed order.
            if (!ChapterOrdersOf(stale).All(reviewedChapterOrders.Contains))
                continue; // at least one anchored chapter was NOT re-reviewed this build → preserve.
            _db.BookFindings.Remove(stale);
        }

        // data-c01: UPSERT the HONEST coverage (reviewed/total) into the (BookId, Language) BookReviewCoverage
        // row inside THIS persist step, so both the findings and the coverage commit atomically on the same
        // SaveChanges — and the same detach-on-DbUpdateException hygiene below covers this row too. Reaching here
        // means the build PERSISTED (the caller skips this method on total-failure / no-op / briefs-missing), so
        // a bad build never overwrites a good prior coverage row (the cache-preservation contract).
        var coverage = await _db.BookReviewCoverages
            .FirstOrDefaultAsync(c => c.BookId == bookId && c.Language == lang, ct);
        if (coverage == null)
        {
            _db.BookReviewCoverages.Add(new BookReviewCoverage
            {
                BookId = bookId,
                Language = lang,
                ChaptersReviewed = chaptersReviewed,
                ChaptersTotal = chaptersTotal
            });
        }
        else
        {
            coverage.ChaptersReviewed = chaptersReviewed;
            coverage.ChaptersTotal = chaptersTotal;
            // UpdatedAt is bumped by the SaveChanges override when the entity is Modified.
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // A unique-index violation (e.g. the (BookId, Language, DedupKey) constraint, or a concurrent
            // build racing the same key) must not leave this SCOPED DbContext dirty — the caller goes on to
            // read CountAsync off the same context, and a future caller reusing it would re-attempt the failed
            // writes. Mirror BookSummaryService.RunBuildAsync: log, DETACH every Added/Modified BookFinding AND
            // the coverage row queued this batch (data-c01) so the failed batch is not retried, then surface a
            // clean failure for the build to report.
            _logger.LogWarning(ex,
                "Failed to persist BookFinding rows for book {BookId} ({Lang}); detaching the dirty batch", bookId, lang);

            foreach (var entry in _db.ChangeTracker.Entries<BookFinding>().ToList())
            {
                if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                    entry.State = EntityState.Detached;
            }

            foreach (var entry in _db.ChangeTracker.Entries<BookReviewCoverage>().ToList())
            {
                if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                    entry.State = EntityState.Detached;
            }

            throw new InvalidOperationException(
                $"Failed to persist whole-book review findings for book {bookId} ({lang}).", ex);
        }
    }

    /// <summary>The order set an EXISTING finding with NO usable anchors maps to — the single order 0, matching
    /// the primaryOrder=0 no-anchor convention <see cref="UnionAndDedup"/> uses for INCOMING findings (so a
    /// no-anchor row is deletable only when 0 is itself a reviewed order). Shared instance to avoid re-allocating.</summary>
    private static readonly IReadOnlyCollection<int> NoAnchorOrders = new[] { 0 };

    /// <summary>
    /// Derives the FULL set of chapter orders an EXISTING persisted <see cref="BookFinding"/> anchors, from its
    /// <see cref="BookFinding.ChapterAnchorsJson"/> (a serialized <c>List&lt;FindingChapterAnchor&gt;</c>): EVERY
    /// anchor's <c>Order</c> (deduped), or <see cref="NoAnchorOrders"/> ({0}) when there are none — the SAME
    /// no-anchor convention <see cref="UnionAndDedup"/> uses for INCOMING findings. Used by the be-c02 scoped
    /// delete to require that ALL of a finding's anchored chapters were reviewed this build before a vanished-open
    /// row is deleted, so a MULTI-chapter continuity finding is not wiped when only its first anchor was
    /// re-reviewed. The JSON is not SQL-queryable, so this runs in memory on the already-loaded rows. Deserialized
    /// with <see cref="DeserializeOpts"/> (case-insensitive CamelCase), matching the CamelCase writer in
    /// <see cref="ProjectToEntity"/>. A malformed / empty payload is treated defensively as {0} (a review-content
    /// wipe must never be triggered by a parse blip — order 0 is only deletable when 0 is itself a reviewed order).
    /// </summary>
    private static IReadOnlyCollection<int> ChapterOrdersOf(BookFinding finding)
    {
        if (string.IsNullOrWhiteSpace(finding.ChapterAnchorsJson))
            return NoAnchorOrders;
        try
        {
            var anchors = JsonSerializer.Deserialize<List<FindingChapterAnchor>>(finding.ChapterAnchorsJson, DeserializeOpts);
            if (anchors is { Count: > 0 })
                return anchors.Select(a => a.Order).Distinct().ToList();
            return NoAnchorOrders;
        }
        catch (JsonException)
        {
            return NoAnchorOrders;
        }
    }

    // ─── Score rollup ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rolls up the per-dimension keep/improve/cut counts from a set of persisted findings and assigns a
    /// holistic Score label per dimension by a simple, documented rule:
    ///   • "weak"   when cut findings outnumber keep findings (more to remove than to preserve), OR there is
    ///              at least one severity-3 (major) improve/cut finding;
    ///   • "strong" when there are keep findings AND no improve/cut findings (only strengths surfaced);
    ///   • "mixed"  otherwise (the common case: some strengths, some fixable weaknesses).
    /// Only dimensions that produced at least one finding get a score row.
    /// </summary>
    private static List<BookReviewDimensionScore> RollUpScoresFromFindings(IReadOnlyList<BookFinding> findings)
    {
        var scores = new List<BookReviewDimensionScore>();

        foreach (var dimension in Dimensions)
        {
            var inDim = findings.Where(f =>
                string.Equals(f.Dimension, dimension, StringComparison.OrdinalIgnoreCase)).ToList();
            if (inDim.Count == 0)
                continue;

            var keep = inDim.Count(f => string.Equals(f.Verdict, "keep", StringComparison.OrdinalIgnoreCase));
            var improve = inDim.Count(f => string.Equals(f.Verdict, "improve", StringComparison.OrdinalIgnoreCase));
            var cut = inDim.Count(f => string.Equals(f.Verdict, "cut", StringComparison.OrdinalIgnoreCase));

            var hasMajorProblem = inDim.Any(f =>
                f.Severity >= 3 &&
                (string.Equals(f.Verdict, "improve", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f.Verdict, "cut", StringComparison.OrdinalIgnoreCase)));

            string label;
            if (cut > keep || hasMajorProblem)
                label = "weak";
            else if (keep > 0 && improve == 0 && cut == 0)
                label = "strong";
            else
                label = "mixed";

            scores.Add(new BookReviewDimensionScore
            {
                Dimension = dimension,
                Score = label,
                KeepCount = keep,
                ImproveCount = improve,
                CutCount = cut
            });
        }

        return scores;
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the assembled book context as the prompt-prefix section, EXACTLY as <see cref="BookContextAssembler"/>
    /// produced it. The assembler already wraps the BookBrief in a [BOOK_CONTEXT]…[/BOOK_CONTEXT] block (its own
    /// FormatBookBrief emits the markers) and appends the chapter briefs after it, so this MUST NOT add a SECOND
    /// [BOOK_CONTEXT] wrapper: doing so nested the markers and stranded every chapter brief between the inner
    /// [/BOOK_CONTEXT] and the outer one, leaving the model — and only the model, since the eval harness and
    /// every other assembly.Text consumer read it raw — with a malformed, double-wrapped context. Passing
    /// assembly.Text through verbatim gives the review the SAME context shape every other consumer of the
    /// assembler sends; the caller appends the dimension/combined instruction after the trailing blank line.
    /// </summary>
    private static string BuildBookContextSection(string assembledText)
    {
        if (string.IsNullOrWhiteSpace(assembledText))
            return string.Empty;
        return assembledText.Trim() + "\n\n";
    }

    private async Task<Dictionary<int, (Guid Id, string Title)>> LoadChaptersByOrderAsync(
        Guid bookId, CancellationToken ct)
    {
        var chapters = await _db.Chapters
            .AsNoTracking()
            .Where(c => c.BookId == bookId)
            .Select(c => new { c.Id, c.Order, c.Title })
            .ToListAsync(ct);

        // A book should have unique chapter Orders; if two share an Order, the first wins (deterministic).
        var map = new Dictionary<int, (Guid Id, string Title)>();
        foreach (var c in chapters.OrderBy(c => c.Order))
            map.TryAdd(c.Order, (c.Id, c.Title ?? string.Empty));
        return map;
    }

    /// <summary>Cheap probe: does (bookId, lang) have any usable structured briefs (the dense path the
    /// review reads)? Composes the L1 briefs through the SAME freshness gate the assembler uses, without
    /// running any LLM call, so status agrees with what a build would actually find.</summary>
    private async Task<bool> HasUsableBriefsAsync(Guid bookId, string lang, CancellationToken ct)
    {
        // The assembler routes through BookContextAssembler, which itself composes briefs via
        // BookSummaryService. Rather than couple to that internal here, run the same cheap composition probe
        // the assembler uses: a structured assembly with at least one included brief OR a BookBrief.
        var assembly = await _contextAssembler.AssembleAsync(
            bookId, lang, consumingTasks: new[] { AiTaskType.BookReview }, ct);
        return BookContextAssembly.HasUsableBriefs(assembly);
    }
}
