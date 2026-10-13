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
/// single book-scope PARENT job. Routes are on <c>BookProofreadController</c> under <c>api/books/{bookId}/proofread</c>.
/// </summary>
public interface IBookProofreadService
{
    /// <summary>
    /// Plans the run (chapters with text, minus the fresh ones unless <paramref name="force"/>) and starts it in
    /// the background. Started=false with no job when nothing needs running; an already-active run for the same
    /// (book, language) is returned as Started=true with ITS job id (the loser-gets-the-active-id rule).
    /// </summary>
    Task<StartBookProofreadResponse> StartAsync(Guid bookId, string language, bool force, CancellationToken ct);

    /// <summary>
    /// c20: starts a run over exactly the status's FailedChapters (force semantics for them, every other chapter
    /// untouched). Same answer shape and rules as <see cref="StartAsync"/>; Started=false with no job when the
    /// record is empty.
    /// </summary>
    Task<StartBookProofreadResponse> StartFailedAsync(Guid bookId, string language, CancellationToken ct);

    /// <summary>
    /// Coverage, staleness, the in-flight run id, the plan + ETA a normal and a forced start would have right
    /// now (the same <c>Plan</c> the start runs), and the chapters whose Active result is flagged unreliable.
    /// </summary>
    Task<BookProofreadStatusDto> GetStatusAsync(Guid bookId, string language, CancellationToken ct);

    /// <summary>
    /// Asks the active run with this job id to stop before its next chapter. False when none matches, or when the
    /// run has already taken its last chapter and is finishing (be-c04): a cancel never changes a run that is
    /// writing, or has written, its terminal status.
    /// </summary>
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
/// chapter in flight finishes and keeps its result. Only application shutdown aborts a chapter mid-run. The run
/// is the ONLY writer of the parent's status: <see cref="Cancel"/> just cancels the token, and the run's own token
/// registration writes the "Stopping" note (be-c04).
/// Finished chapters are durable, which is also the restart story: re-running after an API restart skips
/// the fresh ones.</para>
///
/// <para>PARENT COUNTS. TotalChunks = planned chapters; CompletedChunks counts chapters PROOFREAD (a failed
/// or unreliable chapter moves the position but not the count). So a terminal Succeeded with
/// Completed &lt; Total is a partial run, and Canceled's Completed is "chapters proofread and saved", which is
/// exactly what the client's partial/canceled copy prints.</para>
///
/// <para>PARENT PERCENT AND ETA (c10). Chapters are wildly uneven, so the parent's EstimatedCompletionPercent is
/// WORD-weighted and its EstimatedRemainingSeconds is computed here from the plan's word counts and the observed
/// pace, both published by <see cref="BookProofreadRunProgress"/> (formula in its remarks); the chunk fields above
/// stay chapters.</para>
///
/// <para>RETRY THE FAILED CHAPTERS (c20). A chapter whose model call THROWS persists nothing, so it keeps its
/// previous Active result; when that result is reliable and newer than the chapter's last edit it still counts as
/// covered and fresh, so a normal start skips it. A chapter that comes back UNRELIABLE is different (be-c01, be-c05):
/// its new result archives the previous Active row (<c>UnifiedAnalysisService.ArchivePreviousActiveAsync</c>, which
/// marks that row's pending suggestions Superseded; restoring them is a recorded open gap) and, being flagged, is
/// never covered or fresh, so a normal start re-plans it. Every attempt's outcome goes to the registry's
/// in-memory failed-chapters record; the status lists what is still uncovered (<see cref="FailedStates"/>) and
/// <see cref="StartFailedAsync"/> plans exactly that, forced. Lost on API restart (then: force, as before).</para>
/// </summary>
public sealed class BookProofreadService : IBookProofreadService
{
    /// <summary>
    /// The run's TICK while a unit is in flight. It republishes the parent's word-weighted percent and ETA
    /// (<see cref="BookProofreadRunProgress.Refresh"/>) from the child's chunk progress, so a 3-hour chapter moves
    /// smoothly instead of jumping at its end; and since that publish moves the parent's LastUpdatedUtc, it is
    /// also the heartbeat that keeps the parent from the tracker's 30-minute prune during a long chapter.
    /// </summary>
    internal static TimeSpan ParentHeartbeatInterval { get; set; } = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AnalysisProgressTracker _progress;
    private readonly BookProofreadBuildRegistry _registry;
    private readonly IOptions<AiOptions> _aiOptions;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<BookProofreadService> _logger;
    private readonly TimeProvider _clock;
    private readonly BookProofreadFaultInjection _faults;

    /// <param name="clock">The ONE clock the run's ETA reads (elapsed since the parent started); the system clock
    /// unless a test registers a <see cref="TimeProvider"/>.</param>
    /// <param name="faults">The TEST-ONLY fault seam (c20); inert when not registered and in every non-Development
    /// host (see <see cref="BookProofreadFaultInjection"/>).</param>
    public BookProofreadService(
        IServiceScopeFactory scopeFactory,
        AnalysisProgressTracker progress,
        BookProofreadBuildRegistry registry,
        IOptions<AiOptions> aiOptions,
        IHostApplicationLifetime lifetime,
        ILogger<BookProofreadService> logger,
        TimeProvider? clock = null,
        BookProofreadFaultInjection? faults = null)
    {
        _scopeFactory = scopeFactory;
        _progress = progress;
        _registry = registry;
        _aiOptions = aiOptions;
        _lifetime = lifetime;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _faults = faults ?? BookProofreadFaultInjection.None;
    }

    /// <summary>The background run started by the last successful <see cref="StartAsync"/>; a test seam only.</summary>
    internal Task? RunTask { get; private set; }

    public Task<StartBookProofreadResponse> StartAsync(Guid bookId, string language, bool force, CancellationToken ct) =>
        StartCoreAsync(bookId, language, force, onlyFailed: false, ct);

    public Task<StartBookProofreadResponse> StartFailedAsync(Guid bookId, string language, CancellationToken ct) =>
        StartCoreAsync(bookId, language, force: true, onlyFailed: true, ct);

    private async Task<StartBookProofreadResponse> StartCoreAsync(
        Guid bookId, string language, bool force, bool onlyFailed, CancellationToken ct)
    {
        var lang = BaselineLanguageResolver.Normalize(language);
        var (states, tier) = await LoadPlanningInputsAsync(bookId, lang, ct);
        var only = onlyFailed ? FailedStates(bookId, lang, states).Select(s => s.ChapterId).ToHashSet() : null;
        var plan = Plan(states, force, _aiOptions.Value, lang, tier, only);
        var planned = plan.Planned;

        StartBookProofreadResponse Response(Guid jobId, bool started) =>
            new(jobId, started, plan.SkippedFresh, planned.Count, plan.EstimatedChunks, plan.EstimatedSeconds);

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

        // be-c04: from TryStart on, every path either hands the entry to the background run (whose finally calls
        // Complete) or completes it here. A throw left outside both used to leave the book "running" forever, its
        // phantom job id returned to every later start.
        BookProofreadRunProgress runProgress;
        try
        {
            // Registered BEFORE the id is returned so the first poll cannot 404.
            _progress.StartJob(parentId, AnalysisScope.Book, AnalysisType.Proofread, bookId, null, null,
                $"Queued whole-book proofread of {planned.Count} chapters");
            _progress.SetTotalChunks(parentId, planned.Count);
            // The first ETA the poll sees is the one this response returns (c10).
            runProgress = new BookProofreadRunProgress(_progress, parentId, planned, plan.EstimatedSeconds, _clock);
            runProgress.Refresh();
        }
        catch
        {
            _registry.Complete(bookId, lang);
            // The id never reaches a caller, but the job may already be tracked: do not leave it Running.
            try { _progress.SetStatus(parentId, AnalysisProgressStatus.Failed, "Whole-book proofread failed to start."); } catch { }
            throw;
        }

        RunTask = Task.Run(async () =>
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancelSource.Token, stopping);
                await RunAsync(parentId, bookId, lang, planned, runProgress, linked.Token, stopping);
            }
            catch (Exception ex)
            {
                try { _logger.LogError(ex, "Whole-book proofread {JobId} for book {BookId} failed", parentId, bookId); } catch { }
                try { runProgress.Finish(AnalysisProgressStatus.Failed); } catch { }
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
        var (states, tier) = await LoadPlanningInputsAsync(bookId, lang, ct);
        var normal = Plan(states, force: false, _aiOptions.Value, lang, tier);
        var forced = Plan(states, force: true, _aiOptions.Value, lang, tier);
        var failedStates = FailedStates(bookId, lang, states);
        var failed = Plan(states, force: true, _aiOptions.Value, lang, tier,
            only: failedStates.Select(s => s.ChapterId).ToHashSet());

        return new BookProofreadStatusDto(
            ActiveJobId: _registry.TryGetActive(bookId, lang),
            ChaptersWithText: states.Count,
            ChaptersWithActiveProofread: states.Count(s => s.IsCovered),
            // Edited is a subset of covered and unreliable is disjoint from it, so the sum counts no chapter twice.
            ChaptersStale: states.Count(s => s.IsEdited) + states.Count(s => s.HasUnreliableResult),
            LastRunAt: states.Select(s => s.NewestReliableAt).Where(d => d.HasValue).Max(),
            PlannedChapters: normal.Planned.Count,
            EstimatedChunks: normal.EstimatedChunks,
            EstimatedSeconds: normal.EstimatedSeconds,
            ForcePlannedChapters: forced.Planned.Count,
            ForceEstimatedChunks: forced.EstimatedChunks,
            ForceEstimatedSeconds: forced.EstimatedSeconds,
            UnreliableChapters: states
                .Where(s => s.HasUnreliableResult)
                .Select(s => new BookProofreadUnreliableChapterDto(s.ChapterId, s.Title, s.Order))
                .ToList(),
            FailedChapters: failedStates
                .Select(s => new BookProofreadFailedChapterDto(s.ChapterId, s.Title, s.Order))
                .ToList(),
            FailedEstimatedChunks: failed.EstimatedChunks,
            FailedEstimatedSeconds: failed.EstimatedSeconds,
            ChaptersEdited: states.Count(s => s.IsEdited),
            ChaptersUnreliable: states.Count(s => s.HasUnreliableResult));
    }

    /// <summary>
    /// c20: the chapters of the registry's failed-chapters record that still need a retry, in reading order. A
    /// recorded chapter drops out when it no longer has text, or when EVERY one of its units now holds a reliable
    /// Active result in this language created at or after the failed attempt started (a later per-chapter run or a
    /// retry covered it). Measured from the attempt's START, so a scene that succeeded inside the failed attempt
    /// already counts as covered and only the unit that failed needs a newer result.
    /// </summary>
    internal List<ChapterProofreadState> FailedStates(Guid bookId, string language, IReadOnlyList<ChapterProofreadState> states)
    {
        var record = _registry.FailedChapters(bookId, language);
        if (record.Count == 0) return new List<ChapterProofreadState>();
        return states
            .Where(s => s.Units.Count > 0
                && record.TryGetValue(s.ChapterId, out var failedAt)
                && !(s.ReliableThrough is DateTimeOffset covered && covered >= failedAt))
            .ToList();
    }

    /// <summary>The chapter states and the book's Proofread tier: everything <see cref="Plan"/> reads, in one scope.</summary>
    private async Task<(List<ChapterProofreadState> States, AiTier Tier)> LoadPlanningInputsAsync(
        Guid bookId, string language, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var states = await LoadChapterStatesAsync(db, bookId, language, ct);
        var tier = await BookAiTierResolver.ResolveAsync(db, bookId, AiTaskType.Proofread, _logger, ct);
        return (states, tier);
    }

    /// <summary>
    /// Only cancels the run's token source; it writes NOTHING to the parent job (be-c04). The parent's status has
    /// one writer, the run: it registers the "Stopping" note on its own token (<see cref="RunAsync"/>) and closes
    /// the registry's cancel intake before deciding its terminal status, so a cancel that arrives as the run
    /// finishes is refused here (false: the controller then answers canceled=false with the job's status) instead
    /// of overwriting a terminal status with Running.
    /// </summary>
    public bool Cancel(Guid bookId, Guid jobId) => _registry.TryCancel(bookId, jobId);

    /// <summary>The parent's message while a canceled run finishes the chapter in flight.</summary>
    internal const string StoppingMessage = "Stopping after the current chapter";

    // ── The run ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Works through <paramref name="planned"/> in reading order, at most MaxParallelBookProofreadChapters at a
    /// time (default 1: the 8GB card is single-slot). Workers TAKE the next chapter only while
    /// <paramref name="cancelToken"/> is clear, which is what "stop before the next chapter" means; the chapter
    /// itself runs under <paramref name="stoppingToken"/> only. <paramref name="runProgress"/> owns the parent's
    /// word-weighted percent and ETA; the chapter counts stay on the tracker's chunk fields.
    /// </summary>
    internal async Task RunAsync(
        Guid parentId, Guid bookId, string language, IReadOnlyList<ChapterProofreadState> planned,
        BookProofreadRunProgress runProgress, CancellationToken cancelToken, CancellationToken stoppingToken)
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
                // Wall clock, not _clock: it is compared with AnalysisResult.CreatedAt, which is stamped with it.
                var attemptStartedAt = DateTimeOffset.UtcNow;
                try
                {
                    ok = await RunChapterAsync(bookId, language, index, chapter, runProgress, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return; // application shutdown: the child is already marked Canceled
                }

                // c20: the failed-chapters record follows every attempt, so a retry that succeeds clears it.
                _registry.RecordChapterOutcome(bookId, language, chapter.ChapterId, ok, attemptStartedAt);
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

        // be-c04: the run is the ONLY writer of the parent's status. A cancel reaches it through the token, and the
        // note runs synchronously inside the canceller's Cancel() (shutdown is not an author's stop: no note).
        var stoppingNote = cancelToken.Register(() =>
        {
            if (!stoppingToken.IsCancellationRequested)
                _progress.SetStatus(parentId, AnalysisProgressStatus.Running, StoppingMessage);
        });
        try
        {
            await Task.WhenAll(Enumerable.Range(0, maxParallel).Select(_ => Worker()));
        }
        finally
        {
            // Close the intake (waits out a cancel being delivered under the registry's lock), then drop the note
            // (Dispose waits for a callback running on another thread), BEFORE any terminal status is written, so
            // no "Stopping" can land after it. Also on the throw path: the caller writes Failed after this.
            _registry.CloseCancelIntake(bookId, language, parentId);
            stoppingNote.Dispose();
        }

        AnalysisProgressStatus status;
        string message;
        if (processed < total)
        {
            var why = stoppingToken.IsCancellationRequested ? "the server is shutting down" : "it was canceled";
            _logger.LogInformation(
                "Whole-book proofread {JobId} for book {BookId} stopped because {Why}: {Done} of {Total} chapters proofread",
                parentId, bookId, why, proofread, total);
            status = AnalysisProgressStatus.Canceled;
            message = $"Whole-book proofread stopped: {proofread} of {total} chapters proofread.";
        }
        else if (failed == 0)
        {
            status = AnalysisProgressStatus.Succeeded;
            message = $"Whole-book proofread finished: {proofread} chapters proofread.";
        }
        else if (proofread == 0)
        {
            status = AnalysisProgressStatus.Failed;
            message = $"Whole-book proofread failed: none of the {total} chapters could be proofread.";
        }
        else
        {
            // Partial: Succeeded with CompletedChunks < TotalChunks (see the class remarks).
            status = AnalysisProgressStatus.Succeeded;
            message = $"Whole-book proofread finished partially: {proofread} of {total} chapters proofread, {failed} failed.";
        }

        // The estimate first, so the poll that sees the terminal status also sees 100 % / 0 s.
        runProgress.Finish(status);
        _progress.SetStatus(parentId, status, message);
    }

    /// <summary>
    /// Proofreads one chapter: its scenes, one Scene-scope run each, when the scenes hold the chapter's current
    /// text (<see cref="ChapterTextSource.ScenesHoldTheChaptersCurrentText"/>, the export's own rule, so the
    /// annotated export finds the results on the units it renders); otherwise the chapter row. True when every
    /// unit produced a result that is not flagged unreliable.
    /// </summary>
    private async Task<bool> RunChapterAsync(
        Guid bookId, string language, int chapterIndex, ChapterProofreadState chapter,
        BookProofreadRunProgress runProgress, CancellationToken stoppingToken)
    {
        var parentId = runProgress.ParentId;
        var allOk = true;
        for (var unitIndex = 0; unitIndex < chapter.Units.Count; unitIndex++)
        {
            var unit = chapter.Units[unitIndex];
            var childId = Guid.NewGuid();
            var unitFailed = false;
            var unitUnreliable = false;
            var scope = unit.SceneId.HasValue ? AnalysisScope.Scene : AnalysisScope.Chapter;
            _progress.StartJob(childId, scope, AnalysisType.Proofread, bookId, chapter.ChapterId, unit.SceneId,
                "Queued Proofread job (whole-book run)");
            runProgress.UnitStarted(chapterIndex, unitIndex, childId);

            using var heartbeatStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var heartbeat = HeartbeatAsync(runProgress, heartbeatStop.Token);
            try
            {
                // TEST-ONLY seam (c20): inert unless armed in a Development host; see BookProofreadFaultInjection.
                if (_faults.TryConsume(chapter.ChapterId))
                    throw new InvalidOperationException(
                        $"Injected test fault ({BookProofreadFaultInjection.ConfigKey}) for chapter {chapter.ChapterId}");

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
                    unitUnreliable = true;
                    _logger.LogWarning(
                        "Whole-book proofread {JobId}: chapter {ChapterId} scene {SceneId} produced an UNRELIABLE result {ResultId}; counted as not proofread",
                        parentId, chapter.ChapterId, unit.SceneId, result.Id);
                    allOk = false;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _progress.SetStatus(childId, AnalysisProgressStatus.Canceled, "Proofread canceled due to application shutdown.");
                unitFailed = true;
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Whole-book proofread {JobId}: chapter {ChapterId} scene {SceneId} failed; continuing with the next",
                    parentId, chapter.ChapterId, unit.SceneId);
                _progress.SetStatus(childId, AnalysisProgressStatus.Failed, ex.Message);
                allOk = false;
                unitFailed = true;
            }
            finally
            {
                heartbeatStop.Cancel();
                try { await heartbeat; } catch (OperationCanceledException) { }
                // Any outcome: the unit's words are processed (the percent counts them). A unit that produced no
                // result, OR an unreliable one, is no evidence of the pace, so its words leave the ETA's rate
                // (be-c07, final-r02). Since be-c01 a chunked chapter whose chunks all failed fast (an Ollama 500,
                // a dead provider, a local timeout: one attempt each) does not throw: it persists an unreliable
                // result in ~0 s for all its words, which counted as pace collapsed the ETA toward 0 again. The
                // flag cannot say WHICH chunks fell back (that is only in the run log), so every unreliable unit
                // leaves the rate, a slow-but-real one (unrelated output, dropped content) included: its time stays
                // in the clock, so the cost is an ETA that errs long, never one that collapses.
                runProgress.UnitFinished(chapterIndex, unitIndex, failed: unitFailed || unitUnreliable);
            }
        }
        return allOk;
    }

    /// <summary>The tick: republish the parent's estimate (which also touches it) until the unit ends.</summary>
    private static async Task HeartbeatAsync(BookProofreadRunProgress runProgress, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(ParentHeartbeatInterval);
        while (await timer.WaitForNextTickAsync(ct))
            runProgress.Refresh();
    }

    // ── Planning ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One proofreadable unit: the chapter row (SceneId null) or one scene.</summary>
    internal sealed record ProofreadUnit(Guid? SceneId, DateTimeOffset UpdatedAt);

    /// <summary>
    /// A chapter with text, the units a run would proofread, and what their current results say. Every flag is
    /// read off each unit's CURRENT result in the planning language through the shared
    /// <see cref="ProofreadUnitResults"/> rule the annotated export also calls (be-c05):
    /// <paramref name="IsCovered"/>: every unit's current result is reliable (the status's coverage).
    /// <paramref name="IsFresh"/>: covered, and each of those results is at least as new as its unit's last edit.
    /// <see cref="IsEdited"/>: covered but not fresh (the status's ChaptersEdited).
    /// <paramref name="HasUnreliableResult"/>: some unit's current result is flagged unreliable (UnreliableChapters,
    /// ChaptersUnreliable; exactly the chapters <c>?unreliable=skip</c> skips). Disjoint from covered.
    /// <paramref name="NewestReliableAt"/>: the newest of those results when covered (LastRunAt), else null.
    /// <paramref name="ReliableThrough"/> (c20): the OLDEST of them when covered, i.e. every unit has a reliable
    /// result at least this new; null when any unit has none.
    /// </summary>
    internal sealed record ChapterProofreadState(
        Guid ChapterId,
        int Order,
        int WordCount,
        IReadOnlyList<ProofreadUnit> Units,
        bool IsCovered,
        bool IsFresh,
        DateTimeOffset? NewestReliableAt,
        string Title = "",
        bool HasUnreliableResult = false,
        DateTimeOffset? ReliableThrough = null)
    {
        /// <summary>Every unit has a reliable result, and at least one of them predates its unit's last edit.</summary>
        public bool IsEdited => IsCovered && !IsFresh;
    }

    /// <summary>What a run with this <c>force</c> would do: the chapters it proofreads, the fresh ones it skips, its ETA.</summary>
    internal sealed record ProofreadPlan(
        IReadOnlyList<ChapterProofreadState> Planned, int SkippedFresh, int EstimatedChunks, int EstimatedSeconds);

    /// <summary>
    /// THE planning rule, shared by <see cref="StartAsync"/> (which runs it) and <see cref="GetStatusAsync"/> (which
    /// shows it on the consent before the author starts, both with and without force): candidates are chapters
    /// with at least one unit; planned are the candidates that are not fresh, or all of them when forced; the
    /// estimate is <see cref="Estimate"/> over the planned ones. One method, so the ETA the author agrees to is
    /// the ETA the run starts with. <paramref name="only"/> (c20, the retry of the failed chapters) narrows the
    /// candidates to those chapter ids before the rule applies; the retry passes force=true with it.
    /// </summary>
    internal static ProofreadPlan Plan(
        IReadOnlyList<ChapterProofreadState> states, bool force, AiOptions opts, string language, AiTier tier,
        IReadOnlySet<Guid>? only = null)
    {
        var candidates = states.Where(s => s.Units.Count > 0 && (only == null || only.Contains(s.ChapterId))).ToList();
        var planned = candidates.Where(s => force || !s.IsFresh).ToList();
        var (chunks, seconds) = Estimate(planned, opts, language, tier);
        return new ProofreadPlan(planned, candidates.Count - planned.Count, chunks, seconds);
    }

    /// <summary>The scene layer WITHOUT its bodies: ids, order and the two timestamps the unit rule reads.</summary>
    internal static IQueryable<SceneStampRow> SceneStampsQuery(AppDbContext db, IReadOnlyCollection<Guid> chapterIds)
        => db.Scenes.AsNoTracking()
            .Where(s => chapterIds.Contains(s.ChapterId))
            .OrderBy(s => s.Order)
            .Select(s => new SceneStampRow(s.Id, s.ChapterId, s.Order, s.CreatedAt, s.UpdatedAt));

    /// <summary>The SFDT of the given chapters' scenes (never <c>ContentText</c>), for the renderable check.</summary>
    internal static IQueryable<SceneBodyRow> SceneBodiesQuery(AppDbContext db, IReadOnlyCollection<Guid> chapterIds)
        => db.Scenes.AsNoTracking()
            .Where(s => chapterIds.Contains(s.ChapterId))
            .Select(s => new SceneBodyRow(s.Id, s.ContentSfdt));

    internal sealed record SceneStampRow(Guid Id, Guid ChapterId, int Order, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
    internal sealed record SceneBodyRow(Guid Id, string? ContentSfdt);

    /// <summary>
    /// Every chapter with text (<see cref="ChapterTextPredicate.HasText"/>) in reading order, with its units and
    /// freshness. A unit is FRESH when its current Active Proofread result (<see cref="ProofreadUnitResults"/>:
    /// Chapter scope keyed by chapter with SceneId null, or Scene scope keyed by scene, newest in this language)
    /// was created at or after the unit's last edit and is NOT flagged
    /// <see cref="AnalysisResult.ProofreadResultUnreliable"/>: an unreliable run is a failed run the author should
    /// not be told is done, so a non-force run replaces it. A chapter is fresh when all its units are.
    /// </summary>
    internal static async Task<List<ChapterProofreadState>> LoadChapterStatesAsync(
        AppDbContext db, Guid bookId, string language, CancellationToken ct)
    {
        var chapters = await db.Chapters.AsNoTracking()
            .Where(c => c.BookId == bookId)
            .Where(ChapterTextPredicate.HasText)
            .OrderBy(c => c.Order)
            .Select(c => new { c.Id, c.Order, c.WordCount, c.UpdatedAt, c.Title })
            .ToListAsync(ct);
        if (chapters.Count == 0) return new List<ChapterProofreadState>();

        var chapterIds = chapters.Select(c => c.Id).ToList();
        // Bodies stay in the database for the layer as a whole: this load runs on every status and start call.
        var scenes = await SceneStampsQuery(db, chapterIds).ToListAsync(ct);

        // The renderable-unit rule parses the SFDT (BookExportService.HasRenderableContent), which SQL cannot
        // do, so the body is read ONLY for the scenes of chapters whose scene layer holds the current text.
        var writtenChapterIds = scenes.GroupBy(s => s.ChapterId)
            .Where(g => ChapterTextSource.StampsHoldTheChaptersCurrentText(
                g.Select(s => new ChapterTextSource.SceneStamp(s.CreatedAt, s.UpdatedAt)).ToList()))
            .Select(g => g.Key)
            .ToList();
        var renderableSceneIds = new HashSet<Guid>();
        if (writtenChapterIds.Count > 0)
        {
            var bodies = await SceneBodiesQuery(db, writtenChapterIds).ToListAsync(ct);
            foreach (var b in bodies)
                if (BookExportService.HasRenderableContent(b.ContentSfdt)) renderableSceneIds.Add(b.Id);
        }

        var results = await db.AnalysisResults.AsNoTracking()
            .Where(a => a.ChapterId != null && chapterIds.Contains(a.ChapterId.Value)
                && a.AnalysisType == AnalysisType.Proofread
                && a.Status == AnalysisStatus.Active
                && (a.Scope == AnalysisScope.Chapter || a.Scope == AnalysisScope.Scene))
            .OrderBy(a => a.Id)
            .Select(a => new ProofreadUnitResults.ResultFacts(
                a.ChapterId!.Value, a.SceneId, a.Scope, a.CreatedAt, a.Language, a.ProofreadResultUnreliable))
            .ToListAsync(ct);
        // be-c05: THE unit predicate the annotated export also calls, so the status and the file agree on which
        // result is a unit's (unit key + language) and whether it is unreliable.
        var current = ProofreadUnitResults.CurrentByUnit(results, language, r => r);

        var states = new List<ChapterProofreadState>(chapters.Count);
        foreach (var c in chapters)
        {
            var chapterScenes = scenes.Where(s => s.ChapterId == c.Id).ToList();
            List<ProofreadUnit> units;
            if (ChapterTextSource.StampsHoldTheChaptersCurrentText(
                    chapterScenes.Select(s => new ChapterTextSource.SceneStamp(s.CreatedAt, s.UpdatedAt)).ToList()))
            {
                units = chapterScenes
                    .Where(s => renderableSceneIds.Contains(s.Id))
                    .Select(s => new ProofreadUnit(s.Id, s.UpdatedAt))
                    .ToList();
            }
            else
            {
                units = new List<ProofreadUnit> { new(null, c.UpdatedAt) };
            }

            var unitCurrent = units
                .Select(u => current.TryGetValue(new ProofreadUnitResults.UnitKey(c.Id, u.SceneId), out var r)
                    ? r : (ProofreadUnitResults.ResultFacts?)null)
                .ToList();
            var verdicts = unitCurrent.Select(ProofreadUnitResults.VerdictOf).ToList();

            // Covered: every unit's current result is reliable. Fresh: covered AND each one is at least as new as
            // its unit's last edit. Unreliable: some unit's current result is flagged. A result keyed to no unit
            // (the frozen chapter row's on a scene-split chapter) is not in the file and counts for neither.
            var covered = units.Count > 0 && verdicts.All(v => v == ProofreadUnitResults.Verdict.Reliable);
            var isFresh = covered && units.Select((u, i) => unitCurrent[i]!.Value.CreatedAt >= u.UpdatedAt).All(f => f);
            var unreliable = verdicts.Any(v => v == ProofreadUnitResults.Verdict.Unreliable);

            states.Add(new ChapterProofreadState(
                c.Id, c.Order, c.WordCount, units,
                IsCovered: covered,
                IsFresh: isFresh,
                NewestReliableAt: covered ? unitCurrent.Max(r => r!.Value.CreatedAt) : null,
                Title: c.Title ?? "",
                HasUnreliableResult: unreliable,
                // c20: every unit has a reliable result at least this new.
                ReliableThrough: covered ? unitCurrent.Min(r => r!.Value.CreatedAt) : null));
        }
        return states;
    }

    /// <summary>
    /// The consent gate's ETA. Chunks per chapter = ceil(words / the target RunAsync actually chunks at), via
    /// the SAME accessor (<see cref="UnifiedAnalysisService.ProofreadChunkTargetWordsFor"/>) so the estimate
    /// cannot drift from the real sizing; a scene-split chapter is at least one chunk per scene, since each scene
    /// is its own run. Seconds = units x <see cref="AiOptions.ProofreadSecondsPerUnitOverhead"/> + chunks x
    /// <see cref="AiOptions.ProofreadSecondsPerChunkEstimate"/> (be-c07): every unit is its own
    /// UnifiedAnalysisService.RunAsync call and pays its time outside the chunk stage once, which a per-chunk
    /// constant alone spread over the chunks and so under-promised a book of many short chapters 4-5x. Both
    /// constants and their derivation are on <see cref="AiOptions"/>.
    /// </summary>
    internal static (int Chunks, int Seconds) Estimate(
        IReadOnlyList<ChapterProofreadState> planned, AiOptions opts, string language, AiTier tier)
    {
        var target = Math.Max(1, UnifiedAnalysisService.ProofreadChunkTargetWordsFor(opts, language, tier));
        var chunks = planned.Sum(c => Math.Max(
            c.Units.Count,
            (int)Math.Ceiling(Math.Max(0, c.WordCount) / (double)target)));
        var units = planned.Sum(c => c.Units.Count);
        var seconds = (long)units * Math.Max(0, opts.ProofreadSecondsPerUnitOverhead)
                      + (long)chunks * Math.Max(0, opts.ProofreadSecondsPerChunkEstimate);
        return (chunks, (int)Math.Min(int.MaxValue, seconds));
    }
}
