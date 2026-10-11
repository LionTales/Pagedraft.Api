using System.Collections.Concurrent;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// Process-wide registry of IN-PROGRESS whole-book proofread runs, keyed by (BookId, Language).
///
/// Copy of <see cref="BookSummaryBuildRegistry"/> plus a per-job <see cref="CancellationTokenSource"/> so the
/// cancel endpoint can stop a run between chapters. MUST be a SINGLETON: the run executes on a background DI
/// scope while status/cancel requests run on their own scopes. <see cref="TryStart"/> is the atomic
/// compare-and-set; only one active run per key.
/// </summary>
public sealed class BookProofreadBuildRegistry
{
    private sealed record Entry(Guid JobId, CancellationTokenSource Cts);

    private readonly ConcurrentDictionary<(Guid BookId, string Language), Entry> _active = new();

    private static string Normalize(string language) =>
        string.IsNullOrWhiteSpace(language) ? "he" : language;

    /// <summary>
    /// Registers <paramref name="jobId"/> as the active run for (bookId, language). Returns false when a run is
    /// already registered for the key. On success <paramref name="cts"/> is the source the run should observe
    /// (link it to ApplicationStopping); on failure it is null.
    /// </summary>
    public bool TryStart(Guid bookId, string language, Guid jobId, out CancellationTokenSource? cts)
    {
        var entry = new Entry(jobId, new CancellationTokenSource());
        if (_active.TryAdd((bookId, Normalize(language)), entry))
        {
            cts = entry.Cts;
            return true;
        }
        entry.Cts.Dispose();
        cts = null;
        return false;
    }

    /// <summary>Clears the active run (and disposes its token source). Idempotent.</summary>
    public void Complete(Guid bookId, string language)
    {
        if (_active.TryRemove((bookId, Normalize(language)), out var entry))
            entry.Cts.Dispose();
    }

    /// <summary>The active run's jobId for (bookId, language), or null when none is in progress.</summary>
    public Guid? TryGetActive(Guid bookId, string language) =>
        _active.TryGetValue((bookId, Normalize(language)), out var e) ? e.JobId : null;

    /// <summary>Requests cancellation of the active run for (bookId, language). False when none is active.</summary>
    public bool TryCancel(Guid bookId, string language)
    {
        if (!_active.TryGetValue((bookId, Normalize(language)), out var e)) return false;
        return Cancel(e);
    }

    /// <summary>Requests cancellation of the active run of <paramref name="bookId"/> with this jobId (any language).</summary>
    public bool TryCancel(Guid bookId, Guid jobId)
    {
        foreach (var kv in _active)
            if (kv.Key.BookId == bookId && kv.Value.JobId == jobId) return Cancel(kv.Value);
        return false;
    }

    private static bool Cancel(Entry e)
    {
        try { e.Cts.Cancel(); return true; }
        catch (ObjectDisposedException) { return false; } // run completed between lookup and cancel
    }
}
