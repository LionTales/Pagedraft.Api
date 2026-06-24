using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;
using static Pagedraft.Api.Tests.BookReviewTestHelpers;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Controller-surface tests for the whole-book review REST endpoints on <see cref="BooksController"/>
/// (wb2-c03): POST review (build dispatch / BriefsMissing guidance / no-op), GET review/status, GET
/// review/findings, and PATCH review/findings/{id}/status (open→acknowledged→dismissed→done, idempotent,
/// invalid → BadRequest). Mirrors <see cref="BookReviewServiceTests"/> conventions: a real in-memory DB and
/// a MOCKED <see cref="IAiRouter"/> (no live model), with the full assembler chain wired so seeded structured
/// briefs flow into the review build.
/// </summary>
public class BooksReviewControllerTests
{


    // ─── POST review: dispatch returns a pollable jobId ───────────────────────────────────────────

    [Fact]
    public async Task BuildBookReview_FreshBook_ReturnsPollableJobId()
    {
        using var provider = BuildProvider(out _, FindingsPerDimension(perDimensionCount: 1));
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var controller = BuildController(provider);
        var action = await controller.BuildBookReview(bookId, new BuildBookReviewRequest("he"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var response = Assert.IsType<StartBookReviewBuildResponse>(ok.Value);

        Assert.NotNull(response.JobId);
        Assert.False(response.NoOp);
        Assert.False(response.BriefsMissing);
        Assert.False(response.Ready);
        Assert.Equal("he", response.Language);

        // The pre-registered job is pollable and labelled as a book-scoped BookReview job.
        var progressAction = controller.GetBookReviewProgress(bookId, response.JobId!.Value);
        var progressOk = Assert.IsType<OkObjectResult>(progressAction.Result);
        var progress = Assert.IsType<AnalysisProgressDto>(progressOk.Value);
        Assert.Equal(nameof(AnalysisType.BookReview), progress.AnalysisType);
        Assert.Equal(nameof(AnalysisScope.Book), progress.Scope);
    }

    // ─── POST review: briefs-missing surfaces guidance, spends no model calls ──────────────────────

    [Fact]
    public async Task BuildBookReview_NoBriefs_SurfacesBriefsMissingGuidance_NoLlmCalls()
    {
        using var provider = BuildProvider(out var routerMock, FindingsPerDimension(perDimensionCount: 1));
        var db = provider.GetRequiredService<AppDbContext>();

        // A book with a chapter but NO structured briefs → the review must NOT dispatch / spend model calls.
        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "No Briefs", Language = "he" });
        db.Chapters.Add(new Chapter { Id = Guid.NewGuid(), BookId = bookId, Order = 0, Title = "A", ContentText = "תוכן בלי תקציר." });
        await db.SaveChangesAsync();

        var controller = BuildController(provider);
        var action = await controller.BuildBookReview(bookId, new BuildBookReviewRequest("he"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var response = Assert.IsType<StartBookReviewBuildResponse>(ok.Value);

        Assert.True(response.BriefsMissing);
        Assert.Null(response.JobId);
        Assert.False(response.Ready);
        Assert.False(response.NoOp);
        Assert.Contains("summary", response.Message, StringComparison.OrdinalIgnoreCase);

        // No per-dimension model calls were issued on the briefs-missing path.
        routerMock.Verify(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ─── POST review: a fresh existing review is a no-op ──────────────────────────────────────────

    [Fact]
    public async Task BuildBookReview_AlreadyFresh_ReturnsNoOpReady()
    {
        using var provider = BuildProvider(out _, FindingsPerDimension(perDimensionCount: 1));
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        // Build once synchronously through the service so a fresh review exists.
        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        var controller = BuildController(provider);
        var action = await controller.BuildBookReview(bookId, new BuildBookReviewRequest("he"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var response = Assert.IsType<StartBookReviewBuildResponse>(ok.Value);

        Assert.True(response.NoOp);
        Assert.True(response.Ready);
        Assert.Null(response.JobId);
        Assert.False(response.BriefsMissing);
        Assert.Equal(6, response.FindingCount); // one finding per dimension
    }

    // ─── GET review/status: shape after a build ───────────────────────────────────────────────────

    [Fact]
    public async Task GetBookReviewStatus_AfterBuild_ReportsCoverageShape()
    {
        using var provider = BuildProvider(out _, FindingsPerDimension(perDimensionCount: 1));
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        var controller = BuildController(provider);
        var action = await controller.GetBookReviewStatus(bookId, "he", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var status = Assert.IsType<BookReviewStatusDto>(ok.Value);

        Assert.Equal(bookId, status.BookId);
        Assert.Equal("he", status.Language);
        Assert.True(status.HasReview);
        Assert.Equal(6, status.FindingCount);
        Assert.True(status.HasBriefs);
        Assert.False(status.StaleVsBriefs);
        Assert.False(status.BuiltWithDifferentModel);
        Assert.True(status.Ready);
        Assert.NotNull(status.LastUpdatedAt);
    }

    [Fact]
    public async Task GetBookReviewStatus_UnknownBook_ReturnsNotFound()
    {
        using var provider = BuildProvider(out _, FindingsPerDimension(perDimensionCount: 1));
        var controller = BuildController(provider);

        var action = await controller.GetBookReviewStatus(Guid.NewGuid(), "he", CancellationToken.None);
        Assert.IsType<NotFoundResult>(action.Result);
    }

    // ─── GET review/findings: list + dimension scores ─────────────────────────────────────────────

    [Fact]
    public async Task GetBookReviewFindings_AfterBuild_ReturnsFindingsAndScores()
    {
        var byDim = FindingsPerDimension(perDimensionCount: 0);
        byDim["plot"] = JsonFindings(
            new FindingSpec("keep", 1, "Plot strength", 1),
            new FindingSpec("keep", 2, "Another strength", 2));
        byDim["pacing"] = JsonFindings(new FindingSpec("improve", 3, "Major pacing problem", 1));

        using var provider = BuildProvider(out _, byDim);
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 3);

        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        var controller = BuildController(provider);
        var action = await controller.GetBookReviewFindings(bookId, "he", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dto = Assert.IsType<BookReviewFindingsDto>(ok.Value);

        Assert.Equal(bookId, dto.BookId);
        Assert.Equal(3, dto.Findings.Count); // 2 plot + 1 pacing
        Assert.All(dto.Findings, f => Assert.False(string.IsNullOrWhiteSpace(f.Dimension)));

        var scores = dto.Scores.ToDictionary(s => s.Dimension);
        Assert.Equal("strong", scores["plot"].Score);
        Assert.Equal(2, scores["plot"].KeepCount);
        Assert.Equal("weak", scores["pacing"].Score); // a major (sev-3) improve

        // The plot finding's chapter anchor was projected to the DTO with the backfilled chapter id.
        var plotFinding = dto.Findings.First(f => f.Dimension == "plot");
        Assert.NotEmpty(plotFinding.ChapterAnchors);
    }

    // ─── PATCH status: persists open→acknowledged→dismissed→done AND is idempotent ─────────────────

    [Fact]
    public async Task UpdateFindingStatus_PersistsTransitions_AndIsIdempotent()
    {
        using var provider = BuildProvider(out _, FindingsPerDimension(perDimensionCount: 1));
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        var findingId = await db.BookFindings.AsNoTracking()
            .Where(f => f.BookId == bookId).Select(f => f.Id).FirstAsync();

        var controller = BuildController(provider);

        // Starts open.
        Assert.Equal("open", await StatusOf(db, findingId));

        // open → acknowledged (accepts the imperative verb).
        var ack = await controller.UpdateFindingStatus(bookId, findingId, new UpdateFindingStatusRequest("acknowledge"), CancellationToken.None);
        AssertFindingStatus(ack, "acknowledged");
        Assert.Equal("acknowledged", await StatusOf(db, findingId));

        // Idempotent: PATCH the SAME status again → success no-op, still acknowledged.
        var ackAgain = await controller.UpdateFindingStatus(bookId, findingId, new UpdateFindingStatusRequest("acknowledged"), CancellationToken.None);
        AssertFindingStatus(ackAgain, "acknowledged");
        Assert.Equal("acknowledged", await StatusOf(db, findingId));

        // acknowledged → dismissed.
        var dismiss = await controller.UpdateFindingStatus(bookId, findingId, new UpdateFindingStatusRequest("dismiss"), CancellationToken.None);
        AssertFindingStatus(dismiss, "dismissed");
        Assert.Equal("dismissed", await StatusOf(db, findingId));

        // dismissed → done.
        var done = await controller.UpdateFindingStatus(bookId, findingId, new UpdateFindingStatusRequest("done"), CancellationToken.None);
        AssertFindingStatus(done, "done");
        Assert.Equal("done", await StatusOf(db, findingId));

        // done → open (reopen).
        var reopen = await controller.UpdateFindingStatus(bookId, findingId, new UpdateFindingStatusRequest("open"), CancellationToken.None);
        AssertFindingStatus(reopen, "open");
        Assert.Equal("open", await StatusOf(db, findingId));
    }

    [Fact]
    public async Task UpdateFindingStatus_InvalidValue_ReturnsBadRequest()
    {
        using var provider = BuildProvider(out _, FindingsPerDimension(perDimensionCount: 1));
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var svc = provider.GetRequiredService<BookReviewService>();
        await svc.BuildBookReviewAsync(bookId, "he");

        var findingId = await db.BookFindings.AsNoTracking()
            .Where(f => f.BookId == bookId).Select(f => f.Id).FirstAsync();

        var controller = BuildController(provider);

        var bad = await controller.UpdateFindingStatus(bookId, findingId, new UpdateFindingStatusRequest("resolve"), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(bad.Result);

        var empty = await controller.UpdateFindingStatus(bookId, findingId, new UpdateFindingStatusRequest(""), CancellationToken.None);
        Assert.IsType<BadRequestObjectResult>(empty.Result);

        // The finding was not mutated by the rejected requests.
        Assert.Equal("open", await StatusOf(db, findingId));
    }

    [Fact]
    public async Task UpdateFindingStatus_UnknownFinding_ReturnsNotFound()
    {
        using var provider = BuildProvider(out _, FindingsPerDimension(perDimensionCount: 1));
        var db = provider.GetRequiredService<AppDbContext>();
        var bookId = await SeedReviewableBookAsync(db, chapterCount: 2);

        var controller = BuildController(provider);
        var action = await controller.UpdateFindingStatus(bookId, Guid.NewGuid(), new UpdateFindingStatusRequest("done"), CancellationToken.None);
        Assert.IsType<NotFoundResult>(action.Result);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static void AssertFindingStatus(ActionResult<BookFindingDto> action, string expected)
    {
        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var dto = Assert.IsType<BookFindingDto>(ok.Value);
        Assert.Equal(expected, dto.Status);
    }

    private static async Task<string> StatusOf(AppDbContext db, Guid findingId) =>
        await db.BookFindings.AsNoTracking().Where(f => f.Id == findingId).Select(f => f.Status).SingleAsync();

    private static BooksController BuildController(ServiceProvider provider) => new(
        db: provider.GetRequiredService<AppDbContext>(),
        bookIntelligence: null!,
        styleBaseline: null!,
        bookSummary: null!,
        bookReview: provider.GetRequiredService<BookReviewService>(),
        reviewRegistry: provider.GetRequiredService<BookReviewBuildRegistry>(),
        progress: provider.GetRequiredService<AnalysisProgressTracker>(),
        scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
        appLifetime: new TestApplicationLifetime(),
        logger: NullLogger<BooksController>.Instance);

    private sealed class TestApplicationLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    // FindingSpec, FindingsPerDimension, JsonFindings, SeedReviewableBookAsync live in BookReviewTestHelpers
    // (shared with BookReviewServiceTests).

    private sealed class DimensionFindingsHolder
    {
        public Dictionary<string, string> ByDimension = new(StringComparer.Ordinal);
    }

    private static ServiceProvider BuildProvider(
        out Mock<IAiRouter> routerMock,
        Dictionary<string, string> dimensionFindings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddSingleton<SfdtConversionService>();
        services.AddSingleton<PromptFactory>();

        var holder = new DimensionFindingsHolder { ByDimension = dimensionFindings };
        services.AddSingleton(holder);

        routerMock = new Mock<IAiRouter>();
        routerMock
            .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AiRequest req, CancellationToken _) =>
            {
                var instruction = req.Instruction ?? string.Empty;
                var content = string.Empty;
                foreach (var dim in new[] { "plot", "character", "pacing", "tone", "theme", "continuity" })
                {
                    if (instruction.Contains($"\"dimension\": \"{dim}\""))
                    {
                        holder.ByDimension.TryGetValue(dim, out var json);
                        content = json ?? string.Empty;
                        break;
                    }
                }
                return new AiResponse { Content = content, Model = ActiveModel, Provider = "test-provider" };
            });
        services.AddSingleton(routerMock.Object);
        // These controller-surface tests drive the per-dimension mock (keyed on the per-dimension prompt's
        // `"dimension": "{dim}"` token), so pin the per-dimension fan-out. The single-combined default is
        // covered directly in BookReviewServiceTests; here the strategy is irrelevant to what is asserted
        // (dispatch / no-op / status / findings / PATCH), so keep the six-call mock these tests were built on.
        services.Configure<AiOptions>(o => o.BookReviewSingleCombined = false);

        services.AddScoped<ChapterBriefService>();
        services.AddScoped<BookSummaryService>();
        services.AddScoped<BookContextAssembler>();
        services.AddScoped<BookReviewService>();
        services.AddSingleton<AnalysisProgressTracker>();
        services.AddSingleton<BookSummaryBuildRegistry>();
        services.AddSingleton<BookReviewBuildRegistry>();

        return services.BuildServiceProvider();
    }
}
