using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// be-c03 (wave3-spine review findings 6 + 24): the book-profile refresh must SURVIVE the client that
/// started it, and must never run twice at once for one book.
///
/// Before this fix <c>BooksController.RefreshProfile</c> awaited
/// <c>BookIntelligenceService.RefreshProfileAsync</c> inline with the REQUEST CancellationToken, so an
/// ordinary teardown - closing the dashboard panel, entering focus mode, switching the assistant to Edit
/// help, reloading mid-build - did not merely stop observing a build that costs minutes of GPU time, it
/// CANCELED it, and the profile was silently never built while the row read ready.
///
/// WHY THESE TESTS DRIVE THE ENDPOINT THROUGH A FAKE BUILDER. The two collisions the dedup half prevents
/// - the unique-index violations on <c>ChunkSummary (BookId, ChapterId)</c> and <c>BookProfile (BookId)</c>
/// when two passes overlap - CANNOT be reproduced against the EF InMemory provider, which does not
/// enforce unique indexes. So the guard is asserted at the seam that implements it (the coordinator and
/// the endpoint's token handling), with a builder whose timing the TEST owns rather than a model. No live
/// GPU, no sleeps, no timing tolerances.
/// </summary>
public class BookProfileBuildContinuationTests
{
    /// <summary>How long a test waits for a signal that MUST arrive. Only a broken guard can exhaust it,
    /// and every use is paired with a second signal that fires when the guard is broken, so a regression
    /// fails on an assertion rather than on this timeout.</summary>
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(30);

    // ─── The endpoint: a client teardown must not cancel the build ───────────────────────────────

    [Fact]
    public async Task RefreshProfile_CallerAbortsMidBuild_DoesNotCancelTheBuildAndTheProfileStillCommits()
    {
        var fixture = new Fixture();
        var builder = fixture.Builder;
        var controller = fixture.NewController();

        using var request = new CancellationTokenSource();
        var call = controller.RefreshProfile(fixture.BookId, new RefreshProfileRequest("he"), request.Token);

        // The build is running and has not been allowed to finish yet.
        await builder.Entered.WaitAsync(SignalTimeout);

        // The client goes away: panel closed / focus mode / Edit help / reload. This aborts the REQUEST.
        request.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);

        // THE ASSERTION THAT NAMES THE DEFECT: the token the build runs under is not the request's, so the
        // caller's disappearance did not cancel it.
        Assert.False(builder.TokenSeenByBuild.IsCancellationRequested);

        // And the build really does run to completion and commit, with the caller long gone.
        builder.Release();
        await builder.Finished.WaitAsync(SignalTimeout);
        Assert.True(builder.RanToCompletion);

        await using var verify = fixture.NewDbContext();
        var committed = await verify.Set<BookProfile>().FirstOrDefaultAsync(p => p.BookId == fixture.BookId);
        Assert.NotNull(committed);
        Assert.Equal(Fixture.BuiltGenre, committed!.Genre);
    }

    [Fact]
    public async Task RefreshProfile_ReturnsTheProfileTheBuildCommittedOnItsOwnScope()
    {
        var fixture = new Fixture();
        fixture.Builder.Release();

        var result = await fixture.NewController()
            .RefreshProfile(fixture.BookId, new RefreshProfileRequest("he"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<BookProfileDto>(ok.Value);
        Assert.Equal(fixture.BookId, dto.BookId);
        // The build wrote the row through a DIFFERENT DbContext, as a real background scope does, so a
        // caller that returned the entity it was handed instead of re-reading would be projecting another
        // scope's tracked state.
        Assert.Equal(Fixture.BuiltGenre, dto.Genre);
    }

    /// <summary>
    /// Bugbot (PR #55): ct bounds the WAIT, but it used to bound the POST-BUILD READ too. If the client went
    /// away in the window between the build committing and that read returning, the read threw and the caller
    /// was told the refresh failed - while the profile sat committed in the database. The twin of
    /// <c>Wave3StageSignalContractTests.Update_CommitsTheRename_EvenWhenTheRequestIsCancelledDuringTheCountsReread</c>,
    /// which be-f03 fixed on <c>BooksController.Update</c> without this endpoint noticing.
    ///
    /// WHY THE TRIGGER IS A <c>Set&lt;BookProfile&gt;()</c> OVERRIDE AND NOT be-f03's SaveChangesInterceptor.
    /// Cancelling when the build's own SaveChangesAsync commits would land the cancellation while the endpoint
    /// is still inside <c>Completion.WaitAsync(ct)</c>, and a cancelled WAIT is CONTRACTUAL here (be-c03: the
    /// caller may stop waiting). That would test the wrong thing and fail even with the fix in place. The one
    /// seam that puts the abort strictly BETWEEN the wait and the read is the read's own first move - asking
    /// the request DbContext for the BookProfile set. No threads, no sleeps, no timing tolerance.
    /// </summary>
    [Fact]
    public async Task RefreshProfile_ReturnsTheCommittedProfile_WhenTheRequestIsCancelledAfterTheBuildCommitted()
    {
        using var request = new CancellationTokenSource();
        var fixture = new Fixture { CancelAtProfileRead = request };
        fixture.Builder.Release();

        var result = await fixture.NewController()
            .RefreshProfile(fixture.BookId, new RefreshProfileRequest("he"), request.Token);

        // Sanity: the window really was exercised - the read did begin with the request already aborted.
        Assert.True(request.IsCancellationRequested);

        // THE ASSERTION THAT NAMES THE DEFECT: the response still carries the profile the build committed,
        // instead of the read throwing and turning finished, paid-for work into a reported failure.
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<BookProfileDto>(ok.Value);
        Assert.Equal(fixture.BookId, dto.BookId);
        Assert.Equal(Fixture.BuiltGenre, dto.Genre);

        // And what it reported is what is really in the database, read on a context that was never cancelled.
        await using var verify = fixture.NewDbContext();
        var committed = await verify.Set<BookProfile>()
            .FirstOrDefaultAsync(p => p.BookId == fixture.BookId, CancellationToken.None);
        Assert.NotNull(committed);
        Assert.Equal(Fixture.BuiltGenre, committed!.Genre);
    }

    [Fact]
    public async Task RefreshProfile_UnknownBook_IsStillNotFoundAndStartsNoBuild()
    {
        var fixture = new Fixture();
        fixture.Builder.Release();

        var result = await fixture.NewController()
            .RefreshProfile(Guid.NewGuid(), new RefreshProfileRequest("he"), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Equal(0, fixture.Builder.Invocations);
    }

    [Fact]
    public async Task RefreshProfile_ServerShuttingDown_RefusesWithoutStartingABuild()
    {
        var fixture = new Fixture();
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        fixture.ShutdownToken = stopping.Token;
        fixture.Builder.Release();

        var result = await fixture.NewController()
            .RefreshProfile(fixture.BookId, new RefreshProfileRequest("he"), CancellationToken.None);

        var status = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(503, status.StatusCode);
        Assert.Equal(0, fixture.Builder.Invocations);
    }

    // ─── The endpoint: two callers, one build (finding 24) ───────────────────────────────────────

    [Fact]
    public async Task RefreshProfile_TwoConcurrentCallers_RunExactlyOneBuild()
    {
        var fixture = new Fixture();
        var builder = fixture.Builder;

        // Two controller instances over two DbContexts - two requests, as in production.
        var first = fixture.NewController().RefreshProfile(fixture.BookId, new RefreshProfileRequest("he"), CancellationToken.None);
        await builder.Entered.WaitAsync(SignalTimeout);
        var second = fixture.NewController().RefreshProfile(fixture.BookId, new RefreshProfileRequest("he"), CancellationToken.None);

        // Wait for the second caller to reach the coordinator, WITHOUT assuming which way it went: either
        // it joined (the endpoint logs that, and the log line exists precisely to record the dedup firing)
        // or it started a second build (the builder is entered a second time). Whichever happens, the
        // assertion below is what decides the test - the wait never decides it.
        await Task.WhenAny(fixture.JoinLogged, builder.EnteredAgain).WaitAsync(SignalTimeout);

        builder.Release();
        var firstResult = await first;
        var secondResult = await second;

        // THE ASSERTION: one build, not two concurrent whole-book model runs on a single-GPU host.
        Assert.Equal(1, builder.Invocations);

        foreach (var result in new[] { firstResult, secondResult })
        {
            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var dto = Assert.IsType<BookProfileDto>(ok.Value);
            Assert.Equal(Fixture.BuiltGenre, dto.Genre);
        }
    }

    [Fact]
    public async Task RefreshProfile_AfterABuildFinishes_TheNextCallStartsAFreshBuild()
    {
        var fixture = new Fixture();
        fixture.Builder.Release();

        await fixture.NewController().RefreshProfile(fixture.BookId, new RefreshProfileRequest("he"), CancellationToken.None);
        await fixture.NewController().RefreshProfile(fixture.BookId, new RefreshProfileRequest("he"), CancellationToken.None);

        // The coordinator deduplicates CONCURRENT callers; it is not a cache. A later caller must get a
        // real rebuild, never the previous build's answer.
        Assert.Equal(2, fixture.Builder.Invocations);
    }

    // ─── The coordinator itself ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Coordinator_JoinedCaller_GetsTheSameTaskAndTheRunningBuildsLanguage()
    {
        var builder = new GatedProfileBuilder();
        var coordinator = NewCoordinator(builder);
        var bookId = Guid.NewGuid();

        var owner = coordinator.StartOrJoin(bookId, "he", CancellationToken.None);
        await builder.Entered.WaitAsync(SignalTimeout);
        var joiner = coordinator.StartOrJoin(bookId, "en", CancellationToken.None);

        Assert.False(owner.Joined);
        Assert.True(joiner.Joined);
        Assert.Same(owner.Completion, joiner.Completion);
        // The key is the BOOK, so a caller asking for another language joins and is TOLD which language is
        // actually being built, instead of silently believing it got its own.
        Assert.Equal("he", joiner.BuildLanguage);
        Assert.Equal("he", builder.LanguageSeenByBuild);

        builder.Release();
        await owner.Completion.WaitAsync(SignalTimeout);
        Assert.Equal(1, builder.Invocations);
    }

    [Fact]
    public async Task Coordinator_FaultedBuild_FaultsEveryCallerAndClearsTheSlot()
    {
        var builder = new GatedProfileBuilder { Fault = new InvalidOperationException("book has no chapters") };
        var coordinator = NewCoordinator(builder);
        var bookId = Guid.NewGuid();

        var owner = coordinator.StartOrJoin(bookId, "he", CancellationToken.None);
        await builder.Entered.WaitAsync(SignalTimeout);
        var joiner = coordinator.StartOrJoin(bookId, "he", CancellationToken.None);
        builder.Release();

        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.Completion);
        await Assert.ThrowsAsync<InvalidOperationException>(() => joiner.Completion);

        // A failed build must not pin the slot forever - the next caller gets a real attempt.
        builder.Fault = null;
        var next = coordinator.StartOrJoin(bookId, "he", CancellationToken.None);
        await next.Completion.WaitAsync(SignalTimeout);

        Assert.False(next.Joined);
        Assert.Equal(2, builder.Invocations);
    }

    [Fact]
    public async Task Coordinator_BuildsForDifferentBooks_DoNotBlockEachOther()
    {
        var builder = new GatedProfileBuilder();
        var coordinator = NewCoordinator(builder);

        var first = coordinator.StartOrJoin(Guid.NewGuid(), "he", CancellationToken.None);
        await builder.Entered.WaitAsync(SignalTimeout);
        var second = coordinator.StartOrJoin(Guid.NewGuid(), "he", CancellationToken.None);

        Assert.False(second.Joined);
        Assert.NotSame(first.Completion, second.Completion);

        builder.Release();
        await Task.WhenAll(first.Completion, second.Completion).WaitAsync(SignalTimeout);
        Assert.Equal(2, builder.Invocations);
    }

    private static BookProfileBuildCoordinator NewCoordinator(IBookProfileBuilder builder) =>
        new(builder, NullLogger<BookProfileBuildCoordinator>.Instance);

    // ─── Fixtures ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An <see cref="IBookProfileBuilder"/> whose timing the TEST owns. It records the token and language
    /// the build was actually given, blocks until released, and (unless faulted) writes the BookProfile row
    /// through its OWN DbContext, the way a real build on a background DI scope does.
    /// </summary>
    private sealed class GatedProfileBuilder : IBookProfileBuilder
    {
        private readonly DbContextOptions<AppDbContext>? _options;
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _enteredAgain = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _invocations;

        public GatedProfileBuilder(DbContextOptions<AppDbContext>? options = null) => _options = options;

        /// <summary>Completes once a build has STARTED, so a test can join it deterministically.</summary>
        public Task Entered => _entered.Task;

        /// <summary>Completes once a SECOND build has started - i.e. the dedup guard did not hold.</summary>
        public Task EnteredAgain => _enteredAgain.Task;

        /// <summary>Completes once the first build has finished, released or faulted.</summary>
        public Task Finished => _finished.Task;

        public int Invocations => Volatile.Read(ref _invocations);
        public CancellationToken TokenSeenByBuild { get; private set; }
        public string? LanguageSeenByBuild { get; private set; }
        public bool RanToCompletion { get; private set; }
        public Exception? Fault { get; set; }

        /// <summary>Opens the gate for this and every later build.</summary>
        public void Release() => _release.TrySetResult();

        public async Task RunAsync(Guid bookId, string language, CancellationToken ct)
        {
            var invocation = Interlocked.Increment(ref _invocations);
            TokenSeenByBuild = ct;
            LanguageSeenByBuild = language;
            if (invocation == 1) _entered.TrySetResult();
            else _enteredAgain.TrySetResult();

            try
            {
                await _release.Task;
                if (Fault != null) throw Fault;

                if (_options != null)
                {
                    await using var db = new AppDbContext(_options);
                    var profile = await db.Set<BookProfile>()
                        .FirstOrDefaultAsync(p => p.BookId == bookId, CancellationToken.None);
                    if (profile == null)
                    {
                        profile = new BookProfile { BookId = bookId };
                        db.Set<BookProfile>().Add(profile);
                    }
                    profile.Genre = Fixture.BuiltGenre;
                    profile.Language = language;
                    await db.SaveChangesAsync(CancellationToken.None);
                }

                RanToCompletion = true;
            }
            finally
            {
                _finished.TrySetResult();
            }
        }
    }

    /// <summary>
    /// One seeded book, one shared in-memory store, one coordinator - and a controller factory that hands
    /// out a FRESH DbContext per call, so every controller is its own request scope.
    /// </summary>
    private sealed class Fixture
    {
        public const string BuiltGenre = "genre-written-by-the-background-scope";

        private readonly DbContextOptions<AppDbContext> _options;
        private readonly BookProfileBuildCoordinator _coordinator;
        private readonly JoinRecordingLogger _logger = new();

        public Fixture()
        {
            _options = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            using (var seed = new AppDbContext(_options))
            {
                seed.Books.Add(new Book { Id = BookId, Title = "Seeded", Language = "he" });
                seed.SaveChanges();
            }

            Builder = new GatedProfileBuilder(_options);
            _coordinator = NewCoordinator(Builder);
        }

        public Guid BookId { get; } = Guid.NewGuid();
        public GatedProfileBuilder Builder { get; }
        public CancellationToken ShutdownToken { get; set; } = CancellationToken.None;

        /// <summary>
        /// When set, the CONTROLLER's DbContext (only - never the seed or verify ones) cancels this source
        /// the moment the endpoint reaches its post-build BookProfile read, so a test can place a client
        /// abort strictly between the wait and the read.
        /// </summary>
        public CancellationTokenSource? CancelAtProfileRead { get; init; }

        /// <summary>Completes when the endpoint logs that a caller joined an in-flight build.</summary>
        public Task JoinLogged => _logger.Joined;

        public AppDbContext NewDbContext() => new(_options);

        public BooksController NewController()
        {
            var appLifetime = new Mock<IHostApplicationLifetime>();
            appLifetime.Setup(l => l.ApplicationStopping).Returns(() => ShutdownToken);

            var db = CancelAtProfileRead == null
                ? NewDbContext()
                : new CancelOnProfileReadDbContext(_options, CancelAtProfileRead);

            return new BooksController(
                db,
                bookIntelligence: null!,
                styleBaseline: null!,
                bookSummary: null!,
                bookReview: null!,
                chapterBrief: null!,
                progress: null!,
                aiTierStatus: null!,
                profileBuilds: _coordinator,
                scopeFactory: new Mock<IServiceScopeFactory>().Object,
                appLifetime: appLifetime.Object,
                logger: _logger);
        }
    }

    /// <summary>
    /// An <see cref="AppDbContext"/> that cancels a caller-supplied token the instant the endpoint asks it
    /// for the <see cref="BookProfile"/> set - the first move of the POST-BUILD read, and therefore the only
    /// point that is provably after the build committed and before the read observes any token. Filtered on
    /// the entity type so nothing else the endpoint touches (the Books existence check) can trip it.
    /// </summary>
    private sealed class CancelOnProfileReadDbContext : AppDbContext
    {
        private readonly CancellationTokenSource _cts;

        public CancelOnProfileReadDbContext(DbContextOptions<AppDbContext> options, CancellationTokenSource cts)
            : base(options) => _cts = cts;

        public override DbSet<TEntity> Set<TEntity>()
        {
            if (typeof(TEntity) == typeof(BookProfile)) _cts.Cancel();
            return base.Set<TEntity>();
        }
    }

    /// <summary>
    /// Signals when <c>RefreshProfile</c> logs a joined build. Matched on the message text, which couples
    /// this fixture to that one log line on purpose: the line's whole reason to exist is to record that the
    /// dedup guard fired, so if it is reworded the coupling should be noticed, not silently lost.
    /// </summary>
    private sealed class JoinRecordingLogger : ILogger<BooksController>
    {
        private readonly TaskCompletionSource _joined = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Joined => _joined.Task;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains("joined the build already in flight", StringComparison.Ordinal))
                _joined.TrySetResult();
        }
    }
}
