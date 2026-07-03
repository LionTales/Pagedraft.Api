using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Tests for the AnalysisProgressTracker.GetActiveJobsByBook helper and the
/// GET api/books/{bookId}/active-analysis-jobs endpoint (rf-b01).
/// All tests are model-free (no live GPU / Ollama required).
/// </summary>
public class ActiveAnalysisJobsTests
{
    // ─── AnalysisProgressTracker.GetActiveJobsByBook unit tests ──────────────────────────────────

    [Fact]
    public void GetActiveJobsByBook_ActiveProofreadJob_ReturnsIt()
    {
        var tracker = new AnalysisProgressTracker();
        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        tracker.StartJob(jobId, AnalysisScope.Chapter, AnalysisType.Proofread, bookId, chapterId, null);

        var results = tracker.GetActiveJobsByBook(bookId);

        Assert.Single(results);
        Assert.Equal(jobId, results[0].JobId);
        Assert.Equal(AnalysisType.Proofread, results[0].AnalysisType);
        Assert.Equal(bookId, results[0].BookId);
        Assert.Equal(chapterId, results[0].ChapterId);
        Assert.Equal(AnalysisProgressStatus.Running, results[0].Status);
    }

    [Fact]
    public void GetActiveJobsByBook_ActiveLineEditJob_ReturnsIt()
    {
        var tracker = new AnalysisProgressTracker();
        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        tracker.StartJob(jobId, AnalysisScope.Chapter, AnalysisType.LineEdit, bookId, chapterId, null);

        var results = tracker.GetActiveJobsByBook(bookId);

        Assert.Single(results);
        Assert.Equal(jobId, results[0].JobId);
        Assert.Equal(AnalysisType.LineEdit, results[0].AnalysisType);
    }

    [Fact]
    public void GetActiveJobsByBook_BothProofreadAndLineEditActive_ReturnsBoth()
    {
        var tracker = new AnalysisProgressTracker();
        var bookId = Guid.NewGuid();
        var jobId1 = Guid.NewGuid();
        var jobId2 = Guid.NewGuid();

        tracker.StartJob(jobId1, AnalysisScope.Chapter, AnalysisType.Proofread, bookId, Guid.NewGuid(), null);
        tracker.StartJob(jobId2, AnalysisScope.Chapter, AnalysisType.LineEdit, bookId, Guid.NewGuid(), null);

        var results = tracker.GetActiveJobsByBook(bookId);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.JobId == jobId1);
        Assert.Contains(results, r => r.JobId == jobId2);
    }

    [Fact]
    public void GetActiveJobsByBook_ExcludesTerminalSucceededJob()
    {
        var tracker = new AnalysisProgressTracker();
        var bookId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        tracker.StartJob(jobId, AnalysisScope.Chapter, AnalysisType.Proofread, bookId, Guid.NewGuid(), null);
        tracker.SetStatus(jobId, AnalysisProgressStatus.Succeeded);

        var results = tracker.GetActiveJobsByBook(bookId);

        Assert.Empty(results);
    }

    [Fact]
    public void GetActiveJobsByBook_ExcludesTerminalFailedJob()
    {
        var tracker = new AnalysisProgressTracker();
        var bookId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        tracker.StartJob(jobId, AnalysisScope.Chapter, AnalysisType.Proofread, bookId, Guid.NewGuid(), null);
        tracker.SetStatus(jobId, AnalysisProgressStatus.Failed, "Something went wrong");

        var results = tracker.GetActiveJobsByBook(bookId);

        Assert.Empty(results);
    }

    [Fact]
    public void GetActiveJobsByBook_ExcludesTerminalCanceledJob()
    {
        var tracker = new AnalysisProgressTracker();
        var bookId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        tracker.StartJob(jobId, AnalysisScope.Chapter, AnalysisType.Proofread, bookId, Guid.NewGuid(), null);
        tracker.SetStatus(jobId, AnalysisProgressStatus.Canceled, "Canceled");

        var results = tracker.GetActiveJobsByBook(bookId);

        Assert.Empty(results);
    }

    [Fact]
    public void GetActiveJobsByBook_ExcludesOtherBooksJobs()
    {
        var tracker = new AnalysisProgressTracker();
        var bookId = Guid.NewGuid();
        var otherBookId = Guid.NewGuid();

        tracker.StartJob(Guid.NewGuid(), AnalysisScope.Chapter, AnalysisType.Proofread, otherBookId, Guid.NewGuid(), null);
        tracker.StartJob(Guid.NewGuid(), AnalysisScope.Chapter, AnalysisType.LineEdit, otherBookId, Guid.NewGuid(), null);

        var results = tracker.GetActiveJobsByBook(bookId);

        Assert.Empty(results);
    }

    [Fact]
    public void GetActiveJobsByBook_OnlyThisBooks_NotOtherBooks_Jobs()
    {
        var tracker = new AnalysisProgressTracker();
        var bookId = Guid.NewGuid();
        var otherBookId = Guid.NewGuid();
        var myJobId = Guid.NewGuid();

        tracker.StartJob(myJobId, AnalysisScope.Chapter, AnalysisType.Proofread, bookId, Guid.NewGuid(), null);
        tracker.StartJob(Guid.NewGuid(), AnalysisScope.Chapter, AnalysisType.Proofread, otherBookId, Guid.NewGuid(), null);

        var results = tracker.GetActiveJobsByBook(bookId);

        Assert.Single(results);
        Assert.Equal(myJobId, results[0].JobId);
    }

    [Fact]
    public void GetActiveJobsByBook_EmptyTracker_ReturnsEmptyList()
    {
        var tracker = new AnalysisProgressTracker();
        var bookId = Guid.NewGuid();

        var results = tracker.GetActiveJobsByBook(bookId);

        Assert.Empty(results);
    }

    [Fact]
    public void GetActiveJobsByBook_SceneScopedJob_IsIncluded()
    {
        var tracker = new AnalysisProgressTracker();
        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var sceneId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        tracker.StartJob(jobId, AnalysisScope.Scene, AnalysisType.Proofread, bookId, chapterId, sceneId);

        var results = tracker.GetActiveJobsByBook(bookId);

        Assert.Single(results);
        Assert.Equal(sceneId, results[0].SceneId);
        Assert.Equal(AnalysisScope.Scene, results[0].Scope);
    }

    // ─── Book-level builds are EXCLUDED (chapter/scene reattach endpoint only) ─────────────────────

    [Fact]
    public void GetActiveJobsByBook_ExcludesBookLevelBuilds()
    {
        var tracker = new AnalysisProgressTracker();
        var bookId = Guid.NewGuid();

        // Book-level builds (style baseline / summary / review) are surfaced by their own status
        // endpoints' activeBuildJobId, NOT the chapter/scene reattach endpoint. Even while in flight
        // they must never appear here.
        tracker.StartJob(Guid.NewGuid(), AnalysisScope.Book, AnalysisType.LinguisticAnalysis, bookId, null, null);
        tracker.StartJob(Guid.NewGuid(), AnalysisScope.Book, AnalysisType.Summarization, bookId, null, null);
        tracker.StartJob(Guid.NewGuid(), AnalysisScope.Book, AnalysisType.BookReview, bookId, null, null);

        var results = tracker.GetActiveJobsByBook(bookId);

        Assert.Empty(results);
    }

    [Fact]
    public void GetActiveJobsByBook_ReturnsChapterJobs_ButNotBookLevelBuilds()
    {
        var tracker = new AnalysisProgressTracker();
        var bookId = Guid.NewGuid();
        var chapterJobId = Guid.NewGuid();

        tracker.StartJob(chapterJobId, AnalysisScope.Chapter, AnalysisType.Proofread, bookId, Guid.NewGuid(), null);
        // A book-level review build for the SAME book must not be co-mingled with the chapter job.
        tracker.StartJob(Guid.NewGuid(), AnalysisScope.Book, AnalysisType.BookReview, bookId, null, null);

        var results = tracker.GetActiveJobsByBook(bookId);

        Assert.Single(results);
        Assert.Equal(chapterJobId, results[0].JobId);
        Assert.All(results, r => Assert.NotEqual(AnalysisScope.Book, r.Scope));
    }

    // ─── A job that finishes DURING the snapshot must not leak as active (TOCTOU) ───────────────────

    /// <summary>
    /// Bug 2 race: a job passes the live-status pre-filter as Running, then transitions to terminal
    /// DURING TryGet's snapshot build (which maps current state without its own terminal check). The
    /// post-snapshot terminal re-check must exclude it, so a succeeded/failed/canceled job never leaks
    /// into the active list. Driven deterministically via a one-shot side-effect clock (no threads):
    /// the effect fires on the FIRST GetUtcNow() inside GetActiveJobsByBook — TryGet's TTL read — which
    /// is AFTER the pre-filter (which does not read the clock) and BEFORE the snapshot is materialized.
    /// </summary>
    [Fact]
    public void GetActiveJobsByBook_JobFinishesDuringSnapshot_IsExcluded()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new OneShotSideEffectTimeProvider(now);
        var tracker = new AnalysisProgressTracker(clock);
        var bookId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        tracker.StartJob(jobId, AnalysisScope.Chapter, AnalysisType.Proofread, bookId, Guid.NewGuid(), null);

        // Arm the race: the job finishes (Succeeded) the moment TryGet reads the clock, i.e. after the
        // pre-filter already accepted it as active but before its snapshot status is captured.
        clock.ArmOnce(() => tracker.SetStatus(jobId, AnalysisProgressStatus.Succeeded));

        var results = tracker.GetActiveJobsByBook(bookId);

        Assert.Empty(results);
    }

    // ─── TTL / clock-seam expiry tests (be-c01) ───────────────────────────────────────────────────

    /// <summary>
    /// A non-terminal (Running) job whose LastUpdatedUtc is older than the 30-min TTL is EXCLUDED from
    /// GetActiveJobsByBook — the expiry pruning fires even though the status is not terminal. Driven via
    /// the injected clock (no 30-min real wait). Also asserts a FRESH non-terminal job IS still returned.
    /// </summary>
    [Fact]
    public void GetActiveJobsByBook_StaleNonTerminalJob_IsExcluded_FreshOneIncluded()
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(start);
        var tracker = new AnalysisProgressTracker(clock);
        var bookId = Guid.NewGuid();
        var staleJobId = Guid.NewGuid();

        // Start a Running job at T0…
        tracker.StartJob(staleJobId, AnalysisScope.Chapter, AnalysisType.Proofread, bookId, Guid.NewGuid(), null);

        // …advance the clock PAST the 30-min TTL. The stale job is still Running (non-terminal).
        clock.Advance(TimeSpan.FromMinutes(31));

        // …and start a FRESH Running job at the advanced time.
        var freshJobId = Guid.NewGuid();
        tracker.StartJob(freshJobId, AnalysisScope.Chapter, AnalysisType.LineEdit, bookId, Guid.NewGuid(), null);

        var results = tracker.GetActiveJobsByBook(bookId);

        // Only the fresh job survives; the stale non-terminal job is pruned by the TTL check.
        Assert.Single(results);
        Assert.Equal(freshJobId, results[0].JobId);
        Assert.DoesNotContain(results, r => r.JobId == staleJobId);
    }

    /// <summary>
    /// A non-terminal (Running) job that is still WITHIN the TTL window is included — confirms the
    /// exclusion above is caused by the TTL crossing, not by some unrelated filter.
    /// </summary>
    [Fact]
    public void GetActiveJobsByBook_NonTerminalJobWithinTtl_IsIncluded()
    {
        var start = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(start);
        var tracker = new AnalysisProgressTracker(clock);
        var bookId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        tracker.StartJob(jobId, AnalysisScope.Chapter, AnalysisType.Proofread, bookId, Guid.NewGuid(), null);

        // Advance, but stay JUST inside the 30-min TTL.
        clock.Advance(TimeSpan.FromMinutes(29));

        var results = tracker.GetActiveJobsByBook(bookId);

        Assert.Single(results);
        Assert.Equal(jobId, results[0].JobId);
    }

    // ─── Controller endpoint integration test ─────────────────────────────────────────────────────

    /// <summary>
    /// Verifies the GET .../active-analysis-jobs controller action returns the correct
    /// AnalysisJobSummaryDto list shape with camelCase-serializable records. Uses a real
    /// AnalysisProgressTracker (singleton) and a minimal BooksController wired with in-memory DB.
    /// </summary>
    [Fact]
    public void GetActiveAnalysisJobs_ActiveJobs_ReturnsSummaryDtos()
    {
        var tracker = new AnalysisProgressTracker();
        var bookId = Guid.NewGuid();
        var chapterId1 = Guid.NewGuid();
        var chapterId2 = Guid.NewGuid();
        var jobId1 = Guid.NewGuid();
        var jobId2 = Guid.NewGuid();

        tracker.StartJob(jobId1, AnalysisScope.Chapter, AnalysisType.Proofread, bookId, chapterId1, null);
        tracker.StartJob(jobId2, AnalysisScope.Chapter, AnalysisType.LineEdit, bookId, chapterId2, null);

        var controller = BuildMinimalBooksController(tracker);

        var action = controller.GetActiveAnalysisJobs(bookId);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dtos = Assert.IsType<List<AnalysisJobSummaryDto>>(ok.Value);

        Assert.Equal(2, dtos.Count);
        Assert.Contains(dtos, d => d.JobId == jobId1 && d.AnalysisType == "Proofread" && d.ChapterId == chapterId1);
        Assert.Contains(dtos, d => d.JobId == jobId2 && d.AnalysisType == "LineEdit" && d.ChapterId == chapterId2);
        Assert.All(dtos, d =>
        {
            Assert.Equal("Running", d.Status);
            Assert.Equal("Chapter", d.Scope);
            Assert.False(string.IsNullOrEmpty(d.Message));
        });
    }

    [Fact]
    public void GetActiveAnalysisJobs_NoActiveJobs_ReturnsEmptyList()
    {
        var tracker = new AnalysisProgressTracker();
        var bookId = Guid.NewGuid();

        var controller = BuildMinimalBooksController(tracker);

        var action = controller.GetActiveAnalysisJobs(bookId);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dtos = Assert.IsType<List<AnalysisJobSummaryDto>>(ok.Value);

        Assert.Empty(dtos);
    }

    [Fact]
    public void GetActiveAnalysisJobs_TerminalJobsExcluded_ReturnsEmpty()
    {
        var tracker = new AnalysisProgressTracker();
        var bookId = Guid.NewGuid();
        var jobId = Guid.NewGuid();

        tracker.StartJob(jobId, AnalysisScope.Chapter, AnalysisType.Proofread, bookId, Guid.NewGuid(), null);
        tracker.SetStatus(jobId, AnalysisProgressStatus.Succeeded);

        var controller = BuildMinimalBooksController(tracker);

        var action = controller.GetActiveAnalysisJobs(bookId);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dtos = Assert.IsType<List<AnalysisJobSummaryDto>>(ok.Value);

        Assert.Empty(dtos);
    }

    [Fact]
    public void GetActiveAnalysisJobs_BookLevelBuildsExcluded()
    {
        var tracker = new AnalysisProgressTracker();
        var bookId = Guid.NewGuid();
        var chapterJobId = Guid.NewGuid();

        tracker.StartJob(chapterJobId, AnalysisScope.Chapter, AnalysisType.LineEdit, bookId, Guid.NewGuid(), null);
        // In-flight book-level builds for the same book are surfaced by their own status endpoints and
        // must NOT be returned by the chapter/scene reattach endpoint.
        tracker.StartJob(Guid.NewGuid(), AnalysisScope.Book, AnalysisType.Summarization, bookId, null, null);
        tracker.StartJob(Guid.NewGuid(), AnalysisScope.Book, AnalysisType.BookReview, bookId, null, null);

        var controller = BuildMinimalBooksController(tracker);

        var action = controller.GetActiveAnalysisJobs(bookId);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dtos = Assert.IsType<List<AnalysisJobSummaryDto>>(ok.Value);

        Assert.Single(dtos);
        Assert.Equal(chapterJobId, dtos[0].JobId);
        Assert.All(dtos, d => Assert.NotEqual("Book", d.Scope));
    }

    [Fact]
    public void GetActiveAnalysisJobs_OtherBooksJobsExcluded()
    {
        var tracker = new AnalysisProgressTracker();
        var bookId = Guid.NewGuid();
        var otherBookId = Guid.NewGuid();

        // Only add a job for the OTHER book
        tracker.StartJob(Guid.NewGuid(), AnalysisScope.Chapter, AnalysisType.Proofread, otherBookId, Guid.NewGuid(), null);

        var controller = BuildMinimalBooksController(tracker);

        var action = controller.GetActiveAnalysisJobs(bookId);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dtos = Assert.IsType<List<AnalysisJobSummaryDto>>(ok.Value);

        Assert.Empty(dtos);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static BooksController BuildMinimalBooksController(AnalysisProgressTracker tracker)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new AppDbContext(options);

        // Minimal mocks for services not exercised by this endpoint.
        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        var appLifetimeMock = new Mock<IHostApplicationLifetime>();
        appLifetimeMock.Setup(l => l.ApplicationStopping).Returns(CancellationToken.None);

        return new BooksController(
            db,
            bookIntelligence: null!,
            styleBaseline: null!,
            bookSummary: null!,
            bookReview: null!,
            chapterBrief: null!,
            progress: tracker,
            scopeFactory: scopeFactoryMock.Object,
            appLifetime: appLifetimeMock.Object,
            logger: NullLogger<BooksController>.Instance);
    }
}

/// <summary>
/// Minimal test <see cref="TimeProvider"/> whose "now" can be advanced by the test (be-c01). Avoids
/// adding the Microsoft.Extensions.TimeProvider.Testing NuGet package for a single controllable clock.
/// </summary>
internal sealed class MutableTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public MutableTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}

/// <summary>
/// A fixed-time <see cref="TimeProvider"/> that runs a ONE-SHOT side effect on its next
/// <see cref="GetUtcNow"/> call, then reverts to a plain clock. Used to deterministically simulate a
/// job transitioning to terminal DURING GetActiveJobsByBook's TryGet (the Bug 2 TOCTOU window) without
/// threads. Disarms BEFORE invoking the effect so the effect's own clock reads (SetStatus /
/// PruneExpired) do not re-trigger it.
/// </summary>
internal sealed class OneShotSideEffectTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _now;
    private Action? _onNext;

    public OneShotSideEffectTimeProvider(DateTimeOffset now) => _now = now;

    public void ArmOnce(Action onNext) => _onNext = onNext;

    public override DateTimeOffset GetUtcNow()
    {
        var effect = _onNext;
        _onNext = null; // disarm first so the effect's own GetUtcNow reads don't recurse
        effect?.Invoke();
        return _now;
    }
}
