using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;
using Harness = Pagedraft.Api.Tests.BookProofreadServiceTests.Harness;

namespace Pagedraft.Api.Tests;

/// <summary>
/// The whole-book proofread parent's WORD-weighted percent and server-side ETA (plan todo c10). The owner's live
/// run on a 55-chapter book whose first chapter is 38,209 words read '~16 hours' at 1/55 because the client
/// multiplied the giant's time by the chapters left. These pin the server formula in
/// <see cref="BookProofreadRunProgress"/>: processed words (finished units + the in-flight child's chunk share)
/// over the plan's words, and (be-c07) the ETA: the start estimate counted down by the clock until the first word of
/// real work lands, then a blend of that prior and the observed pace weighted by the processed fraction, with a
/// failed unit's words out of the pace.
/// </summary>
public class BookProofreadRunProgressTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    private static BookProofreadService.ChapterProofreadState Chapter(int order, int words, int units = 1) =>
        new(Guid.NewGuid(), order, words,
            Enumerable.Range(0, units)
                .Select(_ => new BookProofreadService.ProofreadUnit(units == 1 ? null : Guid.NewGuid(), T0))
                .ToList(),
            IsCovered: false, IsFresh: false, NewestReliableAt: null);

    /// <summary>A tracker with the parent registered, a progress over <paramref name="words"/>, and its clock.</summary>
    private static (AnalysisProgressTracker Tracker, BookProofreadRunProgress Progress, MutableTimeProvider Clock, Guid ParentId)
        Run(int configuredSeconds, params int[] words)
    {
        var tracker = new AnalysisProgressTracker();
        var parentId = Guid.NewGuid();
        tracker.StartJob(parentId, AnalysisScope.Book, AnalysisType.Proofread, Guid.NewGuid(), null, null);
        tracker.SetTotalChunks(parentId, words.Length);
        var clock = new MutableTimeProvider(T0);
        var planned = words.Select((w, i) => Chapter(i, w)).ToList();
        return (tracker, new BookProofreadRunProgress(tracker, parentId, planned, configuredSeconds, clock), clock, parentId);
    }

    private static Guid Child(AnalysisProgressTracker tracker, int totalChunks = 0)
    {
        var id = Guid.NewGuid();
        tracker.StartJob(id, AnalysisScope.Chapter, AnalysisType.Proofread, Guid.NewGuid(), Guid.NewGuid(), null);
        if (totalChunks > 0) tracker.SetTotalChunks(id, totalChunks);
        return id;
    }

    /// <summary>
    /// The blend of the class remarks, spelled from its definition: prior seconds per word P/W weighted by the share
    /// of words NOT yet evidenced, observed seconds per word elapsed/evidence weighted by the share that is.
    /// </summary>
    private static int Blend(double prior, double totalWords, double evidence, double elapsed, double remainingWords)
    {
        var f = evidence / totalWords;
        var rate = (1 - f) * (prior / totalWords) + f * (elapsed / evidence);
        return (int)Math.Round(rate * remainingWords, MidpointRounding.AwayFromZero);
    }

    private static AnalysisProgressSnapshot Snap(AnalysisProgressTracker tracker, Guid jobId)
    {
        Assert.True(tracker.TryGet(jobId, out var snap));
        return snap!;
    }

    // ── The formula ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void UnevenPlan_AfterTheGiantFinishes_TheEtaIsItsPaceTimesTheWordsLeft_NotTimesTheChaptersLeft()
    {
        var (tracker, progress, clock, parentId) = Run(configuredSeconds: 6560, 38000, 1200, 1500);
        var giant = Child(tracker);
        progress.UnitStarted(0, 0, giant);

        var t = TimeSpan.FromHours(3);
        clock.Advance(t);
        progress.UnitFinished(0, 0, failed: false);

        var parent = Snap(tracker, parentId);
        // 93 % of the words are evidence, so the observed pace dominates: 745 s (the pure pace read 767 s).
        var expected = Blend(prior: 6560, totalWords: 40700, evidence: 38000, elapsed: t.TotalSeconds, remainingWords: 2700);
        Assert.Equal(745, expected);
        Assert.Equal(expected, parent.EstimatedRemainingSeconds);
        Assert.True(parent.EstimatedRemainingSeconds < t.TotalSeconds / 10,
            $"ETA {parent.EstimatedRemainingSeconds}s is the chapter clock (~{t.TotalSeconds * 2}s), not the word pace");
        Assert.Equal((int)Math.Floor(100.0 * 38000 / 40700), parent.EstimatedCompletionPercent); // 93
        // The chapter fields are untouched by the estimate: they stay the run's own chapter counts.
        Assert.Equal(3, parent.TotalChunks);
        Assert.Equal(0, parent.CompletedChunks);
    }

    [Fact]
    public void MidUnitChunkProgress_MovesProcessedWordsByThatShareOfTheUnit()
    {
        var (tracker, progress, clock, parentId) = Run(configuredSeconds: 6560, 38000, 1200, 1500);
        var giant = Child(tracker, totalChunks: 4);
        progress.UnitStarted(0, 0, giant);
        tracker.ChunkCompleted(giant, 1, 4);
        tracker.ChunkCompleted(giant, 2, 4);
        clock.Advance(TimeSpan.FromMinutes(90));

        progress.Refresh(); // the heartbeat tick, not a unit boundary

        var parent = Snap(tracker, parentId);
        Assert.Equal((int)Math.Floor(100.0 * 19000 / 40700), parent.EstimatedCompletionPercent); // 46
        Assert.Equal(Blend(prior: 6560, totalWords: 40700, evidence: 19000, elapsed: 5400, remainingWords: 21700),
            parent.EstimatedRemainingSeconds);
    }

    [Fact]
    public void BeforeAnyWordIsProcessed_TheStartEstimateCountsDownByTheClock_ToAFloor()
    {
        var (tracker, progress, clock, parentId) = Run(configuredSeconds: 6560, 38000, 1200, 1500);

        progress.Refresh();
        Assert.Equal(6560, Snap(tracker, parentId).EstimatedRemainingSeconds); // the value the author consented to
        Assert.Equal(0, Snap(tracker, parentId).EstimatedCompletionPercent);

        // A unit running with no chunk total yet (a single-shot run never gets one) processes no word until it ends,
        // so the prior is all there is: it counts down by the elapsed time instead of holding still.
        progress.UnitStarted(0, 0, Child(tracker));
        clock.Advance(TimeSpan.FromMinutes(20));
        progress.Refresh();
        Assert.Equal(6560 - 20 * 60, Snap(tracker, parentId).EstimatedRemainingSeconds);
        Assert.Equal(0, Snap(tracker, parentId).EstimatedCompletionPercent);

        // Past the prior with the unit still running: the floor, never 0 (the client reads 0 as "nothing left").
        clock.Advance(TimeSpan.FromHours(2));
        progress.Refresh();
        Assert.Equal(BookProofreadRunProgress.RunningFloorSeconds, Snap(tracker, parentId).EstimatedRemainingSeconds);
    }

    [Fact]
    public void AfterTheFirstUnit_TheEtaBlendsThePriorWithThePace_SoAColdFirstUnitMovesItOnlyByItsShare()
    {
        // Five equal units, prior 500 s = 0.1 s/word. The first one runs cold: 400 s for its 1,000 words.
        var (tracker, progress, clock, parentId) = Run(configuredSeconds: 500, 1000, 1000, 1000, 1000, 1000);
        progress.UnitStarted(0, 0, Child(tracker));
        clock.Advance(TimeSpan.FromSeconds(400));
        progress.UnitFinished(0, 0, failed: false);

        // rate = 0.8 x 0.1 + 0.2 x 0.4 = 0.16 s/word, x 4,000 words left.
        var eta = Snap(tracker, parentId).EstimatedRemainingSeconds!.Value;
        Assert.Equal(Blend(prior: 500, totalWords: 5000, evidence: 1000, elapsed: 400, remainingWords: 4000), eta);
        Assert.Equal(640, eta);
        Assert.InRange(eta, 401, 1599); // strictly between the prior's 400 s and the pure pace's 1,600 s

        // At one instant, more processed words never raise it: half the next unit's chunks land, no time passes.
        var next = Child(tracker, totalChunks: 2);
        progress.UnitStarted(1, 0, next);
        tracker.ChunkCompleted(next, 1, 2);
        progress.Refresh();
        var after = Snap(tracker, parentId).EstimatedRemainingSeconds!.Value;
        Assert.True(after < eta, $"{after} s after more words vs {eta} s before");
    }

    [Fact]
    public void WhenThePaceMatchesThePrior_TheBlendIsExactlyTheCountdown()
    {
        // The two phases agree: a unit that takes exactly the prior's time for its words leaves P - elapsed.
        var (tracker, progress, clock, parentId) = Run(configuredSeconds: 500, 1000, 1000, 1000, 1000, 1000);
        progress.UnitStarted(0, 0, Child(tracker));
        clock.Advance(TimeSpan.FromSeconds(100));
        progress.UnitFinished(0, 0, failed: false);

        Assert.Equal(500 - 100, Snap(tracker, parentId).EstimatedRemainingSeconds);
    }

    [Fact]
    public void FastFailures_LeaveThePace_SoTheyCannotCollapseTheEtaTowardZero()
    {
        // Ten units, prior 1,000 s = 0.1 s/word. Unit 0 is real work at the prior's pace (100 s); then eight units
        // fail in a second each. Counted as pace, 9,000 words in 108 s read ~12 s for the last unit.
        var (tracker, progress, clock, parentId) = Run(configuredSeconds: 1000, Enumerable.Repeat(1000, 10).ToArray());
        progress.UnitStarted(0, 0, Child(tracker));
        clock.Advance(TimeSpan.FromSeconds(100));
        progress.UnitFinished(0, 0, failed: false);
        for (var i = 1; i <= 8; i++)
        {
            progress.UnitStarted(i, 0, Child(tracker));
            clock.Advance(TimeSpan.FromSeconds(1));
            progress.UnitFinished(i, 0, failed: true);
        }

        var parent = Snap(tracker, parentId);
        Assert.Equal(90, parent.EstimatedCompletionPercent); // failed words still count toward the percent
        // Evidence is unit 0's 1,000 words only: rate = 0.9 x 0.1 + 0.1 x (108 / 1000), x 1,000 words left.
        Assert.Equal(Blend(prior: 1000, totalWords: 10000, evidence: 1000, elapsed: 108, remainingWords: 1000),
            parent.EstimatedRemainingSeconds);
        Assert.Equal(101, parent.EstimatedRemainingSeconds);
    }

    [Fact]
    public void FastFailuresBeforeAnyRealWork_KeepThePriorForTheWordsLeft()
    {
        // No evidence at all: the countdown, capped by the prior's cost of the words still unprocessed.
        var (tracker, progress, clock, parentId) = Run(configuredSeconds: 1000, Enumerable.Repeat(1000, 10).ToArray());
        for (var i = 0; i < 9; i++)
        {
            progress.UnitStarted(i, 0, Child(tracker));
            clock.Advance(TimeSpan.FromSeconds(1));
            progress.UnitFinished(i, 0, failed: true);
        }

        // min(1000 - 9, 0.1 s/word x 1,000 words left) = 100 s (counted as pace it read 9 / 9000 x 1000 = 1 s).
        Assert.Equal(100, Snap(tracker, parentId).EstimatedRemainingSeconds);
        Assert.Equal(90, Snap(tracker, parentId).EstimatedCompletionPercent);
    }

    [Fact]
    public void ThePercentIsWordWeighted_MonotoneNonDecreasing_AndAt99UntilTheTerminal()
    {
        var (tracker, progress, clock, parentId) = Run(configuredSeconds: 100, 38000, 1200, 1500);
        var seen = new List<int>();
        void Record() => seen.Add(Snap(tracker, parentId).EstimatedCompletionPercent);

        var giant = Child(tracker, totalChunks: 4);
        progress.UnitStarted(0, 0, giant);
        Record();
        tracker.ChunkCompleted(giant, 1, 4);
        tracker.ChunkCompleted(giant, 2, 4);
        progress.Refresh();
        Record();
        // The child re-plans its chunks (4 -> 8): its share would read 25 %, the parent must not go backwards.
        tracker.SetTotalChunks(giant, 8);
        progress.Refresh();
        Record();
        progress.UnitFinished(0, 0, failed: false);
        Record();
        for (var i = 1; i < 3; i++)
        {
            progress.UnitStarted(i, 0, Child(tracker));
            clock.Advance(TimeSpan.FromSeconds(30));
            progress.UnitFinished(i, 0, failed: false);
            Record();
        }

        Assert.Equal(seen.OrderBy(p => p), seen);
        Assert.Equal(46, seen[1]);
        Assert.Equal(46, seen[2]);
        Assert.Equal(99, seen[^1]); // every word processed, but the job is not terminal yet
        Assert.Equal(0, Snap(tracker, parentId).EstimatedRemainingSeconds);

        progress.Finish(AnalysisProgressStatus.Succeeded);
        Assert.Equal(100, Snap(tracker, parentId).EstimatedCompletionPercent);
        Assert.Equal(0, Snap(tracker, parentId).EstimatedRemainingSeconds);
    }

    [Fact]
    public void ACanceledRunKeepsItsLastPercent_AndHasNothingLeftToWaitFor()
    {
        var (tracker, progress, clock, parentId) = Run(configuredSeconds: 100, 1000, 1000);
        progress.UnitStarted(0, 0, Child(tracker));
        clock.Advance(TimeSpan.FromSeconds(60));
        progress.UnitFinished(0, 0, failed: false);

        progress.Finish(AnalysisProgressStatus.Canceled);

        Assert.Equal(50, Snap(tracker, parentId).EstimatedCompletionPercent);
        Assert.Equal(0, Snap(tracker, parentId).EstimatedRemainingSeconds);
    }

    [Theory]
    [InlineData(10, 3, new long[] { 4, 3, 3 })]
    [InlineData(500, 1, new long[] { 500 })]
    [InlineData(0, 1, new long[] { 1 })]
    [InlineData(1, 2, new long[] { 1, 1 })]
    public void AChaptersWordsAreSharedEvenlyAcrossItsScenes_NeverBelowOnePerUnit(int words, int units, long[] expected)
    {
        Assert.Equal(expected, BookProofreadRunProgress.UnitWords(Chapter(0, words, units)));
    }

    /// <summary>The chapter progress route (<c>AnalysisController.GetAnalysisProgress</c>), as the per-chapter UI polls it.</summary>
    private static AnalysisController ChapterProgressRoute(Harness h)
    {
        var sp = h.Provider.CreateScope().ServiceProvider;
        return new AnalysisController(
            sp.GetRequiredService<AppDbContext>(), unifiedAnalysis: null!, h.Tracker,
            sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<AnalysisController>.Instance,
            sp.GetRequiredService<IHostApplicationLifetime>());
    }

    private static JsonElement Wire<T>(ActionResult<T> result) =>
        JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result.Result).Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>
    /// be-c11: read through the REAL routes (was: a DTO the test built itself, which proved only its own constructor
    /// call). Mid-run, the whole-book run's CHILD chapter job on the chapter progress route carries no estimate while
    /// its parent on the proofread progress route does; and a chunked chapter job on the same chapter route keeps the
    /// plain chunk ratio.
    /// </summary>
    [Fact]
    public async Task NonBookJobsCarryNoEstimate_OnTheirRealProgressRoute_AndKeepTheChunkRatio()
    {
        using var h = new Harness();
        var chapterId = h.AddChapter(0, "ראשון");
        JsonElement? childWire = null, parentWire = null;
        h.OnModelCall = _ =>
        {
            var parentId = h.Registry.TryGetActive(h.BookId, "he")!.Value;
            var child = h.Tracker.GetActiveJobsByBook(h.BookId).Single();
            childWire = Wire(ChapterProgressRoute(h).GetAnalysisProgress(h.BookId, chapterId, child.JobId));
            var scope = h.Provider.CreateScope().ServiceProvider;
            parentWire = Wire(new BookProofreadController(
                scope.GetRequiredService<AppDbContext>(), scope.GetRequiredService<BookProofreadService>(),
                h.Tracker, scope.GetRequiredService<IHostApplicationLifetime>()).GetProgress(h.BookId, parentId));
        };

        await BookProofreadServiceTests.StartAndFinish(h, h.Service());

        Assert.NotNull(childWire);
        Assert.Equal("Chapter", childWire!.Value.GetProperty("scope").GetString());
        Assert.Equal(JsonValueKind.Null, childWire.Value.GetProperty("estimatedRemainingSeconds").ValueKind);
        Assert.Equal(JsonValueKind.Number, parentWire!.Value.GetProperty("estimatedRemainingSeconds").ValueKind);

        // A chunked chapter job (one of four chunks done) on the same route: the chunk ratio, no estimate.
        var chunkedJob = Guid.NewGuid();
        h.Tracker.StartJob(chunkedJob, AnalysisScope.Chapter, AnalysisType.Proofread, h.BookId, chapterId, null);
        h.Tracker.SetTotalChunks(chunkedJob, 4);
        h.Tracker.ChunkCompleted(chunkedJob, 1, 4);
        Assert.Null(Snap(h.Tracker, chunkedJob).WeightedCompletionPercent);

        var chunked = Wire(ChapterProgressRoute(h).GetAnalysisProgress(h.BookId, chapterId, chunkedJob));
        Assert.Equal(JsonValueKind.Null, chunked.GetProperty("estimatedRemainingSeconds").ValueKind);
        Assert.Equal(25, chunked.GetProperty("estimatedCompletionPercent").GetInt32());
    }

    // ── Through the real run ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheRealRun_StartsAtTheStartEstimate_PacesByWords_AndEndsAt100()
    {
        var clock = new MutableTimeProvider(T0);
        using var h = new Harness(clock);
        h.AddChapter(0, "giant", wordCount: 38000);
        h.AddChapter(1, "small-one", wordCount: 1200);
        h.AddChapter(2, "small-two", wordCount: 1500);
        var t = TimeSpan.FromHours(3);
        AnalysisProgressSnapshot? atStart = null, afterGiant = null;
        Guid parentId = Guid.Empty;
        h.OnModelCall = input =>
        {
            parentId = h.Registry.TryGetActive(h.BookId, "he")!.Value;
            if (input.Contains("giant") && atStart == null)
            {
                atStart = h.Snapshot(parentId);
                clock.Advance(t); // the giant takes three hours of the ETA clock
            }
            else if (input.Contains("small-one"))
            {
                afterGiant = h.Snapshot(parentId);
            }
        };

        var response = await BookProofreadServiceTests.StartAndFinish(h, h.Service());

        Assert.NotNull(atStart);
        Assert.Equal(response.EstimatedSeconds, atStart!.EstimatedRemainingSeconds);
        Assert.Equal(0, atStart.EstimatedCompletionPercent);

        Assert.NotNull(afterGiant);
        Assert.Equal(Blend(prior: response.EstimatedSeconds, totalWords: 40700, evidence: 38000, elapsed: t.TotalSeconds,
            remainingWords: 2700), afterGiant!.EstimatedRemainingSeconds);
        Assert.Equal(93, afterGiant.EstimatedCompletionPercent);
        Assert.Equal(1, afterGiant.CompletedChunks); // the label still counts chapters: 1 of 3
        Assert.Equal(3, afterGiant.TotalChunks);

        var done = h.Snapshot(response.JobId);
        Assert.Equal(AnalysisProgressStatus.Succeeded, done.Status);
        Assert.Equal(100, done.EstimatedCompletionPercent);
        Assert.Equal(0, done.EstimatedRemainingSeconds);
        Assert.Equal(3, done.CompletedChunks);
    }

    [Fact]
    public async Task AFailedChapterCountsAsProcessed_ButNotAsPace()
    {
        var clock = new MutableTimeProvider(T0);
        using var h = new Harness(clock);
        h.AddChapter(0, "broken", wordCount: 1000);
        h.AddChapter(1, "fine", wordCount: 1000);
        AnalysisProgressSnapshot? afterFailure = null;
        var advanced = false;
        h.OnModelCall = input =>
        {
            if (input.Contains("broken"))
            {
                if (!advanced) clock.Advance(TimeSpan.FromSeconds(5)); // once, whatever the retry policy
                advanced = true;
                throw new InvalidOperationException("model down");
            }
            afterFailure = h.Snapshot(h.Registry.TryGetActive(h.BookId, "he")!.Value);
        };

        var response = await BookProofreadServiceTests.StartAndFinish(h, h.Service());

        Assert.NotNull(afterFailure);
        Assert.Equal(0, afterFailure!.CompletedChunks);                 // not proofread...
        Assert.Equal(50, afterFailure.EstimatedCompletionPercent);       // ...but its words were processed
        // ...but no evidence of the pace (be-c07): the prior for the 1,000 words left, min(P - 5, P x 1000 / 2000).
        // Counted as pace, the failure's 5 s per 1,000 words read 5 s for the chapter still to run.
        var prior = response.EstimatedSeconds;
        Assert.True(prior / 2.0 > BookProofreadRunProgress.RunningFloorSeconds && prior - 5 > prior / 2.0,
            $"fixture: the start estimate {prior} s must make the prior's share of the words left the binding term");
        Assert.Equal((int)Math.Round(prior * 1000 / 2000.0, MidpointRounding.AwayFromZero),
            afterFailure.EstimatedRemainingSeconds);

        var done = h.Snapshot(response.JobId);
        Assert.Equal(AnalysisProgressStatus.Succeeded, done.Status);   // partial
        Assert.Equal(1, done.CompletedChunks);
        Assert.Equal(100, done.EstimatedCompletionPercent);
    }

    [Fact]
    public async Task AChunkedChapterWhoseChunksAllFailFast_IsUnreliableNotThrown_AndStillLeavesThePace()
    {
        // final-r02 (CR-3): since be-c01 a chunked chapter never throws. Every chunk that fails fast (here an Ollama
        // 500, never retried) falls back, and the chapter persists an UNRELIABLE result in ~0 s for all its words.
        // Counted as pace, that collapsed the ETA toward 0 exactly as before be-c07.
        var clock = new MutableTimeProvider(T0);
        using var h = new Harness(clock);
        var target = UnifiedAnalysisService.ProofreadChunkTargetWordsFor(new AiOptions(), "he", AiTier.Fast);
        const string dead = "DEADPROVIDER";
        var paragraph = dead + " " + string.Join(" ", Enumerable.Repeat("שלום", target - 1));
        var chunkedText = string.Join("\n\n", Enumerable.Repeat(paragraph, 3));
        var chunkedId = Guid.NewGuid();
        using (var db = h.Db())
        {
            db.Chapters.Add(new Chapter
            {
                Id = chunkedId, BookId = h.BookId, Order = 0, Title = "chunked", ContentText = chunkedText,
                WordCount = target * 3,
                CreatedAt = BookProofreadServiceTests.Edited, UpdatedAt = BookProofreadServiceTests.Edited
            });
            db.SaveChanges();
        }
        h.Targets[chunkedId] = (chunkedText, chunkedId, null);
        h.AddChapter(1, "held", wordCount: target * 3);

        var deadCalls = 0;
        AnalysisProgressSnapshot? whileHeld = null;
        h.OnModelCall = input =>
        {
            if (input.Contains(dead))
            {
                Interlocked.Increment(ref deadCalls);
                clock.Advance(TimeSpan.FromSeconds(1)); // a fast failure: one second per chunk
                throw new HttpRequestException("Ollama request failed: 500 Internal Server Error");
            }
            // The second chapter, held inside its model call: the ETA the author reads right now.
            whileHeld = h.Snapshot(h.Registry.TryGetActive(h.BookId, "he")!.Value);
        };

        var response = await BookProofreadServiceTests.StartAndFinish(h, h.Service());

        // Preconditions: the first chapter went through the chunked path, every chunk failed once (not retried), and
        // it persisted an UNRELIABLE result rather than throwing.
        Assert.Equal(3, deadCalls);
        var unreliable = Assert.Single(h.ActiveProofreads(), a => a.ChapterId == chunkedId);
        Assert.Equal("chunked", unreliable.ModelName);
        Assert.True(unreliable.ProofreadResultUnreliable);

        Assert.NotNull(whileHeld);
        Assert.Equal(50, whileHeld!.EstimatedCompletionPercent); // its words still count toward the percent
        // No evidence of the pace: the countdown prior for the words left, min(P - 3, P x half), as for a THROWN
        // chapter. Counted as pace, 3 s for half the book read about half of that.
        var prior = response.EstimatedSeconds;
        var countdown = (int)Math.Round(Math.Min(prior - 3, prior * 0.5), MidpointRounding.AwayFromZero);
        Assert.True(countdown > BookProofreadRunProgress.RunningFloorSeconds,
            $"fixture: the countdown {countdown} s must sit above the floor so a collapse is visible");
        Assert.Equal(countdown, whileHeld.EstimatedRemainingSeconds);
    }

    [Fact]
    public void TheProgressRoute_CarriesTheEstimateOnTheWire()
    {
        using var h = new Harness();
        var parentId = Guid.Parse("6f1c2b8e-0d4a-4c1e-9a77-2f5e3b9d1a01");
        h.Tracker.StartJob(parentId, AnalysisScope.Book, AnalysisType.Proofread, h.BookId, null, null);
        h.Tracker.SetTotalChunks(parentId, 55);
        h.Tracker.ChunkStarted(parentId, 2, 55);
        h.Tracker.SetRunEstimate(parentId, 69, 15840);
        var scope = h.Provider.CreateScope().ServiceProvider;
        var controller = new BookProofreadController(
            scope.GetRequiredService<AppDbContext>(), scope.GetRequiredService<BookProofreadService>(),
            h.Tracker, scope.GetRequiredService<IHostApplicationLifetime>());

        var ok = Assert.IsType<OkObjectResult>(controller.GetProgress(h.BookId, parentId).Result);
        var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(
            "{\"jobId\":\"6f1c2b8e-0d4a-4c1e-9a77-2f5e3b9d1a01\",\"analysisType\":\"Proofread\",\"scope\":\"Book\"," +
            $"\"bookId\":\"{h.BookId}\",\"chapterId\":null,\"sceneId\":null,\"status\":\"Running\"," +
            "\"currentChunk\":2,\"totalChunks\":55,\"completedChunks\":0,\"message\":\"Running chunk 2/55\"," +
            "\"estimatedCompletionPercent\":69,\"bookReviewWindowCount\":null,\"bookReviewRanContinuityReduce\":null," +
            "\"bookReviewFailedWindows\":null,\"estimatedRemainingSeconds\":15840}",
            json);
    }
}
