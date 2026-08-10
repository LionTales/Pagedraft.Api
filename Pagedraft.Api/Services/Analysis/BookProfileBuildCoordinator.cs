using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// Runs ONE book-profile refresh (<see cref="BookIntelligenceService.RefreshProfileAsync"/>) for a book.
/// Exists as an interface purely so the endpoint's continuation + dedup policy can be tested without a
/// live model, a DI container or a GPU; the shipped implementation is
/// <see cref="ScopedBookProfileBuilder"/> and has no logic of its own.
/// </summary>
public interface IBookProfileBuilder
{
    Task RunAsync(Guid bookId, string language, CancellationToken ct);
}

/// <summary>
/// The shipped <see cref="IBookProfileBuilder"/>: resolves <see cref="BookIntelligenceService"/> on a
/// FRESH DI scope per build, so the build's <c>DbContext</c> is not the request's.
///
/// That is the whole point of this class. The build outlives the request that started it (be-c03), and a
/// request-scoped <c>DbContext</c> is disposed when its request completes - so a build still holding one
/// would fault the moment the caller's request ended, which is exactly the teardown this fix exists to
/// survive. Mirrors the background-scope pattern the other three whole-book builds already use
/// (<c>BooksController.BuildBookSummary</c> / <c>BuildStyleBaseline</c> / <c>BuildBookReview</c>).
/// </summary>
public sealed class ScopedBookProfileBuilder : IBookProfileBuilder
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ScopedBookProfileBuilder(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task RunAsync(Guid bookId, string language, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var intelligence = scope.ServiceProvider.GetRequiredService<BookIntelligenceService>();
        await intelligence.RefreshProfileAsync(bookId, language, ct);
    }
}

/// <summary>What a caller of <see cref="BookProfileBuildCoordinator.StartOrJoin"/> got back.</summary>
/// <param name="Completion">
/// The build's task. AWAIT IT WITH THE REQUEST TOKEN ONLY AS A WAIT BOUND
/// (<c>Completion.WaitAsync(requestToken)</c>): abandoning the wait must never cancel the build.
/// </param>
/// <param name="Joined">
/// True when this caller ATTACHED to a build that was already running, false when it started one. Only
/// informational for the caller (both get the same task); it exists so the endpoint can log a joined
/// call, which is the observable signal that the dedup guard did its job.
/// </param>
/// <param name="BuildLanguage">
/// The language the RUNNING build was started with - verbatim, as its caller sent it, since that is what
/// the build passes on to the model. For a joined caller it may differ from the one it asked for; see the
/// key rationale on <see cref="BookProfileBuildCoordinator"/>.
/// </param>
public readonly record struct BookProfileBuild(Task Completion, bool Joined, string BuildLanguage);

/// <summary>
/// Process-wide SINGLE-FLIGHT guard for the book-profile refresh, keyed by BookId.
///
/// WHY IT EXISTS (be-c03, review findings 6 + 24). <c>POST books/{id}/profile/refresh</c> is fired from
/// several transient client observations with no jobId and no reattach, so two tabs - or the import
/// handoff racing the dashboard's status row - can issue it at the same time. The build is not safe to
/// run twice concurrently:
///   - <c>ChunkSummary</c> is UNIQUE on (BookId, ChapterId) and <c>BookProfile</c> is UNIQUE on BookId,
///     and both are written read-then-add with no transaction, so on a first build the two passes
///     collide on the index and one loses a whole checkpoint window of already-paid summarization.
///   - Phase 2 has no freshness gate, so each run issues four whole-book model calls; two runs make it
///     eight concurrent calls on a single-GPU host.
/// The collisions cannot be reproduced in the test suite (the EF InMemory provider does not enforce
/// unique indexes), which is why this guard is tested at THIS seam instead.
///
/// KEYED BY BOOK, NOT BY (BOOK, LANGUAGE) - deliberately different from
/// <see cref="BookSummaryBuildRegistry"/> / <see cref="StyleBaselineBuildRegistry"/> /
/// <see cref="BookReviewBuildRegistry"/>, which key by both. Those three cache a row PER LANGUAGE. This
/// build does not: <c>BookProfile</c> is one row per book and <c>ChunkSummary</c> is one row per
/// (book, chapter), each carrying Language as a plain column. Two languages therefore contend for the
/// SAME rows, and a per-language key would permit exactly the collision above. A caller that asked for a
/// different language than the running build joins it anyway and receives that build's language; the
/// endpoint logs the mismatch. That is the single-row-per-book design showing through, not a new limit.
///
/// NOT A CACHE. The slot is cleared when the build finishes, so the next call after completion starts a
/// fresh build. This deduplicates CONCURRENT callers; it never serves a stale answer to a later one.
///
/// MUST BE A SINGLETON - the whole guard is one shared map across request scopes, same reason as the
/// three registries above.
/// </summary>
public sealed class BookProfileBuildCoordinator
{
    private readonly IBookProfileBuilder _builder;
    private readonly ILogger<BookProfileBuildCoordinator> _logger;

    /// <summary>
    /// The in-flight build per book. The value is a <see cref="Lazy{T}"/> with
    /// <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/> because
    /// <c>ConcurrentDictionary.GetOrAdd</c>'s factory overload may run its factory MORE THAN ONCE under
    /// contention - which for this map would mean starting the second build this class exists to prevent.
    /// Constructing the Lazy is free; only the winner's <c>.Value</c> ever starts work.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, Entry> _inFlight = new();

    private sealed class Entry
    {
        public Entry(string language, Lazy<Task> build)
        {
            Language = language;
            Build = build;
        }

        public string Language { get; }
        public Lazy<Task> Build { get; }
    }

    public BookProfileBuildCoordinator(
        IBookProfileBuilder builder,
        ILogger<BookProfileBuildCoordinator> logger)
    {
        _builder = builder;
        _logger = logger;
    }

    /// <summary>
    /// Start the profile build for <paramref name="bookId"/>, or return the one already running.
    ///
    /// The work is started with <see cref="Task.Run(Func{Task}, CancellationToken)"/> and
    /// <see cref="CancellationToken.None"/>, so it is not attached to the calling request's execution in
    /// any way; <paramref name="buildToken"/> is the token the BUILD itself honours and must be a
    /// server-lifetime token (<c>IHostApplicationLifetime.ApplicationStopping</c>), never a request token.
    /// A shutdown must still be able to stop it; a client teardown must not.
    /// </summary>
    public BookProfileBuild StartOrJoin(Guid bookId, string language, CancellationToken buildToken)
    {
        var mine = new Entry(
            language,
            new Lazy<Task>(
                () => Task.Run(() => _builder.RunAsync(bookId, language, buildToken), CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));

        var winner = _inFlight.GetOrAdd(bookId, mine);
        var joined = !ReferenceEquals(winner, mine);

        Task completion;
        try
        {
            completion = winner.Build.Value;
        }
        catch (Exception ex)
        {
            // The factory itself threw (it only calls Task.Run, so this is pathological - a starved thread
            // pool). Clear the slot so the failure is not pinned forever, and hand the caller a faulted task
            // so it fails the same way a faulted build does.
            Release(bookId, winner);
            return new BookProfileBuild(Task.FromException(ex), joined, winner.Language);
        }

        if (!joined)
        {
            // Only the caller that STARTED the build owns clearing the slot, and it does so from a
            // continuation rather than from the request: the request may be abandoned long before the build
            // ends, and the slot must be released when the WORK ends. Reading t.Exception also OBSERVES a
            // faulted build, so an abandoned request cannot leave an unobserved task exception behind.
            completion.ContinueWith(
                t =>
                {
                    Release(bookId, winner);
                    if (t.IsFaulted)
                        _logger.LogError(t.Exception, "Book profile build failed for book {BookId}", bookId);
                    else if (t.IsCanceled)
                        _logger.LogInformation("Book profile build for book {BookId} was canceled (server shutting down).", bookId);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return new BookProfileBuild(completion, joined, winner.Language);
    }

    /// <summary>Clears the slot ONLY if it still holds <paramref name="entry"/>, so a build that finished
    /// after a later one started can never remove the later one's registration.</summary>
    private void Release(Guid bookId, Entry entry) =>
        _inFlight.TryRemove(new KeyValuePair<Guid, Entry>(bookId, entry));
}
