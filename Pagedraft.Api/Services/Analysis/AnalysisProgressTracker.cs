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

    /// <summary>
    /// HOW MANY chunks have finished — a COUNT, never an index. The FE spells this out verbatim
    /// ("3 of 10 completed") and divides by it to estimate the remaining time, so it must never run
    /// ahead of the work actually done. Kept in [0, <see cref="TotalChunks"/>].
    /// </summary>
    public int CompletedChunks { get; init; }

    /// <summary>
    /// The 1-based index of the chunk most recently started or finished — a POSITION, not a count.
    /// With parallel chunks this can (correctly) exceed <see cref="CompletedChunks"/>: chunk 3 may
    /// finish while chunk 2 is still running. Surfaced as <c>AnalysisProgressDto.currentChunk</c>.
    /// </summary>
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
    /// <summary>
    /// PER-ENTRY MONITOR. The tracker's <c>ConcurrentDictionary</c> makes the MAP safe; it does nothing for
    /// this object, which every parallel chunk task of a job mutates. Every read and every write of the
    /// fields below — including the TTL read — happens under this lock, so a read-modify-write (the
    /// completion count) cannot be lost and a snapshot cannot mix fields from two different updates.
    /// Never hold it while calling back into the tracker: no method takes two entry locks at once.
    /// </summary>
    public object SyncRoot { get; } = new();

    public Guid JobId { get; init; }
    public AnalysisScope Scope { get; init; }
    public AnalysisType AnalysisType { get; init; }
    public Guid? BookId { get; init; }
    public Guid? ChapterId { get; init; }
    public Guid? SceneId { get; init; }
    public int TotalChunks { get; set; }

    /// <summary>HOW MANY chunks finished (a COUNT). See <see cref="AnalysisProgressSnapshot.CompletedChunks"/>.</summary>
    public int CompletedChunks { get; set; }

    /// <summary>The 1-based index of the last chunk started/finished (a POSITION, not a count).</summary>
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
/// Intended for short-lived jobs (entries are pruned after a TTL).
///
/// THREAD-SAFETY — WHAT IS ACTUALLY GUARANTEED. The previous one-word claim ("Thread-safe") was false:
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> protects the MAP, not the mutable
/// <c>AnalysisProgressState</c> it hands out, and the chunk callbacks are invoked from the parallel chunk
/// tasks of one job. Every entry now carries its own monitor (<c>AnalysisProgressState.SyncRoot</c>) and
/// every field read and write goes through it. So:
///   • a completion is NEVER LOST — <see cref="ChunkCompleted"/>'s read-modify-write of the count is
///     atomic per entry (it used to be a check-then-act across threads);
///   • a snapshot is COHERENT — <see cref="TryGet"/> copies Total/Completed/Current/Status/Message under
///     the same lock, so it can never mix halves of two different updates.
/// NOT guaranteed, and deliberately so: nothing orders concurrent updates against each other (two chunks
/// finishing at once both count, but which one leaves its index in <c>CurrentChunkIndex</c> and its text in
/// <c>Message</c> is whichever ran last), and nothing is guaranteed ACROSS entries — a caller wanting a
/// consistent view of several jobs at one instant does not get it from <see cref="GetActiveJobsByBook"/>.
///
/// COUNT vs INDEX (be-c01 follow-up). <c>CompletedChunks</c> is HOW MANY finished; <c>CurrentChunkIndex</c>
/// is WHICH one last moved. It used to store "the highest index that has finished" in the count field,
/// which is only the same number when chunks run strictly in order — the chunked Proofread/LineEdit paths
/// run two at a time, so a later chunk finishing first inflated the count the FE spells out on screen.
/// Callers pass their 1-based index; the tracker owns the counting. See <see cref="ChunkCompleted"/>.
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
        lock (state.SyncRoot)
        {
            state.TotalChunks = totalChunks;
            state.Status = AnalysisProgressStatus.Running;
            state.Message = message ?? state.Message;
            state.LastUpdatedUtc = _timeProvider.GetUtcNow();
        }
        PruneExpired();
    }

    /// <summary>
    /// Report that the chunk at 1-based <paramref name="chunkIndex"/> has STARTED. Moves the position
    /// only; the completion count is untouched (see <see cref="ChunkCompleted"/>).
    /// </summary>
    public void ChunkStarted(Guid jobId, int chunkIndex, int totalChunks)
    {
        if (!_jobs.TryGetValue(jobId, out var state)) return;
        lock (state.SyncRoot)
        {
            state.TotalChunks = totalChunks;
            state.CurrentChunkIndex = chunkIndex;
            state.Status = AnalysisProgressStatus.Running;
            state.Message = $"Running chunk {chunkIndex}/{totalChunks}";
            state.LastUpdatedUtc = _timeProvider.GetUtcNow();
        }
        PruneExpired();
    }

    /// <summary>
    /// Report that ONE chunk has FINISHED. <paramref name="chunkIndex"/> is its 1-based POSITION (it lands
    /// in <c>CurrentChunkIndex</c> and the message); the completion COUNT is owned here and incremented by
    /// exactly one, under the entry lock.
    ///
    /// THE CONTRACT CALLERS MUST HONOUR: call this EXACTLY ONCE per reserved chunk, on every outcome
    /// (success, fallback, per-chunk failure) — the count no longer self-heals from a monotonic max, so a
    /// chunk that never reports leaves the job short of 100%. When a whole tail of reserved chunks is
    /// abandoned at once (an aborted reduce pass), say so with <see cref="MarkAllChunksCompleted"/> rather
    /// than firing this with the total.
    ///
    /// WHY NOT max(index): with chunks in flight two at a time, chunk 3 routinely finishes before chunk 2,
    /// and "the highest index that finished" then reads as 3 done when 2 are — a number the FE renders
    /// verbatim and divides by for its ETA.
    /// </summary>
    public void ChunkCompleted(Guid jobId, int chunkIndex, int totalChunks)
    {
        if (!_jobs.TryGetValue(jobId, out var state)) return;
        lock (state.SyncRoot)
        {
            state.TotalChunks = totalChunks;
            state.CurrentChunkIndex = chunkIndex;
            // Count, do not max-with-the-index. Clamped to the total so a double-report (or a caller whose
            // reservation shrank) can never push the readout past 100% — the FE clamps the same way.
            var completed = state.CompletedChunks + 1;
            state.CompletedChunks = totalChunks > 0 ? Math.Min(completed, totalChunks) : completed;
            state.Status = AnalysisProgressStatus.Running;
            state.Message = $"Completed chunk {chunkIndex}/{totalChunks}";
            state.LastUpdatedUtc = _timeProvider.GetUtcNow();
        }
        PruneExpired();
    }

    /// <summary>
    /// Account for EVERY reserved chunk at once: the DRAIN for a pass that gave up part-way and will never
    /// report its remaining chunks individually (BookReview's continuity reduce throwing mid-pass). Without
    /// it those chunks stay unreported and the job stalls short of 100% forever.
    ///
    /// This is the ONLY caller-facing way to move the count by more than one, and it is deliberately a
    /// separate method: the old code expressed it as <c>ChunkCompleted(total, total)</c>, which worked only
    /// because the count was a monotonic max — the very conflation that made a parallel run over-report.
    /// Monotonic (never lowers the count) and idempotent.
    /// </summary>
    public void MarkAllChunksCompleted(Guid jobId, int totalChunks)
    {
        if (!_jobs.TryGetValue(jobId, out var state)) return;
        lock (state.SyncRoot)
        {
            state.TotalChunks = totalChunks;
            state.CurrentChunkIndex = Math.Max(state.CurrentChunkIndex, totalChunks);
            if (state.CompletedChunks < totalChunks)
                state.CompletedChunks = totalChunks;
            state.Status = AnalysisProgressStatus.Running;
            state.Message = $"Completed chunk {totalChunks}/{totalChunks}";
            state.LastUpdatedUtc = _timeProvider.GetUtcNow();
        }
        PruneExpired();
    }

    /// <summary>
    /// Move a tracked job to a new status. Virtual only so a test can OBSERVE the transition (see the
    /// class remarks); the behaviour here is the whole contract.
    /// </summary>
    public virtual void SetStatus(Guid jobId, AnalysisProgressStatus status, string? message = null)
    {
        if (!_jobs.TryGetValue(jobId, out var state)) return;
        lock (state.SyncRoot)
        {
            state.Status = status;
            if (!string.IsNullOrWhiteSpace(message))
                state.Message = message!;
            state.LastUpdatedUtc = _timeProvider.GetUtcNow();
        }
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
        lock (state.SyncRoot)
        {
            state.BookReviewWindowCount = windowCount;
            state.BookReviewRanContinuityReduce = ranContinuityReduce;
            state.BookReviewFailedWindows = failedWindows;
            state.LastUpdatedUtc = _timeProvider.GetUtcNow();
        }
        PruneExpired();
    }

    /// <summary>
    /// Snapshot one job. The copy is taken under the entry lock, so the returned values are all from the
    /// SAME update — Completed/Total/Current can never be spliced across two concurrent chunk reports.
    /// </summary>
    public bool TryGet(Guid jobId, out AnalysisProgressSnapshot? snapshot)
    {
        snapshot = null;
        if (!_jobs.TryGetValue(jobId, out var state))
            return false;

        lock (state.SyncRoot)
        {
            if (_timeProvider.GetUtcNow() - state.LastUpdatedUtc <= _ttl)
            {
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
            }
        }

        // Expired: drop it OUTSIDE the entry lock (never hold one lock while taking another).
        if (snapshot == null)
        {
            _jobs.TryRemove(jobId, out _);
            return false;
        }
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
            // terminal — skip before the full TryGet path. Read under the entry lock (Status is written
            // by SetStatus from a background job task) and released before TryGet takes it again.
            bool skip;
            lock (state.SyncRoot)
            {
                skip = state.BookId != bookId
                    || state.Scope == AnalysisScope.Book
                    || IsTerminalStatus(state.Status);
            }
            if (skip) continue;

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
            // LastUpdatedUtc is written under the entry lock, so read it the same way — a torn read of the
            // timestamp is how a live job gets pruned out from under its own poller.
            bool expired;
            lock (kvp.Value.SyncRoot)
            {
                expired = now - kvp.Value.LastUpdatedUtc > _ttl;
            }
            if (expired)
                _jobs.TryRemove(kvp.Key, out _);
        }
    }
}

