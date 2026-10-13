using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Analysis;
using Xunit;
using Harness = Pagedraft.Api.Tests.BookProofreadServiceTests.Harness;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Plan todo c20: after a whole-book run, a chapter whose attempt THREW keeps its previous Active result, which the
/// freshness rule still counts as fresh when it is reliable and newer than the last edit, so a normal start plans
/// nothing for it (an unreliable result archives the previous one and is never fresh, so a normal start re-plans
/// that chapter; it is still listed as failed). The registry now keeps
/// a failed-chapters record (in memory), the status lists it with its own estimate, and
/// <c>POST .../proofread?onlyFailed=true</c> plans exactly those chapters and clears them on success. Also pins the
/// TEST-ONLY fault seam used by the live gate (inert outside Development).
/// </summary>
public class BookProofreadRetryFailedTests
{
    private static readonly DateTimeOffset Edited = BookProofreadServiceTests.Edited;

    private static async Task<StartBookProofreadResponse> Finish(BookProofreadService svc, Task<StartBookProofreadResponse> start)
    {
        var response = await start;
        if (response.Started && svc.RunTask != null) await svc.RunTask;
        return response;
    }

    private static Task<BookProofreadStatusDto> Status(Harness h, string language = "he") =>
        h.Service().GetStatusAsync(h.BookId, language, CancellationToken.None);

    /// <summary>Two chapters, both with a FRESH result from an earlier run; a forced run in which the first fails.</summary>
    private static async Task<(Guid Failed, Guid Fine)> ForcedRunWhereTheFirstChapterFails(Harness h)
    {
        var failed = h.AddChapter(0, "ראשון", wordCount: 600);
        var fine = h.AddChapter(1, "שני", wordCount: 300);
        h.AddActiveResult(failed, null, Edited.AddMinutes(1));
        h.AddActiveResult(fine, null, Edited.AddMinutes(1));
        h.OnModelCall = input => { if (input.Contains("ראשון")) throw new InvalidOperationException("model down"); };

        var svc = h.Service();
        var run = await Finish(svc, svc.StartAsync(h.BookId, "he", force: true, CancellationToken.None));
        Assert.True(run.Started);
        Assert.Equal(1, h.Snapshot(run.JobId).CompletedChunks); // partial: the first chapter failed
        h.OnModelCall = null;
        return (failed, fine);
    }

    // ── The record and the status ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AFailedChapterIsListed_WhileANormalStartStillPlansNothingForIt()
    {
        using var h = new Harness();
        var (failed, _) = await ForcedRunWhereTheFirstChapterFails(h);

        var status = await Status(h);

        var listed = Assert.Single(status.FailedChapters);
        Assert.Equal((failed, "ראשון", 0), (listed.ChapterId, listed.Title, listed.Order));
        // The trap c20 exists for: the failed chapter's OLD result still counts as fresh.
        Assert.Equal(0, status.PlannedChapters);
        Assert.Equal(0, status.ChaptersStale);
        Assert.True(status.FailedEstimatedSeconds > 0);
    }

    [Fact]
    public async Task TheFailedEstimate_IsExactlyWhatTheRetryStartPlans()
    {
        using var h = new Harness();
        await ForcedRunWhereTheFirstChapterFails(h);
        var status = await Status(h);
        // A run registered for the key makes the start answer with its plan and run nothing.
        Assert.True(h.Registry.TryStart(h.BookId, "he", Guid.NewGuid(), out _));
        var svc = h.Service();

        var plan = await svc.StartFailedAsync(h.BookId, "he", CancellationToken.None);

        Assert.Null(svc.RunTask);
        Assert.Equal(1, plan.PlannedChapters);
        Assert.Equal(status.FailedChapters.Count, plan.PlannedChapters);
        Assert.Equal(status.FailedEstimatedChunks, plan.EstimatedChunks);
        Assert.Equal(status.FailedEstimatedSeconds, plan.EstimatedSeconds);
        Assert.Equal(0, plan.SkippedFresh);
    }

    [Fact]
    public async Task TheRetryRunsExactlyTheFailedChapter_LeavesTheOtherAlone_AndClearsTheRecord()
    {
        using var h = new Harness();
        var (failed, fine) = await ForcedRunWhereTheFirstChapterFails(h);
        var fineResultBefore = h.ActiveProofreads().Single(r => r.ChapterId == fine).Id;
        var inputsBefore = h.RouterInputs.Count;
        var svc = h.Service();

        var retry = await Finish(svc, svc.StartFailedAsync(h.BookId, "he", CancellationToken.None));

        Assert.True(retry.Started);
        Assert.Equal(1, retry.PlannedChapters);
        var parent = h.Snapshot(retry.JobId);
        Assert.Equal(AnalysisProgressStatus.Succeeded, parent.Status);
        Assert.Equal(1, parent.TotalChunks);
        Assert.Equal(1, parent.CompletedChunks);
        var newInputs = h.RouterInputs.Skip(inputsBefore).ToList();
        Assert.NotEmpty(newInputs);
        Assert.All(newInputs, i => Assert.Contains("ראשון", i));
        Assert.Equal(fineResultBefore, h.ActiveProofreads().Single(r => r.ChapterId == fine).Id);
        Assert.True(h.ActiveProofreads().Single(r => r.ChapterId == failed).CreatedAt > Edited.AddMinutes(1));

        var status = await Status(h);
        Assert.Empty(status.FailedChapters);
        Assert.Equal(0, status.FailedEstimatedChunks);
        Assert.Equal(0, status.FailedEstimatedSeconds);
    }

    [Fact]
    public async Task ARetryThatFailsAgain_KeepsTheChapterListed()
    {
        using var h = new Harness();
        var (failed, _) = await ForcedRunWhereTheFirstChapterFails(h);
        h.OnModelCall = _ => throw new InvalidOperationException("still down");
        var svc = h.Service();

        var retry = await Finish(svc, svc.StartFailedAsync(h.BookId, "he", CancellationToken.None));

        Assert.Equal(AnalysisProgressStatus.Failed, h.Snapshot(retry.JobId).Status);
        Assert.Equal(failed, Assert.Single((await Status(h)).FailedChapters).ChapterId);
    }

    [Fact]
    public async Task OnlyFailed_WithNothingRecorded_StartsNothing()
    {
        using var h = new Harness();
        h.AddChapter(0, "ראשון"); // never proofread: a normal start WOULD plan it
        var svc = h.Service();

        var response = await svc.StartFailedAsync(h.BookId, "he", CancellationToken.None);

        Assert.False(response.Started);
        Assert.Equal(Guid.Empty, response.JobId);
        Assert.Equal(0, response.PlannedChapters);
        Assert.Null(svc.RunTask);
        Assert.Empty(h.RouterInputs);
    }

    [Fact]
    public async Task AnUnreliableResult_IsRecordedAsFailed()
    {
        using var h = new Harness();
        var chapter = h.AddChapter(0, "ראשון");
        // The model answers with an unrelated continuation, so the persisted result is flagged unreliable.
        h.Respond = _ => "Chapter 12: The story continues with completely different content and new characters, far away from the original text.";
        var svc = h.Service();

        var run = await Finish(svc, svc.StartAsync(h.BookId, "he", force: false, CancellationToken.None));

        Assert.True(Assert.Single(h.ActiveProofreads()).ProofreadResultUnreliable); // precondition
        Assert.Equal(0, h.Snapshot(run.JobId).CompletedChunks);
        var status = await Status(h);
        Assert.Equal(chapter, Assert.Single(status.FailedChapters).ChapterId);
        Assert.Equal(chapter, Assert.Single(status.UnreliableChapters).ChapterId);

        h.Respond = null;
        var retry = await Finish(svc, svc.StartFailedAsync(h.BookId, "he", CancellationToken.None));
        Assert.Equal(1, h.Snapshot(retry.JobId).CompletedChunks);
        Assert.Empty((await Status(h)).FailedChapters);
    }

    [Fact]
    public async Task ALaterNormalRunThatSkipsTheFailedChapter_DoesNotHideIt()
    {
        using var h = new Harness();
        var (failed, fine) = await ForcedRunWhereTheFirstChapterFails(h);
        using (var db = h.Db())
        {
            var c = db.Chapters.Single(x => x.Id == fine);
            c.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(5); // edited after its new result: stale
            db.SaveChanges();
        }
        var svc = h.Service();

        var normal = await Finish(svc, svc.StartAsync(h.BookId, "he", force: false, CancellationToken.None));

        Assert.Equal(1, normal.PlannedChapters); // only the edited chapter
        Assert.Equal(failed, Assert.Single((await Status(h)).FailedChapters).ChapterId);
    }

    [Fact]
    public async Task AReliableResultNewerThanTheFailedAttempt_DropsTheChapter_AnOlderOneDoesNot()
    {
        using var h = new Harness();
        var (failed, _) = await ForcedRunWhereTheFirstChapterFails(h);

        h.AddActiveResult(failed, null, Edited.AddMinutes(2)); // older than the attempt
        Assert.Single((await Status(h)).FailedChapters);

        h.AddActiveResult(failed, null, DateTimeOffset.UtcNow.AddMinutes(1), unreliable: true); // newer but unreliable
        Assert.Single((await Status(h)).FailedChapters);

        h.AddActiveResult(failed, null, DateTimeOffset.UtcNow.AddMinutes(1)); // e.g. a per-chapter run since
        Assert.Empty((await Status(h)).FailedChapters);
    }

    [Fact]
    public async Task ASceneThatFailed_ListsItsChapter_UntilThatSceneHasANewerResult()
    {
        using var h = new Harness();
        var chapter = h.AddChapter(0, "פרק");
        h.AddScene(chapter, 0, "אחת", written: true);
        var second = h.AddScene(chapter, 1, "שתיים", written: true);
        h.OnModelCall = input => { if (input.Contains("שתיים")) throw new InvalidOperationException("model down"); };
        var svc = h.Service();

        await Finish(svc, svc.StartAsync(h.BookId, "he", force: false, CancellationToken.None));
        h.OnModelCall = null;

        Assert.Equal(chapter, Assert.Single((await Status(h)).FailedChapters).ChapterId);
        // The first scene's result came from INSIDE the failed attempt, which counts as covered: only the failed
        // scene needs a newer reliable result.
        h.AddActiveResult(chapter, second, DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Empty((await Status(h)).FailedChapters);
    }

    [Fact]
    public async Task TheRecordIsPerLanguage()
    {
        using var h = new Harness();
        await ForcedRunWhereTheFirstChapterFails(h);

        Assert.Single((await Status(h, "he")).FailedChapters);
        Assert.Empty((await Status(h, "en")).FailedChapters);
    }

    [Fact]
    public void Registry_RecordChapterOutcome_AddsOnFailure_RemovesOnSuccess_AndSnapshots()
    {
        var reg = new BookProofreadBuildRegistry();
        var book = Guid.NewGuid();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var t1 = DateTimeOffset.UtcNow;

        Assert.Empty(reg.FailedChapters(book, "he"));
        reg.RecordChapterOutcome(book, "he", a, succeeded: false, t1);
        reg.RecordChapterOutcome(book, "he", b, succeeded: true, t1); // a success for an unrecorded chapter: no-op
        var snapshot = reg.FailedChapters(book, "he");
        Assert.Equal(new[] { a }, snapshot.Keys);
        Assert.Equal(t1, snapshot[a]);

        var t2 = t1.AddMinutes(1);
        reg.RecordChapterOutcome(book, "he", a, succeeded: false, t2); // a later failure moves the attempt time
        Assert.Equal(t2, reg.FailedChapters(book, "he")[a]);
        Assert.Equal(t1, snapshot[a]);                                  // the earlier snapshot is a copy

        Assert.Empty(reg.FailedChapters(book, "en"));
        Assert.Single(reg.FailedChapters(book, ""));                    // blank language normalizes to he
        reg.RecordChapterOutcome(book, "he", a, succeeded: true, t2);
        Assert.Empty(reg.FailedChapters(book, "he"));
    }

    // ── Wire shape and the route ────────────────────────────────────────────────────────────────────

    private static BookProofreadController Controller(Harness h)
    {
        var scope = h.Provider.CreateScope().ServiceProvider;
        return new BookProofreadController(
            scope.GetRequiredService<AppDbContext>(), scope.GetRequiredService<BookProofreadService>(),
            h.Tracker, scope.GetRequiredService<IHostApplicationLifetime>());
    }

    [Fact]
    public async Task StatusRoute_SerializesTheFailedChaptersContract()
    {
        using var h = new Harness();
        var (failed, _) = await ForcedRunWhereTheFirstChapterFails(h);

        var ok = Assert.IsType<OkObjectResult>((await Controller(h).GetStatus(h.BookId, null, CancellationToken.None)).Result);
        var json = JsonSerializer.SerializeToElement(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var item = Assert.Single(json.GetProperty("failedChapters").EnumerateArray().ToList());
        Assert.Equal(failed, item.GetProperty("chapterId").GetGuid());
        Assert.Equal("ראשון", item.GetProperty("title").GetString());
        Assert.Equal(0, item.GetProperty("order").GetInt32());
        Assert.True(json.GetProperty("failedEstimatedChunks").GetInt32() > 0);
        Assert.True(json.GetProperty("failedEstimatedSeconds").GetInt32() > 0);
    }

    [Fact]
    public async Task StartRoute_OnlyFailedWinsOverForce()
    {
        using var h = new Harness();
        var chapter = h.AddChapter(0, "ראשון");
        h.AddActiveResult(chapter, null, Edited.AddMinutes(1)); // fresh: only force would plan it

        var ok = Assert.IsType<OkObjectResult>(
            (await Controller(h).Start(h.BookId, "he", force: true, onlyFailed: true, CancellationToken.None)).Result);
        var response = Assert.IsType<StartBookProofreadResponse>(ok.Value);

        Assert.False(response.Started); // nothing recorded, so nothing to retry, whatever force says
        Assert.Equal(0, response.PlannedChapters);
        Assert.Empty(h.RouterInputs);
    }

    // ── The TEST-ONLY fault seam ────────────────────────────────────────────────────────────────────

    private static IHostEnvironment Env(string name)
    {
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(name);
        return env.Object;
    }

    private static IConfiguration Config(string? value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [BookProofreadFaultInjection.ConfigKey] = value })
            .Build();

    [Fact]
    public void FaultSeam_ArmsOnlyInDevelopment_AndOnlyWhenTheKeyIsSet()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var both = $"{a}, {b},not-a-guid";

        Assert.Same(BookProofreadFaultInjection.None, BookProofreadFaultInjection.FromConfiguration(Config(both), Env("Production")));
        Assert.Same(BookProofreadFaultInjection.None, BookProofreadFaultInjection.FromConfiguration(Config(both), Env("Staging")));
        Assert.Same(BookProofreadFaultInjection.None, BookProofreadFaultInjection.FromConfiguration(Config(null), Env("Development")));
        Assert.Same(BookProofreadFaultInjection.None, BookProofreadFaultInjection.FromConfiguration(Config("junk"), Env("Development")));

        var armed = BookProofreadFaultInjection.FromConfiguration(Config(both), Env("Development"));
        Assert.Equal(2, armed.ArmedCount);
        Assert.True(armed.TryConsume(a));
        Assert.False(armed.TryConsume(a)); // once
        Assert.False(armed.TryConsume(Guid.NewGuid()));
        Assert.Equal(0, BookProofreadFaultInjection.None.ArmedCount);
    }

    // Located from this source file, not AppContext.BaseDirectory: the build output may be redirected
    // (-p:BaseOutputPath), which breaks any walk-up from the binaries.
    private static string ApiProjectDir([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
        => System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(thisFile)!, "..", "Pagedraft.Api"));

    [Fact]
    public void FaultSeam_NoShippedConfigurationCarriesTheKey()
    {
        var apiDir = ApiProjectDir();
        Assert.True(System.IO.File.Exists(System.IO.Path.Combine(apiDir, "appsettings.json")), apiDir);
        var files = System.IO.Directory.GetFiles(apiDir, "appsettings*.json");
        Assert.NotEmpty(files);
        foreach (var f in files)
            Assert.DoesNotContain("FailChapterIdsOnce", System.IO.File.ReadAllText(f));
    }

    [Fact]
    public async Task FaultSeam_FailsTheArmedChapterOnce_ThenTheRetrySucceeds()
    {
        using var h = new Harness();
        var armedChapter = h.AddChapter(0, "ראשון");
        h.AddChapter(1, "שני");
        var faults = BookProofreadFaultInjection.For(armedChapter);
        BookProofreadService Svc()
        {
            var sp = h.Provider.CreateScope().ServiceProvider;
            return new BookProofreadService(
                sp.GetRequiredService<IServiceScopeFactory>(), h.Tracker, h.Registry,
                sp.GetRequiredService<IOptions<AiOptions>>(), sp.GetRequiredService<IHostApplicationLifetime>(),
                NullLogger<BookProofreadService>.Instance, clock: null, faults: faults);
        }

        var first = Svc();
        var run = await Finish(first, first.StartAsync(h.BookId, "he", force: false, CancellationToken.None));

        Assert.Equal(1, h.Snapshot(run.JobId).CompletedChunks);
        Assert.DoesNotContain(h.RouterInputs, i => i.Contains("ראשון")); // failed before the model was called
        Assert.Equal(armedChapter, Assert.Single((await Status(h)).FailedChapters).ChapterId);

        var second = Svc();
        var retry = await Finish(second, second.StartFailedAsync(h.BookId, "he", CancellationToken.None));

        Assert.Equal(1, h.Snapshot(retry.JobId).CompletedChunks);
        Assert.Contains(h.RouterInputs, i => i.Contains("ראשון"));
        Assert.Empty((await Status(h)).FailedChapters);
    }
}
