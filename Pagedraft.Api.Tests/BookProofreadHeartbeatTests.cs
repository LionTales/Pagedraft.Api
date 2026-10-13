using System;
using System.Threading;
using System.Threading.Tasks;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;
using static Pagedraft.Api.Tests.BookProofreadServiceTests;

namespace Pagedraft.Api.Tests;

/// <summary>
/// <see cref="BookProofreadService.ParentHeartbeatInterval"/> is a process-wide static: this collection runs alone so
/// the shortened interval never reaches another test's run.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class BookProofreadHeartbeatCollection
{
    public const string Name = "BookProofreadHeartbeat (static interval)";
}

/// <summary>
/// be-c11: the run's heartbeat (<c>BookProofreadService.HeartbeatAsync</c>) is what keeps the whole-book parent's
/// LastUpdatedUtc moving while ONE long chapter runs, so the tracker's 30-minute expiry does not drop the parent from
/// under its poller. No test fired it before (the shipped interval is 10 s). Here the interval is 10 ms, a chapter is
/// held in the model on a gate, the TRACKER's clock is moved forward past the expiry in two steps, and the test waits
/// for heartbeat ticks (observed as the run-progress clock's reads, which only the tick makes while the unit is held)
/// and asserts the parent's LastUpdatedUtc followed the tracker's clock each time.
/// </summary>
[Collection(BookProofreadHeartbeatCollection.Name)]
public class BookProofreadHeartbeatTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 13, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The run's ETA clock (<see cref="BookProofreadRunProgress"/>): a fixed time that counts its reads. While a unit
    /// is held in the model, only the heartbeat's Refresh reads it, so N more reads = N more ticks.
    /// </summary>
    private sealed class TickCountingClock : TimeProvider
    {
        private readonly object _lock = new();
        private int _reads;
        private int _target = int.MaxValue;
        private TaskCompletionSource? _waiter;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_lock)
            {
                _reads++;
                if (_waiter != null && _reads >= _target) _waiter.TrySetResult();
            }
            return T0;
        }

        /// <summary>Completes once the clock has been read <paramref name="more"/> more times from now.</summary>
        public Task MoreReads(int more)
        {
            lock (_lock)
            {
                _waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _target = _reads + more;
                return _waiter.Task;
            }
        }
    }

    [Fact]
    public async Task WhileOneChapterRuns_TheHeartbeatKeepsTheParentsLastUpdateMoving_PastTheTrackerExpiry()
    {
        var saved = BookProofreadService.ParentHeartbeatInterval;
        BookProofreadService.ParentHeartbeatInterval = TimeSpan.FromMilliseconds(10);
        try
        {
            var trackerClock = new MutableTimeProvider(T0);
            var runClock = new TickCountingClock();
            using var h = new Harness(clock: runClock, tracker: new AnalysisProgressTracker(trackerClock));
            h.AddChapter(0, "ראשון");
            var inModel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.ModelGate = async _ =>
            {
                inModel.TrySetResult();
                await release.Task;
            };
            var svc = h.Service();

            var response = await svc.StartAsync(h.BookId, "he", force: false, CancellationToken.None);
            try
            {
                await inModel.Task.WaitAsync(Bound);
                Assert.Equal(T0, h.Snapshot(response.JobId).LastUpdatedUtc);

                // Two steps of 16 minutes: 32 minutes after the parent started, past the tracker's 30-minute expiry.
                foreach (var step in new[] { 1, 2 })
                {
                    trackerClock.Advance(TimeSpan.FromMinutes(16));
                    // Two ticks: the second tick's read proves the first tick's publish (read, then SetRunEstimate) ended.
                    await runClock.MoreReads(2).WaitAsync(Bound);
                    var parent = h.Snapshot(response.JobId); // TryGet: false once a parent is past the expiry
                    Assert.Equal(T0.AddMinutes(16 * step), parent.LastUpdatedUtc);
                    Assert.Equal(AnalysisProgressStatus.Running, parent.Status);
                }
            }
            finally
            {
                release.TrySetResult(); // a failing assert must not leave the run parked on the gate
            }

            await svc.RunTask!.WaitAsync(Bound);

            var done = h.Snapshot(response.JobId);
            Assert.Equal(AnalysisProgressStatus.Succeeded, done.Status);
            Assert.Equal(1, done.CompletedChunks);
            Assert.Single(h.ActiveProofreads());
        }
        finally
        {
            BookProofreadService.ParentHeartbeatInterval = saved;
        }
    }
}
