using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models.Dtos;
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
/// over the plan's words, the observed seconds-per-word pace on ONE injectable clock, and the start estimate
/// until the first word is processed.
/// </summary>
public class BookProofreadRunProgressTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    private static BookProofreadService.ChapterProofreadState Chapter(int order, int words, int units = 1) =>
        new(Guid.NewGuid(), order, words,
            Enumerable.Range(0, units)
                .Select(_ => new BookProofreadService.ProofreadUnit(units == 1 ? null : Guid.NewGuid(), T0))
                .ToList(),
            HasActiveResult: false, IsFresh: false, NewestResultAt: null);

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
        progress.UnitFinished(0, 0);

        var parent = Snap(tracker, parentId);
        var expected = (int)Math.Round(t.TotalSeconds * 2700 / 38000.0, MidpointRounding.AwayFromZero); // 767 s
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
        Assert.Equal((int)Math.Round(5400.0 / 19000 * 21700, MidpointRounding.AwayFromZero), parent.EstimatedRemainingSeconds);
    }

    [Fact]
    public void BeforeAnyWordIsProcessed_TheEtaIsTheConfiguredStartEstimate()
    {
        var (tracker, progress, clock, parentId) = Run(configuredSeconds: 6560, 38000, 1200, 1500);

        progress.Refresh();
        Assert.Equal(6560, Snap(tracker, parentId).EstimatedRemainingSeconds);
        Assert.Equal(0, Snap(tracker, parentId).EstimatedCompletionPercent);

        // A unit running with no chunk total yet (a single-shot run never gets one) processed nothing either,
        // however long it has been going.
        progress.UnitStarted(0, 0, Child(tracker));
        clock.Advance(TimeSpan.FromMinutes(20));
        progress.Refresh();
        Assert.Equal(6560, Snap(tracker, parentId).EstimatedRemainingSeconds);
        Assert.Equal(0, Snap(tracker, parentId).EstimatedCompletionPercent);
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
        progress.UnitFinished(0, 0);
        Record();
        for (var i = 1; i < 3; i++)
        {
            progress.UnitStarted(i, 0, Child(tracker));
            clock.Advance(TimeSpan.FromSeconds(30));
            progress.UnitFinished(i, 0);
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
        progress.UnitFinished(0, 0);

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

    [Fact]
    public void NonBookJobsCarryNoEstimate_AndKeepTheChunkRatio()
    {
        var tracker = new AnalysisProgressTracker();
        var chapterJob = Child(tracker, totalChunks: 4);
        tracker.ChunkCompleted(chapterJob, 1, 4);

        var snap = Snap(tracker, chapterJob);
        Assert.Null(snap.EstimatedRemainingSeconds);
        Assert.Null(snap.WeightedCompletionPercent);
        Assert.Equal(25, snap.EstimatedCompletionPercent);

        // Every other progress route builds the DTO without the new argument, so it serializes as null.
        var dto = new AnalysisProgressDto(chapterJob, "Proofread", "Chapter", null, null, null, "Running",
            snap.CurrentChunkIndex, snap.TotalChunks, snap.CompletedChunks, snap.Message, snap.EstimatedCompletionPercent);
        var json = JsonSerializer.SerializeToElement(dto, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("estimatedRemainingSeconds").ValueKind);
        Assert.Equal(25, json.GetProperty("estimatedCompletionPercent").GetInt32());
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
        Assert.Equal((int)Math.Round(t.TotalSeconds * 2700 / 38000.0, MidpointRounding.AwayFromZero), afterGiant!.EstimatedRemainingSeconds);
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
    public async Task AFailedChapterCountsAsProcessed()
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
                if (!advanced) clock.Advance(TimeSpan.FromMinutes(2)); // once, whatever the retry policy
                advanced = true;
                throw new InvalidOperationException("model down");
            }
            afterFailure = h.Snapshot(h.Registry.TryGetActive(h.BookId, "he")!.Value);
        };

        var response = await BookProofreadServiceTests.StartAndFinish(h, h.Service());

        Assert.NotNull(afterFailure);
        Assert.Equal(0, afterFailure!.CompletedChunks);                 // not proofread...
        Assert.Equal(50, afterFailure.EstimatedCompletionPercent);       // ...but its words were processed
        Assert.Equal(120, afterFailure.EstimatedRemainingSeconds);       // 120 s per 1000 words x 1000 left

        var done = h.Snapshot(response.JobId);
        Assert.Equal(AnalysisProgressStatus.Succeeded, done.Status);   // partial
        Assert.Equal(1, done.CompletedChunks);
        Assert.Equal(100, done.EstimatedCompletionPercent);
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
