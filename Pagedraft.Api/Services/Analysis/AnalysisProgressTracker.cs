using System.Collections.Concurrent;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Analysis;

public enum AnalysisProgressStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Canceled
}

public sealed class AnalysisProgressSnapshot
{
    public Guid JobId { get; init; }
    public AnalysisScope Scope { get; init; }
    public AnalysisType AnalysisType { get; init; }
    public Guid? BookId { get; init; }
    public Guid? ChapterId { get; init; }
    public Guid? SceneId { get; init; }
    public int TotalChunks { get; init; }
    public int CompletedChunks { get; init; }
    public int CurrentChunkIndex { get; init; }
    public AnalysisProgressStatus Status { get; init; }
    public string Message { get; init; } = string.Empty;
    public DateTimeOffset LastUpdatedUtc { get; init; }
    public int EstimatedCompletionPercent =>
        TotalChunks <= 0 ? 0 : (int)Math.Ceiling(100.0 * CompletedChunks / TotalChunks);

    // ── Whole-book REVIEW build-shape (wb4-c06). The TRANSIENT per-build window/continuity/failed-window
    //    provenance the BookReview build stamps at its terminal so the FE's LIVE progress poll can render the
    //    "N windows[, continuity pass]" detail + the "N windows failed" partial warning right after a build.
    //    This is deliberately the LIVE build-completion channel — the persisted status probe reports these as
    //    0/false (they are build-time-only), so the FE reads them from the terminal progress payload instead.
    //    Null for every other job type and until the review build sets them. ──
    public int? BookReviewWindowCount { get; init; }
    public bool? BookReviewRanContinuityReduce { get; init; }
    public int? BookReviewFailedWindows { get; init; }
}

internal sealed class AnalysisProgressState
{
    public Guid JobId { get; init; }
    public AnalysisScope Scope { get; init; }
    public AnalysisType AnalysisType { get; init; }
    public Guid? BookId { get; init; }
    public Guid? ChapterId { get; init; }
    public Guid? SceneId { get; init; }
    public int TotalChunks { get; set; }
    public int CompletedChunks { get; set; }
    public int CurrentChunkIndex { get; set; }
    public AnalysisProgressStatus Status { get; set; } = AnalysisProgressStatus.Pending;
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset LastUpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    // Whole-book REVIEW build-shape (see AnalysisProgressSnapshot). Null until the review build stamps them.
    public int? BookReviewWindowCount { get; set; }
    public bool? BookReviewRanContinuityReduce { get; set; }
    public int? BookReviewFailedWindows { get; set; }
}

/// <summary>
/// In-memory progress tracker for long-running analysis jobs (currently chunked Proofread).
/// Thread-safe and intended for short-lived jobs (entries are pruned after a TTL).
///
/// NOT sealed, and <see cref="SetStatus"/> is virtual, purely as an OBSERVATION seam (be-c01): the
/// persist-then-signal ordering that <c>UnifiedAnalysisService.PersistThenMarkJobSucceededAsync</c>
/// enforces is only testable if a test can run code at the instant a job is marked Succeeded and check
/// that the row is already queryable by another DbContext. Production has exactly one implementation
/// (the DI singleton registered in Program.cs); do not add a second.
/// </summary>
public class AnalysisProgressTracker
{
    private readonly ConcurrentDictionary<Guid, AnalysisProgressState> _jobs = new();
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(30);

    // Clock seam (be-c01): all TTL/LastUpdated reads go through this so a test can drive expiry without
    // waiting 30 real minutes. Defaults to the real wall-clock (TimeProvider.System) in production; the
    // DI singleton keeps working with the parameterless default. The 30-min TTL VALUE and the
    // expiry-exclusion SEMANTICS are unchanged — only the clock is injectable.
    private readonly TimeProvider _timeProvider;

    public AnalysisProgressTracker(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void StartJob(
        Guid jobId,
        AnalysisScope scope,
        AnalysisType analysisType,
        Guid? bookId,
        Guid? chapterId,
        Guid? sceneId,
        string? message = null)
    {
        var state = new AnalysisProgressState
        {
            JobId = jobId,
            Scope = scope,
            AnalysisType = analysisType,
            BookId = bookId,
            ChapterId = chapterId,
            SceneId = sceneId,
            Status = AnalysisProgressStatus.Running,
            Message = message ?? "Starting analysis…",
            LastUpdatedUtc = _timeProvider.GetUtcNow()
        };
        _jobs.AddOrUpdate(jobId, state, (_, _) => state);
        PruneExpired();
    }

    public void SetTotalChunks(Guid jobId, int totalChunks, string? message = null)
    {
        if (!_jobs.TryGetValue(jobId, out var state)) return;
        state.TotalChunks = totalChunks;
        state.Status = AnalysisProgressStatus.Running;
        state.Message = message ?? state.Message;
        state.LastUpdatedUtc = _timeProvider.GetUtcNow();
        PruneExpired();
    }

    public void ChunkStarted(Guid jobId, int chunkIndex, int totalChunks)
    {
        if (!_jobs.TryGetValue(jobId, out var state)) return;
        state.TotalChunks = totalChunks;
        state.CurrentChunkIndex = chunkIndex;
        state.Status = AnalysisProgressStatus.Running;
        state.Message = $"Running chunk {chunkIndex}/{totalChunks}";
        state.LastUpdatedUtc = _timeProvider.GetUtcNow();
        PruneExpired();
    }

    public void ChunkCompleted(Guid jobId, int chunkIndex, int totalChunks)
    {
        if (!_jobs.TryGetValue(jobId, out var state)) return;
        state.TotalChunks = totalChunks;
        state.CurrentChunkIndex = chunkIndex;
        if (state.CompletedChunks < chunkIndex)
            state.CompletedChunks = chunkIndex;
        state.Status = AnalysisProgressStatus.Running;
        state.Message = $"Completed chunk {chunkIndex}/{totalChunks}";
        state.LastUpdatedUtc = _timeProvider.GetUtcNow();
        PruneExpired();
    }

    /// <summary>
    /// Move a tracked job to a new status. Virtual only so a test can OBSERVE the transition (see the
    /// class remarks); the behaviour here is the whole contract.
    /// </summary>
    public virtual void SetStatus(Guid jobId, AnalysisProgressStatus status, string? message = null)
    {
        if (!_jobs.TryGetValue(jobId, out var state)) return;
        state.Status = status;
        if (!string.IsNullOrWhiteSpace(message))
            state.Message = message!;
        state.LastUpdatedUtc = _timeProvider.GetUtcNow();
        PruneExpired();
    }

    /// <summary>
    /// Stamp the whole-book REVIEW build-shape (window count, whether the continuity reduce pass ran, and the
    /// failed-window count) onto the job so the FE's LIVE progress poll can render the coverage-provenance detail
    /// right after a build. Called by <see cref="BookReviewService"/> at the build terminal BEFORE the terminal
    /// <see cref="SetStatus"/>, so the SAME terminal poll that observes Succeeded/Failed also carries the shape.
    /// No-op when the job is unknown. These values are TRANSIENT (in-memory, TTL-pruned) and review-specific;
    /// the persisted status probe deliberately does NOT carry them (it reports 0/false).
    /// </summary>
    public void SetBookReviewShape(Guid jobId, int windowCount, bool ranContinuityReduce, int failedWindows)
    {
        if (!_jobs.TryGetValue(jobId, out var state)) return;
        state.BookReviewWindowCount = windowCount;
        state.BookReviewRanContinuityReduce = ranContinuityReduce;
        state.BookReviewFailedWindows = failedWindows;
        state.LastUpdatedUtc = _timeProvider.GetUtcNow();
        PruneExpired();
    }

    public bool TryGet(Guid jobId, out AnalysisProgressSnapshot? snapshot)
    {
        snapshot = null;
        if (!_jobs.TryGetValue(jobId, out var state))
            return false;

        if (_timeProvider.GetUtcNow() - state.LastUpdatedUtc > _ttl)
        {
            _jobs.TryRemove(jobId, out _);
            return false;
        }

        snapshot = new AnalysisProgressSnapshot
        {
            JobId = state.JobId,
            Scope = state.Scope,
            AnalysisType = state.AnalysisType,
            BookId = state.BookId,
            ChapterId = state.ChapterId,
            SceneId = state.SceneId,
            TotalChunks = state.TotalChunks,
            CompletedChunks = state.CompletedChunks,
            CurrentChunkIndex = state.CurrentChunkIndex,
            Status = state.Status,
            Message = state.Message,
            LastUpdatedUtc = state.LastUpdatedUtc,
            BookReviewWindowCount = state.BookReviewWindowCount,
            BookReviewRanContinuityReduce = state.BookReviewRanContinuityReduce,
            BookReviewFailedWindows = state.BookReviewFailedWindows
        };
        return true;
    }

    /// <summary>
    /// Returns snapshots of the in-flight CHAPTER/SCENE analysis jobs (Proofread / LineEdit) for a book:
    /// non-terminal (Pending or Running), non-expired, and NOT book-level builds.  Reuses
    /// <see cref="TryGet"/> per key so the TTL check and snapshot mapping are NEVER duplicated.
    ///
    /// Book-level builds (<see cref="AnalysisScope.Book"/> — style baseline, summary, review) are
    /// deliberately EXCLUDED: they are surfaced by their own status endpoints' <c>activeBuildJobId</c>
    /// fields, and the chapter-reattach endpoint + its DTO document chapter/scene jobs only. Leaking a
    /// book-level jobId here would let the FE reattach to a build meant to be tracked elsewhere.
    ///
    /// Terminal status is re-checked on the SNAPSHOT, not just the pre-filter: a job can finish between
    /// the live pre-filter and the <see cref="TryGet"/> snapshot (which maps CURRENT state without a
    /// terminal check of its own), so without the second check a job that succeeds/fails/cancels in that
    /// window would leak into the "active" list.
    ///
    /// Semantics: survives a BROWSER refresh (the server keeps running) but NOT an API restart
    /// (in-memory, 30-min TTL) — identical to how the book-level build registries already behave.
    /// </summary>
    public IReadOnlyList<AnalysisProgressSnapshot> GetActiveJobsByBook(Guid bookId)
    {
        var result = new List<AnalysisProgressSnapshot>();
        foreach (var kvp in _jobs)
        {
            var state = kvp.Value;
            // Quick pre-filter: wrong book, a book-level build (surfaced elsewhere), or already
            // terminal — skip before the full TryGet path.
            if (state.BookId != bookId) continue;
            if (state.Scope == AnalysisScope.Book) continue;
            if (IsTerminalStatus(state.Status)) continue;

            // Delegate to TryGet so the TTL check + snapshot mapping are single-sourced. TryGet builds
            // the snapshot from CURRENT state without re-checking terminal status, so re-check the
            // SNAPSHOT here: a job that finished between the pre-filter above and this snapshot must not
            // be reported as active.
            if (TryGet(kvp.Key, out var snapshot) && snapshot != null
                && !IsTerminalStatus(snapshot.Status))
                result.Add(snapshot);
        }
        return result;
    }

    /// <summary>Returns true when <paramref name="status"/> is a terminal state (Succeeded, Failed, or Canceled).</summary>
    private static bool IsTerminalStatus(AnalysisProgressStatus status) =>
        status == AnalysisProgressStatus.Succeeded
        || status == AnalysisProgressStatus.Failed
        || status == AnalysisProgressStatus.Canceled;

    private void PruneExpired()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var kvp in _jobs)
        {
            if (now - kvp.Value.LastUpdatedUtc > _ttl)
            {
                _jobs.TryRemove(kvp.Key, out _);
            }
        }
    }
}

