using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Data;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;
using static Pagedraft.Api.Tests.BookProofreadServiceTests;

namespace Pagedraft.Api.Tests;

/// <summary>
/// be-c04: the parent job's status has ONE writer, the run. A cancel that lands as the run finishes is refused
/// (canceled=false + the job's status) and never turns a terminal parent back into Running; a cancel during a
/// chapter still stops before the next one; a throw while the start registers the run leaves nothing active.
/// Deterministic: the race is driven from inside the tracker call that writes the terminal status, no sleeps.
/// </summary>
public class BookProofreadCancelRaceTests
{
    /// <summary>
    /// Observes every status write (via the tracker's virtual <see cref="AnalysisProgressTracker.SetStatus"/>) and
    /// runs <see cref="OnBookTerminal"/> ONCE, right after the first Book-scope job is written terminal.
    /// </summary>
    private sealed class SpyTracker : AnalysisProgressTracker
    {
        public readonly ConcurrentQueue<(Guid JobId, AnalysisProgressStatus Status, string? Message)> Writes = new();
        public Action<Guid>? OnBookTerminal;
        private int _fired;

        public override void SetStatus(Guid jobId, AnalysisProgressStatus status, string? message = null)
        {
            base.SetStatus(jobId, status, message);
            Writes.Enqueue((jobId, status, message));
            if (status is AnalysisProgressStatus.Succeeded or AnalysisProgressStatus.Failed or AnalysisProgressStatus.Canceled
                && TryGet(jobId, out var snap) && snap is { Scope: AnalysisScope.Book }
                && Interlocked.Exchange(ref _fired, 1) == 0)
                OnBookTerminal?.Invoke(jobId);
        }
    }

    /// <summary>Throws on its FIRST read (the start's run-progress clock), the system time after.</summary>
    private sealed class ThrowOnceClock : TimeProvider
    {
        private int _reads;
        public override DateTimeOffset GetUtcNow() =>
            Interlocked.Increment(ref _reads) == 1
                ? throw new InvalidOperationException("injected start fault")
                : System.GetUtcNow();
    }

    private static BookProofreadController Controller(Harness h)
    {
        var sp = h.Provider.CreateScope().ServiceProvider;
        return new BookProofreadController(
            sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<BookProofreadService>(),
            h.Tracker, sp.GetRequiredService<IHostApplicationLifetime>());
    }

    private static string Body(IActionResult result) =>
        JsonSerializer.Serialize(Assert.IsType<OkObjectResult>(result).Value);

    /// <summary>
    /// The route's three answers, by BODY (be-c11): the active run accepts ({canceled:true}, its token cancelled), a
    /// finished run refuses with its status ({canceled:false,status}), an unknown job or another book's is 404.
    /// </summary>
    [Fact]
    public void Controller_CancelAnswersCanceledTrueForTheActiveRun_FalseWithTheStatusForAFinishedOne_404Otherwise()
    {
        using var h = new Harness();
        var active = Guid.NewGuid();
        Assert.True(h.Registry.TryStart(h.BookId, "he", active, out var cts));
        h.Tracker.StartJob(active, AnalysisScope.Book, AnalysisType.Proofread, h.BookId, null, null);
        var finished = Guid.NewGuid();
        h.Tracker.StartJob(finished, AnalysisScope.Book, AnalysisType.Proofread, h.BookId, null, null);
        h.Tracker.SetStatus(finished, AnalysisProgressStatus.Succeeded);
        var controller = Controller(h);

        Assert.Equal("{\"canceled\":true}", Body(controller.Cancel(h.BookId, active)));
        Assert.True(cts!.IsCancellationRequested);
        Assert.Equal("{\"canceled\":false,\"status\":\"Succeeded\"}", Body(controller.Cancel(h.BookId, finished)));
        Assert.IsType<NotFoundResult>(controller.Cancel(h.BookId, Guid.NewGuid()));
        Assert.IsType<NotFoundResult>(controller.Cancel(Guid.NewGuid(), finished));
    }

    [Fact]
    public async Task ACancelAsTheParentGoesTerminal_IsRefused_AndTheParentStaysTerminal()
    {
        var spy = new SpyTracker();
        using var h = new Harness(tracker: spy);
        h.AddChapter(0, "ראשון");
        h.AddChapter(1, "שני");
        var controller = Controller(h);
        IActionResult? cancelAnswer = null;
        // The author presses stop at the instant the run writes Succeeded, while the registry still holds the run
        // (its Complete runs later, in the background task's finally): the old gap.
        spy.OnBookTerminal = parentId => cancelAnswer = controller.Cancel(h.BookId, parentId);
        var svc = h.Service();

        var response = await StartAndFinish(h, svc);

        Assert.NotNull(cancelAnswer);
        var parent = h.Snapshot(response.JobId);
        Assert.Equal(AnalysisProgressStatus.Succeeded, parent.Status);
        Assert.Equal("Whole-book proofread finished: 2 chapters proofread.", parent.Message);
        Assert.Equal("{\"canceled\":false,\"status\":\"Succeeded\"}", Body(cancelAnswer!));
        Assert.DoesNotContain(spy.Writes, w => w.JobId == response.JobId && w.Message == BookProofreadService.StoppingMessage);
        Assert.Null(h.Registry.TryGetActive(h.BookId, "he"));
    }

    [Fact]
    public async Task ACancelDuringAChapter_WritesStoppingAtOnce_ThenStopsBeforeTheNextAndEndsCanceled()
    {
        var spy = new SpyTracker();
        using var h = new Harness(tracker: spy);
        h.AddChapter(0, "ראשון");
        h.AddChapter(1, "שני");
        h.AddChapter(2, "שלישי");
        BookProofreadService? svc = null;
        AnalysisProgressSnapshot? afterCancel = null;
        h.OnModelCall = input =>
        {
            if (!input.Contains("ראשון")) return;
            var parentId = h.Registry.TryGetActive(h.BookId, "he")!.Value;
            Assert.True(svc!.Cancel(h.BookId, parentId));
            // The run's own token registration wrote the note synchronously inside the cancel.
            afterCancel = h.Snapshot(parentId);
        };
        svc = h.Service();

        var response = await StartAndFinish(h, svc);

        Assert.NotNull(afterCancel);
        Assert.Equal(AnalysisProgressStatus.Running, afterCancel!.Status);
        Assert.Equal(BookProofreadService.StoppingMessage, afterCancel.Message);
        var parent = h.Snapshot(response.JobId);
        Assert.Equal(AnalysisProgressStatus.Canceled, parent.Status);
        Assert.Equal(1, parent.CompletedChunks);
        Assert.Single(h.RouterInputs);
        // The last write to the parent is the run's terminal one.
        Assert.Equal(AnalysisProgressStatus.Canceled, spy.Writes.Last(w => w.JobId == response.JobId).Status);
        Assert.Equal("{\"canceled\":false,\"status\":\"Canceled\"}", Body(Controller(h).Cancel(h.BookId, response.JobId)));
    }

    [Fact]
    public async Task AThrowWhileTheStartRegistersTheRun_LeavesNothingActive_AndASecondStartRuns()
    {
        var spy = new SpyTracker();
        using var h = new Harness(clock: new ThrowOnceClock(), tracker: spy);
        h.AddChapter(0, "ראשון");
        var svc = h.Service();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.StartAsync(h.BookId, "he", force: false, CancellationToken.None));
        Assert.Equal("injected start fault", ex.Message);

        Assert.Null(h.Registry.TryGetActive(h.BookId, "he"));
        Assert.Null((await svc.GetStatusAsync(h.BookId, "he", CancellationToken.None)).ActiveJobId);
        // The parent was already tracked when the throw came: it is not left Running.
        var orphan = Assert.Single(spy.Writes);
        Assert.Equal(AnalysisProgressStatus.Failed, orphan.Status);
        Assert.Equal(AnalysisScope.Book, h.Snapshot(orphan.JobId).Scope);

        var second = await StartAndFinish(h, h.Service());

        Assert.True(second.Started);
        Assert.NotEqual(orphan.JobId, second.JobId);
        Assert.Equal(AnalysisProgressStatus.Succeeded, h.Snapshot(second.JobId).Status);
        Assert.Single(h.ActiveProofreads());
    }
}
