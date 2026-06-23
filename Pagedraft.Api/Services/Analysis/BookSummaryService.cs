using System.Text.Json;
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
/// Coverage status for a book's cached L2 summary (BookBrief) rollup. Surfaced by the GET status endpoint
/// so the FE can decide whether a (re)build is needed and render progress. MIRRORS
/// <see cref="BookStyleBaselineStatus"/> so the FE reuses the same status/progress UI shape.
/// </summary>
public sealed class BookSummaryStatus
{
    public Guid BookId { get; init; }
    public string Language { get; init; } = "he";

    /// <summary>Total chapters in the book.</summary>
    public int TotalChapters { get; init; }

    /// <summary>Chapters that currently have a FRESH (non-stale, non-missing) L0 structured brief.</summary>
    public int BuiltChapters { get; init; }

    /// <summary>Chapters whose L0 brief is MISSING or STALE (edited after the brief was built, or cross-model).</summary>
    public int StaleCount { get; init; }

    /// <summary>True when a usable cached L2 BookBrief exists for (BookId, Language).</summary>
    public bool HasSummary { get; init; }

    /// <summary>When the cached rollup was last (re)built; null when none exists.</summary>
    public DateTimeOffset? LastUpdatedAt { get; init; }

    /// <summary>Summarization model id that built the cached rollup; null when none exists.</summary>
    public string? BuiltWithModel { get; init; }

    /// <summary>The resolved active Summarization model id (same resolution AiRouter uses). Surfaced so the
    /// FE can warn the rollup was built with a different model and show which model is now active.</summary>
    public string? ActiveModel { get; init; }

    /// <summary>True when a cached rollup exists AND its model differs from <see cref="ActiveModel"/>.</summary>
    public bool BuiltWithDifferentModel { get; init; }

    /// <summary>The jobId of an in-progress build for (BookId, Language), or null when none is running.</summary>
    public Guid? ActiveBuildJobId { get; init; }

    /// <summary>
    /// True when a build would be a genuine no-op: nothing missing/stale AND a usable cached rollup exists
    /// that was built under the ACTIVE model. Mirrors the no-op gate in
    /// <see cref="BookSummaryService.BuildBookSummaryAsync"/> exactly.
    /// </summary>
    public bool IsReady => StaleCount == 0 && HasSummary && !BuiltWithDifferentModel;

    /// <summary>Chapters a build would (re)process: missing + stale. Same as StaleCount; surfaced
    /// explicitly so the FE consent prompt does not need to alias it.</summary>
    public int ChaptersToBuild { get; init; }

    /// <summary>Rough estimate of wall-clock seconds a build would take, accounting for limited parallelism.</summary>
    public int EstimatedSeconds { get; init; }

    /// <summary>Rough USD estimate. Null for local providers (Ollama); coarse for paid providers.</summary>
    public decimal? EstimatedUsd { get; init; }
}

/// <summary>Result of a book-summary (L2 rollup) build job.</summary>
public sealed class BookSummaryBuildResult
{
    /// <summary>True when the build (or a no-op fresh build) ended with a usable cached rollup.</summary>
    public bool Ready { get; init; }

    /// <summary>True when nothing needed (re)building - every chapter was already fresh.</summary>
    public bool NoOp { get; init; }

    /// <summary>Chapters that contributed a usable L1 brief to the rollup.</summary>
    public int BuiltChapters { get; init; }

    public int TotalChapters { get; init; }

    /// <summary>Chapters that did NOT end with a fresh L0 brief after the build attempt (graceful-null
    /// failures or empty content). The job is never aborted by these; they stay stale.</summary>
    public int FailedChapters { get; init; }

    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Heavy "fill coverage" book-wide SUMMARY builder. Aggregates the per-chapter L0 structured briefs into
/// L1 <see cref="ChapterBrief"/>s and a single L2 <see cref="BookBrief"/> (genre/themes/synopsis rollup),
/// and caches the L2 in <see cref="BookSummaryBaseline"/>.
///
/// REUSE: this is the SUMMARY sibling of <see cref="StyleBaselineService"/> and shares its async-job +
/// progress-polling contract verbatim - per-chapter progress through <see cref="AnalysisProgressTracker"/>,
/// limited parallelism (same <see cref="AiOptions.MaxParallelStyleBaselineChapters"/> cap), idempotent
/// skip-when-fresh, a (BookId, Language) cache row carrying the model id - so the FE reuses the same
/// progress UI. It does NOT fork a divergent full-rebuild builder: the heavy per-chapter L0 work is
/// delegated to <see cref="ChapterBriefService"/> (wb1-c01), which is itself idempotent + graceful-null.
///
/// L0 → L1 → L2 composition:
///   • L0 = <see cref="StructuredChunkSummaryData"/> in <see cref="ChunkSummary.StructuredJson"/> (built by
///     ChapterBriefService; the per-chapter LLM call lives there).
///   • L1 = <see cref="ChapterBrief"/> per chapter = L0 + the chapter's Title/Order. Pure projection, not
///     persisted (it recomposes cheaply from the freshness-stamped L0).
///   • L2 = <see cref="BookBrief"/> = genre/sub-genre/audience/literature-level/synopsis REUSED from
///     <see cref="BookProfile"/>, themes REUSED from <see cref="BookBible.ThemesJson"/> and augmented with
///     the union of L1 thematic markers. Deterministic rollup (no extra LLM call); cached here.
/// </summary>
public class BookSummaryService
{
    private readonly AppDbContext _db;
    private readonly ChapterBriefService _chapterBriefService;
    private readonly AnalysisProgressTracker _progress;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly BookSummaryBuildRegistry _buildRegistry;
    private readonly ILogger<BookSummaryService> _logger;

    // Deserialize cached L0 StructuredJson (camelCase, no [JsonPropertyName] on the record) and the
    // BookBible ThemesJson array. Same opts ChapterBriefService uses for the structured round-trip.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Serialize the L2 BookBrief to BookBriefJson with the SAME camelCase policy so the round-trip
    // (build → persist → read → deserialize) is stable.
    private static readonly JsonSerializerOptions SerializeOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public BookSummaryService(
        AppDbContext db,
        ChapterBriefService chapterBriefService,
        AnalysisProgressTracker progress,
        IServiceScopeFactory scopeFactory,
        IOptions<AiOptions> aiOptions,
        BookSummaryBuildRegistry buildRegistry,
        ILogger<BookSummaryService> logger)
    {
        _db = db;
        _chapterBriefService = chapterBriefService;
        _progress = progress;
        _scopeFactory = scopeFactory;
        _aiOptions = aiOptions;
        _buildRegistry = buildRegistry;
        _logger = logger;
    }

    /// <summary>
    /// The resolved active Summarization model id (the same the L0 chapter-brief builder stamps), used as
    /// the cross-model staleness target and the rollup's BuiltWithModel stamp.
    /// </summary>
    internal string? ActiveSummarizationModel =>
        LinguisticModelResolver.ResolveModelForTask(_aiOptions.Value, AiTaskType.Summarization);

    // ─── Status ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Read-only coverage status for (bookId, language). A chapter is "built" when its L0 structured brief
    /// (<see cref="ChunkSummary.StructuredJson"/>) is FRESH, using the SAME freshness definition the L0
    /// builder uses (the shared <see cref="ChapterStyleProfileFreshness.IsFresh"/> predicate on the brief's
    /// CreatedAt + BuiltWithModel vs the chapter's UpdatedAt + the active Summarization model), so status
    /// and build can never drift. Surfaces the cross-model warning and the in-progress build jobId.
    /// </summary>
    public async Task<BookSummaryStatus> GetStatusAsync(
        Guid bookId,
        string language,
        CancellationToken ct = default)
    {
        var lang = BaselineLanguageResolver.Normalize(language);

        var chapters = await _db.Chapters
            .AsNoTracking()
            .Where(c => c.BookId == bookId)
            .Select(c => new { c.Id, c.UpdatedAt })
            .ToListAsync(ct);

        // L0 briefs for this book+language (the (BookId, ChapterId) index is unique; Language is part of
        // the brief identity, so a row in a different language counts as not-built for this language).
        // wb1-r02: project StructuredBuiltAt (NOT CreatedAt) as the structured brief's build timestamp. The
        // flat re-summary path (BookIntelligenceService.SummarizeChaptersAsync) bumps the shared CreatedAt on
        // this same row, so reading CreatedAt here would let a flat re-summary mask a stale structured brief
        // and over-count "built" chapters. StructuredBuiltAt is stamped only by the structured builder.
        var briefs = await _db.ChunkSummaries
            .AsNoTracking()
            .Where(cs => cs.BookId == bookId)
            .Select(cs => new { cs.ChapterId, cs.StructuredBuiltAt, cs.BuiltWithModel, cs.StructuredJson, cs.Language })
            .ToListAsync(ct);

        var briefByChapter = briefs.ToDictionary(b => b.ChapterId);

        var (provider, activeModel) = LinguisticModelResolver.ResolveForTask(_aiOptions.Value, AiTaskType.Summarization);

        var built = 0;
        var stale = 0;
        foreach (var chapter in chapters)
        {
            // wb1-r02: a null StructuredBuiltAt (legacy structured row built before the column existed) is
            // treated as stale → rebuild once (self-heal), mirroring ChapterBriefService.IsFresh.
            if (briefByChapter.TryGetValue(chapter.Id, out var brief)
                && !string.IsNullOrWhiteSpace(brief.StructuredJson)
                && string.Equals(brief.Language, lang, StringComparison.Ordinal)
                && brief.StructuredBuiltAt is { } structuredBuiltAt
                && ChapterStyleProfileFreshness.IsFresh(structuredBuiltAt, brief.BuiltWithModel, chapter.UpdatedAt, activeModel))
                built++;
            else
                stale++; // missing OR no StructuredJson OR wrong language OR no structured stamp OR timestamp/model-stale
        }

        var summary = await _db.BookSummaryBaselines
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.BookId == bookId && b.Language == lang, ct);

        var hasSummary = summary != null && !string.IsNullOrWhiteSpace(summary.BookBriefJson);

        var maxParallel = Math.Max(1, _aiOptions.Value.MaxParallelStyleBaselineChapters);
        var (estimatedSeconds, estimatedUsd) = StyleBaselineService.ComputeEstimate(stale, provider, maxParallel);

        return new BookSummaryStatus
        {
            BookId = bookId,
            Language = lang,
            TotalChapters = chapters.Count,
            BuiltChapters = built,
            StaleCount = stale,
            HasSummary = hasSummary,
            LastUpdatedAt = summary?.UpdatedAt,
            BuiltWithModel = summary?.BuiltWithModel,
            ActiveModel = activeModel,
            BuiltWithDifferentModel = hasSummary
                && !string.Equals(summary!.BuiltWithModel, activeModel, StringComparison.Ordinal),
            ActiveBuildJobId = ResolveActiveBuildJobId(bookId, lang),
            ChaptersToBuild = stale,
            EstimatedSeconds = estimatedSeconds,
            EstimatedUsd = estimatedUsd
        };
    }

    /// <summary>
    /// Resolves the in-progress build jobId for (bookId, lang), but NEVER reports a job already finished.
    /// Mirrors <see cref="StyleBaselineService.ResolveActiveBuildJobId"/>: a lingering registry entry whose
    /// progress is terminal/unknown is self-healed (cleared) and reported as null, so the FE never reattaches
    /// to a dead job.
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

    // ─── Build ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Build (or refresh) the cached L2 book summary for (bookId, language). Enumerates chapters, ensures
    /// each L0 structured brief is fresh via <see cref="ChapterBriefService"/> (idempotent: only stale/
    /// missing chapters trigger an LLM call) with LIMITED parallelism, composes the L1 briefs, rolls them
    /// into a single L2 <see cref="BookBrief"/>, and persists it. Per-chapter failures never abort the job.
    /// Reports per-chapter progress through <paramref name="jobId"/> when supplied.
    ///
    /// IDEMPOTENT: when nothing is missing/stale and a rollup already exists that was built under the active
    /// model, this is a no-op returning ready with no LLM call.
    /// </summary>
    public async Task<BookSummaryBuildResult> BuildBookSummaryAsync(
        Guid bookId,
        string language,
        Guid? jobId = null,
        CancellationToken ct = default)
    {
        var lang = BaselineLanguageResolver.Normalize(language);

        var chapters = await _db.Chapters
            .AsNoTracking()
            .Where(c => c.BookId == bookId)
            .OrderBy(c => c.Order)
            .Select(c => c.Id)
            .ToListAsync(ct);

        var existingSummary = await _db.BookSummaryBaselines
            .FirstOrDefaultAsync(b => b.BookId == bookId && b.Language == lang, ct);

        var preStatus = await GetStatusAsync(bookId, lang, ct);

        if (jobId.HasValue)
        {
            _progress.StartJob(jobId.Value, AnalysisScope.Book, AnalysisType.Summarization,
                bookId, null, null, "Starting book summary build…");
            _progress.SetTotalChunks(jobId.Value, chapters.Count, $"Building book summary for {chapters.Count} chapters");
        }

        // IDEMPOTENT no-op: every chapter L0 brief fresh, a usable cached rollup exists, AND that rollup was
        // itself built under the ACTIVE model. A rollup built under a DIFFERENT model is out of date even
        // when every chapter brief matches (BuiltWithDifferentModel), so we fall through to recompute +
        // restamp it. No LLM call only when all three hold.
        if (preStatus.StaleCount == 0 && preStatus.HasSummary && !preStatus.BuiltWithDifferentModel)
        {
            if (jobId.HasValue)
                _progress.SetStatus(jobId.Value, AnalysisProgressStatus.Succeeded,
                    "Book summary already up to date.");

            return new BookSummaryBuildResult
            {
                Ready = true,
                NoOp = true,
                BuiltChapters = preStatus.BuiltChapters,
                TotalChapters = preStatus.TotalChapters,
                FailedChapters = 0,
                Message = "Book summary already up to date."
            };
        }

        // DEF-2: register this REAL (non-no-op) build so a reload/second tab can reattach. Only the async
        // job path carries a jobId; a jobId-less synchronous call is not reattachable so it is not
        // registered. Wrapped in try/finally so a crash still clears the registration.
        var registered = jobId.HasValue && _buildRegistry.TryStart(bookId, lang, jobId.Value);

        // Dedup the residual controller-guard race: if this is the async path but TryStart lost to a
        // concurrent build already holding the slot, BAIL (do not re-issue the same paid L0 LLM calls) and
        // drive THIS jobId to a terminal status so the losing tab's poll resolves and reattaches to the
        // winner. The jobId == null synchronous path is never registered and must STILL run.
        if (jobId.HasValue && !registered)
        {
            _logger.LogInformation(
                "Book summary build {JobId} for book {BookId} ({Lang}) skipped: a build is already in progress.",
                jobId, bookId, lang);
            _progress.SetStatus(jobId.Value, AnalysisProgressStatus.Canceled,
                "A book summary build is already in progress for this book; reattaching.");
            return new BookSummaryBuildResult
            {
                Ready = preStatus.HasSummary,
                NoOp = true,
                BuiltChapters = preStatus.BuiltChapters,
                TotalChapters = preStatus.TotalChapters,
                FailedChapters = preStatus.StaleCount,
                Message = "A book summary build is already in progress for this book and language."
            };
        }

        try
        {
            return await RunBuildAsync(bookId, lang, jobId, chapters, existingSummary, ct);
        }
        finally
        {
            if (registered)
                _buildRegistry.Complete(bookId, lang);
        }
    }

    /// <summary>
    /// The real (non-no-op) build body, extracted so <see cref="BuildBookSummaryAsync"/> can wrap it in a
    /// try/finally that clears the in-progress registry even on a crash.
    /// </summary>
    private async Task<BookSummaryBuildResult> RunBuildAsync(
        Guid bookId,
        string lang,
        Guid? jobId,
        List<Guid> chapters,
        BookSummaryBaseline? existingSummary,
        CancellationToken ct)
    {
        // (Re)build every chapter L0 brief with LIMITED parallelism. LoadOrBuildChapterBriefAsync is
        // idempotent (fresh chapters return cached without an LLM call) and degrades to null on failure,
        // so one bad chapter never aborts the job. Each build runs on its OWN DI scope (fresh DbContext +
        // scoped ChapterBriefService) because EF Core's DbContext is not thread-safe. Mirrors
        // StyleBaselineService.RunBuildAsync verbatim.
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
                    using var scope = _scopeFactory.CreateScope();
                    var briefService = scope.ServiceProvider.GetRequiredService<ChapterBriefService>();
                    await briefService.LoadOrBuildChapterBriefAsync(bookId, chapterId, lang, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Book summary: chapter {ChapterId} of book {BookId} threw during L0 build; skipping",
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

        // Compose L1 ChapterBriefs from the (now-refreshed) L0 rows, then roll up into the L2 BookBrief.
        var chapterBriefs = await ComposeChapterBriefsAsync(bookId, lang, ct);
        var bookBrief = await ComposeBookBriefAsync(bookId, chapterBriefs, ct);

        // How many chapters now have a fresh L0 brief (post-build truth, reused predicate).
        var postStatus = await GetStatusAsync(bookId, lang, ct);
        var failedChapters = postStatus.StaleCount;

        if (chapterBriefs.Count == 0)
        {
            // No usable L1 briefs → no meaningful rollup. We do NOT clear an existing cached rollup (it may
            // still be a useful older snapshot); we just report not-ready.
            var emptyMsg = "Not enough chapter briefs to build a book summary.";
            if (jobId.HasValue)
                _progress.SetStatus(jobId.Value, AnalysisProgressStatus.Succeeded, emptyMsg);

            return new BookSummaryBuildResult
            {
                Ready = existingSummary != null && !string.IsNullOrWhiteSpace(existingSummary.BookBriefJson),
                NoOp = false,
                BuiltChapters = postStatus.BuiltChapters,
                TotalChapters = postStatus.TotalChapters,
                FailedChapters = failedChapters,
                Message = emptyMsg
            };
        }

        var builtWithModel = ActiveSummarizationModel;
        var bookBriefJson = JsonSerializer.Serialize(bookBrief, SerializeOpts);

        if (existingSummary == null)
        {
            existingSummary = new BookSummaryBaseline
            {
                BookId = bookId,
                Language = lang,
                BookBriefJson = bookBriefJson,
                BuiltChapterCount = chapterBriefs.Count,
                BuiltWithModel = builtWithModel
                // CreatedAt/UpdatedAt stamped by the SaveChanges override.
            };
            _db.BookSummaryBaselines.Add(existingSummary);
        }
        else
        {
            existingSummary.BookBriefJson = bookBriefJson;
            existingSummary.BuiltChapterCount = chapterBriefs.Count;
            existingSummary.BuiltWithModel = builtWithModel;
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // A concurrent build may have inserted the unique (BookId, Language) row first. Detach so the
            // failed insert is not retried, then re-read the winning row.
            _logger.LogWarning(ex, "Failed to persist BookSummaryBaseline for book {BookId}; reloading", bookId);
            _db.Entry(existingSummary).State = EntityState.Detached;
            existingSummary = await _db.BookSummaryBaselines
                .FirstOrDefaultAsync(b => b.BookId == bookId && b.Language == lang, ct);
        }

        var ready = existingSummary != null && !string.IsNullOrWhiteSpace(existingSummary.BookBriefJson);
        var successMsg = failedChapters > 0
            ? $"Book summary built from {postStatus.BuiltChapters}/{postStatus.TotalChapters} chapters ({failedChapters} failed)."
            : $"Book summary built from {postStatus.BuiltChapters}/{postStatus.TotalChapters} chapters.";

        if (jobId.HasValue)
            _progress.SetStatus(jobId.Value, AnalysisProgressStatus.Succeeded, successMsg);

        return new BookSummaryBuildResult
        {
            Ready = ready,
            NoOp = false,
            BuiltChapters = postStatus.BuiltChapters,
            TotalChapters = postStatus.TotalChapters,
            FailedChapters = failedChapters,
            Message = successMsg
        };
    }

    // ─── L1 composition: L0 + chapter metadata → ChapterBrief ───────────────────────────────────────

    /// <summary>
    /// Composes the L1 <see cref="ChapterBrief"/> for every chapter that has a usable L0 structured brief,
    /// ordered by chapter Order. Pure projection from the cached L0 (<see cref="ChunkSummary.StructuredJson"/>)
    /// plus the chapter's Title/Order; a chapter without an L0 brief (failed/empty) is simply omitted.
    /// Public so the FE-facing read path and tests can compose L1 without forcing a (re)build.
    /// </summary>
    public async Task<IReadOnlyList<ChapterBrief>> ComposeChapterBriefsAsync(
        Guid bookId,
        string language,
        CancellationToken ct = default)
    {
        var lang = BaselineLanguageResolver.Normalize(language);

        var chapters = await _db.Chapters
            .AsNoTracking()
            .Where(c => c.BookId == bookId)
            .OrderBy(c => c.Order)
            .Select(c => new { c.Id, c.Title, c.Order })
            .ToListAsync(ct);

        var briefs = await _db.ChunkSummaries
            .AsNoTracking()
            .Where(cs => cs.BookId == bookId && cs.Language == lang)
            .Select(cs => new { cs.ChapterId, cs.StructuredJson })
            .ToListAsync(ct);

        var jsonByChapter = briefs
            .Where(b => !string.IsNullOrWhiteSpace(b.StructuredJson))
            .ToDictionary(b => b.ChapterId, b => b.StructuredJson!);

        var result = new List<ChapterBrief>();
        foreach (var chapter in chapters)
        {
            if (!jsonByChapter.TryGetValue(chapter.Id, out var json))
                continue;

            var l0 = ParseL0(json);
            if (l0 == null)
                continue;

            result.Add(new ChapterBrief
            {
                Title = string.IsNullOrWhiteSpace(chapter.Title) ? $"Chapter {chapter.Order + 1}" : chapter.Title,
                Order = chapter.Order,
                // The L0 carries no flat summary (ChapterBriefService leaves SummaryText to
                // BookIntelligenceService); the L1 Summary stays null and the structured fields below carry
                // the signal. Plot/character/thematic state map straight through from L0.
                Summary = null,
                PlotEvents = l0.PlotEvents,
                CharacterStates = l0.CharacterStates,
                ThematicMarkers = l0.ThematicMarkers,
                ToneNotes = l0.ToneNotes,
                OpenThreads = l0.OpenThreads
            });
        }

        return result;
    }

    // ─── L2 composition: L1 rollup + BookProfile/BookBible reuse → BookBrief ─────────────────────────

    /// <summary>
    /// Composes the single L2 <see cref="BookBrief"/>: a genre/themes/synopsis-level rollup. REUSES
    /// <see cref="BookProfile"/> (genre, sub-genre, audience, literature level, synopsis) and
    /// <see cref="BookBible.ThemesJson"/> (curated themes) where present rather than inventing parallel
    /// state, and AUGMENTS the theme set with the union of the L1 thematic markers (so a book with no
    /// curated themes still gets a themes rollup from the chapter briefs). Deterministic - no LLM call.
    /// </summary>
    public async Task<BookBrief> ComposeBookBriefAsync(
        Guid bookId,
        IReadOnlyList<ChapterBrief> chapterBriefs,
        CancellationToken ct = default)
    {
        var profile = await _db.BookProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.BookId == bookId, ct);

        var bible = await _db.BookBibles
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.BookId == bookId, ct);

        // Curated themes from the BookBible (JSON array of ThemeEntry: name/description/significance).
        var themes = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ParseBibleThemeNames(bible?.ThemesJson))
        {
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
                themes.Add(name);
        }

        // Augment with the union of L1 thematic markers (deduped, curated themes win order).
        foreach (var brief in chapterBriefs)
        {
            foreach (var marker in brief.ThematicMarkers)
            {
                if (!string.IsNullOrWhiteSpace(marker) && seen.Add(marker))
                    themes.Add(marker);
            }
        }

        return new BookBrief
        {
            Genre = profile?.Genre,
            SubGenre = profile?.SubGenre,
            TargetAudience = profile?.TargetAudience,
            LiteratureLevel = profile?.LiteratureLevel,
            Themes = themes,
            Synopsis = profile?.Synopsis
        };
    }

    // ─── Parse helpers ──────────────────────────────────────────────────────────────────────────────

    private static StructuredChunkSummaryData? ParseL0(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<StructuredChunkSummaryData>(json, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses the curated theme NAMES out of <see cref="BookBible.ThemesJson"/>. Tolerates both the
    /// structured ThemeEntry array (objects with a "name") and a plain string array, degrading to an empty
    /// list on null/blank/invalid (the BookBible is optional supporting data, never required).
    /// </summary>
    private static IReadOnlyList<string> ParseBibleThemeNames(string? themesJson)
    {
        if (string.IsNullOrWhiteSpace(themesJson))
            return Array.Empty<string>();

        try
        {
            using var doc = JsonDocument.Parse(themesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<string>();

            var names = new List<string>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    var s = el.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) names.Add(s!);
                }
                else if (el.ValueKind == JsonValueKind.Object
                    && el.TryGetProperty("name", out var nameEl)
                    && nameEl.ValueKind == JsonValueKind.String)
                {
                    var s = nameEl.GetString();
                    if (!string.IsNullOrWhiteSpace(s)) names.Add(s!);
                }
            }
            return names;
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }
}
