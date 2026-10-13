using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;
using static Pagedraft.Api.Tests.BookProofreadServiceTests;

namespace Pagedraft.Api.Tests;

/// <summary>
/// be-c11: the run's PARALLEL worker path (<c>MaxParallelBookProofreadChapters</c> = 2), which no other test reaches
/// (every other harness runs the shipped default of 1, one worker, so the Interlocked counters and a cancel with two
/// chapters in flight were never exercised). Two chapters are held in the model at once on gates
/// (TaskCompletionSource) the test releases in a chosen order; no sleeps. Each wait is bounded so a regression to a
/// single worker fails with a timeout instead of hanging.
/// </summary>
public class BookProofreadParallelRunTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static Action<IServiceCollection> TwoWide =>
        s => s.Configure<AiOptions>(o => o.MaxParallelBookProofreadChapters = 2);

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The four chapters, by the marker their text carries (no marker is a substring of another or of the template).</summary>
    private static readonly string[] Markers = { "ראשון", "שני", "שלישי", "רביעי" };

    /// <summary>Counts the units inside the model at once; records each chapter's child job id as it enters.</summary>
    private sealed class InFlight
    {
        private int _now;
        private int _max;
        public readonly ConcurrentDictionary<Guid, Guid> ChildOf = new();
        public int Max => Volatile.Read(ref _max);

        public void Enter(Harness h, Guid chapterId)
        {
            var now = Interlocked.Increment(ref _now);
            int seen;
            while (now > (seen = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, now, seen) != seen) { }
            var child = h.Tracker.GetActiveJobsByBook(h.BookId).Single(j => j.ChapterId == chapterId);
            ChildOf[chapterId] = child.JobId;
        }

        public void Leave() => Interlocked.Decrement(ref _now);
    }

    [Fact]
    public async Task TwoWide_HoldsTwoChaptersInFlight_CountsEachOutcomeOnce_AndListsTheFailuresInReadingOrder()
    {
        using var h = new Harness(configure: TwoWide);
        var ids = Markers.Select((m, i) => h.AddChapter(i, m)).ToArray();
        var flight = new InFlight();
        var firstIn = Gate();
        var secondIn = Gate();
        var fourthIn = Gate();
        var releaseFirst = Gate();
        var releaseSecond = Gate();
        h.ModelGate = async input =>
        {
            var index = Array.FindIndex(Markers, input.Contains);
            flight.Enter(h, ids[index]);
            try
            {
                switch (index)
                {
                    case 0:
                        firstIn.TrySetResult();
                        await releaseFirst.Task;
                        throw new InvalidOperationException("model down (first)");
                    case 1:
                        secondIn.TrySetResult();
                        await releaseSecond.Task;
                        return;
                    case 2:
                        throw new InvalidOperationException("model down (third)");
                    default:
                        fourthIn.TrySetResult();
                        return;
                }
            }
            finally { flight.Leave(); }
        };
        var svc = h.Service();

        var response = await svc.StartAsync(h.BookId, "he", force: false, CancellationToken.None);

        // Both workers are inside the model at once: chapter 1 entered while chapter 0 is still held.
        await Task.WhenAll(firstIn.Task, secondIn.Task).WaitAsync(Bound);
        Assert.Equal(2, h.Tracker.GetActiveJobsByBook(h.BookId).Count(j => j.Scope == AnalysisScope.Chapter));
        // The second worker finishes chapter 1, takes chapter 2 (fails) and chapter 3, while chapter 0 is still held:
        // so the failures are RECORDED in the order 2, then 0.
        releaseSecond.TrySetResult();
        await fourthIn.Task.WaitAsync(Bound);
        releaseFirst.TrySetResult();
        await svc.RunTask!.WaitAsync(Bound);

        Assert.Equal(2, flight.Max);
        Assert.Equal(4, h.RouterInputs.Count); // each chapter taken exactly once across the two workers

        var parent = h.Snapshot(response.JobId);
        Assert.Equal(AnalysisProgressStatus.Succeeded, parent.Status);
        Assert.Equal(4, parent.TotalChunks);
        Assert.Equal(2, parent.CompletedChunks);
        Assert.Equal("Whole-book proofread finished partially: 2 of 4 chapters proofread, 2 failed.", parent.Message);
        Assert.Equal(100, parent.EstimatedCompletionPercent);
        Assert.Equal(0, parent.EstimatedRemainingSeconds);

        // Every finished chapter's result persisted, and each child job ended with its own outcome.
        var results = h.ActiveProofreads();
        Assert.Equal(new[] { ids[1], ids[3] }.OrderBy(x => x), results.Select(r => r.ChapterId!.Value).OrderBy(x => x));
        Assert.All(results, r => Assert.Equal(flight.ChildOf[r.ChapterId!.Value], r.JobId));
        Assert.Equal(AnalysisProgressStatus.Failed, h.Snapshot(flight.ChildOf[ids[0]]).Status);
        Assert.Equal(AnalysisProgressStatus.Succeeded, h.Snapshot(flight.ChildOf[ids[1]]).Status);
        Assert.Equal(AnalysisProgressStatus.Failed, h.Snapshot(flight.ChildOf[ids[2]]).Status);
        Assert.Equal(AnalysisProgressStatus.Succeeded, h.Snapshot(flight.ChildOf[ids[3]]).Status);

        // The failed list is in READING order, not the order the workers recorded the failures.
        var status = await h.Service().GetStatusAsync(h.BookId, "he", CancellationToken.None);
        Assert.Equal(new[] { (ids[0], 0), (ids[2], 2) }, status.FailedChapters.Select(c => (c.ChapterId, c.Order)));
        Assert.Equal(2, status.ChaptersWithActiveProofread);
        Assert.Null(h.Registry.TryGetActive(h.BookId, "he"));
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public async Task TwoWide_ACancelWithTwoChaptersInFlight_FinishesBoth_TakesNoMore_AndCountsEachOnce(
        bool secondFails, int expectedProofread)
    {
        using var h = new Harness(configure: TwoWide);
        var ids = Markers.Select((m, i) => h.AddChapter(i, m)).ToArray();
        var flight = new InFlight();
        var firstIn = Gate();
        var secondIn = Gate();
        var releaseFirst = Gate();
        var releaseSecond = Gate();
        h.ModelGate = async input =>
        {
            var index = Array.FindIndex(Markers, input.Contains);
            flight.Enter(h, ids[index]);
            try
            {
                if (index == 0)
                {
                    firstIn.TrySetResult();
                    await releaseFirst.Task;
                }
                else if (index == 1)
                {
                    secondIn.TrySetResult();
                    await releaseSecond.Task;
                    if (secondFails) throw new InvalidOperationException("model down (second)");
                }
            }
            finally { flight.Leave(); }
        };
        var svc = h.Service();

        var response = await svc.StartAsync(h.BookId, "he", force: false, CancellationToken.None);
        await Task.WhenAll(firstIn.Task, secondIn.Task).WaitAsync(Bound);

        Assert.True(svc.Cancel(h.BookId, response.JobId));
        var stopping = h.Snapshot(response.JobId);
        Assert.Equal(AnalysisProgressStatus.Running, stopping.Status);
        Assert.Equal(BookProofreadService.StoppingMessage, stopping.Message);

        // Release in REVERSE reading order: the worker that finishes first must not take chapter 2.
        releaseSecond.TrySetResult();
        releaseFirst.TrySetResult();
        await svc.RunTask!.WaitAsync(Bound);

        Assert.Equal(2, flight.Max);
        Assert.Equal(2, h.RouterInputs.Count); // chapters 2 and 3 were never taken
        Assert.DoesNotContain(h.RouterInputs, i => i.Contains(Markers[2]) || i.Contains(Markers[3]));

        var parent = h.Snapshot(response.JobId);
        Assert.Equal(AnalysisProgressStatus.Canceled, parent.Status);
        Assert.Equal(4, parent.TotalChunks);
        Assert.Equal(expectedProofread, parent.CompletedChunks);
        Assert.Equal($"Whole-book proofread stopped: {expectedProofread} of 4 chapters proofread.", parent.Message);

        // Both in-flight chapters kept their own outcome: a result for each success, a Failed child for the failure.
        var expectedResults = secondFails ? new[] { ids[0] } : new[] { ids[0], ids[1] };
        Assert.Equal(expectedResults.OrderBy(x => x), h.ActiveProofreads().Select(r => r.ChapterId!.Value).OrderBy(x => x));
        Assert.Equal(AnalysisProgressStatus.Succeeded, h.Snapshot(flight.ChildOf[ids[0]]).Status);
        Assert.Equal(secondFails ? AnalysisProgressStatus.Failed : AnalysisProgressStatus.Succeeded,
            h.Snapshot(flight.ChildOf[ids[1]]).Status);

        // Only an ATTEMPTED failure enters the retry record; the two chapters the cancel skipped are not "failed".
        var status = await h.Service().GetStatusAsync(h.BookId, "he", CancellationToken.None);
        Assert.Equal(secondFails ? new[] { ids[1] } : Array.Empty<Guid>(), status.FailedChapters.Select(c => c.ChapterId));
        Assert.Null(h.Registry.TryGetActive(h.BookId, "he"));
        Assert.False(svc.Cancel(h.BookId, response.JobId));
    }
}
