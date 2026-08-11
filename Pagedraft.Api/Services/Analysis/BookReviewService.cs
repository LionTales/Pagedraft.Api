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

    /// <summary>
    /// Wave 3 / M3. Findings still at status <c>open</c> - never touched by the author. NOT derivable from
    /// <see cref="FindingCount"/> minus <see cref="ResolvedFindingCount"/>, because <c>acknowledged</c> is a
    /// third bucket that is neither.
    /// </summary>
    public int OpenFindingCount { get; init; }

    /// <summary>
    /// Wave 3 / M3. Findings at status <c>dismissed</c> or <c>done</c> - the same partition the shipped
    /// findings ledger calls "resolved" (its active group is open + acknowledged). Working-through progress is
    /// this over <see cref="FindingCount"/>, so the stage spine can render it WITHOUT downloading the whole
    /// findings list. Read the split from <see cref="FindingStatusPartition"/>; do not re-spell the status
    /// strings at a second call site.
    /// </summary>
    public int ResolvedFindingCount { get; init; }

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
///
/// FILE-SIZE WAIVER (CLAUDE.md's ~700-line soft ceiling) — STATED HONESTLY, BECAUSE THE LAST ONE WAS NOT.
/// This file is STILL OVER the ceiling and there is no version of this note that makes that acceptable by
/// assertion. The b1-b8 review recorded a "pre-existing waiver" for this file; be-c09 (P2-7) went looking for it
/// and IT DID NOT EXIST — a claimed waiver is exactly the false-invariant-comment class this subsystem has now
/// shipped five times, so what follows is a measurement, not a defence.
///
/// WHAT WAS EXTRACTED (be-c09, a pure MOVE — no behavior change, same test count before and after):
///   • <see cref="BookReviewDigests"/> — the two REDUCE digests + their rationale caps and no-anchor token. They
///     decide what a reduce pass SEES, which is the same thing as deciding its anchor allowlist; they belong with
///     each other (the two must AGREE) far more than with the orchestration.
///   • <see cref="BookFindingReconciler"/> — the persist-time MATCH TIERS, the scoped-delete predicate, and the
///     anchor-scope tri-state they both read. Pure, static, and the only part of the persist step that decides
///     anything; the service keeps the EF work.
///   • <c>ChapterAnchorResolver.LogResolution</c> — observability over the resolver's OWN counters, moved to the
///     class that owns them.
/// Also already extracted by the b1-b8 change set itself, for the same reason: <see cref="ChapterAnchorResolver"/>,
/// <see cref="NearDuplicateCollapser"/> (its own, separately-argued waiver), <see cref="SynthesisMergeMap"/>,
/// <see cref="DigestAnchorGate"/>, <see cref="BookReviewResponseParser"/>, <see cref="WindowOutcome"/>.
///
/// WHAT REMAINS, AND WHY IT IS STILL TOO BIG: one sequenced build pipeline (assemble → window MAP → synthesis
/// reduce → continuity reduce → union/dedup → repair → persist → roll up) plus the status/coverage probes, all
/// sharing this class's injected state (_db, _router, _progress, _contextAssembler). Every remaining member is
/// either a step of that one flow or a private helper of a step, so a further split would cut the pipeline in the
/// middle and thread this class's fields through the seam — buying a line count, not a boundary. The honest next
/// cut, if this grows again, is the four public result/status DTOs at the top of this file (they are not part of
/// the service at all) and then the continuity-reduce PLANNING (grouping/packing), which is genuinely a separate
/// algorithm. Neither was done here: be-c09's charter was the members the review NAMED, and a refactor that
/// wanders is a refactor that hides a behavior change.
/// </summary>
public class BookReviewService
{
    /// <summary>The six editorial dimensions the review fans out over. Order is stable for deterministic
    /// progress reporting and dedup-iteration; the model is instructed to stamp each finding's dimension.</summary>
    private static readonly string[] Dimensions =
        { "plot", "character", "pacing", "tone", "theme", "continuity" };

    // NIT-5: internal (not private) so SynthesisMergeMap.cs — which round-trips the SAME BookFinding.ChapterAnchorsJson
    // field via the SAME shape (List&lt;FindingChapterAnchor&gt;) — can reference these directly instead of
    // re-declaring an identical pair of its own. Two independently-maintained option sets for the same wire shape
    // is drift waiting to happen; single-sourcing them here removes the possibility rather than documenting it.
    internal static readonly JsonSerializerOptions DeserializeOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal static readonly JsonSerializerOptions SerializeOpts = new()
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
    private readonly DynamicTermRepairService _dynamicTermRepair;
    private readonly IBookEntityProvider _bookEntityProvider;

    public BookReviewService(
        AppDbContext db,
        BookContextAssembler contextAssembler,
        PromptFactory promptFactory,
        IAiRouter router,
        AnalysisProgressTracker progress,
        BookReviewBuildRegistry buildRegistry,
        IOptions<AiOptions> aiOptions,
        ILogger<BookReviewService> logger,
        DynamicTermRepairService dynamicTermRepair,
        IBookEntityProvider bookEntityProvider)
    {
        _db = db;
        _contextAssembler = contextAssembler;
        _promptFactory = promptFactory;
        _router = router;
        _progress = progress;
        _buildRegistry = buildRegistry;
        _aiOptions = aiOptions;
        _logger = logger;
        _dynamicTermRepair = dynamicTermRepair;
        _bookEntityProvider = bookEntityProvider;
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

        // Status rides along on the SAME projection (Wave 3 / M3): the working-through counts cost one more
        // column on a query that already runs, not a second request and not the full findings payload.
        var findings = await _db.BookFindings
            .AsNoTracking()
            .Where(f => f.BookId == bookId && f.Language == lang)
            .Select(f => new { f.UpdatedAt, f.BuiltWithModel, f.Status })
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
            OpenFindingCount = findings.Count(f => FindingStatusPartition.IsOpen(f.Status)),
            ResolvedFindingCount = findings.Count(f => FindingStatusPartition.IsResolved(f.Status)),
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
        //    be-c02: the CHARACTER REGISTER is read FIRST (one AsNoTracking row, zero LLM calls) and handed to
        //    the assembler, which charges its block against every window's budget. This is the one analysis
        //    whose stated job is judging characters and it used to be the one analysis with no character data.
        var characterRegister = await LoadCharacterRegisterForReviewAsync(bookId, ct);

        var windows = await _contextAssembler.AssembleWindowsAsync(
            bookId, lang, consumingTasks: new[] { AiTaskType.BookReview },
            characterRegister: characterRegister, ct: ct);

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
        // be-c01: windows that PARSED but returned ZERO findings — a SUSPECTED TRUNCATION (see WindowOutcome).
        // Kept SEPARATE from failedUnits on purpose: a failed window is an OBSERVED failure (error/unparseable) and
        // the FE renders it as "N window(s) failed"; an empty window is an UNPROVEN one. It is surfaced instead
        // where it is honest and already first-class — the coverage claim (reviewed < total) — plus a WARNING log.
        var emptyWindowCount = 0;
        var ranSynthesis = false;          // synthesis reduce pass executed
        var ranContinuityReduce = false;   // continuity reduce pass executed

        // b8 — the synthesis MERGE MAP, declared out here because it is PRODUCED by the synthesis pass inside the
        // windowed block and CONSUMED by UnionAndDedup below it. NULL means "the synthesis pass did not run"
        // (the legacy per-dimension path, or a book with no BookBrief): there is then no merge channel and nothing
        // to report, which is a different thing from "it ran and proposed no merges" (an instance with 0 groups).
        SynthesisMergeMap.Resolution? synthesisMerges = null;

        // wb4-c06 HONEST COVERAGE — the REVIEWED set, and the licence for the DESTRUCTIVE delete. Declared in the
        // OUTER scope (not inside the windowed block) for two reasons: (1) it is populated ONLY by a window whose
        // call produced findings (WindowOutcome.Reviewed), so neither a FAILED window nor an EMPTY (suspected
        // truncation) one inflates the numerator; (2) a downstream persist step (be-c02) reuses this exact set at
        // the PersistPreservingStatusAsync call site below, which lives outside the windowed block, where it scopes
        // the delete-vanished-open pass. Keep it in scope there — do not collapse it to a local count.
        var reviewedPrimaryOrders = new HashSet<int>();

        // be-c03 (P1-2) — THE REVIEWABLE ORDER SET. The destructive delete reasons over THREE chapter-order sets,
        // and this is the middle one: reviewed ⊆ reviewable ⊆ real. It is the set of orders this build actually PUT
        // IN FRONT OF THE MODEL (windowed: the union of every window's PRIMARY orders; legacy: every chapter, since
        // that path concatenates the whole book into ONE context — see each branch below).
        //
        // It is NOT chaptersByOrder.Keys on the windowed path, and that distinction is the whole fix. A GENUINELY
        // EMPTY chapter (a title-only "Part I" divider, a DOCX artefact) produces a NULL block in
        // BookContextAssembler.BuildChapterBlock and is SKIPPED by the windower, so it is never a primary of any
        // window and can NEVER enter reviewedPrimaryOrders — on ANY build, by ANY model, forever. b3's book-wide
        // (no-anchor) delete rule asks "did this build review the finding's WHOLE scope?"; measuring that against the
        // RAW chapter set made the question PERMANENTLY unanswerable on such a book (reviewed ⊇ real could never be
        // true), so every vanished-open BOOK-WIDE finding was preserved on every rebuild = unbounded accumulation:
        // the exact immortal-orphan class b2 was written to kill, resurrected through a different set. The honest
        // question is "did this build review everything it COULD review?" — and that is what this set answers.
        //
        // It must stay SEPARATE from realChapterOrders (do not collapse them): the phantom-anchor half of
        // BookFindingReconciler.IsVanishedOpenDeletable needs the REAL set to tell "this anchor order is no chapter of this book at all"
        // (a phantom → no preservation weight) from "a REAL chapter that was not reviewed this build" (→ PRESERVE,
        // the be-c02 rule that keeps a multi-chapter continuity finding alive when a LATER anchor's window failed).
        // Merging the sets would either resurrect the immortal orphan or start deleting findings we never re-read.
        var totalReviewableOrders = new HashSet<int>();

        // be-c02 (P1-1) — THE DIGEST ANCHOR GATE. The two REDUCE passes anchor from a DIGEST of what the earlier
        // passes found, and b7 derives their allowlist AND their shown-set from the orders that digest prints. So a
        // digest rendered from the windows' RAW anchors would hand the reduce the windows' HALLUCINATIONS as an
        // allowlist, and the resolver — seeing a real order inside the reduce's own shown-set — would then accept
        // them. The gate resolves each finding's anchors against the REAL chapters AND the finding's OWN shown-set
        // BEFORE they can become either. Built once here (it is a pure function of the book's chapters) and threaded
        // into both reduce passes; it is a SEPARATE ChapterAnchorResolver instance from the build's, on purpose —
        // this one only PREVIEWS, and must not add its answers to the drop counters the build warns on.
        var digestAnchorGate = new DigestAnchorGate(chaptersByOrder);

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

            // wb4-c06 HONEST COVERAGE — the DENOMINATOR (total reviewable set, `totalReviewableOrders`, declared in
            // the OUTER scope above because be-c03 also feeds it to the persist step): union of EVERY window's
            // PRIMARY chapter orders (IncludedChapterOrders minus OverlapChapterOrders), regardless of whether that
            // window's model call later succeeds. This is the full set the build is RESPONSIBLE for reviewing,
            // deduped by construction (a chapter is a primary in exactly one window). It replaces the old
            // orderedChapterBriefs.Count denominator, which counted only chapters with a FRESH structured brief
            // and therefore undercounted flat/raw-fallback chapters — letting the SUCCESS-based numerator EXCEED
            // it (the >100% "Reviewed 64/40" regression). `reviewedPrimaryOrders` (the numerator) is a SUBSET of
            // this set by construction, so the reviewed <= total invariant holds ALWAYS. It is populated in the
            // window loop below (unconditionally, BEFORE each model call).

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

                // b7 SHOWN-SET for this window: every chapter whose block its [BOOK_CONTEXT] actually prints —
                // its PRIMARIES *plus* the leading OVERLAP chapters repeated from the previous window. The overlap
                // belongs in here even though the window is not RESPONSIBLE for reporting on it (that is what the
                // firstOrder..lastOrder frame says): the overlap exists precisely so a boundary-straddling issue is
                // visible to one window intact, so an anchor onto it is grounded in text the model was given, not a
                // guess. Coverage (reviewedPrimaryOrders) stays primaries-only — the two sets answer different
                // questions ("what did it SEE" vs "what is it ACCOUNTABLE for") and must not be conflated.
                var shownOrders = window.IncludedChapterOrders.Distinct().OrderBy(o => o).ToArray();

                var windowFindings = await RunCombinedCallAsync(lang, windowContext, ct, frame, shownOrders);
                var outcome = WindowOutcomes.Classify(windowFindings);

                // final-r01: the DESTRUCTIVE licence is asked EXACTLY ONCE, of the ONE predicate that owns the
                // contract — WindowOutcomes.CountsAsReviewed. It was written for precisely this decision and had NO
                // production caller: the loop below switched on the enum directly, so the helper (and its green unit
                // test) DOCUMENTED a safety contract that nothing enforced, which reads as a guarantee and is not one.
                // Worse, the switch's catch-all `default:` arm was FAIL-OPEN: a future WindowOutcome member would land
                // there, have its primaries added to reviewedPrimaryOrders, and thereby LICENSE the delete-vanished-open
                // pass — the exact P0 be-c01 exists to close — while CountsAsReviewed said false. Asking the predicate
                // here makes a new member NOT-reviewed until someone deliberately adds it to CountsAsReviewed.
                var countsAsReviewed = outcome.CountsAsReviewed();

                switch (outcome)
                {
                    case WindowOutcome.Failed:
                        // A null return is a window-level failure (model error / unparseable). It does NOT abort
                        // the build — the other windows still contribute. Overall total failure is decided below
                        // on the fully accumulated + reduced + deduped set.
                        failedWindows++;
                        break;

                    case WindowOutcome.EmptySuspectedTruncation:
                        // be-c01 (P0, DATA LOSS). The call parsed but carried ZERO findings. The old code counted
                        // this as a SUCCESS and marked these chapters REVIEWED — which is exactly what licensed
                        // b3's book-wide delete rule (reviewed ⊇ real) to fire, wiping still-open findings on a
                        // build that produced NOTHING for them. The model has no way to say "these chapters are
                        // clean" that differs by even one byte from a truncated/short-circuited response (see
                        // WindowOutcome), so we take the pessimistic reading: these chapters were NOT reviewed.
                        // Nothing is accumulated (there is nothing to accumulate) and nothing is deleted for them.
                        // NOT counted as a failed window: we did not OBSERVE a failure, and "N window(s) failed"
                        // is a claim we cannot make. The honest surface is the coverage gap (reviewed < total),
                        // which is already first-class on the result, the persisted coverage row and the FE.
                        emptyWindowCount++;
                        _logger.LogWarning(
                            "Book review (window {Index}/{Count}, chapters {First}-{Last}): the model returned ZERO " +
                            "findings. An empty result is INDISTINGUISHABLE from a silent truncation (the schema has " +
                            "no 'clean' verdict), so these chapters are NOT counted as reviewed and NO still-open " +
                            "finding anchored in them will be deleted by this build. Book {BookId} ({Lang}).",
                            windowIndex1Based, windowCount, firstOrder, lastOrder, bookId, lang);
                        break;

                    case WindowOutcome.Reviewed:
                        // wb4-c06 NUMERATOR, be-c01 TIGHTENED: only a window that actually PRODUCED findings counts
                        // its primaries as REVIEWED — the one outcome that proves the model really reviewed them.
                        // reviewedPrimaryOrders stays a subset of totalReviewableOrders by construction (same
                        // primaryOrders source), so reviewed <= total holds always. Gated on CountsAsReviewed, not on
                        // reaching this arm, so the licence and the predicate that documents it cannot drift apart.
                        if (countsAsReviewed)
                        {
                            foreach (var order in primaryOrders)
                                reviewedPrimaryOrders.Add(order);
                        }

                        foreach (var item in windowFindings!)
                            item.Dimension = NormalizeDimension(item.Dimension);
                        accumulated.AddRange(windowFindings!);
                        break;

                    default:
                        // FAIL-CLOSED. A WindowOutcome this loop does not know about: it has NOT been shown to be a
                        // review, so its chapters do NOT join reviewedPrimaryOrders and nothing is deleted for them.
                        // Counted with the failures (the honest surface: we cannot vouch for this window) rather than
                        // silently treated as a success, which is what the old `default:` arm did.
                        failedWindows++;
                        _logger.LogWarning(
                            "Book review (window {Index}/{Count}): unhandled WindowOutcome '{Outcome}'. Treating it as " +
                            "NOT reviewed (fail-closed): its chapters are excluded from the reviewed set and no " +
                            "still-open finding anchored in them will be deleted. Book {BookId} ({Lang}).",
                            windowIndex1Based, windowCount, outcome, bookId, lang);
                        break;
                }

                if (jobId.HasValue)
                    _progress.ChunkCompleted(jobId.Value, windowIndex1Based, totalChunks);
            }

            // be-c01 WINDOW COVERAGE — ONE UNCONDITIONAL line per build. A guard that logs only its POSITIVE count
            // is indistinguishable from a guard that never ran (the lesson the collapser generated: 136 green tests,
            // zero live effect), and this one gates a DESTRUCTIVE pass, so its ZERO must be visible too. It states
            // the whole universe: every window is exactly one of produced / empty / failed, and reviewed+unreviewed
            // partition the reviewable chapters.
            _logger.LogInformation(
                "Book review (window coverage): book {BookId} ({Lang}) - {WindowCount} window(s): {Produced} produced " +
                "findings, {Empty} returned ZERO findings (suspected truncation, NOT counted as reviewed), {Failed} " +
                "failed (error/unparseable). Chapters reviewed {Reviewed}/{Total}. Only reviewed chapters can have a " +
                "vanished still-open finding deleted this build.",
                bookId, lang, windowCount, windowCount - emptyWindowCount - failedWindows, emptyWindowCount,
                failedWindows, reviewedPrimaryOrders.Count, totalReviewableOrders.Count);

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
                    var synthesis = await RunSynthesisAsync(
                        fullBookBrief, accumulated, lang, jobId, digestAnchorGate, characterRegister, ct);
                    if (synthesis != null)
                    {
                        // b8 THE MERGE MAP — the reduce's DELETE channel. Captured here and applied as PASS 0 of
                        // UnionAndDedup (SynthesisMergeMap.Apply), before the near-duplicate collapser's passes. It
                        // names findings in `accumulated` BY REFERENCE, so it must be resolved against the list as it
                        // stood when the digest was built — which is exactly now, before anything is appended below.
                        synthesisMerges = synthesis.Merges;

                        if (synthesis.Findings is { Count: > 0 })
                        {
                            foreach (var item in synthesis.Findings)
                                item.Dimension = NormalizeDimension(item.Dimension);

                            // Append the synthesis's OWN new findings (the book-level observations no window could
                            // see). They flow through the same dedup/collapse as everything else.
                            //
                            // b8 — WHAT THIS APPEND DOES *NOT* DO, corrected. The old comment here claimed a synthesis
                            // finding that duplicates a window finding "dedups away by key, so reconciliation is free".
                            // That was FALSE BY CONSTRUCTION and it is why the reduce never reconciled anything:
                            //   • the dedup key is a SHA-256 of the exact prose, and a finding the model MERGED is new
                            //     prose by definition, so it can never hash-match either original; and
                            //   • the key folds the primary chapter order in, so even a verbatim re-emission anchored
                            //     to the union [1,15] hashes as 1 while the ch15 copy hashes as 15 — two rows.
                            // So appending a "merged" finding beside the two it meant to replace made the list LONGER.
                            // Reconciliation is not free and never was: it costs the merge map above, which is the only
                            // channel in which the model can say "delete these two and keep that one".
                            accumulated.AddRange(synthesis.Findings);
                        }
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
                        fullBookBrief!, continuityPlan, lang, jobId, continuityBaseChunkIndex, totalChunks,
                        digestAnchorGate, ct);
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
                    // Drain any continuity chunks the throwing pass left unreported so the job still reaches
                    // 100% instead of stalling on orphaned reserved chunks. This is a DRAIN of a whole tail,
                    // not one chunk finishing: ChunkCompleted now increments the count by exactly one (it is
                    // a COUNT, no longer a monotonic max over indices), so expressing it as
                    // ChunkCompleted(total, total) would advance the readout by a single chunk and leave the
                    // job stuck. MarkAllChunksCompleted is the method that means "account for all of them".
                    if (jobId.HasValue)
                        _progress.MarkAllChunksCompleted(jobId.Value, totalChunks);
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
            //   • ChaptersReviewed = distinct primaries of the windows that PRODUCED FINDINGS (a strict subset).
            //     reviewed == total iff EVERY window produced at least one finding; a FAILED window (be-c01: or an
            //     EMPTY one, a suspected truncation) lowers reviewed below total; a partial-brief book can NEVER
            //     produce reviewed > total. NOTE reviewed < total does NOT imply failedWindows > 0 — an empty window
            //     lowers coverage without asserting an observed failure. The two counts answer different questions.
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

            // b7 SHOWN-SET on the legacy path: this path concatenates EVERY window's text into ONE context, so the
            // model is shown every chapter and the visibility gate is (correctly) a no-op — but it is STAMPED, not
            // left null, so the property "every persisted anchor was seen by its author" holds on BOTH paths rather
            // than being an accident of which toggle is on. It is derived from what the windows actually printed,
            // not from chaptersByOrder: a genuinely empty chapter is never rendered into any window, so it is not
            // something the model saw, and an anchor to it would be a guess here too.
            var legacyShownOrders = windows
                .SelectMany(w => w.IncludedChapterOrders)
                .Distinct().OrderBy(o => o).ToArray();
            foreach (var bucket in perDimension)
            {
                if (bucket != null)
                    StampShownSet(bucket, legacyShownOrders);
            }

            // be-c02 + be-c03: the legacy path reviews the WHOLE book in ONE concatenated context (not per-window),
            // so there is no partial coverage to reason about here — EVERY chapter order is reviewed together, and
            // every chapter order is therefore also REVIEWABLE. Seed BOTH sets from the SAME source, in ONE loop, so
            // they can never drift apart:
            //   • reviewedPrimaryOrders (be-c02) scopes the delete-vanished-open pass. Without the seed the set would
            //     be empty here and the delete would NEVER fire, silently preserving regenerated noise.
            //   • totalReviewableOrders (be-c03) is the denominator of b3's book-wide (no-anchor) superset rule.
            // Because the two are IDENTICAL on this path, `reviewed ⊇ reviewable` holds on EVERY legacy build and
            // the book-wide rule fires exactly as it did before be-c03: no behavior change here. The P1-2
            // immortal-orphan bug is WINDOWED-ONLY — only that path derives `reviewed` from window PRIMARIES, and the
            // windower SKIPS genuinely empty chapters, so its `reviewed` could never cover the raw chapter set.
            // KEEP THEM WELDED: seeding `reviewed` from the rendered chapters alone while leaving `reviewable` at the
            // raw keys (or vice versa) would re-open exactly that hole on this path.
            foreach (var order in chaptersByOrder.Keys)
            {
                reviewedPrimaryOrders.Add(order);
                totalReviewableOrders.Add(order);
            }

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
        //
        //         The model's chapter references are UNTRUSTED (it invents orders, and has been seen reading one
        //         out of a chapter TITLE — a 1-chapter book whose only chapter is titled "פרק 16" got anchors
        //         claiming orders 1 and 16). ChapterAnchorResolver resolves each one against the REAL chapters —
        //         by order, then by title, else DROP — so a phantom anchor is never persisted with an empty
        //         chapterId. See ChapterAnchorResolver.LogResolution: the drops are never swallowed silently.
        //         b4: UnionAndDedup also runs the NEAR-DUPLICATE COLLAPSE after the exact-key dedup. The key is a
        //         SHA-256 of the exact prose, so it cannot absorb the model's re-wording of the same finding (book
        //         2cf6fcf2: 20 rows, 20 distinct keys, ~10 real findings — one tone finding emitted FOUR times).
        //         The collapse is BUILD-TIME only and moves no stored key, so Status preservation is untouched.
        var anchorResolver = new ChapterAnchorResolver(chaptersByOrder);

        // b7 gate coverage, measured BEFORE the dedup collapses anything: how many of the findings this build
        // produced carry the shown-set of the pass that wrote them. Every production pass stamps one, so this
        // should equal the total — and when it does not, the log below says so instead of silently degrading.
        var allItems = perDimension.Where(d => d != null).SelectMany(d => d!).ToList();
        var gatedFindings = allItems.Count(i => i.VisibleChapterOrders != null);

        var deduped = UnionAndDedup(perDimension, anchorResolver, builtWithModel, lang, synthesisMerges, _logger);
        anchorResolver.LogResolution(_logger, bookId, lang, gatedFindings, allItems.Count);

        // Shared repair config + Mode for BOTH the glossary (5b) and dynamic (5c) stages below. Hoisted here so
        // the two seams read the SAME Mode and mirror UnifiedAnalysisService.ApplyAnalysisRepairAsync, where Mode
        // selects WHICH deterministic/dynamic stage runs under the Enabled/PerType gate. repairMode defaults to
        // Off when the block is null (a null block is a full no-op, exactly like Enabled=false).
        var repairCfg = _aiOptions.Value.AnalysisRepair;
        var repairMode = repairCfg?.Mode ?? Ai.AnalysisRepairMode.Off;

        // 5a-gate. be-c02 — the LAYER gate (Enabled + PerType-allows "BookReview"), evaluated ONCE for BOTH
        //     stages below and logged ONCE, mirroring UnifiedAnalysisService.ApplyAnalysisRepairAsync's single
        //     per-call gate log. Enabled/PerType is a whole-LAYER knob: when it closes, NEITHER the glossary
        //     (5b) nor the dynamic (5c) stage runs, whatever Mode says. Evaluating it here fixes an OBSERVABILITY
        //     asymmetry the h1 todo left behind one layer up from the predicate it de-duplicated:
        //       - the glossary skip used to be logged only from INSIDE ApplyGlossaryToFindings, which under
        //         Mode=Off/Dynamic is never CALLED — so half the layer vanished silently under 2 of the 4 Modes;
        //       - the dynamic skip was logged unconditionally, so a Mode=Off build emitted exactly ONE line that
        //         named only the dynamic stage, reading as "just the dynamic stage was gated out" when in fact
        //         the ENTIRE layer was.
        //     One line naming BOTH stages (and the Mode it did not need to consult) removes the asymmetry AND
        //     today's DOUBLE line under Mode=GlossaryThenDynamic, where the two hooks logged the same reason
        //     twice. Debug ONLY: a gated-out type is a normal steady state (BookReview is routinely absent from
        //     a PerType allowlist) and must never rise to INFO/WARN.
        //     NOTE: ApplyGlossaryToFindings KEEPS its own identical Enabled/PerType gate — deliberate
        //     defence-in-depth for the other callers/tests that drive it directly; this hoist short-circuits
        //     ahead of it rather than replacing it.
        var repairLayerGateReason = Ai.AnalysisRepairGate.Evaluate(repairCfg, AnalysisType.BookReview.ToString());
        var repairLayerGateOpen = repairLayerGateReason == Ai.AnalysisRepairGateReason.Allowed;
        if (!repairLayerGateOpen)
        {
            _logger.LogDebug(
                "AnalysisRepair: type={Type} gate closed ({Reason}); skipping the ENTIRE repair layer for book " +
                "{BookId} ({Lang}): BOTH the glossary stage and the dynamic (span-scoped) stage are skipped, " +
                "regardless of Mode={Mode}",
                AnalysisType.BookReview, repairLayerGateReason, bookId, lang, repairMode);
        }

        // 5b. f5-wire JOB 2 — DETERMINISTIC GLOSSARY SAFETY NET over the finalised findings, BEFORE persist.
        //     BookReview runs on the SAME gemma4:12b as LiteraryAnalysis and emits the SAME structured Hebrew
        //     prose (findings[].rationale / suggestedAction), which leaks English terms STOCHASTICALLY — yet it
        //     never flows through UnifiedAnalysisService.ApplyAnalysisRepairAsync (that seam feeds RunAsync/
        //     streaming, not this whole-book engine), so the glossary must be hooked HERE. It Hebraises each
        //     finding's Rationale + (non-null) SuggestedAction IN PLACE, touching NOTHING else. Gated on the
        //     repair layer (Enabled + PerType-allows "BookReview") and a Hebrew book; NO new LLM (glossary
        //     only). Fail-safe: it can NEVER throw into the build — on any fault the un-repaired findings are
        //     persisted. Placed AFTER UnionAndDedup deliberately: DedupKey is derived from the RAW model
        //     rationale there, so leaving it untouched here keeps Status preservation stable across rebuilds
        //     (the model re-emits the same leak, we re-derive the same key) — the repair is display-only.
        //     MODE GATE: runs ONLY when Mode is Glossary or GlossaryThenDynamic, mirroring the glossary stage in
        //     UnifiedAnalysisService.ApplyAnalysisRepairAsync EXACTLY. Under the SHIPPED default (Mode=GlossaryThenDynamic)
        //     this glossary stage still runs; Mode=Off / Dynamic now correctly SKIP it here
        //     just as they do on the RunAsync seam (was previously un-gated, a Mode contract violation).
        //     ApplyGlossaryToFindings keeps its own Enabled/PerType gate (belt-and-braces).
        //     be-c06: the predicate itself lives ONCE, on the enum (AnalysisRepairModeExtensions.RunsGlossary) —
        //     the "mirrors UnifiedAnalysisService EXACTLY" claim above is now enforced by construction (one
        //     shared predicate) rather than by two longhand copies that had to be kept in step by hand.
        //     be-c02: the Enabled/PerType half is the HOISTED repairLayerGateOpen above (evaluated + logged once
        //     for the whole layer), so this seam now reads exactly like 5c: layer gate AND Mode-selects-stage.
        if (repairLayerGateOpen && repairMode.RunsGlossary())
        {
            try
            {
                var repairedFindings = ApplyGlossaryToFindings(
                    deduped, lang, repairCfg, _logger);
                if (repairedFindings > 0)
                    _logger.LogInformation(
                        "Book review glossary repair: cleaned English leaks in {Count} of {Total} finding(s) for book {BookId} ({Lang}).",
                        repairedFindings, deduped.Count, bookId, lang);
            }
            catch (Exception ex)
            {
                // Belt-and-braces: a repair fault must NOT fail persistence (the layer's "can never throw into the
                // engine" invariant). ApplyGlossaryToFindings already guards per-finding; this also covers the gate.
                _logger.LogWarning(ex,
                    "Book review glossary repair threw for book {BookId} ({Lang}); persisting un-repaired findings (fail-safe).",
                    bookId, lang);
            }
        }

        // 5c. d4-wire — DYNAMIC span-scoped repair, layered AFTER the glossary above per Ai:AnalysisRepair.Mode
        //     (AnalysisRepairMode). GATED exactly like ApplyGlossaryToFindings above (Enabled + PerType-allows
        //     "BookReview") PLUS Mode — Mode is an ADDITIONAL knob layered UNDER Enabled/PerType, never a
        //     substitute for them, so a null block / Enabled=false / a PerType exclusion must disable this
        //     block too, regardless of Mode. Runs ONLY when that gate passes AND Mode is Dynamic or
        //     GlossaryThenDynamic — under the SHIPPED default (Mode=GlossaryThenDynamic) this block DOES run
        //     (after the glossary above); Mode=Glossary/Off is the rollback that skips it. When it runs, it repairs the SAME finalised findings'
        //     Rationale + (non-null) SuggestedAction IN PLACE via DynamicTermRepairService.RepairFindingsAsync
        //     (bidirectional, unlike the Hebrew-only glossary), touching nothing else. Fail-safe: can NEVER
        //     throw into the build; on any fault the un-repaired (or glossary-only-repaired) findings persist.
        //     be-c06: the Mode half of this gate is the SHARED predicate (AnalysisRepairModeExtensions.RunsDynamic),
        //     the same one UnifiedAnalysisService.ApplyAnalysisRepairAsync's dynamic stage calls. Enabled/PerType
        //     still gate FIRST and independently — RunsDynamic() answers ONLY "does this Mode select the stage".
        // h1-observable-gate-skip: the Enabled/PerType half of this gate is OBSERVABLE — it names WHICH of the
        // three reasons (null block / Enabled=false / PerType exclusion) closed it, since each has a different
        // fix. A Mode that simply does not select the dynamic stage (e.g. Mode=Glossary) is NOT logged, that is
        // ordinary stage selection, not a silently-skipped type. Byte-identical gate semantics to the original
        // inline expression: `cfg is not null && cfg.Enabled && PerTypeAllows(cfg, "BookReview")` <=>
        // `Evaluate(...) == Allowed`.
        // be-c02: that evaluation + its Debug line now live ONCE at 5a-gate above, covering BOTH stages, because
        // the reason is a property of the LAYER, not of this stage — logging it here (and only here) made a
        // Mode=Off build report the dynamic stage as the only thing gated out while the glossary half of the
        // path stayed invisible, and made a Mode=GlossaryThenDynamic build log the same reason twice.
        var dynamicGateOpen = repairLayerGateOpen && repairMode.RunsDynamic();
        if (dynamicGateOpen)
        {
            try
            {
                // e3: fetch the per-book proper-noun LEAVE set LAZILY, ONLY inside this dynamic gate — under
                // the rollback Mode=Glossary/Off this gate never opens, so it never hits the DbContext. Deterministic +
                // fail-safe (empty set on any fault / missing book = current behavior); the outer try/catch below
                // also covers an unforeseen throw, so a fetch fault can never fail persistence.
                //
                // final-r02: pass the SAME `lang` handed to RepairFindingsAsync below — the review language this
                // build is scoped to. The repair layer resolves the classifier's expected script from it, and the
                // provider resolves its HARVEST direction from it through the same helper, so harvest and classify
                // agree BY CONSTRUCTION. (The provider used to key the harvest on the book's STORED language, which
                // could disagree with `lang` and leave the entity lever inert in the disagreeing direction.)
                var bookEntities = await _bookEntityProvider.GetEntitiesAsync(bookId, lang, ct).ConfigureAwait(false);
                var dynamicRepairedFindings = await _dynamicTermRepair.RepairFindingsAsync(deduped, lang, bookEntities, ct)
                    .ConfigureAwait(false);
                if (dynamicRepairedFindings > 0)
                    _logger.LogInformation(
                        "Book review dynamic repair: cleaned foreign-script leaks in {Count} of {Total} finding(s) for book {BookId} ({Lang}).",
                        dynamicRepairedFindings, deduped.Count, bookId, lang);
            }
            catch (Exception ex)
            {
                // Belt-and-braces: mirrors the glossary catch above — a dynamic-repair fault must NOT fail
                // persistence. RepairFindingsAsync already guards per-finding; this also covers the gate.
                _logger.LogWarning(ex,
                    "Book review dynamic repair threw for book {BookId} ({Lang}); persisting un-repaired findings (fail-safe).",
                    bookId, lang);
            }
        }

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
        // b2: the book's REAL chapter orders, threaded into the persist step so its scoped delete can tell a
        // "real chapter that was not reviewed this build" (PRESERVE — the be-c02 rule) from an "anchor order that
        // is no chapter of this book at all" (an INVALID/phantom anchor, which must NOT block deletion forever).
        // reviewedPrimaryOrders is always a SUBSET of this set on both paths (window primaries and the legacy
        // whole-book seed are both derived from chaptersByOrder), which is exactly why a phantom order could never
        // enter it — and so, pre-fix, could never be deleted.
        var realChapterOrders = chaptersByOrder.Keys.ToHashSet();
        // be-c03: the three sets travel TOGETHER into the persist step, and the containment chain
        // reviewed ⊆ reviewable ⊆ real holds on BOTH paths by construction:
        //   • windowed — reviewed = the primaries of windows that PRODUCED findings; reviewable = the primaries of
        //     ALL windows (empty chapters are never windowed, so reviewable ⊊ real on a book with one); real = every
        //     Chapters row.
        //   • legacy   — reviewed = reviewable = real (the whole book in one context; seeded in one loop above).
        // totalReviewableOrders.Count is also exactly `persistedTotal` on both paths (chaptersTotalCount on the
        // windowed path, chaptersByOrder.Count on the legacy one), so the coverage DENOMINATOR the user reads and the
        // set the book-wide delete rule measures against are the same thing, said twice.
        if (!totalFailure)
            await PersistPreservingStatusAsync(
                bookId, lang, deduped, reviewedPrimaryOrders, totalReviewableOrders, realChapterOrders,
                persistedReviewed, persistedTotal, ct);

        var totalNow = await _db.BookFindings.CountAsync(
            f => f.BookId == bookId && f.Language == lang, ct);

        // wb4-c06 HONEST coverage tail. The windowed (default) path speaks in chapters/windows/passes; the
        // legacy per-dimension path keeps its dimensions wording. The success/partial message leads with the
        // honest "Reviewed N/N chapters across W windows" claim (+ a continuity note and a failed-window count
        // when relevant), so the FE never has to trust a possibly-truncated single call. No em-dash (U+2014)
        // anywhere — a regular hyphen only. `coverage` is the shared HONEST prefix.
        // be-c01: an EMPTY window is named EXPLICITLY here (it is not in failedUnits, so the "N window(s) failed"
        // clause below would never mention it, and a silently-degraded coverage number is how this class of bug
        // hides). It rides the shared `coverage` prefix, so it shows in BOTH the success and the partial message.
        var coverage = $"Reviewed {chaptersReviewedCount}/{chaptersTotalCount} chapters across " +
                       $"{windowCountForResult} window(s)" +
                       (ranContinuityReduce ? " + continuity pass" : string.Empty) +
                       (emptyWindowCount > 0
                           ? $", {emptyWindowCount} window(s) returned no findings (possible truncation; their " +
                             "chapters were not counted as reviewed)"
                           : string.Empty);

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

                // The tracker owns the completion COUNT now — it increments per call under the entry lock —
                // so pass this dimension's 1-based INDEX, matching the ChunkStarted above. The local
                // Interlocked tally this used to keep is gone: two counters for one number is how they drift.
                if (jobId.HasValue)
                    _progress.ChunkCompleted(jobId.Value, index + 1, Dimensions.Length);
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
    /// (wb4-c03 <see cref="PromptFactory.BuildBookReviewWindowPrompt"/>). <see cref="WindowIndex"/> is 1-BASED
    /// (the frame says "window X of N"); <see cref="FirstOrder"/> / <see cref="LastOrder"/> are the min/max
    /// PRIMARY chapter orders the window is responsible for, so the frame names exactly the chapters shown
    /// (overlap chapters excluded).
    /// </summary>
    private readonly record struct WindowFrame(int WindowIndex, int WindowCount, int FirstOrder, int LastOrder);

    /// <summary>
    /// Runs ONE combined review call for a single window of the windowed MAP path: prepends the shared
    /// [BOOK_CONTEXT] to the wb4-c03 <see cref="PromptFactory.BuildBookReviewWindowPrompt"/> for
    /// <paramref name="window"/>, calls the router with <see cref="AiTaskType.BookReview"/>, and parses the
    /// multi-dimension findings[] via the shared <see cref="UnifiedAnalysisService.ExtractJson"/> extractor.
    /// Returns the parsed findings, or NULL when the model errors or the output cannot be parsed (the caller
    /// treats null as a window failure). Mirrors <see cref="RunDimensionAsync"/>'s request shape +
    /// null-on-failure contract.
    /// NOTE the EMPTY case is NOT collapsed into null here: a parsed-but-zero-finding result is returned as an
    /// EMPTY list and CLASSIFIED by the caller (<see cref="WindowOutcomes.Classify"/>), because the windowed caller
    /// treats it as a suspected truncation (be-c01) while other shapes may not. Do not "simplify" empty to null:
    /// the two carry different information (nothing parsed vs nothing reported).
    ///
    /// NIT-7: <paramref name="window"/> is a REQUIRED parameter, not optional. It used to default to null with a
    /// ternary selecting <see cref="PromptFactory.BuildBookReviewCombinedPrompt"/> (the whole-book, non-windowed
    /// prompt) for the null case — but this method has exactly ONE call site (inside the windowed MAP loop in
    /// <see cref="RunBuildAsync"/>), and it always constructs and passes a <see cref="WindowFrame"/>. The
    /// non-windowed branch was therefore unreachable in production; confirmed by grep before removing it, per
    /// house discipline that a comment/branch asserting reachability is a finding until verified. If a future
    /// caller genuinely needs the non-windowed combined prompt again, call
    /// <see cref="PromptFactory.BuildBookReviewCombinedPrompt"/> directly rather than reviving an optional
    /// parameter that silently changes this method's prompt shape.
    ///
    /// b7 SHOWN-SET. <paramref name="shownOrders"/> is the set of chapter orders THIS call's [BOOK_CONTEXT]
    /// actually displays — a window shows only its own chapters (primaries + overlap), not the whole book. It is
    /// used TWICE, and the two uses are the whole point:
    ///   • it is appended to the prompt as the explicit anchor ALLOWLIST (the model is told which orders exist
    ///     for it), and
    ///   • it is STAMPED on every parsed finding (<see cref="BookFindingItem.VisibleChapterOrders"/>) so
    ///     <see cref="ChapterAnchorResolver"/> can DROP an anchor to a chapter this call never saw — even when
    ///     that chapter is perfectly real, which is exactly the mis-anchoring b1's resolver could not catch.
    /// A null shown-set leaves both off (unconstrained), preserving the pre-b7 behaviour for any caller that
    /// cannot state what it displayed.
    /// </summary>
    private async Task<List<BookFindingItem>?> RunCombinedCallAsync(
        string lang,
        string bookContextSection,
        CancellationToken ct,
        WindowFrame window,
        IReadOnlyCollection<int>? shownOrders = null)
    {
        try
        {
            var promptBody = _promptFactory.BuildBookReviewWindowPrompt(
                lang, window.WindowIndex, window.WindowCount, window.FirstOrder, window.LastOrder);
            var instruction = bookContextSection + promptBody + AllowlistSuffix(lang, shownOrders);

            var request = new AiRequest
            {
                InputText = string.Empty, // the whole-book context lives in the instruction's [BOOK_CONTEXT]
                Instruction = instruction,
                TaskType = AiTaskType.BookReview,
                Language = lang,
                JsonMode = true
            };

            var scope = $"window {window.WindowIndex}/{window.WindowCount}";

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

            // be-c05: parsed through the defensive two-stage parser, so a stray/malformed `merges` key (which THIS
            // pass's prompt never even asks for) cannot throw on the whole document and sink a window's findings.
            // The findings tri-state is IDENTICAL to the old strict parse — and it is load-bearing for be-c01:
            // an explicit `"findings": null` is a FAILURE (null), while an ABSENT key or `[]` is an EMPTY list that
            // the caller classifies as a SUSPECTED TRUNCATION. Do not collapse them.
            var parsed = BookReviewResponseParser.Parse(json, DeserializeOpts, scope, _logger);
            if (parsed.Findings == null)
            {
                _logger.LogWarning("Book review ({Scope}): JSON had no findings array; treating as failure.", scope);
                return null;
            }

            StampShownSet(parsed.Findings, shownOrders);
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

    // ─── b7: the SHOWN-SET seam (one helper pair, used by EVERY anchor-bearing pass) ──────────────────

    /// <summary>
    /// b7: renders the anchor ALLOWLIST clause for a pass, ready to append to its instruction (with the blank-line
    /// separator every block in these prompts uses). A NULL shown-set means the caller declared no visibility, so
    /// nothing is appended and the prompt is byte-identical to its pre-b7 shape; an EMPTY set is a real statement
    /// ("this pass shows you no chapter orders") and DOES emit a clause. Single-sourced through
    /// <see cref="PromptFactory.BuildChapterAnchorAllowlistRule"/> so the sentence the model reads and the set the
    /// resolver enforces are rendered from the SAME collection.
    /// </summary>
    private string AllowlistSuffix(string lang, IReadOnlyCollection<int>? shownOrders) =>
        shownOrders == null
            ? string.Empty
            : "\n\n" + _promptFactory.BuildChapterAnchorAllowlistRule(lang, shownOrders);

    /// <summary>
    /// b7: stamps the emitting pass's shown-set onto every finding it produced, which is what lets
    /// <see cref="ChapterAnchorResolver"/> — running LATER, once, over the whole accumulated set in
    /// <see cref="UnionAndDedup"/> — still know WHICH chapters each individual finding's author could see. Without
    /// this the resolver only knows the book's chapters, and a window-2 finding anchored to a window-1 chapter is
    /// indistinguishable from a correct anchor.
    /// </summary>
    private static void StampShownSet(List<BookFindingItem> findings, IReadOnlyCollection<int>? shownOrders)
    {
        if (shownOrders == null)
            return; // unconstrained: leave VisibleChapterOrders null (no gate)
        foreach (var f in findings)
            f.VisibleChapterOrders = shownOrders;
    }

    // ─── Synthesis reduce pass (wb4-c04) ──────────────────────────────────────────────────────────
    //
    // be-c09 (P2-7): the digest RENDERING this pass reads — the rationale caps, the no-anchor token, and both
    // reduce digests themselves — now lives in BookReviewDigests. What remains here is the PASS: the model call,
    // its prompt, and the parse.

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
    ///
    /// b8 MERGE MAP: the pass also returns the model's optional <c>merges</c> map, VALIDATED against the ids this
    /// digest actually printed (<see cref="SynthesisMergeMap.Resolve"/>). It is the reduce's only way to REMOVE a
    /// finding; without it, "merge these two" could only ever be expressed by ADDING a third. The resolution is
    /// produced whether or not the kill-switch is on — with the switch OFF it is measured and logged, never applied.
    ///
    /// AND THE PROMPT ASKS FOR IT EITHER WAY (be-c06). <see cref="PromptFactory.BuildBookReviewSynthesisPrompt"/>
    /// takes no switch and carries the merge contract in both states, by design: the OFF state is a MEASURE state,
    /// and a measurement taken against an input the ON build would not receive would be worthless. The consequence is
    /// stated plainly rather than hidden — an OFF build is NOT a pre-b8 build, because the model answers the b8
    /// prompt (id column, 260-char cap, "express a merge ONLY through `merges`") in both. See
    /// <see cref="SynthesisMergeMap"/>, KILL-SWITCH.
    /// </summary>
    /// <param name="characterRegister">
    /// be-c02: the same suppression-filtered register the windows carry, rendered ONCE here (this pass runs
    /// once, so there is no per-window multiplier) into the same <c>[BOOK_CHARACTERS]</c> block. The synthesis
    /// pass sees NO chapter text at all - its whole view of the book is the BookBrief plus the digest - so a
    /// holistic character observation ("this character is named in nine chapters and never resolved") has no
    /// other way to know who the cast is. Its cost is subtracted from the DIGEST's budget below, not added on
    /// top of it.
    /// </param>
    private async Task<SynthesisOutcome?> RunSynthesisAsync(
        BookBrief bookBrief,
        IReadOnlyList<BookFindingItem> accumulatedFindings,
        string lang,
        Guid? jobId,
        DigestAnchorGate anchorGate,
        CharacterRegister? characterRegister,
        CancellationToken ct)
    {
        try
        {
            var briefBlock = BookContextAssembler.FormatBookBrief(bookBrief);
            // be-c02. The register block occupies the SAME window as the digest, so the digest's budget must be
            // reduced by it exactly as it already is by the brief block. Passed in rather than re-rendered
            // inside the digest builder so the block that is CHARGED is provably the block that is EMITTED.
            var registerBlock = BookContextAssembler.FormatCharacterRegisterBlock(characterRegister);
            var (digestBlock, shownOrders, idMap) =
                BookReviewDigests.BuildSynthesisDigest(
                    accumulatedFindings, lang, briefBlock, registerBlock, anchorGate, _contextAssembler, _logger);

            // Input mirrors the combined call: whole-book context in the instruction's [BOOK_CONTEXT], then the
            // compact [WINDOW_FINDINGS] digest, then the synthesis prompt body. InputText stays empty.
            //
            // b7 SHOWN-SET. Synthesis sees NO chapter text and NO chapter headings — its [BOOK_CONTEXT] is the
            // BookBrief alone (genre / themes / synopsis). The ONLY chapter orders in front of it are the ones
            // printed in the EMITTED digest lines. So that is its shown-set, and any other order in its output is
            // a number it made up. (Note this also repairs a b1 oversight: the generic ChapterOrderRule baked into
            // the synthesis prompt tells the model to copy orders "from the chapter heading inside [BOOK_CONTEXT]",
            // a heading this pass is never given. The allowlist appended below names the orders that ARE there.)
            //
            // be-c02 (P1-1). "The orders the digest prints" is only a safe allowlist if the DIGEST itself is safe.
            // It was not: it printed the windows' RAW anchors, so a window that anchored to a chapter it never read
            // put that chapter into the SYNTHESIS allowlist — and the resolver then accepted the synthesis's copy of
            // it, because by then the order was both real and "shown". The gate (DigestAnchorGate) closes that loop:
            // an order reaches this digest only if the finding that carries it will KEEP it through resolution.
            var bookContextSection = briefBlock + "\n\n"
                + (registerBlock.Length > 0 ? registerBlock + "\n\n" : string.Empty)
                + digestBlock + "\n\n";
            var instruction = bookContextSection + _promptFactory.BuildBookReviewSynthesisPrompt(lang)
                + AllowlistSuffix(lang, shownOrders);

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

            // be-c05 (P1-5). THE TWO CHANNELS ARE PARSED SEPARATELY. `merges` is an OPTIONAL, model-supplied side
            // channel, and until this fix a single malformed value in it — an object where the array belongs, ids as
            // "W3,W7", ids as numbers — threw on the WHOLE document and the catch below discarded the synthesis
            // ENTIRELY, taking its book-level findings (the holistic observations NO window can produce) with it. And
            // the b8 kill-switch could not prevent that, because the PROMPT ASKS FOR `merges` WHETHER OR NOT THE
            // SWITCH IS ON. BookReviewResponseParser confines a merges fault to the merge map: worst case is ZERO
            // merge groups, never a lost finding.
            var parsed = BookReviewResponseParser.Parse(json, DeserializeOpts, "synthesis", _logger);

            // be-c05 (P2-8). `"findings": null` is NOT a failure here. System.Text.Json writes an explicit null OVER
            // the `= new()` initialiser (the RepairableFields lesson — RepairableFields.For(BookReviewResult) guards
            // this exact case for this exact DTO, and this consumer did not), and b8's prompt now EXPLICITLY invites a
            // merges-only answer ("if there are no duplicates, omit `merges`… there is no limit on the number of
            // groups"; the findings cap is on `findings` alone). So a synthesis with nothing new to ADD but real
            // merges to PROPOSE legitimately emits {"findings": null, "merges": [...]} — and the old code threw BOTH
            // away. Zero synthesis findings is a normal outcome; it is not a reason to discard the merge map.
            var findings = parsed.Findings;
            if (findings == null)
            {
                _logger.LogWarning(
                    "Book review (synthesis): the JSON carried an explicit `\"findings\": null`; treating it as ZERO " +
                    "synthesis findings. The `merges` map in the SAME response is still honoured — a merges-only " +
                    "answer is a shape the synthesis prompt explicitly allows (be-c05).");
                findings = new List<BookFindingItem>();
            }

            // Self-labelled dimension (plot/pacing/theme for arc-level notes) — normalise defensively so a bad
            // self-label never poisons the dedup key or score rollup, exactly as the window path does.
            foreach (var f in findings)
                f.Dimension = NormalizeDimension(f.Dimension);

            StampShownSet(findings, shownOrders);

            // b8: resolve the merge map against the ids THIS digest printed. Validation is fail-closed and runs
            // regardless of the kill-switch; only the APPLY step (SynthesisMergeMap.Apply, PASS 0 of UnionAndDedup)
            // honours the switch, so the OFF state still measures what the model proposed instead of being blind.
            // Every group the parser recovered arrives here UNCHANGED and faces the full all-or-nothing validation:
            // be-c05 widened what the model may TYPE, never what it may DO.
            var mergeMapEnabled = _aiOptions.Value.BookReview.SynthesisMergeMap;
            var merges = SynthesisMergeMap.Resolve(mergeMapEnabled, parsed.Merges, idMap, _logger);

            return new SynthesisOutcome(findings, merges);
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

    /// <summary>What the synthesis reduce produced: the findings it ADDS (as before) and, b8, the validated
    /// MERGE MAP it wants applied to the accumulated set (the channel that lets a reduce REMOVE, not only add).
    /// A null <see cref="SynthesisOutcome"/> from <see cref="RunSynthesisAsync"/> still means "the pass failed";
    /// an outcome with zero findings and zero merge groups means "it ran and had nothing to say".</summary>
    private sealed record SynthesisOutcome(
        List<BookFindingItem> Findings,
        SynthesisMergeMap.Resolution Merges);

    // ─── Hierarchical continuity reduce pass (wb4-c05) ───────────────────────────────────────────────

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
            "continuity | 0,0 | " + new string('x', BookReviewDigests.ContinuityRationaleDigestChars) + "\n",
            charsPerToken));
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
        DigestAnchorGate anchorGate,
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
            // b7: a continuity group's shown-set is exactly the chapters whose skeleton LINES it prints. When the
            // whole book fits one group that is every chapter (so the gate costs a legitimate cross-book continuity
            // finding nothing); when it does not, a group sees only its slice and must not anchor outside it.
            var findings = await RunContinuityCallAsync(
                briefBlock, skeleton, lang, ct, SkeletonOrders(plan.Groups[0].Briefs))
                ?? new List<BookFindingItem>();

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
            var found = await RunContinuityCallAsync(briefBlock, skeleton, lang, ct, SkeletonOrders(group.Briefs));
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
            // b7: like synthesis, this pass sees NO chapter content — only the orders its digest lines print — so
            // that is its shown-set.
            var (digestSkeleton, digestOrders) =
                BookReviewDigests.BuildContinuityFindingsDigest(
                    groupFindings, lang, briefBlock, anchorGate, _contextAssembler, _logger);
            finalFindings = await RunContinuityCallAsync(briefBlock, digestSkeleton, lang, ct, digestOrders)
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
        CancellationToken ct,
        IReadOnlyCollection<int>? shownOrders = null)
    {
        try
        {
            var bookContextSection = briefBlock + "\n\n" + skeletonBlock + "\n\n";
            var instruction = bookContextSection + _promptFactory.BuildBookReviewContinuityReducePrompt(lang)
                + AllowlistSuffix(lang, shownOrders);

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

            // be-c05: same defensive parse as the window and synthesis passes. This prompt does not ask for `merges`
            // either, which is precisely why it must not be able to DIE of one: a stray merge key in a continuity
            // group's output used to throw on the whole document and cost that group all of its findings.
            var parsed = BookReviewResponseParser.Parse(json, DeserializeOpts, "continuity", _logger);
            if (parsed.Findings == null)
            {
                _logger.LogWarning("Book review (continuity): JSON had no findings array; treating as zero continuity findings.");
                return null;
            }

            StampShownSet(parsed.Findings, shownOrders);
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

    /// <summary>b7: the chapter orders a continuity SKELETON block prints — one line per brief, so the shown-set is
    /// exactly those briefs' Orders. Single-sourced here so the group call, the plan and the allowlist all read the
    /// same set the skeleton renderer emits.</summary>
    private static IReadOnlyCollection<int> SkeletonOrders(IReadOnlyList<ChapterBrief> briefs) =>
        briefs.Select(b => b.Order).Distinct().OrderBy(o => o).ToArray();

    /// <summary>Normalises a model-supplied dimension to one of the six known dimensions (case-insensitive,
    /// trimmed). An unknown or blank value falls back to "plot" — the same unknown-dimension fallback the
    /// per-dimension prompt uses — so a bad self-label never poisons the dedup key or score rollup.
    /// be-c09: internal (not private) so <see cref="BookReviewDigests"/> — which prints this exact normalised
    /// dimension in every digest line — calls the ONE implementation rather than growing a second copy of it
    /// (the same single-sourcing NIT-5 applied to <see cref="DeserializeOpts"/>).</summary>
    internal static string NormalizeDimension(string? dimension)
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
    ///
    /// P3-3, NO ALLOWLIST ON THIS CALL. Unlike every windowed-path call (<see cref="RunCombinedCallAsync"/>,
    /// <see cref="RunSynthesisAsync"/>, the continuity reduce), the instruction built here is NOT suffixed with
    /// <see cref="AllowlistSuffix"/> — the caller (<see cref="RunPerDimensionFanOutAsync"/>, the legacy path) only
    /// STAMPS a shown-set on the returned findings after the fact (see the "b7 SHOWN-SET on the legacy path"
    /// comment at the call site), it never tells the MODEL what that set is. The shown-set itself is derived from
    /// the concatenated window text handed to <paramref name="bookContextSection"/>'s caller, which is every
    /// chapter in the book — but a big book's concatenated context can exceed the model's num_ctx, and the AI
    /// provider silently truncates an over-budget prompt rather than failing it. If that happens, the model
    /// genuinely did not see some tail chapters, yet the shown-set still lists them as shown (it is derived from
    /// what was ASSEMBLED, not from what the model actually attended to) — the resolver's visibility gate then
    /// treats an anchor into that tail as SEEN when it may have been a guess, precisely where the b7 gate exists
    /// to catch a wrong one. Documented, not gated: the toggle that selects this path ships OFF by default
    /// (<see cref="AiOptions.BookReviewSingleCombined"/> = true), it is kept only for a future larger-GPU
    /// re-measure, and adding an allowlist here would still not fix the underlying truncation risk — only
    /// windowing (the default path) bounds the per-call context to a size that is known to fit.
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

    // ─── Glossary safety net (f5-wire JOB 2) ──────────────────────────────────────────────────────

    /// <summary>
    /// DETERMINISTIC English -> Hebrew glossary safety net for the whole-book review ENGINE path. BookReview
    /// runs on the SAME gemma4:12b as LiteraryAnalysis and emits the SAME structured Hebrew prose, which
    /// leaks English terms stochastically, but it NEVER flows through the RunAsync/streaming repair seam
    /// (<c>UnifiedAnalysisService.ApplyAnalysisRepairAsync</c>) — so it gets its own hook here. Each finalised
    /// <see cref="BookFinding"/>'s Rationale + (non-null) SuggestedAction is Hebraised IN PLACE via
    /// <see cref="RepairableFields.For(BookFinding)"/> + <see cref="GlossaryRepairPass.RepairFields"/> (the
    /// SAME single glossary the seam uses; NO new LLM). Everything else on the entity — Dimension / Verdict /
    /// Severity / EvidenceJson / ChapterAnchorsJson / DedupKey / Status / BuiltWithModel — is never exposed to
    /// the glossary, so it stays byte-identical.
    ///
    /// GATE (mirrors ApplyAnalysisRepairAsync): runs only when <see cref="Ai.AnalysisRepairGate.Evaluate"/>
    /// (Enabled + PerType allows "BookReview") is Allowed AND the book language is Hebrew (the Hebrew check
    /// lives inside <see cref="GlossaryRepairPass.RepairFields"/>).
    /// A null/off config, a PerType exclusion, or a non-Hebrew book is a strict no-op. NOTE: this hook is
    /// deliberately glossary-ONLY and ignores <see cref="Ai.AnalysisRepairOptions.GuardOnly"/> — BookReview
    /// never runs the value-scoped LLM stage (this glossary hook itself makes no model call; the separate
    /// dynamic span-scoped stage, gated by Mode=Dynamic/GlossaryThenDynamic, is the only model-calling repair on this path).
    ///
    /// FAIL-SAFE: per-finding try/catch means a repair fault on one finding leaves THAT finding un-repaired
    /// and continues (mirrors the engine's non-fatal reduce-pass pattern); combined with the caller's outer
    /// try/catch, the pass can NEVER throw into the review build. Static + pure (config passed in) so it is
    /// unit-testable WITHOUT the windowed engine or a GPU. Returns the count of findings whose prose changed.
    /// </summary>
    internal static int ApplyGlossaryToFindings(
        IReadOnlyList<BookFinding> findings,
        string language,
        Ai.AnalysisRepairOptions? cfg,
        ILogger? logger = null)
    {
        // final-r01 null-collection guard: a null/empty incoming set is a no-op (never throws).
        if (findings is null || findings.Count == 0)
            return 0;

        // Layer gate: a null block or Enabled=false is a FULL no-op; a non-empty PerType map that excludes
        // "BookReview" also skips. (The Hebrew-book gate is enforced inside GlossaryRepairPass.RepairFields.)
        //
        // h1-observable-gate-skip: name WHICH of the three reasons closed the gate via the shared
        // AnalysisRepairGate predicate (also consulted by UnifiedAnalysisService.ApplyAnalysisRepairAsync,
        // BookIntelligenceService.RepairStructuredProfileJsonAsync, and this class's dynamic-repair hook below),
        // Debug-only — BookReview is routinely gated out on non-BookReview PerType allowlists, so this must
        // never rise to INFO/WARN.
        var gateReason = Ai.AnalysisRepairGate.Evaluate(cfg, AnalysisType.BookReview.ToString());
        if (gateReason != Ai.AnalysisRepairGateReason.Allowed)
        {
            logger?.LogDebug(
                "AnalysisRepair: type={Type} gate closed ({Reason}); skipping glossary repair",
                AnalysisType.BookReview, gateReason);
            return 0;
        }

        var changedFindings = 0;

        // OUTER FAIL-SAFE: the whole walk is wrapped so the method itself can NEVER throw — even an
        // unexpected enumerator/gate fault swallows to a warning and returns the count-so-far. This makes the
        // static method the self-contained "can never throw into the engine" unit (the call site adds one more
        // belt-and-braces catch). On any fault the already-repaired findings stand and the rest are left as-is.
        try
        {
            foreach (var finding in findings)
            {
                if (finding is null) continue; // NULL-GUARD: never walk a null element.
                try
                {
                    // RepairableFields.For(BookFinding) exposes ONLY Rationale + (non-null) SuggestedAction, so
                    // the glossary can touch nothing else. RepairFields is itself Hebrew-gated + guard-gated (a
                    // clean field is byte-identical at zero cost; a null SuggestedAction is never exposed).
                    var changed = GlossaryRepairPass.RepairFields(RepairableFields.For(finding), language);
                    if (changed > 0)
                        changedFindings++;
                }
                catch (Exception ex)
                {
                    // FAIL-SAFE per finding: a fault on ONE finding must not abort the others. Keep this finding
                    // un-repaired and continue (GlossaryRepairResult.Fault observability idiom).
                    logger?.LogWarning(ex,
                        "Book review glossary repair threw for a finding (dimension={Dimension}); keeping it un-repaired (fail-safe).",
                        finding.Dimension);
                }
            }
        }
        catch (Exception ex)
        {
            // Belt-and-braces: any fault OUTSIDE a single finding's body (e.g. a throwing enumerator) is
            // swallowed too, so persistence proceeds with whatever was repaired before the fault.
            logger?.LogWarning(ex,
                "Book review glossary repair pass faulted; persisting with {Count} finding(s) repaired so far (fail-safe).",
                changedFindings);
        }

        return changedFindings;
    }

    // ─── Union + dedup ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Unions the per-dimension findings into one set and dedups by
    /// <see cref="BookFinding.ComputeDedupKey"/>(dimension, primaryChapterOrder, rationale) where
    /// primaryChapterOrder = the first RESOLVED chapter anchor's Order, else NULL (a book-wide, NO-ANCHOR
    /// finding). First occurrence of a dedup key wins; later duplicates are dropped. Each item is projected to a
    /// <see cref="BookFinding"/> with anchors/evidence RESOLVED against the book's real chapters by
    /// <paramref name="anchors"/> (Phase-3 navigation), Status defaulted to "open" (the persist step preserves any
    /// prior user Status), and BuiltWithModel stamped.
    ///
    /// b3 — the key is derived from the RESOLVED anchors, so PROJECT FIRST, then dedup (pre-b3 the key came from
    /// the RAW model anchor, before resolution). Two reasons:
    ///   • STABILITY. The model guesses orders (it has read one out of a chapter TITLE). Two builds that guess
    ///     DIFFERENT phantom orders which both resolve to the same real chapter used to produce two different keys
    ///     and hence two rows; resolving first collapses them onto one stable key.
    ///   • THE SENTINEL. "No anchor" must be distinguishable from "anchored to chapter 0" — and after b1 a finding
    ///     whose anchors are all unresolvable BECOMES a no-anchor finding, so the two are now routinely produced by
    ///     the same build. <see cref="BookFinding.ComputeDedupKey"/> takes an <c>int?</c> for exactly this.
    /// The rationale + dimension halves of the key still come from the RAW model item (the term-repair layers
    /// rewrite Rationale in place AFTER this, and the key must not move when they do — see step 5b).
    ///
    /// Projecting before the dedup check means an exact-duplicate item has its anchors resolved (and any drop
    /// counted) before it is discarded, so the resolver's diagnostic counters count dropped REFERENCES, including
    /// those of items the dedup then collapses. That is a log-only effect.
    ///
    /// b4 — TWO dedup stages, in this order:
    ///   1. EXACT-KEY (here). Byte-identical prose (modulo case / trim / whitespace runs) → one row. Cheap, and it
    ///      is the stage whose key is PERSISTED, so it must stay exactly as it is: the persist step re-matches a
    ///      cached row by that key to carry the user's Status across a rebuild.
    ///   2. NEAR-DUPLICATE COLLAPSE (<see cref="NearDuplicateCollapser"/>). The exact key is a HASH, so it cannot
    ///      absorb RE-WORDING — and the model routinely emits one finding two to four times with a word changed,
    ///      which is what the user was seeing as "the same finding, listed again". The collapser buckets by
    ///      (dimension, RESOLVED chapter order) and merges rationales whose normalized content-token sets are
    ///      highly similar; it then folds a BOOK-WIDE copy into the ANCHORED copy of the same finding (b4b), and
    ///      finally merges what the model filed under TWO DIFFERENT dimensions when the two are NEAR-IDENTICAL
    ///      (b4c — a much stricter cut-off, because two dimensions are two questions asked of the same prose).
    ///      It runs AFTER the exact-key stage (cheaper filter first) and changes NO key: it only decides which of
    ///      the freshly built findings reach the persist step, so there is nothing to migrate.
    ///
    /// b8 — and BEFORE either of those, PASS 0: the SYNTHESIS MERGE MAP (<see cref="SynthesisMergeMap.Apply"/>). It
    /// runs FIRST because it is the only pass that read the findings' MEANING rather than their tokens: the reduce
    /// model saw every accumulated finding at once and named the ones that are one finding. The token metric is
    /// provably exhausted on this corpus (a true duplicate at 0.455 sits BELOW a genuinely distinct pair at 0.462,
    /// so no threshold can separate them), so the model's own judgement is the only signal that can. Running it
    /// first means the deterministic passes then work on the already-merged set and never re-litigate it. It is
    /// gated by a kill-switch that ships OFF, and it changes NO dedup key (the merge unions ANCHORS onto a survivor
    /// that is one of the originals, by APPEND, so the first anchor — the key's primary-order input — cannot move).
    /// </summary>
    private static List<BookFinding> UnionAndDedup(
        IReadOnlyList<List<BookFindingItem>?> perDimension,
        ChapterAnchorResolver anchors,
        string? builtWithModel,
        string lang,
        SynthesisMergeMap.Resolution? synthesisMerges = null,
        ILogger? logger = null)
    {
        var byKey = new Dictionary<string, BookFinding>(StringComparer.Ordinal);

        // The collapse pass buckets on the RESOLVED primary chapter order — the SAME value that fed the dedup key
        // (ProjectToEntity hands it back rather than us re-deriving it from the serialized anchors, so the bucket
        // key and the dedup key can never drift apart).
        var candidates = new List<NearDuplicateCollapser.Candidate>();

        // b8: the RAW model item each candidate was projected from, index-aligned with `candidates`. The merge map
        // names ITEMS (that is what the digest listed), the collapse works on ENTITIES; this is the bridge, and it
        // is built here rather than reconstructed later so the two can never fall out of step.
        var candidateItems = new List<BookFindingItem>();

        foreach (var dimensionFindings in perDimension)
        {
            if (dimensionFindings == null)
                continue;

            foreach (var item in dimensionFindings)
            {
                if (string.IsNullOrWhiteSpace(item.Rationale))
                    continue; // a finding with no rationale is not actionable and cannot dedup stably

                var entity = ProjectToEntity(item, anchors, builtWithModel, lang, out var primaryOrder);

                // NIT-8, PRE-EXISTING: first occurrence wins, and the discarded duplicate's ENTIRE projected entity
                // is dropped with it — including any EXTRA anchors it carried that the first occurrence did not.
                // The dedup key hashes (dimension, primaryOrder, rationale) only, so two items with byte-identical
                // rationale/dimension/primary-order but DIFFERENT full anchor lists (e.g. [3,5] vs [3,7] — both
                // resolve to the same primary order 3) collide on the SAME key, and whichever arrived second loses
                // its "7" silently. b8's SynthesisMergeMap.Apply inherits this: it unions anchors onto a merge
                // survivor from whatever the exact-key dedup already kept, so it can never recover an anchor lost
                // here. Not fixed: the two items ARE the same finding (identical rationale), just with the model
                // giving slightly different chapter lists across windows, and there is no principled way to prefer
                // one item's anchor list over the other's without evidence beyond "which one this loop saw first".
                if (byKey.ContainsKey(entity.DedupKey))
                    continue; // first occurrence wins

                byKey[entity.DedupKey] = entity;
                candidates.Add(new NearDuplicateCollapser.Candidate(entity, primaryOrder));
                candidateItems.Add(item);
            }
        }

        // ── PASS 0: the synthesis MERGE MAP (b8) — the model's own "these are one finding" call. ──
        var merged = SynthesisMergeMap.Apply(candidates, candidateItems, synthesisMerges, logger);

        return NearDuplicateCollapser.Collapse(merged, logger);
    }

    /// <summary>
    /// Projects a model <see cref="BookFindingItem"/> into a <see cref="BookFinding"/> entity. Anchors and
    /// evidence are CHAPTER-level only (never character offsets) and are RESOLVED against the book's REAL
    /// chapters by <see cref="ChapterAnchorResolver"/>: by Order, then by Title, else DROPPED.
    ///
    /// The model is UNTRUSTED on chapter references (see the resolver's remarks: it has been seen inventing an
    /// order, or reading one out of a chapter TITLE). An anchor that matches no real chapter is NOT persisted —
    /// previously such an anchor was written with <c>ChapterId = Guid.Empty</c>, which is unusable for navigation
    /// AND (its order being no real chapter's order) un-deletable by the scoped delete-vanished-open pass, so it
    /// accumulated forever. A finding whose anchors are ALL dropped is still KEPT: its rationale can be valid
    /// book-wide criticism, so it simply becomes a NO-ANCHOR finding (an empty anchors list) — an existing,
    /// supported state — rather than a finding with a phantom anchor.
    ///
    /// b3: this is also where the DEDUP KEY is stamped (both the current key and the transient legacy-V1 key the
    /// persist step migrates from), because the key's primary-order input is the first RESOLVED anchor — which
    /// only exists here. See <see cref="UnionAndDedup"/> for why the key must be derived post-resolution.
    /// </summary>
    /// <param name="primaryChapterOrder">OUT: the resolved primary chapter order that fed the dedup key (the first
    /// resolved anchor's Order, or NULL for a no-anchor / book-wide finding). Handed back so the near-duplicate
    /// collapse pass buckets on the SAME value the key hashes instead of re-deriving it from the serialized JSON.</param>
    private static BookFinding ProjectToEntity(
        BookFindingItem item,
        ChapterAnchorResolver resolver,
        string? builtWithModel,
        string lang,
        out int? primaryChapterOrder)
    {
        // b7: the shown-set of the PASS that produced this finding (null = the producer declared no visibility →
        // unconstrained). Threaded per-ITEM, not per-build, because the whole point is that different findings in
        // the SAME accumulated set were written by passes that saw DIFFERENT chapters.
        var shownOrders = item.VisibleChapterOrders;

        // RESOLVE the anchors (order → title → shown? → drop). Null-safe: the model can emit "chapterAnchors": null,
        // which the deserializer leaves null; a finding with only rationale/verdict is still valid, so a null
        // list projects to no anchors (not a crash).
        var anchors = new List<FindingChapterAnchor>();
        if (item.ChapterAnchors != null)
        {
            foreach (var a in item.ChapterAnchors)
            {
                if (resolver.TryResolveAnchor(a, out var resolved, shownOrders))
                    anchors.Add(resolved);
                // else: unresolvable (no such chapter) OR real-but-UNSEEN by this pass → DROPPED. Both are counted
                // by the resolver, separately, and the build logs a warning for each class.
            }
        }

        // RESOLVE the evidence by chapterOrder (evidence carries no title). An order that is not a real chapter
        // order is a phantom nav target, so the item is dropped rather than pinned to a chapter it is not in.
        // b7: so is an order naming a real chapter this pass never saw — the model cannot have excerpted it.
        // Null-safe for the same reason as anchors above: "evidence": null projects to no evidence.
        var evidence = new List<FindingEvidence>();
        if (item.Evidence != null)
        {
            foreach (var e in item.Evidence)
            {
                if (resolver.TryResolveEvidence(e, out var resolved, shownOrders))
                    evidence.Add(resolved);
            }
        }

        var severity = Math.Clamp(item.Severity, 1, 3);

        // b3 DEDUP KEY. The primary chapter order is the first RESOLVED anchor's Order, or NULL when the finding
        // anchors NO chapter — the model emitted none, or every anchor it emitted was unresolvable and dropped
        // above. NULL, not 0: chapter 0 is a REAL chapter in every (0-based) book, so the old 0-as-"no anchor"
        // sentinel made a book-wide finding hash identically to a first-chapter one.
        int? primaryOrder = anchors.Count > 0 ? anchors[0].Order : null;
        primaryChapterOrder = primaryOrder; // b4: same value, handed to the near-duplicate collapse bucketing.
        var dedupKey = BookFinding.ComputeDedupKey(item.Dimension, primaryOrder, item.Rationale);

        // MIGRATION SHIM (transient, never persisted). The key this finding WOULD have had under the pre-b3
        // derivation — RAW (unresolved) first anchor order, 0 when the model emitted none. The persist step uses it
        // to re-match a cached row that was written under V1 so the user's Status survives the derivation change,
        // then upgrades that row's stored key. Derived from the SAME raw model item V1 hashed, which is why this
        // works where a recompute-from-the-persisted-row could not (see BookFinding.ComputeLegacyDedupKeyV1).
        var rawPrimaryOrder = item.ChapterAnchors is { Count: > 0 } rawAnchors ? rawAnchors[0].Order : 0;
        var legacyKey = BookFinding.ComputeLegacyDedupKeyV1(item.Dimension, rawPrimaryOrder, item.Rationale);

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
            Status = FindingStatusPartition.Open,
            DedupKey = dedupKey,
            LegacyDedupKeyV1 = legacyKey,
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
    /// decision on rebuild). A fresh finding is matched to its cached row in THREE tiers — the current dedup key,
    /// then b3's legacy-V1 key, then b4b's RE-WORDING match — run TIER-MAJOR: every incoming finding goes through
    /// tier 1 before ANY finding is offered to tier 2, and every leftover through tier 2 before ANY reaches tier 3
    /// (be-c04 — see <see cref="BookFindingReconciler.MatchIncomingToExistingRows"/> for why the old per-finding interleaving could let a
    /// fuzzy GUESS steal the row an exact key matched, and RESURRECT a dismissed finding as an open card).
    /// All three tiers mean the same thing ("this fresh finding IS that row") and are handled identically:
    ///   • MATCH: keep the existing Status (acknowledged/dismissed/done stays; an existing "open" stays
    ///     "open") and refresh the content (verdict/severity/rationale/evidence/anchors/suggestedAction) +
    ///     BuiltWithModel + UpdatedAt — the finding regenerated (identically, or re-worded) but its text may have
    ///     shifted. The one exception: a RE-WORDING match whose fresh copy anchors NO chapter does not blank a real
    ///     anchor the cached row already has (the anchored side keeps the chapter link) — and, be-c08 / P3-6, such a
    ///     row keeps its own DEDUP KEY too, because the primary chapter order is an input to that key and a row whose
    ///     key disagreed with its own anchors lost those anchors on the very next build.
    ///   • NEW: insert as "open".
    ///   • VANISHED (existing row whose key is NOT in the new set): a user decision must not be lost, so
    ///     DELETE ONLY rows still "open" (pure regenerated noise the model no longer surfaces) and PRESERVE
    ///     any the user acted on (acknowledged/dismissed/done) — they remain as a record of that decision.
    ///     be-c02 SCOPING: the delete is FURTHER gated to only findings whose EVERY anchored chapter order is in
    ///     <paramref name="reviewedChapterOrders"/> (the primaries of windows that PRODUCED FINDINGS this
    ///     build). A vanished-open finding anchored to ANY chapter whose window FAILED (or was never covered) is
    ///     PRESERVED — we did not actually re-review that chapter, so its absence from `incoming` is a
    ///     truncation/failure artifact, NOT the model retracting the finding. Checking ALL anchors (not just the
    ///     first) matters for MULTI-chapter continuity findings: one whose first anchor was re-reviewed but a
    ///     later anchored chapter's window failed must survive. Without this scope a partial rebuild (some windows
    ///     fail) would silently wipe the prior still-open findings — and their user Status path was already handled
    ///     above — for the un-reviewed chapters. be-c01 (2026-07-13) TIGHTENED what "reviewed" means: a window that
    ///     PARSED but returned ZERO findings is a SUSPECTED TRUNCATION (the model cannot express "clean" distinctly
    ///     from "cut short" — see <see cref="WindowOutcome"/>), so its chapters are NOT in
    ///     <paramref name="reviewedChapterOrders"/> either. Only a window that PRODUCED findings licenses a delete
    ///     on its chapters; a finding anchored ENTIRELY within such chapters IS deleted — those windows reviewed
    ///     them and no longer surface it (regenerated noise).
    ///     b2 IMMORTAL-ORPHAN FIX: that scope is evaluated over the anchors that name a REAL chapter of this book
    ///     only (see <see cref="BookFindingReconciler.IsVanishedOpenDeletable"/>). An anchor order the book does not have is an INVALID
    ///     (phantom) anchor — it can never appear in <paramref name="reviewedChapterOrders"/> (a subset of the real
    ///     orders), so under the raw all-anchors test it blocked the delete on EVERY rebuild, forever. The be-c02
    ///     preservation intent is untouched; an invalid anchor simply carries no preservation weight.
    ///
    /// DECISION (delete-open vs superseded status): we DELETE vanished "open" rows rather than introduce a
    /// "superseded" status. A superseded status would need a migration + widening the status set + FE
    /// handling, with no user value (an open finding the model no longer surfaces is exactly regenerated
    /// noise). Preserving user-acted rows already covers the only case where losing the row would lose
    /// information. So: delete-open / preserve-touched, no schema change.
    /// </summary>
    /// <param name="reviewedChapterOrders">The distinct PRIMARY chapter orders of the windows that PRODUCED
    /// FINDINGS this build (<c>reviewedPrimaryOrders</c>, passed through verbatim; a window that errored, was
    /// unparseable, or came back EMPTY is excluded — see <see cref="WindowOutcome"/>). A vanished-open finding is
    /// deleted ONLY when EVERY REAL chapter order it anchors is in this set; a finding anchored to any un-reviewed
    /// REAL chapter is preserved. On a build where every window produced findings this covers every reviewable
    /// chapter, so the delete behaves exactly as before (no behavior change).</param>
    /// <param name="reviewableChapterOrders">be-c03: the chapter orders this build actually PUT IN FRONT OF THE
    /// MODEL (<c>totalReviewableOrders</c>) — the union of every window's PRIMARY orders on the windowed path, every
    /// chapter order on the legacy whole-book-context path. A GENUINELY EMPTY chapter (title-only divider, DOCX
    /// artefact) is skipped by the windower, so it is in <paramref name="realChapterOrders"/> but NOT here. This is
    /// the denominator of the b3 BOOK-WIDE (no-anchor) rule: such a finding is retractable only by a build that
    /// reviewed everything it COULD review. Measuring it against the RAW chapter set instead made the test
    /// PERMANENTLY unsatisfiable on any book with an empty chapter, so every vanished-open book-wide finding was
    /// preserved on every rebuild, forever (the b2 immortal-orphan class, resurrected).
    /// Contains <paramref name="reviewedChapterOrders"/>; is contained by <paramref name="realChapterOrders"/>.</param>
    /// <param name="realChapterOrders">b2: the book's REAL chapter orders (<c>chaptersByOrder.Keys</c>, 0-based).
    /// An anchor order OUTSIDE this set is a phantom the model invented (b1 stops NEW ones being written, but rows
    /// persisted before that fix still carry them) and is IGNORED when scoping the delete — otherwise it pins the
    /// row alive on every future rebuild. <paramref name="reviewedChapterOrders"/> is always a subset of this
    /// set. Kept SEPARATE from <paramref name="reviewableChapterOrders"/> on purpose (be-c03): only the REAL set can
    /// tell a phantom anchor (no preservation weight) from a real-but-unreviewed chapter (PRESERVE).</param>
    /// <param name="chaptersReviewed">data-c01 HONEST coverage numerator to persist (see
    /// <see cref="BookReviewCoverage"/>): chapters actually reviewed this build. Upserted into the (BookId,
    /// Language) coverage row inside this SAME persist step so the status probe stays honest across a reload.</param>
    /// <param name="chaptersTotal">data-c01 HONEST coverage denominator to persist: chapters this build was
    /// responsible for. Always &gt;= <paramref name="chaptersReviewed"/>. Equals
    /// <paramref name="reviewableChapterOrders"/>.Count on both build paths.</param>
    private async Task PersistPreservingStatusAsync(
        Guid bookId,
        string lang,
        IReadOnlyList<BookFinding> incoming,
        IReadOnlySet<int> reviewedChapterOrders,
        IReadOnlySet<int> reviewableChapterOrders,
        IReadOnlySet<int> realChapterOrders,
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

        // b3 KEY MIGRATION. Rows cached under the PRE-b3 derivation carry a stale key, so matching on the current
        // key alone would orphan EVERY user-acted finding and silently drop its Status. The CURRENT key is tried
        // first (for every incoming finding), then the fresh finding's LEGACY-V1 key (BookFinding.LegacyDedupKeyV1,
        // re-derived in ProjectToEntity from the same raw model item V1 hashed); a legacy hit preserves the row and
        // UPGRADES its stored key in place. Idempotent and self-healing: after one rebuild the row matches on the
        // current key and the fallback never fires again for it.
        //
        // Matched rows are tracked BY ID, not by key: with two key derivations in play, key-set membership can no
        // longer answer "was this cached row claimed by an incoming finding?" (a legacy-matched row's stored key is
        // in NEITHER incoming key set until we rewrite it). A row is claimed at most once — by the STRONGEST tier
        // that wants it (be-c04), never merely by the first finding in list order — so two incoming findings can
        // never be folded onto the same row.
        var matchedExistingIds = new HashSet<Guid>();

        // b4b TIER 3 — the RE-WORDING match. The two key tiers above are hashes, so they only see a finding the
        // model re-emitted CHARACTER-FOR-CHARACTER. When it rephrases one instead, the fresh finding matches
        // nothing, is inserted as a new open row, and lands NEXT TO the row it is a rewording of — visibly, because
        // a user-acted (acknowledged/dismissed/done) row is never deleted and is therefore always still there. That
        // is the duplicate pair the user reported. Offer every existing row (with the chapter it is compared on)
        // to the collapser as a possible original; see NearDuplicateCollapser.FindPersistedNearDuplicate for the
        // Status semantics, which are exactly the exact-key tier's (the row survives, Status untouched, content
        // refreshed) because a rewording of a finding IS that finding.
        var persistedCandidates = new List<NearDuplicateCollapser.PersistedCandidate>(existing.Count);
        foreach (var row in existing)
        {
            var anchorOrders = BookFindingReconciler.ChapterOrdersOf(row);
            if (anchorOrders is null)
                continue; // UNKNOWN scope (unparseable anchors) → never fuzzy-matched, just as it is never deleted.
            persistedCandidates.Add(
                NearDuplicateCollapser.Prepare(
                    row, BookFindingReconciler.ComparisonOrderOf(anchorOrders, realChapterOrders)));
        }

        // be-c04: the three tiers run TIER-MAJOR (every incoming finding through tier 1, then the leftovers through
        // tier 2, then the leftovers through tier 3) rather than FINDING-major, so an EXACT-key match can never lose
        // its row to an earlier finding's 0.45-similarity guess. See BookFindingReconciler.MatchIncomingToExistingRows.
        var matches = BookFindingReconciler.MatchIncomingToExistingRows(
            incoming, existingByKey, persistedCandidates, realChapterOrders, matchedExistingIds,
            out var legacyMatches, _logger);

        var rewordFolds = 0;
        var rewordFoldsOntoUserActed = 0;
        var keyUpgrades = 0;

        for (var i = 0; i < incoming.Count; i++)
        {
            var fresh = incoming[i];
            fresh.BookId = bookId;

            if (matches[i] is not { } match)
            {
                // NEW: insert as "open" (already defaulted on the projected entity).
                _db.BookFindings.Add(fresh);
                continue;
            }

            var prior = match.Row;
            var priorIsUserActed = FindingStatusPartition.IsUserActed(prior.Status);
            if (match.ViaReword)
            {
                rewordFolds++;
                if (priorIsUserActed)
                {
                    rewordFoldsOntoUserActed++;

                    // AUDIT TRAIL FOR A DESTRUCTIVE PATH (P2-2 / be-f01), mirroring SynthesisMergeMap's KEPT/DELETED
                    // log for its own destructive operation. This fuzzy fold is the ONLY path that can rewrite a
                    // USER-ACTED row's prose: Status is preserved correctly, but if the 0.45-0.60 similarity guess
                    // is WRONG, the user's acknowledgement/dismissal now attaches to text they never read, and the
                    // OLD text is unrecoverable the moment it is overwritten below. A bare count cannot tell a
                    // correct fold from a bad one, so log the actual before/after snippets and the score that cleared
                    // (or the be-c07 stricter bar it had to clear, on an anchor mismatch).
                    _logger.LogInformation(
                        "Book review (dedup): book {BookId} ({Lang}) — fuzzy re-wording fold onto USER-ACTED row " +
                        "{RowId} (status '{Status}', score {Score} >= required {Required}). OLD: [{OldDim}] \"{Old}\". " +
                        "NEW: [{NewDim}] \"{New}\".",
                        bookId, lang, prior.Id, prior.Status,
                        match.Score.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
                        match.RequiredThreshold.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
                        prior.Dimension, SynthesisMergeMap.Snippet(prior.Rationale),
                        fresh.Dimension, SynthesisMergeMap.Snippet(fresh.Rationale));
                }
            }

            // MATCH (any tier — all three mean "this fresh finding IS that row"): preserve the user's Status,
            // refresh content + model + UpdatedAt. The row was already claimed (BY ID) by the matcher, so it is
            // neither offered to another fresh finding nor seen as VANISHED by the delete pass below.
            prior.Dimension = fresh.Dimension;
            prior.Verdict = fresh.Verdict;
            prior.Severity = fresh.Severity;
            prior.Rationale = fresh.Rationale;
            prior.EvidenceJson = fresh.EvidenceJson;

            // THE KEY AND THE ANCHORS TRAVEL TOGETHER (be-c08 / P3-6). A fold whose fresh copy anchors NO chapter
            // while the row it claims anchors a REAL one must not blank that anchor: the anchored side always keeps
            // the chapter link (the same rule the build-time cross-bucket fold enforces). Only the ANCHOR is
            // preserved, never the evidence — evidence is excerpts, and pairing one copy's quotes with another copy's
            // prose would fabricate a finding neither states.
            //
            // ...and when the row keeps its ANCHORS it must ALSO keep its KEY, because the primary chapter order is
            // an INPUT to that key (b3). Writing the anchor-less fresh key onto an anchored row made the row's key
            // disagree with the row's own anchors, and that is not a cosmetic inconsistency — it is a NON-FIXED-POINT
            // that DESTROYS the very anchor this branch exists to protect, on the NEXT build:
            //     build N   — the row (anchored ch5) is fuzzy-claimed by a book-wide copy: anchors KEPT, key
            //                 overwritten with the copy's "no-anchor" key.
            //     build N+1 — the model emits the same book-wide copy. Its key now EQUALS the row's stored key, so
            //                 TIER 1 claims it — and a tier-1 match is not a KeepPriorAnchors match (the guard is
            //                 fuzzy-tier-only, and rightly so: on a key match the order is an input to the key, so an
            //                 anchored row and an anchor-less finding cannot normally share one). The `else` below
            //                 fires and the row's chapter link is BLANKED.
            // Keeping the row's own key closes the loop: the book-wide copy misses both key tiers again, the fuzzy
            // tier re-claims the row, and the anchors survive every rebuild. It is now a fixed point.
            //
            // SAFE ON THE UNIQUE INDEX (BookId, Language, DedupKey) — by construction, thanks to be-c04's TIER-MAJOR
            // matching: tier 1 has already run over EVERY incoming finding, so any row still unclaimed by the time
            // the fuzzy tier reaches it carries a key that NO incoming finding will write. Keeping that key can
            // therefore never collide with one.
            if (match.KeepPriorAnchors)
            {
                // Neither the key nor the anchors move. (The rationale IS refreshed, so the key's rationale input is
                // now stale — which is the same, already-accepted disagreement every repaired row carries: the key is
                // hashed on the RAW prose and DynamicTermRepairService rewrites the stored prose afterwards. What must
                // NOT drift is the key's PRIMARY-ORDER input, because that is what BookFindingReconciler.ComparisonOrderOf and the collapser
                // bucket on.)
            }
            else
            {
                // b3 KEY MIGRATION (P2-3 / be-f01): a row's STORED key actually changes value exactly here, when the
                // fresh finding's key differs from the one the row was cached under — every tier-2 (legacy) hit, and
                // every tier-3 (fuzzy) fold that is NOT anchor-preserving. Measured on the WRITE itself (not inferred
                // from the tier) so the count stays correct even if a future tier's invariant changes.
                if (!string.Equals(prior.DedupKey, fresh.DedupKey, StringComparison.Ordinal))
                    keyUpgrades++;
                prior.DedupKey = fresh.DedupKey; // b3: a no-op on a current-key match; the UPGRADE on a legacy one.
                prior.ChapterAnchorsJson = fresh.ChapterAnchorsJson;
            }

            prior.SuggestedAction = fresh.SuggestedAction;
            prior.BuiltWithModel = fresh.BuiltWithModel;
            // prior.Status intentionally untouched (user decision preserved; "open" stays "open").
            _db.Entry(prior).State = EntityState.Modified; // force UpdatedAt refresh via the override
        }

        // Observability for a pass that SUPPRESSES rows: a silent suppressor is indistinguishable from a bug.
        // UNCONDITIONAL (P1-6 / be-f01) — it must fire when nothing was folded too, so "0 folds" is distinguishable
        // from "this pass never ran".
        _logger.LogInformation(
            "Book review (dedup): book {BookId} ({Lang}) — {Folds} freshly built finding(s) were RE-WORDINGS of an " +
            "existing row and were folded onto it instead of being inserted as duplicate cards; {UserActed} of those " +
            "rows carry a user Status (acknowledged/dismissed/done), which is PRESERVED (the finding is not re-opened).",
            bookId, lang, rewordFolds, rewordFoldsOntoUserActed);

        // Observability for the b3 dedup-key MIGRATION shim (P2-3 / be-f01) — UNCONDITIONAL, because the whole point
        // is a RETIREMENT CRITERION: nobody can ever demonstrate zero legacy-keyed rows remain if a zero count looks
        // identical to "this counter was never wired". legacyMatches = findings recovered ONLY via the pre-b3
        // ComputeLegacyDedupKeyV1 key (tier 2); keyUpgrades = rows whose STORED key was actually rewritten this
        // build (every legacy match, plus every fuzzy re-wording fold — both claim a row under a key that differs
        // from the incoming finding's current one). Once legacyMatches is 0 across enough rebuilds, the shim can be
        // retired.
        _logger.LogInformation(
            "Book review (dedup-key migration): book {BookId} ({Lang}) — {LegacyMatches} finding(s) matched via the " +
            "LEGACY (pre-b3) dedup key this build; {KeyUpgrades} existing row(s) had their stored key rewritten in " +
            "place as a result (legacy matches plus fuzzy re-wording folds). Zero legacy matches, sustained across " +
            "rebuilds, is the signal that no row still carries the pre-b3 key and the shim can be retired.",
            bookId, lang, legacyMatches, keyUpgrades);

        // VANISHED rows: delete ONLY those still "open" (regenerated noise); preserve user-acted ones. be-c02:
        // AND scope the delete to chapters actually REVIEWED this build — a still-open finding is deleted ONLY when
        // every REAL chapter order it anchors is in `reviewedChapterOrders` (windows that SUCCEEDED). A finding
        // anchored to ANY real chapter whose window FAILED / was uncovered is PRESERVED (its absence from
        // `incoming` is a truncation/failure artifact, not the model retracting it), stopping a partial rebuild
        // from silently wiping prior open findings. b2: an anchor order that is NOT a real chapter order of this
        // book is a PHANTOM (pre-b1 rows carry them) and is IGNORED by that scope — it could never be "reviewed",
        // so requiring it kept the row alive forever. See BookFindingReconciler.IsVanishedOpenDeletable for the full
        // case analysis. The anchor orders of an EXISTING row are derived in memory from its persisted
        // ChapterAnchorsJson (every anchor's Order; EMPTY for a no-anchor/book-wide finding, null when the payload
        // does not parse — see BookFindingReconciler.ChapterOrdersOf), since it is not SQL-queryable.
        foreach (var stale in existing)
        {
            if (matchedExistingIds.Contains(stale.Id))
                continue; // still present (matched on the current OR the legacy key) → handled above
            if (FindingStatusPartition.IsUserActed(stale.Status))
                continue; // acknowledged/dismissed/done (or an unknown member) → preserve the user's decision.
            // be-c02 multi-anchor scope + b2 invalid-anchor exclusion + b3 no-anchor rule (be-c03: measured against
            // the REVIEWABLE set, not the raw chapter set) — see BookFindingReconciler.IsVanishedOpenDeletable.
            if (!BookFindingReconciler.IsVanishedOpenDeletable(
                    BookFindingReconciler.ChapterOrdersOf(stale),
                    reviewedChapterOrders, reviewableChapterOrders, realChapterOrders))
                continue; // a REAL anchored chapter was NOT re-reviewed this build → preserve.
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
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            // A unique-index violation (e.g. the (BookId, Language, DedupKey) constraint, or a concurrent
            // build racing the same key) must not leave this SCOPED DbContext dirty — the caller goes on to
            // read CountAsync off the same context, and a future caller reusing it would re-attempt the failed
            // writes. Mirror BookSummaryService.RunBuildAsync: log, DETACH every Added/Modified BookFinding AND
            // the coverage row queued this batch (data-c01) so the failed batch is not retried, then surface a
            // clean failure for the build to report.
            //
            // be-c08 (P3-15) — WHY THE FILTER IS ONE CLAUSE WIDER THAN `DbUpdateException`. Not every save failure is
            // a DbUpdateException, and the one that is not is reachable from exactly the code this batch runs: EF
            // orders a batch's commands itself, and when the rows' keys form a CYCLE (two rows swapping DedupKeys in
            // one SaveChanges — which the fuzzy tier's in-place key rewrites can produce under the unique index) it
            // gives up BEFORE issuing any SQL and throws a plain InvalidOperationException ("a circular dependency was
            // detected"). That never reached this handler, so the detach hygiene was SKIPPED on the one failure that
            // needs it just as much: the context stays dirty, the caller's CountAsync runs on it, and the next
            // SaveChanges on this scoped context re-attempts the whole failed batch. The handler's job is "a failed
            // batch must not poison the context", and that job does not depend on which exception type reports it.
            // (OperationCanceledException is deliberately NOT caught: a cancelled request tears the scope down anyway,
            // and swallowing it here would turn a cancellation into a persist failure.)
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
    /// Reads the book's persisted CHARACTER REGISTER for the review (automatic-coverage plan, d1 §3). ONE
    /// <c>AsNoTracking</c> row, and ZERO model calls.
    ///
    /// <para><b>WHY NOT <c>AnalysisContextService.LoadCharacterRegisterAsync</c></b>, which is the method that
    /// obviously loads a register: because that method can EXTRACT. It runs the character pre-pass when a
    /// chapter has not contributed yet, and d1 §2 bounds the whole feature at one extraction call per analysis
    /// request with a whole-book review triggering ZERO. Calling it here would let a review of a 40-chapter
    /// book decide to spend model time on an extraction nobody asked for, on a machine where the review is
    /// already the longest job there is. The review wants the register IF ONE EXISTS and nothing otherwise.</para>
    ///
    /// <para>The suppression + normalization projection is
    /// <see cref="CharacterRegisterMerge.ForAnalysis"/> - the SAME one the per-analysis path applies - so an
    /// entry the author struck out is not described to the review's model either. Re-deriving that rule here
    /// is exactly the divergent second implementation the merge file's header forbids.</para>
    ///
    /// <para>FAIL-SAFE, matching every other reader of this column: a missing bible, a blank column or
    /// unparseable JSON degrades to NULL and the review proceeds exactly as it did before the register reached
    /// it. The fault is LOGGED rather than swallowed (a fail-safe that swallows blinds its caller's logger),
    /// and no register CONTENT is logged - character names are the author's manuscript.</para>
    /// </summary>
    private async Task<CharacterRegister?> LoadCharacterRegisterForReviewAsync(Guid bookId, CancellationToken ct)
    {
        var json = await _db.BookBibles.AsNoTracking()
            .Where(b => b.BookId == bookId)
            .Select(b => b.CharacterRegisterJson)
            .FirstOrDefaultAsync(ct);

        if (!CharacterRegisterService.TryDeserialize(json, out var stored, out var fault) || stored is null)
        {
            if (fault != null)
                _logger.LogError(
                    fault,
                    "Book review: the character register for book {BookId} could not be parsed; the review runs " +
                    "without character context (it is not failed for this).",
                    bookId);
            return null;
        }

        return CharacterRegisterMerge.ForAnalysis(stored);
    }

    /// <summary>
    /// Returns the assembled book context as the prompt-prefix section, EXACTLY as <see cref="BookContextAssembler"/>
    /// produced it. The assembler already wraps the BookBrief in a [BOOK_CONTEXT]…[/BOOK_CONTEXT] block (its own
    /// FormatBookBrief emits the markers) and appends the chapter briefs after it, so this MUST NOT add a SECOND
    /// [BOOK_CONTEXT] wrapper: doing so nested the markers and stranded every chapter brief between the inner
    /// [/BOOK_CONTEXT] and the outer one, leaving the model — and only the model, since the eval harness and
    /// every other assembly.Text consumer read it raw — with a malformed, double-wrapped context. Passing
    /// assembly.Text through verbatim gives the review the SAME context shape every other consumer of the
    /// assembler sends; the caller appends the dimension/combined instruction after the trailing blank line.
    /// <para>The <c>[BOOK_CHARACTERS]</c> block (be-c02) is already inside <c>assembledText</c> too, placed and
    /// charged by the assembler; nothing is added here.</para>
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
