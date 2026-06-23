using System.Collections.Concurrent;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// Process-wide registry of IN-PROGRESS book-summary (L2 BookBrief) builds, keyed by (BookId, Language).
///
/// Exact sibling of <see cref="StyleBaselineBuildRegistry"/> (DEF-2): a build started in one tab/session
/// must be visible as BUILDING after a page reload or in a second tab so the FE can reattach to the running
/// job instead of starting a duplicate. The build runs on a background DI scope while the status request
/// that needs to see it runs on a SEPARATE request scope, so this MUST be a SINGLETON to share one map.
///
/// Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>; only one active build per key
/// (the unique (BookId, Language) cache row). <see cref="TryStart"/> is the atomic compare-and-set.
/// </summary>
public sealed class BookSummaryBuildRegistry
{
    private readonly ConcurrentDictionary<(Guid BookId, string Language), Guid> _active = new();

    private static string Normalize(string language) =>
        string.IsNullOrWhiteSpace(language) ? "he" : language;

    /// <summary>
    /// Registers <paramref name="jobId"/> as the active build for (bookId, language). Returns false when a
    /// build is already registered for the key (caller should not start a second one).
    /// </summary>
    public bool TryStart(Guid bookId, string language, Guid jobId) =>
        _active.TryAdd((bookId, Normalize(language)), jobId);

    /// <summary>Clears the active build for (bookId, language). Idempotent (safe to call when none active).</summary>
    public void Complete(Guid bookId, string language) =>
        _active.TryRemove((bookId, Normalize(language)), out _);

    /// <summary>The active build's jobId for (bookId, language), or null when no build is in progress.</summary>
    public Guid? TryGetActive(Guid bookId, string language) =>
        _active.TryGetValue((bookId, Normalize(language)), out var jobId) ? jobId : null;
}
