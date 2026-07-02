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
/// </summary>
public sealed class AnalysisProgressTracker
{
    private readonly ConcurrentDictionary<Guid, AnalysisProgressState> _jobs = new();
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(30);

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
            LastUpdatedUtc = DateTimeOffset.UtcNow
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
        state.LastUpdatedUtc = DateTimeOffset.UtcNow;
        PruneExpired();
    }

    public void ChunkStarted(Guid jobId, int chunkIndex, int totalChunks)
    {
        if (!_jobs.TryGetValue(jobId, out var state)) return;
        state.TotalChunks = totalChunks;
        state.CurrentChunkIndex = chunkIndex;
        state.Status = AnalysisProgressStatus.Running;
        state.Message = $"Running chunk {chunkIndex}/{totalChunks}";
        state.LastUpdatedUtc = DateTimeOffset.UtcNow;
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
        state.LastUpdatedUtc = DateTimeOffset.UtcNow;
        PruneExpired();
    }

    public void SetStatus(Guid jobId, AnalysisProgressStatus status, string? message = null)
    {
        if (!_jobs.TryGetValue(jobId, out var state)) return;
        state.Status = status;
        if (!string.IsNullOrWhiteSpace(message))
            state.Message = message!;
        state.LastUpdatedUtc = DateTimeOffset.UtcNow;
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
        state.LastUpdatedUtc = DateTimeOffset.UtcNow;
        PruneExpired();
    }

    public bool TryGet(Guid jobId, out AnalysisProgressSnapshot? snapshot)
    {
        snapshot = null;
        if (!_jobs.TryGetValue(jobId, out var state))
            return false;

        if (DateTimeOffset.UtcNow - state.LastUpdatedUtc > _ttl)
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

    private void PruneExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kvp in _jobs)
        {
            if (now - kvp.Value.LastUpdatedUtc > _ttl)
            {
                _jobs.TryRemove(kvp.Key, out _);
            }
        }
    }
}

