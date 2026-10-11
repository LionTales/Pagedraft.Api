using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// The whole-book proofread run: one ordinary Proofread analysis per chapter, in reading order, behind a
/// single book-scope PARENT job. Routes are on <c>BooksController</c> under <c>api/books/{bookId}/proofread</c>.
/// </summary>
public interface IBookProofreadService
{
    /// <summary>
    /// Plans the run (chapters with text, minus the fresh ones unless <paramref name="force"/>) and starts it in
    /// the background. Started=false with no job when nothing needs running; an already-active run for the same
    /// (book, language) is returned as Started=true with ITS job id (the loser-gets-the-active-id rule).
    /// </summary>
    Task<StartBookProofreadResponse> StartAsync(Guid bookId, string language, bool force, CancellationToken ct);

    /// <summary>Coverage, staleness and the in-flight run id for (book, language).</summary>
    Task<BookProofreadStatusDto> GetStatusAsync(Guid bookId, string language, CancellationToken ct);

    /// <summary>Asks the active run with this job id to stop before its next chapter. False when none matches.</summary>
    bool Cancel(Guid bookId, Guid jobId);
}

/// <summary>
/// Whole-book proofread, the fan-out of <see cref="StyleBaselineService"/>'s RunBuildAsync shape (per-chapter
/// DI scope, bounded parallelism, a failed chapter logged and skipped, chapters reported as the parent's
/// chunks) with two additions:
///
/// <para>CHILD JOBS. Every chapter (or scene, see below) gets its OWN Chapter/Scene-scope Proofread job in
/// <see cref="AnalysisProgressTracker"/> and runs through the ordinary
/// <see cref="UnifiedAnalysisService.RunAsync"/> with that job id. So the chapter UI cannot tell a whole-book
/// run from a per-chapter one: <c>GET active-analysis-jobs</c> (which excludes Book scope) lists the child,
/// the per-chapter running badges light, and the result persisted is the same Active row, archiving the
/// previous one. RunAsync's own chunk sizing is untouched (memory pagedraft-chunk-threshold-contract).</para>
///
/// <para>CANCEL BETWEEN CHAPTERS. The registry's token source stops the run before its NEXT chapter; the
/// chapter in flight finishes and keeps its result. Only application shutdown aborts a chapter mid-run.
/// Finished chapters are durable, which is also the restart story: re-running after an API restart skips
/// the fresh ones.</para>
///
/// <para>PARENT COUNTS. TotalChunks = planned chapters; CompletedChunks counts chapters PROOFREAD (a failed
/// or unreliable chapter moves the position but not the count). So a terminal Succeeded with
/// Completed &lt; Total is a partial run, and Canceled's Completed is "chapters proofread and saved", which is
/// exactly what the client's partial/canceled copy prints.</para>
/// </summary>
public sealed class BookProofreadService : IBookProofreadService
{
    /// <summary>
    /// The parent's LastUpdatedUtc only moves at chapter boundaries, and the tracker prunes an entry 30 minutes
    /// after its last update. A long chapter (~45+ chunks at ~40 s each) would therefore lose the parent from
    /// under its poller, so the parent is touched on this cadence while a chapter runs.
    /// </summary>
    internal static TimeSpan ParentHeartbeatInterval { get; set; } = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AnalysisProgressTracker _progress;
    private readonly BookProofreadBuildRegistry _registry;
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<BookProofreadService> _logger;

    public BookProofreadService(
        IServiceScopeFactory scopeFactory,
        AnalysisProgressTracker progress,
        BookProofreadBuildRegistry registry,
        IOptions<AiOptions> aiOptions,
        IHostApplicationLifetime lifetime,
        ILogger<BookProofreadService> logger)
    {
        _scopeFactory = scopeFactory;
        _progress = progress;
        _registry = registry;
        _aiOptions = aiOptions;
        _lifetime = lifetime;
        _logger = logger;
    }

    /// <summary>The background run started by the last successful <see cref="StartAsync"/>; a test seam only.</summary>
    internal Task? RunTask { get; private set; }

    public async Task<StartBookProofreadResponse> StartAsync(Guid bookId, string language, bool force, CancellationToken ct)
    {
        var lang = BaselineLanguageResolver.Normalize(language);

        List<ChapterProofreadState> states;
        AiTier tier;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            states = await LoadChapterStatesAsync(db, bookId, lang, ct);
            tier = await BookAiTierResolver.ResolveAsync(db, bookId, AiTaskType.Proofread, _logger, ct);
        }

        var candidates = states.Where(s => s.Units.Count > 0).ToList();
        var planned = candidates.Where(s => force || !s.IsFresh).ToList();
        var skippedFresh = candidates.Count - planned.Count;
        var (estimatedChunks, estimatedSeconds) = Estimate(planned, _aiOptions.Value, lang, tier);

        StartBookProofreadResponse Response(Guid jobId, bool started) =>
            new(jobId, started, skippedFresh, planned.Count, estimatedChunks, estimatedSeconds);

        // A run already in flight wins over everything, including "nothing to do": the caller reattaches.
        if (_registry.TryGetActive(bookId, lang) is Guid active)
            return Response(active, started: true);

        if (planned.Count == 0)
            return Response(Guid.Empty, started: false);

        var stopping = _lifetime.ApplicationStopping;
        var parentId = Guid.NewGuid();
        if (!_registry.TryStart(bookId, lang, parentId, out var cancelSource) || cancelSource is null)
        {
            // Lost the compare-and-set to a concurrent start: hand back the winner's job.
            var winner = _registry.TryGetActive(bookId, lang);
            return winner is Guid w ? Response(w, started: true) : Response(Guid.Empty, started: false);
        }

        // Registered BEFORE the id is returned so the first poll cannot 404.
        _progress.StartJob(parentId, AnalysisScope.Book, AnalysisType.Proofread, bookId, null, null,
            $"Queued whole-book proofread of {planned.Count} chapters");
        _progress.SetTotalChunks(parentId, planned.Count);

        RunTask = Task.Run(async () =>
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancelSource.Token, stopping);
                await RunAsync(parentId, bookId, lang, planned, linked.Token, stopping);
            }
            catch (Exception ex)
            {
                try { _logger.LogError(ex, "Whole-book proofread {JobId} for book {BookId} failed", parentId, bookId); } catch { }
                try { _progress.SetStatus(parentId, AnalysisProgressStatus.Failed, "Whole-book proofread failed."); } catch { }
            }
            finally
            {
                _registry.Complete(bookId, lang);
            }
        }, CancellationToken.None);

        return Response(parentId, started: true);
    }

    public async Task<BookProofreadStatusDto> GetStatusAsync(Guid bookId, string language, CancellationToken ct)
    {
        var lang = BaselineLanguageResolver.Normalize(language);
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var states = await LoadChapterStatesAsync(db, bookId, lang, ct);

        return new BookProofreadStatusDto(
            ActiveJobId: _registry.TryGetActive(bookId, lang),
            ChaptersWithText: states.Count,
            ChaptersWithActiveProofread: states.Count(s => s.HasActiveResult),
            ChaptersStale: states.Count(s => s.HasActiveResult && !s.IsFresh),
            LastRunAt: states.Select(s => s.NewestResultAt).Where(d => d.HasValue).Max());
    }

    public bool Cancel(Guid bookId, Guid jobId)
    {
        if (!_registry.TryCancel(bookId, jobId)) return false;
        _progress.SetStatus(jobId, AnalysisProgressStatus.Running, "Stopping after the current chapter");
        return true;
    }

    // ── The run ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Works through <paramref name="planned"/> in reading order, at most MaxParallelBookProofreadChapters at a
    /// time (default 1: the 8GB card is single-slot). Workers TAKE the next chapter only while
    /// <paramref name="cancelToken"/> is clear, which is what "stop before the next chapter" means; the chapter
    /// itself runs under <paramref name="stoppingToken"/> only.
    /// </summary>
    internal async Task RunAsync(
        Guid parentId, Guid bookId, string language, IReadOnlyList<ChapterProofreadState> planned,
        CancellationToken cancelToken, CancellationToken stoppingToken)
    {
        var total = planned.Count;
        var maxParallel = Math.Clamp(_aiOptions.Value.MaxParallelBookProofreadChapters, 1, Math.Max(1, total));
        var next = -1;
        var proofread = 0;
        var failed = 0;
        var processed = 0;

        async Task Worker()
        {
            while (!cancelToken.IsCancellationRequested)
            {
                var index = Interlocked.Increment(ref next);
                if (index >= total) return;

                var chapter = planned[index];
                _progress.ChunkStarted(parentId, index + 1, total);

                bool ok;
                try
                {
                    ok = await RunChapterAsync(parentId, bookId, language, chapter, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return; // application shutdown: the child is already marked Canceled
                }

                Interlocked.Increment(ref processed);
                if (ok)
                {
                    Interlocked.Increment(ref proofread);
                    _progress.ChunkCompleted(parentId, index + 1, total);
                }
                else
                {
                    Interlocked.Increment(ref failed);
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, maxParallel).Select(_ => Worker()));

        if (processed < total)
        {
            var why = stoppingToken.IsCancellationRequested ? "the server is shutting down" : "it was canceled";
            _logger.LogInformation(
                "Whole-book proofread {JobId} for book {BookId} stopped because {Why}: {Done} of {Total} chapters proofread",
                parentId, bookId, why, proofread, total);
            _progress.SetStatus(parentId, AnalysisProgressStatus.Canceled,
                $"Whole-book proofread stopped: {proofread} of {total} chapters proofread.");
        }
        else if (failed == 0)
        {
            _progress.SetStatus(parentId, AnalysisProgressStatus.Succeeded,
                $"Whole-book proofread finished: {proofread} chapters proofread.");
        }
        else if (proofread == 0)
        {
            _progress.SetStatus(parentId, AnalysisProgressStatus.Failed,
                $"Whole-book proofread failed: none of the {total} chapters could be proofread.");
        }
        else
        {
            // Partial: Succeeded with CompletedChunks < TotalChunks (see the class remarks).
            _progress.SetStatus(parentId, AnalysisProgressStatus.Succeeded,
                $"Whole-book proofread finished partially: {proofread} of {total} chapters proofread, {failed} failed.");
        }
    }

    /// <summary>
    /// Proofreads one chapter: its scenes, one Scene-scope run each, when the scenes hold the chapter's current
    /// text (<see cref="ChapterTextSource.ScenesHoldTheChaptersCurrentText"/>, the export's own rule, so the
    /// annotated export finds the results on the units it renders); otherwise the chapter row. True when every
    /// unit produced a result that is not flagged unreliable.
    /// </summary>
    private async Task<bool> RunChapterAsync(
        Guid parentId, Guid bookId, string language, ChapterProofreadState chapter, CancellationToken stoppingToken)
    {
        var allOk = true;
        foreach (var unit in chapter.Units)
        {
            var childId = Guid.NewGuid();
            var scope = unit.SceneId.HasValue ? AnalysisScope.Scene : AnalysisScope.Chapter;
            _progress.StartJob(childId, scope, AnalysisType.Proofread, bookId, chapter.ChapterId, unit.SceneId,
                "Queued Proofread job (whole-book run)");

            using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var heartbeat = HeartbeatAsync(parentId, heartbeatStop.Token);
            try
            {
                using var diScope = _scopeFactory.CreateScope();
                var unified = diScope.ServiceProvider.GetRequiredService<UnifiedAnalysisService>();
                var result = await unified.RunAsync(
                    scope, AnalysisType.Proofread, unit.SceneId ?? chapter.ChapterId,
                    customPrompt: null, language, childId, stoppingToken);

                // RunAsync marks the child Succeeded once the row is committed; this only covers a path that
                // returned without signalling, so the client's poll never hangs on a finished job.
                if (_progress.TryGet(childId, out var snap) && snap is { Status: AnalysisProgressStatus.Running or AnalysisProgressStatus.Pending })
                    _progress.SetStatus(childId, AnalysisProgressStatus.Succeeded, "Proofread finished");

                if (result.ProofreadResultUnreliable)
                {
                    _logger.LogWarning(
                        "Whole-book proofread {JobId}: chapter {ChapterId} scene {SceneId} produced an UNRELIABLE result {ResultId}; counted as not proofread",
                        parentId, chapter.ChapterId, unit.SceneId, result.Id);
                    allOk = false;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _progress.SetStatus(childId, AnalysisProgressStatus.Canceled, "Proofread canceled due to application shutdown.");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Whole-book proofread {JobId}: chapter {ChapterId} scene {SceneId} failed; continuing with the next",
                    parentId, chapter.ChapterId, unit.SceneId);
                _progress.SetStatus(childId, AnalysisProgressStatus.Failed, ex.Message);
                allOk = false;
            }
            finally
            {
                heartbeatStop.Cancel();
                try { await heartbeat; } catch (OperationCanceledException) { }
            }
        }
        return allOk;
    }

    private async Task HeartbeatAsync(Guid parentId, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(ParentHeartbeatInterval);
        while (await timer.WaitForNextTickAsync(ct))
            _progress.SetStatus(parentId, AnalysisProgressStatus.Running);
    }

    // ── Planning ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One proofreadable unit: the chapter row (SceneId null) or one scene.</summary>
    internal sealed record ProofreadUnit(Guid? SceneId, DateTimeOffset UpdatedAt);

    /// <summary>A chapter with text, the units a run would proofread, and whether its results are fresh.</summary>
    internal sealed record ChapterProofreadState(
        Guid ChapterId,
        int Order,
        int WordCount,
        IReadOnlyList<ProofreadUnit> Units,
        bool HasActiveResult,
        bool IsFresh,
        DateTimeOffset? NewestResultAt);

    /// <summary>
    /// Every chapter with text (<see cref="ChapterTextPredicate.HasText"/>) in reading order, with its units and
    /// freshness. A unit is FRESH when its Active Proofread result (Chapter scope keyed by chapter with SceneId
    /// null, or Scene scope keyed by scene) was created at or after the unit's last edit, in this language, and
    /// is NOT flagged <see cref="AnalysisResult.ProofreadResultUnreliable"/>: an unreliable run is a failed run
    /// the author should not be told is done, so a non-force run replaces it. A chapter is fresh when all its
    /// units are.
    /// </summary>
    internal static async Task<List<ChapterProofreadState>> LoadChapterStatesAsync(
        AppDbContext db, Guid bookId, string language, CancellationToken ct)
    {
        var chapters = await db.Chapters.AsNoTracking()
            .Where(c => c.BookId == bookId)
            .Where(ChapterTextPredicate.HasText)
            .OrderBy(c => c.Order)
            .Select(c => new { c.Id, c.Order, c.WordCount, c.UpdatedAt })
            .ToListAsync(ct);
        if (chapters.Count == 0) return new List<ChapterProofreadState>();

        var chapterIds = chapters.Select(c => c.Id).ToList();
        var scenes = await db.Scenes.AsNoTracking()
            .Where(s => chapterIds.Contains(s.ChapterId))
            .OrderBy(s => s.Order)
            .ToListAsync(ct);

        var results = await db.AnalysisResults.AsNoTracking()
            .Where(a => a.ChapterId != null && chapterIds.Contains(a.ChapterId.Value)
                && a.AnalysisType == AnalysisType.Proofread
                && a.Status == AnalysisStatus.Active
                && (a.Scope == AnalysisScope.Chapter || a.Scope == AnalysisScope.Scene))
            .Select(a => new { a.ChapterId, a.SceneId, a.Scope, a.CreatedAt, a.Language, a.ProofreadResultUnreliable })
            .ToListAsync(ct);

        var states = new List<ChapterProofreadState>(chapters.Count);
        foreach (var c in chapters)
        {
            var chapterScenes = scenes.Where(s => s.ChapterId == c.Id).ToList();
            List<ProofreadUnit> units;
            if (ChapterTextSource.ScenesHoldTheChaptersCurrentText(chapterScenes))
            {
                units = chapterScenes
                    .Where(s => BookExportService.HasRenderableContent(s.ContentSfdt))
                    .Select(s => new ProofreadUnit(s.Id, s.UpdatedAt))
                    .ToList();
            }
            else
            {
                units = new List<ProofreadUnit> { new(null, c.UpdatedAt) };
            }

            var unitResults = units
                .Select(u => results.Where(r => u.SceneId.HasValue
                        ? r.Scope == AnalysisScope.Scene && r.SceneId == u.SceneId
                        : r.Scope == AnalysisScope.Chapter && r.SceneId == null && r.ChapterId == c.Id)
                    .ToList())
                .ToList();

            var all = unitResults.SelectMany(r => r).ToList();
            var isFresh = units.Count > 0 && units.Select((u, i) => unitResults[i].Any(r =>
                    r.CreatedAt >= u.UpdatedAt
                    && !r.ProofreadResultUnreliable
                    && BaselineLanguageResolver.Normalize(r.Language) == language))
                .All(fresh => fresh);

            states.Add(new ChapterProofreadState(
                c.Id, c.Order, c.WordCount, units,
                HasActiveResult: all.Count > 0,
                IsFresh: isFresh,
                NewestResultAt: all.Count > 0 ? all.Max(r => r.CreatedAt) : null));
        }
        return states;
    }

    /// <summary>
    /// The consent gate's ETA. Chunks per chapter = ceil(words / the target RunAsync actually chunks at), via
    /// the SAME accessor (<see cref="UnifiedAnalysisService.ProofreadChunkTargetWordsFor"/>) so the estimate
    /// cannot drift from the real sizing; a scene-split chapter is at least one chunk per scene, since each scene
    /// is its own run. Seconds = chunks x <see cref="AiOptions.ProofreadSecondsPerChunkEstimate"/>.
    /// </summary>
    internal static (int Chunks, int Seconds) Estimate(
        IReadOnlyList<ChapterProofreadState> planned, AiOptions opts, string language, AiTier tier)
    {
        var target = Math.Max(1, UnifiedAnalysisService.ProofreadChunkTargetWordsFor(opts, language, tier));
        var chunks = planned.Sum(c => Math.Max(
            c.Units.Count,
            (int)Math.Ceiling(Math.Max(0, c.WordCount) / (double)target)));
        return (chunks, chunks * Math.Max(0, opts.ProofreadSecondsPerChunkEstimate));
    }
}
