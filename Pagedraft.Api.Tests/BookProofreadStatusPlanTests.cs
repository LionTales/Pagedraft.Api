using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
/// Plan todo c7 (owner decisions 2026-10-12), API half: the status carries the ETA of both starts (normal and
/// forced) from the SAME planning method the start runs, and lists the chapters whose Active Proofread result is
/// flagged unreliable. The wire shape is pinned too, because the client todo c8 consumes it field by field.
/// </summary>
public class BookProofreadStatusPlanTests
{
    private static readonly DateTimeOffset Edited = BookProofreadServiceTests.Edited;

    /// <summary>
    /// What StartAsync would answer for this state, WITHOUT running anything: with a run already registered for
    /// (book, language) the start hands back that run's id together with the plan it computed, and stops there.
    /// </summary>
    private static async Task<StartBookProofreadResponse> PlanViaStart(Harness h, bool force)
    {
        var svc = h.Service();
        var response = await svc.StartAsync(h.BookId, "he", force, CancellationToken.None);
        Assert.Null(svc.RunTask); // nothing ran, so the state the next call reads is unchanged
        return response;
    }

    [Fact]
    public async Task StatusEstimates_AreExactlyWhatTheStartPlans_ForcedAndNot()
    {
        using var h = new Harness();
        var fresh = h.AddChapter(0, "ראשון", wordCount: 600);
        var stale = h.AddChapter(1, "שני", wordCount: 100);
        h.AddChapter(2, "שלישי", wordCount: 300);                 // never proofread
        var split = h.AddChapter(3, "רביעי", wordCount: 40);
        h.AddScene(split, 0, "אחת", written: true);
        h.AddScene(split, 1, "שתיים", written: true);              // scene-split: at least one chunk per scene
        h.AddChapter(4, "ריק", wordCount: 0);                      // no text: neither plan sees it
        h.AddActiveResult(fresh, null, Edited.AddMinutes(1));
        h.AddActiveResult(stale, null, Edited.AddMinutes(-1));
        Assert.True(h.Registry.TryStart(h.BookId, "he", Guid.NewGuid(), out _));

        var status = await h.Service().GetStatusAsync(h.BookId, "he", CancellationToken.None);
        var normal = await PlanViaStart(h, force: false);
        var forced = await PlanViaStart(h, force: true);

        Assert.Equal(normal.PlannedChapters, status.PlannedChapters);
        Assert.Equal(normal.EstimatedChunks, status.EstimatedChunks);
        Assert.Equal(normal.EstimatedSeconds, status.EstimatedSeconds);
        Assert.Equal(forced.PlannedChapters, status.ForcePlannedChapters);
        Assert.Equal(forced.EstimatedChunks, status.ForceEstimatedChunks);
        Assert.Equal(forced.EstimatedSeconds, status.ForceEstimatedSeconds);

        // Non-vacuous: the two plans differ exactly by the fresh chapter, and both follow the formula.
        Assert.Equal(3, status.PlannedChapters);
        Assert.Equal(4, status.ForcePlannedChapters);
        var opts = new AiOptions();
        var target = UnifiedAnalysisService.ProofreadChunkTargetWordsFor(opts, "he", AiTier.Fast);
        int Ceil(int words) => (int)Math.Ceiling(words / (double)target);
        var normalChunks = Ceil(100) + Ceil(300) + Math.Max(2, Ceil(40));
        Assert.Equal(normalChunks, status.EstimatedChunks);
        Assert.Equal(normalChunks + Ceil(600), status.ForceEstimatedChunks);
        Assert.Equal(status.EstimatedChunks * opts.ProofreadSecondsPerChunkEstimate, status.EstimatedSeconds);
        Assert.Equal(status.ForceEstimatedChunks * opts.ProofreadSecondsPerChunkEstimate, status.ForceEstimatedSeconds);
    }

    [Fact]
    public async Task EverythingFresh_TheNormalPlanIsZero_AndTheForcedPlanIsNot()
    {
        using var h = new Harness();
        var chapter = h.AddChapter(0, "ראשון", wordCount: 120);
        h.AddActiveResult(chapter, null, Edited.AddMinutes(1));

        var status = await h.Service().GetStatusAsync(h.BookId, "he", CancellationToken.None);
        var start = await h.Service().StartAsync(h.BookId, "he", force: false, CancellationToken.None);

        Assert.False(start.Started);
        Assert.Equal(0, status.PlannedChapters);
        Assert.Equal(0, status.EstimatedChunks);
        Assert.Equal(0, status.EstimatedSeconds);
        Assert.Equal(start.EstimatedSeconds, status.EstimatedSeconds);
        Assert.Equal(1, status.ForcePlannedChapters);
        Assert.True(status.ForceEstimatedSeconds > 0);
    }

    [Fact]
    public void Plan_IsTheOneRule_FreshSkippedUnlessForced_UnitlessChaptersNeverPlanned()
    {
        var units = new[] { new BookProofreadService.ProofreadUnit(null, Edited) };
        BookProofreadService.ChapterProofreadState State(int order, bool fresh, bool withUnits = true) =>
            new(Guid.NewGuid(), order, WordCount: 50,
                withUnits ? units : Array.Empty<BookProofreadService.ProofreadUnit>(),
                HasActiveResult: fresh, IsFresh: fresh, NewestResultAt: null);
        var states = new[] { State(0, fresh: true), State(1, fresh: false), State(2, fresh: false, withUnits: false) };
        var opts = new AiOptions();

        var normal = BookProofreadService.Plan(states, force: false, opts, "he", AiTier.Fast);
        var forced = BookProofreadService.Plan(states, force: true, opts, "he", AiTier.Fast);

        Assert.Equal(new[] { 1 }, normal.Planned.Select(p => p.Order));
        Assert.Equal(1, normal.SkippedFresh);
        Assert.Equal(new[] { 0, 1 }, forced.Planned.Select(p => p.Order));
        Assert.Equal(0, forced.SkippedFresh);
        Assert.Equal(BookProofreadService.Estimate(forced.Planned, opts, "he", AiTier.Fast),
            (forced.EstimatedChunks, forced.EstimatedSeconds));
    }

    // ── Unreliable chapters ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UnreliableChapters_ListsEveryChapterWithAnUnreliableActiveResult_InReadingOrder()
    {
        using var h = new Harness();
        var sceneScoped = h.AddChapter(2, "פרק סצנות");
        var reliable = h.AddChapter(0, "תקין");
        var chapterScoped = h.AddChapter(1, "פרק שבור");
        var otherLanguage = h.AddChapter(3, "אנגלית");
        var archived = h.AddChapter(4, "בארכיון");
        h.AddActiveResult(reliable, null, Edited.AddMinutes(1));
        h.AddActiveResult(chapterScoped, null, Edited.AddMinutes(1), unreliable: true);
        h.AddActiveResult(sceneScoped, Guid.NewGuid(), Edited.AddMinutes(1), unreliable: true);
        h.AddActiveResult(otherLanguage, null, Edited.AddMinutes(1), unreliable: true, language: "en");
        using (var db = h.Db())
        {
            db.AnalysisResults.Add(new AnalysisResult
            {
                BookId = h.BookId, ChapterId = archived, Scope = AnalysisScope.Chapter,
                AnalysisType = AnalysisType.Proofread, Status = AnalysisStatus.Archived, Language = "he",
                CreatedAt = Edited.AddMinutes(1), ProofreadResultUnreliable = true, Type = "Proofread"
            });
            db.SaveChanges();
        }

        var status = await h.Service().GetStatusAsync(h.BookId, "he", CancellationToken.None);

        Assert.Equal(
            new[] { (chapterScoped, "פרק שבור", 1), (sceneScoped, "פרק סצנות", 2) },
            status.UnreliableChapters.Select(u => (u.ChapterId, u.Title, u.Order)));
    }

    [Fact]
    public async Task UnreliableChapters_IsEmptyWhenNothingIsFlagged()
    {
        using var h = new Harness();
        var chapter = h.AddChapter(0, "ראשון");
        h.AddActiveResult(chapter, null, Edited.AddMinutes(1));

        var status = await h.Service().GetStatusAsync(h.BookId, "he", CancellationToken.None);

        Assert.NotNull(status.UnreliableChapters);
        Assert.Empty(status.UnreliableChapters);
    }

    // ── Wire shape (the c8 client contract) ───────────────────────────────────────────────────────────

    [Fact]
    public async Task StatusRoute_SerializesTheC8ContractFieldNames()
    {
        using var h = new Harness();
        var chapter = h.AddChapter(0, "ראשון", wordCount: 300);
        h.AddActiveResult(chapter, null, Edited.AddMinutes(1), unreliable: true);
        var scope = h.Provider.CreateScope().ServiceProvider;
        var controller = new BookProofreadController(
            scope.GetRequiredService<AppDbContext>(), scope.GetRequiredService<BookProofreadService>(),
            h.Tracker, scope.GetRequiredService<IHostApplicationLifetime>());

        var ok = Assert.IsType<OkObjectResult>((await controller.GetStatus(h.BookId, null, CancellationToken.None)).Result);
        var json = JsonSerializer.SerializeToElement(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        foreach (var name in new[]
                 {
                     "activeJobId", "chaptersWithText", "chaptersWithActiveProofread", "chaptersStale", "lastRunAt",
                     "plannedChapters", "estimatedChunks", "estimatedSeconds",
                     "forcePlannedChapters", "forceEstimatedChunks", "forceEstimatedSeconds", "unreliableChapters"
                 })
            Assert.True(json.TryGetProperty(name, out _), $"missing {name}");

        var unreliable = Assert.Single(json.GetProperty("unreliableChapters").EnumerateArray().ToList());
        Assert.Equal(chapter, unreliable.GetProperty("chapterId").GetGuid());
        Assert.Equal("ראשון", unreliable.GetProperty("title").GetString());
        Assert.Equal(0, unreliable.GetProperty("order").GetInt32());
        // The unreliable result is stale, so the normal plan runs it: the consent's ETA is not zero.
        Assert.Equal(1, json.GetProperty("plannedChapters").GetInt32());
        Assert.True(json.GetProperty("estimatedSeconds").GetInt32() > 0);
    }
}
