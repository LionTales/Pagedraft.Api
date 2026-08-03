using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Pagedraft.Api.Services.Analysis.Hebrew;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// COMPLETED IS A COUNT, NOT AN INDEX (be-c01 follow-up). <c>AnalysisProgressState.CompletedChunks</c> used to
/// store "the highest 1-based index that has finished" (<c>if (Completed &lt; chunkIndex) Completed = chunkIndex</c>)
/// while the chunked Proofread/LineEdit paths run chunks TWO AT A TIME behind a <c>SemaphoreSlim</c>. Those are
/// the same number only when chunks finish strictly in order, which is exactly what parallelism removes. The
/// client now renders the field verbatim ("3 of 10 completed") and divides by it to estimate the remaining time,
/// so an inflated count is a wrong sentence on screen and a wrong ETA.
///
/// These are ORDERING assertions, not timing ones — the house style of
/// <see cref="AnalysisJobPersistOrderingTests"/>. Nothing sleeps and nothing polls: a router double GATES the
/// first chunk on a <see cref="TaskCompletionSource"/> so it can never finish, which INVERTS the completion order
/// (every later chunk finishes before the first one), and the probe reads the tracker from INSIDE a later chunk's
/// model call. The semaphore is released in <c>ProcessChunk</c>'s <c>finally</c>, AFTER <c>ChunkCompleted</c>, so
/// "the Nth model call has started" is a happens-after of "the (N-1)th chunk was recorded complete" — a real
/// ordering edge, not a delay.
/// </summary>
public class AnalysisProgressCompletedCountTests
{
    /// <summary>
    /// One observation taken at the top of a model call: what the tracker was REPORTING as completed, and how
    /// many model calls had actually RETURNED by then. A chunk is recorded complete only after its call returns,
    /// so <c>Reported &lt;= ActuallyFinished</c> is an invariant of a correct counter.
    /// </summary>
    private readonly record struct CountProbe(int CallNumber, int Reported, int ActuallyFinished);

    /// <summary>
    /// A router whose per-chunk latency is INVERTED: the FIRST chunk dispatched blocks forever (until the test
    /// releases it) while every other chunk returns immediately, so chunk 2, 3, … all finish before chunk 1.
    /// With the shipped cap of 2 parallel chunks that is the everyday shape — one worker slow, the other
    /// churning through the tail.
    /// </summary>
    private sealed class InvertedLatencyRouter : IAiRouter
    {
        private readonly AnalysisProgressTracker _tracker;
        private readonly Guid _jobId;
        private readonly TaskCompletionSource<bool> _firstChunkGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;
        private int _returned;

        /// <summary>Completes when the THIRD model call begins — i.e. once exactly one chunk has finished.</summary>
        public readonly TaskCompletionSource<bool> ThirdCallReached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public readonly ConcurrentQueue<CountProbe> Probes = new();

        public InvertedLatencyRouter(AnalysisProgressTracker tracker, Guid jobId)
        {
            _tracker = tracker;
            _jobId = jobId;
        }

        public int CallCount => Volatile.Read(ref _started);

        public void ReleaseFirstChunk() => _firstChunkGate.TrySetResult(true);

        public async Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _started);
            var finished = Volatile.Read(ref _returned);
            _tracker.TryGet(_jobId, out var snapshot);
            Probes.Enqueue(new CountProbe(call, snapshot?.CompletedChunks ?? 0, finished));

            if (call == 1)
            {
                // The inversion. This chunk holds one of the two permits for the whole run.
                await _firstChunkGate.Task;
            }
            else if (call == 3)
            {
                ThirdCallReached.TrySetResult(true);
            }

            Interlocked.Increment(ref _returned);
            // Proofread merges the model output back into the chapter, so echo the chunk unchanged.
            return new AiResponse { Content = request.InputText, Provider = "test", Model = "test" };
        }

        public IAsyncEnumerable<string> StreamCompleteAsync(
            AiRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("the chunked analysis paths never stream");
    }

    /// <summary>900 Hebrew words: comfortably over the Hebrew chunk target, so RunAsync routes to the CHUNKED path
    /// and produces well over the three chunks this probe needs.</summary>
    private static string LongHebrewText() =>
        string.Join(" ", Enumerable.Range(0, 900).Select(i => $"מילה{i % 40}"));

    [Fact]
    public async Task ChunkedProofread_NeverReportsMoreChunksCompletedThanHaveActuallyFinished()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new AppDbContext(options);

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var inputText = LongHebrewText();

        db.Books.Add(new Book { Id = bookId, Title = "T", Language = "he" });
        await db.SaveChangesAsync();

        var tracker = new AnalysisProgressTracker();
        var router = new InvertedLatencyRouter(tracker, jobId);

        var contextMock = new Mock<IAnalysisContextService>();
        contextMock
            .Setup(c => c.BuildContextAsync(
                It.IsAny<AnalysisScope>(), chapterId, AnalysisType.Proofread, It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AnalysisContext
            {
                TargetText = inputText,
                Scope = AnalysisScope.Chapter,
                AnalysisType = AnalysisType.Proofread,
                BookId = bookId,
                ChapterId = chapterId,
                SceneId = null
            });

        var svc = new UnifiedAnalysisService(
            db, router, new PromptFactory(), new SfdtConversionService(),
            Options.Create(new AiOptions()), NullLogger<UnifiedAnalysisService>.Instance,
            tracker, contextMock.Object, new SuggestionDiffService(),
            new KtivMaleChecker(new HebrewStyleOptions { EnforceKtivMale = false }),
            new AnalysisRepairService(new Mock<IAiRouter>().Object, NullLogger<AnalysisRepairService>.Instance),
            new DynamicTermRepairService(new Mock<IAiRouter>().Object, NullLogger<DynamicTermRepairService>.Instance),
            new StubBookEntityProvider());

        // Mirror the controller: it StartJob's before dispatching the background RunAsync.
        tracker.StartJob(jobId, AnalysisScope.Chapter, AnalysisType.Proofread, bookId, chapterId, null, "queued");

        var runTask = svc.RunAsync(
            AnalysisScope.Chapter, AnalysisType.Proofread, chapterId,
            customPrompt: null, language: "he", jobId: jobId, ct: CancellationToken.None);

        // Wait for the ordering edge, not for a duration. The timeout is a HANG GUARD only: if the run never
        // reaches a third chunk this test is not exercising the parallel shape, and the assertion below says so.
        var reachedThirdCall = true;
        try
        {
            await router.ThirdCallReached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            reachedThirdCall = false;
        }

        router.ReleaseFirstChunk();
        await runTask;

        Assert.True(reachedThirdCall,
            $"the chunked Proofread run never dispatched a THIRD chunk (only {router.CallCount} model call(s)), " +
            "so the out-of-order completion this test exists to catch was never produced. The input is no " +
            "longer chunking into 3+ pieces, or the parallel cap changed - fix the fixture, not the assertion.");

        var probes = router.Probes.ToArray();
        var overReport = probes.FirstOrDefault(p => p.Reported > p.ActuallyFinished);
        Assert.True(overReport == default,
            $"COMPLETED-COUNT OVER-REPORT on the chunked Proofread path: at model call #{overReport.CallNumber} " +
            $"the tracker reported {overReport.Reported} chunk(s) completed while only " +
            $"{overReport.ActuallyFinished} chunk(s) had actually finished. CompletedChunks is a COUNT of " +
            "finished chunks, not the highest INDEX that finished - with two chunks in flight a later chunk " +
            "finishing first must not advance it past the number really done. The client renders this number " +
            "verbatim ('N of M completed') and divides by it for the ETA, so an inflated count is a false " +
            "sentence on screen. Count in AnalysisProgressTracker.ChunkCompleted; do not max with the index.");

        // The pointed case: the third model call starts exactly one chunk after the first completion, with the
        // gated chunk 1 still outstanding. Anything but 1 here is the index leaking into the count.
        var thirdCall = probes.Single(p => p.CallNumber == 3);
        Assert.True(thirdCall.Reported == 1,
            $"COMPLETED-COUNT is tracking the chunk INDEX, not the number finished: at the THIRD model " +
            $"call the tracker reported {thirdCall.Reported} completed. Exactly ONE chunk (the second) " +
            "has finished at that point - chunk 1 is gated open and every later chunk is still queued - " +
            "so the only correct answer is 1. Anything higher is the index of whichever chunk happened " +
            "to finish first leaking into the count. If this reads 0, the inversion in " +
            "InvertedLatencyRouter stopped producing out-of-order completion and the fixture, not the " +
            "assertion, is what broke.");
    }

    /// <summary>
    /// THE DEGENERATE SHAPE: a blank chunk completes AT DISPATCH. The empty/whitespace fast path in
    /// <c>UnifiedAnalysisService.ProcessChunk</c> does NOT wait on the semaphore - it fires ChunkStarted +
    /// ChunkCompleted synchronously as the chunk is dispatched and returns. When that chunk is the LAST of N,
    /// the old max-with-the-index rule made the run read "N of N", 100%, for its entire remaining duration:
    /// the exact inverse of the "0% reads as stalled" problem the progress sentence was written to fix.
    ///
    /// WHY THIS IS DRIVEN AT THE TRACKER AND NOT THROUGH A RUN. The fast path is currently DEFENSIVE, not
    /// reachable: <c>BuildChunkSegmentsCore</c> drops whitespace-only paragraph parts and every chunk it emits
    /// is built from at least one non-empty trimmed segment, so no realistic chapter yields a blank chunk. A
    /// "real run" version of this test would therefore assert nothing at all - it would never take the branch.
    /// The reachability claim is pinned below rather than trusted, so if the chunker ever does start emitting a
    /// blank chunk, this test says so and a path-level test becomes worth writing.
    /// </summary>
    [Fact]
    public void ABlankChunkCompletingAtDispatch_DoesNotReportTheWholeRunComplete()
    {
        // The reachability pin for the docblock above: today's chunker emits no blank chunk.
        var chunks = UnifiedAnalysisService.ChunkForProofreadForTest(
            "פסקה ראשונה עם מילים.\n\n\n   \n\nפסקה שנייה עם עוד מילים.\n\n" + LongHebrewText() + "\n\n   \n",
            targetWordsPerChunk: 250);
        // NON-VACUITY FLOOR: Assert.All over an EMPTY list passes silently, which would turn the
        // reachability pin below into a green assertion about nothing (verified by mutation: replacing
        // `chunks` with an empty list left this test passing). The chunker must have produced something
        // before "none of them is blank" means anything.
        Assert.True(chunks.Count >= 3,
            $"the reachability pin is driving {chunks.Count} chunk(s); it needs a real multi-chunk split " +
            "to say anything about whether the chunker can emit a BLANK chunk. Fix the fixture text or " +
            "the target word count, not the assertion.");
        Assert.All(chunks, c => Assert.False(string.IsNullOrWhiteSpace(c.Text),
            "the proofread chunker now emits a BLANK chunk, so UnifiedAnalysisService's empty-chunk fast path " +
            "(which completes at dispatch without waiting on the semaphore) is reachable from real text. This " +
            "test's tracker-level drive is no longer the only coverage the shape needs - add a path-level one."));

        var tracker = new AnalysisProgressTracker();
        var jobId = Guid.NewGuid();
        tracker.StartJob(jobId, AnalysisScope.Chapter, AnalysisType.Proofread,
            Guid.NewGuid(), Guid.NewGuid(), null, "queued");
        tracker.SetTotalChunks(jobId, 10, "Queued 10 proofread chunks");

        // Chunks 1-9 are dispatched and RUNNING (none finished). Chunk 10 is blank: the fast path reports it
        // started and completed in the same breath, before any real chunk has come back.
        for (var i = 1; i <= 9; i++)
            tracker.ChunkStarted(jobId, i, 10);
        tracker.ChunkStarted(jobId, 10, 10);
        tracker.ChunkCompleted(jobId, 10, 10);

        Assert.True(tracker.TryGet(jobId, out var snapshot) && snapshot != null);

        Assert.True(snapshot!.CompletedChunks == 1 && snapshot.EstimatedCompletionPercent == 10,
            $"DEGENERATE 100%: one BLANK chunk at index 10 of 10, completing at DISPATCH, made the run report " +
            $"{snapshot.CompletedChunks} of 10 chunks completed ({snapshot.EstimatedCompletionPercent}%) while " +
            "the other 9 chunks had not finished - the run then sits at that reading for its whole remaining " +
            "duration, which is the exact inverse of the '0% reads as stalled' problem the progress sentence " +
            "was written to fix. Expected 1 of 10 (10%): the blank chunk IS one completed unit of the reserved " +
            "denominator, but it is ONE, not its index. CompletedChunks must COUNT completions in " +
            "AnalysisProgressTracker.ChunkCompleted, not max with the chunk index.");

        // The index is still wanted, and still correct - only the COUNT was wrong. AnalysisProgressDto.currentChunk
        // carries it, and the two fields deliberately disagree here.
        Assert.Equal(10, snapshot.CurrentChunkIndex);
    }

    /// <summary>
    /// The DRAIN still reaches 100%. BookReview's continuity reduce pass, when it throws mid-pass, abandons a
    /// whole TAIL of reserved chunks that will never report individually; it used to say so with
    /// <c>ChunkCompleted(total, total)</c>, which only worked because the count was a monotonic max over indices.
    /// Now that ChunkCompleted increments by one, that call would advance the readout by a single chunk and leave
    /// the job stuck below 100% forever - so the drain has its own method. Pin it: this is the regression the
    /// count change could have caused.
    /// </summary>
    [Fact]
    public void MarkAllChunksCompleted_DrainsAnAbandonedTailToOneHundredPercent()
    {
        var tracker = new AnalysisProgressTracker();
        var jobId = Guid.NewGuid();
        tracker.StartJob(jobId, AnalysisScope.Book, AnalysisType.BookReview,
            Guid.NewGuid(), null, null, "queued");
        tracker.SetTotalChunks(jobId, 8, "Reviewing 5 window(s) across the book");

        for (var i = 1; i <= 5; i++)
        {
            tracker.ChunkStarted(jobId, i, 8);
            tracker.ChunkCompleted(jobId, i, 8);
        }
        tracker.ChunkStarted(jobId, 6, 8);
        tracker.ChunkCompleted(jobId, 6, 8);

        // Chunks 7 and 8 (continuity) are abandoned by a throwing pass.
        tracker.MarkAllChunksCompleted(jobId, 8);

        Assert.True(tracker.TryGet(jobId, out var snapshot) && snapshot != null);
        Assert.Equal(8, snapshot!.CompletedChunks);
        Assert.Equal(100, snapshot.EstimatedCompletionPercent);

        // Idempotent and monotonic: draining twice does not push the count past the total.
        tracker.MarkAllChunksCompleted(jobId, 8);
        Assert.True(tracker.TryGet(jobId, out var again) && again != null);
        Assert.Equal(8, again!.CompletedChunks);
    }
}
