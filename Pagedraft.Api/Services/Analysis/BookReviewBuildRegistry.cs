using System.Collections.Concurrent;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// Process-wide registry of IN-PROGRESS whole-book review builds, keyed by (BookId, Language).
///
/// Exact sibling of <see cref="BookSummaryBuildRegistry"/> (DEF-2): a review build started in one
/// tab/session must be visible as BUILDING after a page reload or in a second tab so the FE can reattach to
/// the running job instead of starting a duplicate. The build runs on a background DI scope while the status
/// request that needs to see it runs on a SEPARATE request scope, so this MUST be a SINGLETON to share one
/// map.
///
/// Kept as a SEPARATE registry from the summary one (rather than a shared generic): the review build and
/// the summary build are independent jobs that can legitimately run concurrently for the same book, so they
/// need independent slots — folding them into one key space would make a running summary build block a
/// review build (and vice versa).
///
/// Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>; only one active review build per key.
/// <see cref="TryStart"/> is the atomic compare-and-set.
/// </summary>
public sealed class BookReviewBuildRegistry
{
    private readonly ConcurrentDictionary<(Guid BookId, string Language), Guid> _active = new();

    private static string Normalize(string language) =>
        string.IsNullOrWhiteSpace(language) ? "he" : language;

    /// <summary>
    /// Registers <paramref name="jobId"/> as the active review build for (bookId, language). Returns false
    /// when a build is already registered for the key (caller should not start a second one).
    /// </summary>
    public bool TryStart(Guid bookId, string language, Guid jobId) =>
        _active.TryAdd((bookId, Normalize(language)), jobId);

    /// <summary>Clears the active review build for (bookId, language). Idempotent (safe when none active).</summary>
    public void Complete(Guid bookId, string language) =>
        _active.TryRemove((bookId, Normalize(language)), out _);

    /// <summary>The active review build's jobId for (bookId, language), or null when none is in progress.</summary>
    public Guid? TryGetActive(Guid bookId, string language) =>
        _active.TryGetValue((bookId, Normalize(language)), out var jobId) ? jobId : null;
}
