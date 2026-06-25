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

    /// <summary>Per-dimension prompts that failed to parse / errored and contributed ZERO findings. The job
    /// is never aborted by these; the other dimensions still persist.</summary>
    public int FailedDimensions { get; init; }

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
    /// that clears the in-progress registry even on a crash. Assembles the budgeted [BOOK_CONTEXT] ONCE,
    /// then PRODUCES the per-dimension findings via one of two strategies selected by
    /// <see cref="AiOptions.BookReviewSingleCombined"/> (default true = a single combined call across all six
    /// dimensions via <see cref="RunCombinedAsync"/>; false = the per-dimension fan-out via
    /// <see cref="RunPerDimensionFanOutAsync"/>). Both feed the SAME union/dedup/persist/rollup/reporting tail.
    /// </summary>
    private async Task<BookReviewBuildResult> RunBuildAsync(
        Guid bookId,
        string lang,
        Guid? jobId,
        CancellationToken ct)
    {
        // 1. Assemble the budgeted book context ONCE, budgeted to the BookReview task's window.
        var assembly = await _contextAssembler.AssembleAsync(
            bookId, lang, consumingTasks: new[] { AiTaskType.BookReview }, ct);

        // 2. BRIEFS-ABSENT GUARD (before spending any model calls). The review reads the dense structured
        //    briefs; producing findings from the degraded flat-text fallback would be unanchored noise. We
        //    gate on: the assembly did NOT use the structured-brief path (UsedStructuredBriefs == false) OR
        //    it has no usable brief content at all (BookBrief == null AND no included chapter briefs). In
        //    either case surface a clear "build the book summary first" outcome and spend NO model calls.
        var briefsAbsent = !BookContextAssembly.HasUsableBriefs(assembly);
        if (briefsAbsent)
        {
            const string guidance = "Build the book summary first; the whole-book review reads the chapter briefs.";
            _logger.LogInformation(
                "Book review build for book {BookId} ({Lang}) skipped: no usable structured briefs " +
                "(UsedStructuredBriefs={Used}, BookBrief={HasBookBrief}, IncludedBriefs={IncludedCount}).",
                bookId, lang, assembly.UsedStructuredBriefs, assembly.BookBrief != null,
                assembly.IncludedChapterBriefs.Count);

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

            return new BookReviewBuildResult
            {
                Ready = false,
                NoOp = false,
                BriefsMissing = true,
                FindingCount = existingCount,
                FailedDimensions = 0,
                Message = guidance
            };
        }

        // Build the [BOOK_CONTEXT] section once (shared by both the combined call and per-dimension fan-out).
        var bookContextSection = BuildBookContextSection(assembly.Text);

        // Chapter Order → (Id, Title) for backfilling anchors/evidence chapterId (Phase-3 navigation).
        var chaptersByOrder = await LoadChaptersByOrderAsync(bookId, ct);

        // 3. PRODUCE the per-dimension finding arrays. Two strategies, selected by AiOptions
        //    .BookReviewSingleCombined (default true = ONE combined call across all six dimensions; false =
        //    the per-dimension fan-out of six calls). BOTH return a Dimensions-length array of
        //    List<BookFindingItem>? (null entry == that dimension produced no findings) plus the count of
        //    failed dimensions, so the union/dedup/persist/rollup/reporting below is IDENTICAL for both.
        var singleCombined = _aiOptions.Value.BookReviewSingleCombined;
        var (perDimension, failedDimensions) = singleCombined
            ? await RunCombinedAsync(lang, bookContextSection, jobId, ct)
            : await RunPerDimensionFanOutAsync(lang, bookContextSection, jobId, ct);

        ct.ThrowIfCancellationRequested();

        // 4 + 5. UNION across dimensions and DEDUP by BookFinding.ComputeDedupKey(dimension, primaryOrder,
        //         rationale). First occurrence wins; later duplicates are merged away.
        var builtWithModel = ActiveBookReviewModel;
        var deduped = UnionAndDedup(perDimension, chaptersByOrder, builtWithModel, lang);

        // 6. TOTAL FAILURE: the build produced ZERO fresh findings. This is a total failure whether the
        //    dimensions ERRORED / were unparseable (null slots, failedDimensions == Dimensions.Length) OR they
        //    parsed cleanly but EVERY one returned an empty findings[] (a degenerate result we must not trust:
        //    typically the model silently truncating a too-large context, or the combined call emitting
        //    {"findings": []}). The earlier gate keyed ONLY on failedDimensions == Dimensions.Length, so an
        //    all-empty build slipped through as a SUCCESS — and worse, the persist below then ran with no
        //    incoming rows and DELETED every still-open cached finding, silently wiping a good prior review.
        //    Keying on the produced-finding count catches the errored AND the empty case, and is the single
        //    point that also covers findings dropped by the rationale filter in UnionAndDedup.
        var totalFailure = deduped.Count == 0;

        // 7. PERSIST preserving user-set Status across the rebuild — ONLY when fresh findings were produced. On
        //    a total failure we SKIP the persist entirely so the cached review survives a bad build; running it
        //    with an empty incoming set would delete every still-open cached finding.
        if (!totalFailure)
            await PersistPreservingStatusAsync(bookId, lang, deduped, ct);

        var totalNow = await _db.BookFindings.CountAsync(
            f => f.BookId == bookId && f.Language == lang, ct);

        string msg;
        if (totalFailure)
        {
            msg = singleCombined
                ? "Whole-book review failed: the combined review call produced no findings. " +
                  "Try again; if it persists the book may be too large for the model context."
                : $"Whole-book review failed: no findings were produced across any of the {Dimensions.Length} dimensions. " +
                  "Try again; if it persists the book may be too large for the model context.";
        }
        else if (!singleCombined && failedDimensions > 0)
        {
            // PARTIAL warning only applies to the per-dimension path: a combined pass either parses (total
            // success) or does not (total failure), so it never reports a partial-dimension warning.
            msg = $"Whole-book review built with warnings: {deduped.Count} findings across " +
                  $"{Dimensions.Length - failedDimensions}/{Dimensions.Length} dimensions ({failedDimensions} failed).";
        }
        else
        {
            msg = singleCombined
                ? $"Whole-book review built: {deduped.Count} findings across the six dimensions in one combined pass."
                : $"Whole-book review built: {deduped.Count} findings across {Dimensions.Length} dimensions.";
        }

        if (jobId.HasValue)
            _progress.SetStatus(
                jobId.Value,
                totalFailure ? AnalysisProgressStatus.Failed : AnalysisProgressStatus.Succeeded,
                msg);

        return new BookReviewBuildResult
        {
            // A total failure is NOT a ready review — even if stale acted-on rows linger, no fresh review
            // was produced this build, so the FE must not treat it as a successful (re)build.
            Ready = !totalFailure,
            NoOp = false,
            BriefsMissing = false,
            FindingCount = totalNow,
            FailedDimensions = failedDimensions,
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
    /// SINGLE-COMBINED strategy (toggle ON, the DEFAULT): runs ONE LLM call that reviews all six dimensions
    /// over the shared [BOOK_CONTEXT], parses the multi-dimension findings[], validates/normalises each
    /// finding's self-labelled dimension to one of the six (so dedup + rollup never key on a bad value), and
    /// BUCKETS the findings into the same Dimensions-length array the per-dimension path produces (so the
    /// shared union/dedup/persist/rollup is identical).
    ///
    /// FAILURE SEMANTICS adapted from the per-dimension total-failure logic (wb2-c05): the single call is the
    /// only producer, so if it returns null (model error / unparseable / empty) that is a TOTAL failure -
    /// reported as failedDimensions == Dimensions.Length so the caller surfaces a FAILED job + non-ready
    /// result, mirroring the per-dimension all-failed case. A successful parse is failedDimensions == 0.
    ///
    /// PROGRESS: a single call cannot sensibly report 0/6 dimensions, so it reports a one-chunk
    /// combined-pass progress (started → completed) rather than the per-dimension 6-chunk progress.
    /// </summary>
    private async Task<(List<BookFindingItem>?[] PerDimension, int FailedDimensions)> RunCombinedAsync(
        string lang,
        string bookContextSection,
        Guid? jobId,
        CancellationToken ct)
    {
        if (jobId.HasValue)
        {
            _progress.SetTotalChunks(jobId.Value, 1, "Reviewing all six dimensions in one pass");
            _progress.ChunkStarted(jobId.Value, 1, 1);
        }

        var combined = await RunCombinedCallAsync(lang, bookContextSection, ct);

        if (jobId.HasValue)
            _progress.ChunkCompleted(jobId.Value, 1, 1);

        var perDimension = new List<BookFindingItem>?[Dimensions.Length];

        // TOTAL failure: the single combined call produced nothing USABLE — either unparseable / errored (null)
        // OR it parsed but returned an EMPTY findings[]. A whole-book developmental review with zero findings
        // across all six dimensions is not a credible "clean" result; it is the degenerate/truncation symptom,
        // so empty is a total failure just like null. The single call is the ONLY producer here (unlike the
        // per-dimension path, where one dimension legitimately returning nothing is a clean dimension, not a
        // failure). Mark EVERY dimension slot as failed (null) so failedDimensions == Dimensions.Length and the
        // caller treats it as a total failure (FAILED job, not a green finish that would then wipe the cache).
        if (combined == null || combined.Count == 0)
            return (perDimension, Dimensions.Length);

        // Bucket the parsed findings into their (validated) dimension slots so the shared UnionAndDedup runs
        // unchanged. Each finding's dimension is normalised to one of the six; an unknown/blank value falls
        // back to "plot" (the per-dimension prompt's own unknown-dimension fallback) so it never poisons the
        // dedup key or score rollup.
        var dimensionIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < Dimensions.Length; i++)
            dimensionIndex[Dimensions[i]] = i;

        foreach (var item in combined)
        {
            var normalized = NormalizeDimension(item.Dimension);
            item.Dimension = normalized;
            var slot = dimensionIndex[normalized];
            (perDimension[slot] ??= new List<BookFindingItem>()).Add(item);
        }

        // A successful combined parse is zero failed dimensions (even if some dimensions legitimately have no
        // findings — that is a clean result, not a failure).
        return (perDimension, 0);
    }

    /// <summary>
    /// Runs the ONE combined review call: prepends the shared [BOOK_CONTEXT] to
    /// <see cref="PromptFactory.BuildBookReviewCombinedPrompt"/>, calls the router with
    /// <see cref="AiTaskType.BookReview"/>, and parses the multi-dimension findings[] via the shared
    /// <see cref="UnifiedAnalysisService.ExtractJson"/> extractor. Returns the parsed findings, or NULL when
    /// the model errors or the output cannot be parsed (the caller treats null as a TOTAL failure). Mirrors
    /// <see cref="RunDimensionAsync"/>'s request shape + null-on-failure contract.
    /// </summary>
    private async Task<List<BookFindingItem>?> RunCombinedCallAsync(
        string lang,
        string bookContextSection,
        CancellationToken ct)
    {
        try
        {
            var instruction = bookContextSection + _promptFactory.BuildBookReviewCombinedPrompt(lang);

            var request = new AiRequest
            {
                InputText = string.Empty, // the whole-book context lives in the instruction's [BOOK_CONTEXT]
                Instruction = instruction,
                TaskType = AiTaskType.BookReview,
                Language = lang,
                JsonMode = true
            };

            var response = await _router.CompleteAsync(request, ct);
            var raw = response.Content;
            if (string.IsNullOrWhiteSpace(raw))
            {
                _logger.LogWarning("Book review (combined): model returned empty output; treating as total failure.");
                return null;
            }

            var json = UnifiedAnalysisService.ExtractJson(raw);
            if (string.IsNullOrWhiteSpace(json))
            {
                _logger.LogWarning("Book review (combined): output had no extractable JSON; treating as total failure.");
                return null;
            }

            var parsed = JsonSerializer.Deserialize<BookReviewResult>(json, DeserializeOpts);
            if (parsed?.Findings == null)
            {
                _logger.LogWarning("Book review (combined): JSON had no findings array; treating as total failure.");
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
                "Book review (combined): threw during the model call/parse; treating as total failure.");
            return null;
        }
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
    ///
    /// DECISION (delete-open vs superseded status): we DELETE vanished "open" rows rather than introduce a
    /// "superseded" status. A superseded status would need a migration + widening the status set + FE
    /// handling, with no user value (an open finding the model no longer surfaces is exactly regenerated
    /// noise). Preserving user-acted rows already covers the only case where losing the row would lose
    /// information. So: delete-open / preserve-touched, no schema change.
    /// </summary>
    private async Task PersistPreservingStatusAsync(
        Guid bookId,
        string lang,
        IReadOnlyList<BookFinding> incoming,
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

        // VANISHED rows: delete ONLY those still "open" (regenerated noise); preserve user-acted ones.
        foreach (var stale in existing)
        {
            if (incomingKeys.Contains(stale.DedupKey))
                continue; // still present → handled above
            if (string.Equals(stale.Status, "open", StringComparison.Ordinal))
                _db.BookFindings.Remove(stale);
            // else: acknowledged/dismissed/done → preserve the user's decision (keep the row).
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
            // writes. Mirror BookSummaryService.RunBuildAsync: log, DETACH every Added/Modified BookFinding so
            // the failed batch is not retried, then surface a clean failure for the build to report.
            _logger.LogWarning(ex,
                "Failed to persist BookFinding rows for book {BookId} ({Lang}); detaching the dirty batch", bookId, lang);

            foreach (var entry in _db.ChangeTracker.Entries<BookFinding>().ToList())
            {
                if (entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
                    entry.State = EntityState.Detached;
            }

            throw new InvalidOperationException(
                $"Failed to persist whole-book review findings for book {bookId} ({lang}).", ex);
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
