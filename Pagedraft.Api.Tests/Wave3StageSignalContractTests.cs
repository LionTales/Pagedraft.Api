using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        // PUT returns a BookDto, and ChapterCount/ChaptersWithTextCount are part of that typed contract - a
        // caller is entitled to treat them as real. If the update response reported 0 chapters, renaming a
        // book would be a typed lie that the book had been un-imported, even though no current client caller
        // re-renders from this response today.
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

    [Fact]
    public async Task Update_CommitsTheRename_EvenWhenTheRequestIsCancelledDuringTheCountsReread()
    {
        // Finding 28: the post-commit counts re-query used to run on the REQUEST token. A client abort (or a
        // DB blip) landing in the window between SaveChangesAsync committing the rename and that re-query
        // returning would throw AFTER the write already persisted - the caller sees a failed rename that in
        // fact went through, and the UI and DB disagree until a reload. Reproduce that exact window with an
        // interceptor that cancels the token the instant SaveChangesAsync finishes committing, then assert
        // the rename still comes back 200 with the real, persisted counts instead of throwing.
        var cts = new CancellationTokenSource();
        var interceptor = new CancelTokenAfterSaveInterceptor(cts);
        using var provider = BuildProvider(interceptor);
        var db = provider.GetRequiredService<AppDbContext>();

        var book = new Book { Id = Guid.NewGuid(), Title = "Before", Language = "he" };
        db.Books.Add(book);
        db.Chapters.Add(new Chapter { BookId = book.Id, Order = 0, Title = "A", WordCount = 10 });
        await db.SaveChangesAsync(); // seed write: the interceptor is not armed yet, so this does not cancel cts.

        var controller = BuildController(provider);
        interceptor.Armed = true; // only the rename's own SaveChangesAsync should trip the cancellation.

        var result = await controller.Update(book.Id, new CreateBookRequest("After", null, "he"), cts.Token);

        Assert.True(cts.IsCancellationRequested); // sanity: the race window was actually exercised.
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<BookDto>(ok.Value);
        Assert.Equal("After", dto.Title);
        Assert.Equal(1, dto.ChapterCount);
        Assert.Equal(1, dto.ChaptersWithTextCount);

        // The rename is not just in the response - it is really committed, independent of the cancelled token.
        var persisted = await db.Books.AsNoTracking().SingleAsync(b => b.Id == book.Id, CancellationToken.None);
        Assert.Equal("After", persisted.Title);
    }

    /// <summary>
    /// Cancels a caller-supplied <see cref="CancellationTokenSource"/> the instant a SaveChangesAsync call
    /// commits, so a test can reproduce "the request token was cancelled in the window right after a write
    /// landed" deterministically. Only fires while <see cref="Armed"/> is true, so seed-data writes done
    /// before a test arms it are unaffected.
    /// </summary>
    private sealed class CancelTokenAfterSaveInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        private readonly CancellationTokenSource _cts;
        public bool Armed;

        public CancelTokenAfterSaveInterceptor(CancellationTokenSource cts) => _cts = cts;

        public override ValueTask<int> SavedChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (Armed) _cts.Cancel();
            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }
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

    // ─── The MECHANICAL completeness oracle (be-c05) ──────────────────────────────────────────────
    //
    // The theory above states the vocabulary by hand on BOTH sides, so a FIFTH status member added to
    // FindingStatusPartition would land in neither bucket and every assertion here would stay green. These
    // tests DISCOVER the vocabulary instead - by reflecting over the declared string constants - so the
    // member itself is the input and nobody has to remember to add a row. Adding a const without
    // classifying it, without listing it in `All`, or without accepting it at the PATCH endpoint is RED.

    /// <summary>Reflects the declared vocabulary. This is the DISCOVERED side of every oracle below: it must
    /// never be replaced with a hand-written list, or the oracles become a restatement of the thing they check.</summary>
    private static IReadOnlyList<string> DeclaredStatusMembers() =>
        typeof(FindingStatusPartition)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

    [Fact]
    public void FindingStatusPartition_ClassifiesEveryMemberOfItsOwnVocabulary()
    {
        var members = DeclaredStatusMembers();

        // Non-vacuity floor: a reflection query that silently returns nothing would pass every loop below.
        Assert.NotEmpty(members);
        Assert.Equal(members.Count, members.Distinct(StringComparer.Ordinal).Count());

        foreach (var member in members)
        {
            Assert.True(
                FindingStatusPartition.BucketOf(member) != FindingStatusBucket.Unknown,
                $"Status member '{member}' is declared by FindingStatusPartition but BucketOf does not " +
                "classify it, so it counts as neither open nor resolved on the spine's progress line AND is " +
                "treated as user-acted by the rebuild reconciler. Give it a bucket in BucketOf.");
        }
    }

    [Fact]
    public void FindingStatusPartition_AllListsExactlyTheDeclaredMembers()
    {
        var declared = DeclaredStatusMembers();
        Assert.NotEmpty(declared);

        Assert.True(
            declared.OrderBy(s => s, StringComparer.Ordinal)
                .SequenceEqual(FindingStatusPartition.All.OrderBy(s => s, StringComparer.Ordinal)),
            "FindingStatusPartition.All has drifted from the declared constants. Declared: [" +
            string.Join(", ", declared.OrderBy(s => s, StringComparer.Ordinal)) + "]; All: [" +
            string.Join(", ", FindingStatusPartition.All.OrderBy(s => s, StringComparer.Ordinal)) + "].");
    }

    [Fact]
    public void FindingStatusPartition_EveryBucketIsReachableFromTheVocabulary()
    {
        var reached = DeclaredStatusMembers().Select(FindingStatusPartition.BucketOf).Distinct().ToList();
        Assert.NotEmpty(reached);

        foreach (FindingStatusBucket bucket in Enum.GetValues(typeof(FindingStatusBucket)))
        {
            if (bucket == FindingStatusBucket.Unknown) continue; // Unknown is by definition NOT a member's bucket
            Assert.True(
                reached.Contains(bucket),
                $"No declared status member classifies as {bucket}, so that bucket is dead vocabulary: either " +
                "a member was removed without removing the bucket, or the bucket was added without a member.");
        }
    }

    [Fact]
    public void FindingStatusPartition_AcceptsEveryMemberAtTheWriteEndpoint()
    {
        var members = DeclaredStatusMembers();
        Assert.NotEmpty(members);

        foreach (var member in members)
        {
            Assert.True(
                FindingStatusPartition.TryParse(member, out var parsed),
                $"Status member '{member}' is declared but PATCH .../review/findings/{{id}}/status rejects it, " +
                "so nothing can ever write it. Add it to AcceptedByInput.");
            Assert.Equal(member, parsed);
            Assert.Contains(member, FindingStatusPartition.AcceptedInputs);
        }
    }

    [Fact]
    public void FindingStatusPartition_AppliesOneCasingPolicyToEveryMember()
    {
        // The policy is stated at FindingStatusPartition: TRIMMED and CASE-INSENSITIVE, everywhere. Pinned
        // mechanically over the discovered vocabulary so it cannot hold for `open` and lapse for a later member,
        // which is precisely how IsOpen (OrdinalIgnoreCase) and the reconciler (Ordinal) came to disagree.
        var members = DeclaredStatusMembers();
        Assert.NotEmpty(members);

        foreach (var member in members)
        {
            var expected = FindingStatusPartition.BucketOf(member);
            foreach (var variant in new[] { member.ToUpperInvariant(), "  " + member + " \t" })
            {
                Assert.Equal(expected, FindingStatusPartition.BucketOf(variant));
                Assert.Equal(FindingStatusPartition.IsOpen(member), FindingStatusPartition.IsOpen(variant));
                Assert.Equal(FindingStatusPartition.IsResolved(member), FindingStatusPartition.IsResolved(variant));
                Assert.Equal(FindingStatusPartition.IsUserActed(member), FindingStatusPartition.IsUserActed(variant));
                Assert.True(
                    FindingStatusPartition.TryParse(variant, out var parsed) && parsed == member,
                    $"TryParse does not apply the trimmed/case-insensitive policy to '{member}': the variant " +
                    $"'{variant}' did not map back to it.");
            }
        }
    }

    [Fact]
    public void FindingStatusPartition_TreatsAnUnknownMemberAsUserActedButNotResolved()
    {
        // The fail-CLOSED half of the policy, and the reason IsUserActed is NOT !IsResolved: a value this build
        // does not know must never be deleted as regenerated noise, and must never be counted as progress.
        const string future = "snoozed";
        Assert.DoesNotContain(future, DeclaredStatusMembers()); // premise: not (yet) a real member

        Assert.Equal(FindingStatusBucket.Unknown, FindingStatusPartition.BucketOf(future));
        Assert.False(FindingStatusPartition.IsOpen(future));
        Assert.False(FindingStatusPartition.IsResolved(future));
        Assert.True(FindingStatusPartition.IsUserActed(future));
        Assert.False(FindingStatusPartition.TryParse(future, out _));
    }

    [Fact]
    public void FindingStatusPartition_IsUserActedIsTheExactComplementOfIsOpen()
    {
        // The single question the reconciler, the vanished-open delete and the near-duplicate fence all ask.
        foreach (var value in DeclaredStatusMembers().Concat(new[] { "snoozed", "", "   " }))
            Assert.Equal(!FindingStatusPartition.IsOpen(value), FindingStatusPartition.IsUserActed(value));

        Assert.True(FindingStatusPartition.IsUserActed(null)); // null is unknown, never open
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
        profileBuilds: null!,
        scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
        appLifetime: new TestApplicationLifetime(),
        logger: NullLogger<BooksController>.Instance);

    /// <summary>
    /// Status-only wiring: a real in-memory DB plus the two status services. The router is mocked and
    /// returns nothing, because no test in this file builds anything - every assertion is about a READ.
    /// </summary>
    private static ServiceProvider BuildProvider(
        Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor? interceptor = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(opt =>
        {
            opt.UseInMemoryDatabase(Guid.NewGuid().ToString());
            if (interceptor != null) opt.AddInterceptors(interceptor);
        });
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
