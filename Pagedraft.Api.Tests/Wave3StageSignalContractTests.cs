using System;
using System.Collections.Generic;
using System.Linq;
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

namespace Pagedraft.Api.Tests;

/// <summary>
/// CONTRACT tests for the signals the Wave 3 stage spine reads (implementation plan, todo w1). The spine
/// renders five stages from one state vocabulary - blocked / not-started / running / behind / ready /
/// unavailable - and its governing rule is that NOTHING may be presented as done unless the app computed it.
/// The retired stepper broke that rule for exactly one reason: the payloads did not carry the facts, so the
/// component invented them. These tests pin the facts onto the wire.
///
/// Three additions are covered here:
///   • M1 - <c>chapterCount</c> / <c>chaptersWithTextCount</c> on <see cref="BookDto"/>, so stage 1 (Import)
///     is computable on the BOOKS LIST, which is the surface where importing is the next action.
///   • The stage-2 `behind` REASON - <c>summaryCoversBuiltChapters</c> on <see cref="BookSummaryStatusDto"/>,
///     the one not-ready input the server computed and then dropped.
///   • M3 - <c>openFindingCount</c> / <c>resolvedFindingCount</c> on <see cref="BookReviewStatusDto"/>, so
///     stage 3's working-through progress renders without downloading the findings ledger.
///
/// Export (stage 5) is covered separately in <see cref="BookExportServiceTests"/>.
///
/// No live model: the router is mocked and no test here triggers a build.
/// </summary>
public class Wave3StageSignalContractTests
{
    /// <summary>The Summarization model the freshness gate resolves to under empty AiOptions (DefaultModel).</summary>
    private const string ActiveModel = "qwen2.5:14b";

    private const string UsableBriefJson = """
        {
          "plotEvents": ["The hero leaves home"],
          "characterStates": [ { "name": "Dana", "state": "fleeing", "emotionalArc": "fear to resolve" } ],
          "thematicMarkers": ["isolation"],
          "toneNotes": "tense",
          "openThreads": ["who sent the letter?"]
        }
        """;

    // ─── M1: the books list can compute stage 1 ───────────────────────────────────────────────────

    [Fact]
    public async Task GetAll_CarriesChapterCounts_SoTheBooksListCanComputeImport()
    {
        using var provider = BuildProvider();
        var db = provider.GetRequiredService<AppDbContext>();

        var emptyBook = new Book { Id = Guid.NewGuid(), Title = "Not imported yet", Language = "he" };
        var importedBook = new Book { Id = Guid.NewGuid(), Title = "Imported", Language = "he" };
        db.Books.AddRange(emptyBook, importedBook);
        db.Chapters.Add(new Chapter { BookId = importedBook.Id, Order = 0, Title = "A", ContentText = "abc", WordCount = 120 });
        db.Chapters.Add(new Chapter { BookId = importedBook.Id, Order = 1, Title = "B", ContentText = "def", WordCount = 340 });
        await db.SaveChangesAsync();

        var controller = BuildController(provider);
        var ok = Assert.IsType<OkObjectResult>((await controller.GetAll(CancellationToken.None)).Result);
        var books = Assert.IsType<List<BookDto>>(ok.Value);

        var empty = books.Single(b => b.Id == emptyBook.Id);
        Assert.Equal(0, empty.ChapterCount);
        Assert.Equal(0, empty.ChaptersWithTextCount);

        var imported = books.Single(b => b.Id == importedBook.Id);
        Assert.Equal(2, imported.ChapterCount);
        Assert.Equal(2, imported.ChaptersWithTextCount);
    }

    [Fact]
    public async Task GetAll_SeparatesChapterRowsFromChaptersThatActuallyHaveText()
    {
        // The two counts are not redundant. A book whose chapters were created empty (hand-added, or an
        // import that produced headings and no bodies) has chapters and no manuscript, and the spine must be
        // able to say so instead of reporting Import ready - the hardcoded-done defect in a new costume.
        using var provider = BuildProvider();
        var db = provider.GetRequiredService<AppDbContext>();

        var book = new Book { Id = Guid.NewGuid(), Title = "Headings only", Language = "he" };
        db.Books.Add(book);
        db.Chapters.Add(new Chapter { BookId = book.Id, Order = 0, Title = "פרק א", WordCount = 0 });
        db.Chapters.Add(new Chapter { BookId = book.Id, Order = 1, Title = "פרק ב", WordCount = 0 });
        db.Chapters.Add(new Chapter { BookId = book.Id, Order = 2, Title = "פרק ג", WordCount = 55 });
        await db.SaveChangesAsync();

        var controller = BuildController(provider);
        var ok = Assert.IsType<OkObjectResult>((await controller.GetAll(CancellationToken.None)).Result);
        var dto = Assert.IsType<List<BookDto>>(ok.Value).Single(b => b.Id == book.Id);

        Assert.Equal(3, dto.ChapterCount);
        Assert.Equal(1, dto.ChaptersWithTextCount);
    }

    [Fact]
    public async Task Create_ReportsAnEmptyBook_NotAnUnknownOne()
    {
        using var provider = BuildProvider();
        var controller = BuildController(provider);

        var created = Assert.IsType<CreatedAtActionResult>(
            (await controller.Create(new CreateBookRequest("חדש", null, "he"), CancellationToken.None)).Result);
        var dto = Assert.IsType<BookDto>(created.Value);

        Assert.Equal(0, dto.ChapterCount);
        Assert.Equal(0, dto.ChaptersWithTextCount);
    }

    [Fact]
    public async Task Update_ReturnsTheRealChapterCounts_NotZero()
    {
        // PUT returns a BookDto, and the books list is refreshed from it. If the update response reported
        // 0 chapters, renaming a book would tell the spine the book had been un-imported.
        using var provider = BuildProvider();
        var db = provider.GetRequiredService<AppDbContext>();

        var book = new Book { Id = Guid.NewGuid(), Title = "Before", Language = "he" };
        db.Books.Add(book);
        db.Chapters.Add(new Chapter { BookId = book.Id, Order = 0, Title = "A", WordCount = 10 });
        db.Chapters.Add(new Chapter { BookId = book.Id, Order = 1, Title = "B", WordCount = 0 });
        await db.SaveChangesAsync();

        var controller = BuildController(provider);
        var ok = Assert.IsType<OkObjectResult>(
            (await controller.Update(book.Id, new CreateBookRequest("After", null, "he"), CancellationToken.None)).Result);
        var dto = Assert.IsType<BookDto>(ok.Value);

        Assert.Equal("After", dto.Title);
        Assert.Equal(2, dto.ChapterCount);
        Assert.Equal(1, dto.ChaptersWithTextCount);
    }

    // ─── Stage 2 `behind`: magnitude AND every reason ─────────────────────────────────────────────

    [Fact]
    public async Task SummaryStatus_PartialRollup_SurfacesTheReasonAStaleCountCannotExplain()
    {
        // The state the spine could not previously describe: every chapter brief is individually fresh
        // (staleCount 0), the model has not changed, and the summary is STILL not ready because the cached
        // rollup was composed over fewer chapters than are fresh now. Without summaryCoversBuiltChapters the
        // client renders `behind` with magnitude 0 and no reason - "out of date, nothing changed".
        using var provider = BuildProvider();
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Partial rollup", Language = "he" });
        db.Chapters.Add(new Chapter { Id = chapterId, BookId = bookId, Order = 0, Title = "A", ContentText = "תוכן.", WordCount = 3 });
        await db.SaveChangesAsync();
        await Task.Delay(10);

        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId,
            ChapterId = chapterId,
            Language = "he",
            StructuredJson = UsableBriefJson,
            BuiltWithModel = ActiveModel,
            StructuredBuiltAt = DateTimeOffset.UtcNow
        });
        db.BookSummaryBaselines.Add(new BookSummaryBaseline
        {
            BookId = bookId,
            Language = "he",
            BookBriefJson = """{ "genre": "Fantasy" }""",
            BuiltWithModel = ActiveModel,
            BuiltChapterCount = 0 // rolled up BEFORE this chapter gained its brief
        });
        await db.SaveChangesAsync();

        var controller = BuildController(provider);
        var ok = Assert.IsType<OkObjectResult>(
            (await controller.GetBookSummaryStatus(bookId, "he", CancellationToken.None)).Result);
        var dto = Assert.IsType<BookSummaryStatusDto>(ok.Value);

        Assert.True(dto.HasSummary);
        Assert.False(dto.Ready);          // → the spine renders `behind`
        Assert.Equal(0, dto.StaleCount);  // magnitude alone says nothing
        Assert.False(dto.BuiltWithDifferentModel);
        Assert.False(dto.SummaryCoversBuiltChapters); // the only true reason, and now it is on the wire
    }

    [Fact]
    public async Task SummaryStatus_CurrentRollup_ReportsCoverageTrueAlongsideReady()
    {
        using var provider = BuildProvider();
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Current rollup", Language = "he" });
        db.Chapters.Add(new Chapter { Id = chapterId, BookId = bookId, Order = 0, Title = "A", ContentText = "תוכן.", WordCount = 3 });
        await db.SaveChangesAsync();
        await Task.Delay(10);

        db.ChunkSummaries.Add(new ChunkSummary
        {
            BookId = bookId,
            ChapterId = chapterId,
            Language = "he",
            StructuredJson = UsableBriefJson,
            BuiltWithModel = ActiveModel,
            StructuredBuiltAt = DateTimeOffset.UtcNow
        });
        db.BookSummaryBaselines.Add(new BookSummaryBaseline
        {
            BookId = bookId,
            Language = "he",
            BookBriefJson = """{ "genre": "Fantasy" }""",
            BuiltWithModel = ActiveModel,
            BuiltChapterCount = 1
        });
        await db.SaveChangesAsync();

        var controller = BuildController(provider);
        var ok = Assert.IsType<OkObjectResult>(
            (await controller.GetBookSummaryStatus(bookId, "he", CancellationToken.None)).Result);
        var dto = Assert.IsType<BookSummaryStatusDto>(ok.Value);

        Assert.True(dto.Ready);
        Assert.True(dto.SummaryCoversBuiltChapters);
    }

    // ─── M3: stage 3 progress without the ledger ──────────────────────────────────────────────────

    [Fact]
    public async Task ReviewStatus_CountsFindingProgress_UsingTheLedgersOwnPartition()
    {
        // The shipped findings ledger groups open + acknowledged as ACTIVE and dismissed + done as RESOLVED.
        // The spine has to count the same way or the two surfaces contradict each other one click apart.
        using var provider = BuildProvider();
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Reviewed", Language = "he" });
        await db.SaveChangesAsync();

        AddFinding(db, bookId, "open", 1);
        AddFinding(db, bookId, "open", 2);
        AddFinding(db, bookId, "acknowledged", 3);
        AddFinding(db, bookId, "dismissed", 4);
        AddFinding(db, bookId, "done", 5);
        await db.SaveChangesAsync();

        var controller = BuildController(provider);
        var ok = Assert.IsType<OkObjectResult>(
            (await controller.GetBookReviewStatus(bookId, "he", CancellationToken.None)).Result);
        var dto = Assert.IsType<BookReviewStatusDto>(ok.Value);

        Assert.Equal(5, dto.FindingCount);
        Assert.Equal(2, dto.OpenFindingCount);      // acknowledged is NOT open
        Assert.Equal(2, dto.ResolvedFindingCount);  // dismissed + done
        // Acknowledged is the third bucket, and the reason open cannot be derived from the other two.
        Assert.Equal(1, dto.FindingCount - dto.OpenFindingCount - dto.ResolvedFindingCount);
    }

    [Fact]
    public async Task ReviewStatus_NoReview_ReportsZeroProgress_NotADoneLookingRollup()
    {
        using var provider = BuildProvider();
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Never reviewed", Language = "he" });
        await db.SaveChangesAsync();

        var controller = BuildController(provider);
        var ok = Assert.IsType<OkObjectResult>(
            (await controller.GetBookReviewStatus(bookId, "he", CancellationToken.None)).Result);
        var dto = Assert.IsType<BookReviewStatusDto>(ok.Value);

        Assert.False(dto.HasReview);
        Assert.False(dto.HasBriefs); // → the spine renders `blocked`, naming the book briefs
        Assert.Equal(0, dto.FindingCount);
        Assert.Equal(0, dto.OpenFindingCount);
        Assert.Equal(0, dto.ResolvedFindingCount);
        Assert.False(dto.Ready);
    }

    [Fact]
    public async Task ReviewStatus_ProgressCountsFollowAStatusPatch()
    {
        // The counts are computed, not stamped: acting on a finding moves them on the very next probe.
        using var provider = BuildProvider();
        var db = provider.GetRequiredService<AppDbContext>();

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "Working through", Language = "he" });
        await db.SaveChangesAsync();

        var finding = AddFinding(db, bookId, "open", 1);
        AddFinding(db, bookId, "open", 2);
        await db.SaveChangesAsync();

        var controller = BuildController(provider);
        await controller.UpdateFindingStatus(bookId, finding.Id, new UpdateFindingStatusRequest("done"), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(
            (await controller.GetBookReviewStatus(bookId, "he", CancellationToken.None)).Result);
        var dto = Assert.IsType<BookReviewStatusDto>(ok.Value);

        Assert.Equal(2, dto.FindingCount);
        Assert.Equal(1, dto.OpenFindingCount);
        Assert.Equal(1, dto.ResolvedFindingCount);
    }

    // ─── The partition helper itself ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("open", true, false)]
    [InlineData("acknowledged", false, false)]
    [InlineData("dismissed", false, true)]
    [InlineData("done", false, true)]
    [InlineData("Open", true, false)]      // stored casing is lowercase, but a read must not depend on it
    [InlineData("", false, false)]
    [InlineData(null, false, false)]
    public void FindingStatusPartition_SplitsTheStatusVocabularyOnce(string? status, bool isOpen, bool isResolved)
    {
        Assert.Equal(isOpen, FindingStatusPartition.IsOpen(status));
        Assert.Equal(isResolved, FindingStatusPartition.IsResolved(status));
    }

    // ─── helpers ──────────────────────────────────────────────────────────────────────────────────

    private static BookFinding AddFinding(AppDbContext db, Guid bookId, string status, int seed)
    {
        var finding = new BookFinding
        {
            Id = Guid.NewGuid(),
            BookId = bookId,
            Language = "he",
            Dimension = "plot",
            Verdict = "improve",
            Severity = 2,
            Rationale = $"finding {seed}",
            EvidenceJson = "[]",
            ChapterAnchorsJson = "[]",
            Status = status,
            DedupKey = BookFinding.ComputeDedupKey("plot", seed, $"finding {seed}"),
            BuiltWithModel = ActiveModel,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.BookFindings.Add(finding);
        return finding;
    }

    private static BooksController BuildController(ServiceProvider provider) => new(
        db: provider.GetRequiredService<AppDbContext>(),
        bookIntelligence: null!,
        styleBaseline: null!,
        bookSummary: provider.GetRequiredService<BookSummaryService>(),
        bookReview: provider.GetRequiredService<BookReviewService>(),
        chapterBrief: null!,
        progress: provider.GetRequiredService<AnalysisProgressTracker>(),
        aiTierStatus: null!,
        scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
        appLifetime: new TestApplicationLifetime(),
        logger: NullLogger<BooksController>.Instance);

    /// <summary>
    /// Status-only wiring: a real in-memory DB plus the two status services. The router is mocked and
    /// returns nothing, because no test in this file builds anything - every assertion is about a READ.
    /// </summary>
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(opt => opt.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddSingleton<SfdtConversionService>();
        services.AddSingleton<PromptFactory>();

        var router = new Mock<IAiRouter>();
        router
            .Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AiResponse { Content = string.Empty, Model = ActiveModel, Provider = "test-provider" });
        services.AddSingleton(router.Object);
        services.Configure<AiOptions>(_ => { });

        services.AddScoped<ChapterBriefService>();
        services.AddScoped<BookSummaryService>();
        services.AddScoped<BookContextAssembler>();
        services.AddScoped<BookReviewService>();
        services.AddScoped<DynamicTermRepairService>();
        services.AddSingleton<IBookEntityProvider, BookEntityProvider>();
        services.AddSingleton<AnalysisProgressTracker>();
        services.AddSingleton<BookSummaryBuildRegistry>();
        services.AddSingleton<BookReviewBuildRegistry>();

        return services.BuildServiceProvider();
    }

    private sealed class TestApplicationLifetime : Microsoft.Extensions.Hosting.IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
