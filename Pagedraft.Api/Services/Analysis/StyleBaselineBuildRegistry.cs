using System.Collections.Concurrent;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// Process-wide registry of IN-PROGRESS book-style-baseline builds, keyed by (BookId, Language).
///
/// PURPOSE (DEF-2): a build started in one tab/session must be visible as BUILDING after a page reload
/// or in a second tab, so the FE can reattach to the running job's progress instead of offering to start
/// a duplicate build. The build runs on a background DI scope (fire-and-forget in BooksController), while
/// the status request that needs to see it runs on a SEPARATE request scope. This registry MUST therefore
/// be a SINGLETON so both scopes share the same map (a scoped registry would be empty in the status scope).
///
/// Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>. Only one active build per key is
/// allowed (the unique (BookId, Language) cache row); <see cref="TryStart"/> fails if one is already
/// registered for the key.
/// </summary>
public sealed class StyleBaselineBuildRegistry
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
