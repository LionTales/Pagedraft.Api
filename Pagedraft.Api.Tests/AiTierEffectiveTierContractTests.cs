using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// THE `effectiveTier` CONTRACT (be-c01): the field means THE TIER THAT WILL ACTUALLY ROUTE, not the storage
/// resolver's answer.
///
/// WHY THIS FILE EXISTS. `BuildAiTierDtoAsync` filled it straight from `BookAiTierResolver`, which knows the
/// three STORAGE rungs (per-task override, book default, fast) and nothing about task eligibility or the
/// language rung, and `DescribeTask` passed that through unclamped. One `PUT {"tier":"thinking"}` therefore
/// made BookReview, LineEdit and an English book's Proofread all report `effectiveTier: "thinking"` - tasks
/// whose own write path answers 409 - and the toggle highlighted "thinking" beside a reason line reading
/// "this task always runs on the fast tier" and a warning reading "set to thinking, actually running fast".
/// Three contradictory statements in one control, with no way for the user to clear it (`storedTier` is null,
/// so the follow-the-book-default link is not even offered).
///
/// BOTH HALVES ARE PINNED HERE, because fixing one alone is a regression:
///   • the CLAMP - the three cases above now read "fast" (and are not vacuous: the task that DOES move on the
///     same book still reads "thinking");
///   • the FALLBACK SIGNAL SURVIVING THE CLAMP - `fallbackActive` is derived from the PRE-clamp resolved tier,
///     so "you asked for thinking and it is not moving" is still reachable. Derived from the clamped value it
///     would be permanently false, which trades a loud lie for a silent one.
///
/// Class named *AiTier* so the standing deterministic filter picks it up.
/// </summary>
[Collection(AiTierEnvironmentCollection.Name)]
public class AiTierEffectiveTierContractTests
{
    private const string Local = "Ollama";

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"aitier-effective-contract-{Guid.NewGuid()}").Options);

    private static BooksController NewController(AppDbContext db, AiTierStatusService tierStatus)
    {
        var scopeFactory = new Mock<IServiceScopeFactory>();
        var lifetime = new Mock<IHostApplicationLifetime>();
        lifetime.Setup(l => l.ApplicationStopping).Returns(CancellationToken.None);
        return new BooksController(
            db,
            bookIntelligence: null!,
            styleBaseline: null!,
            bookSummary: null!,
            bookReview: null!,
            chapterBrief: null!,
            progress: null!,
            aiTierStatus: tierStatus,
            profileBuilds: null!,
            scopeFactory: scopeFactory.Object,
            appLifetime: lifetime.Object,
            logger: NullLogger<BooksController>.Instance);
    }

    /// <summary>A deployment with both tier keys wired - the shipped shape, so these tests measure the CLAMP
    /// rather than a config that happens to have no thinking route at all.</summary>
    private static BooksController Controller(AppDbContext db) =>
        NewController(db, AiTierStatusServiceTests.Service(AiTierStatusServiceTests.ShippedShape()));

    /// <summary>The documented kill switch: the two <c>{task}_thinking</c> keys deleted.</summary>
    private static BooksController KillSwitchedController(AppDbContext db) =>
        NewController(db, AiTierStatusServiceTests.Service(AiTierStatusServiceTests.Options(
            ("LinguisticAnalysis", Local, "local-linguistic"),
            ("Proofread", Local, "local-proofread"),
            ("Proofread_en", Local, "local-proofread-en"))));

    private static async Task<Guid> SeedBookAsync(
        AppDbContext db, string language, string? bookDefault, (AiTaskType Task, string Tier)? overrideRow = null)
    {
        var book = new Book { Title = "T", Language = language, AiTier = bookDefault };
        db.Books.Add(book);
        if (overrideRow is { } row)
            db.BookAiTaskTiers.Add(new BookAiTaskTier
            {
                BookId = book.Id, TaskKey = AiTierPolicy.TaskKeyFor(row.Task), Tier = row.Tier
            });
        await db.SaveChangesAsync();
        return book.Id;
    }

    private static BookAiTierDto Ok(ActionResult<BookAiTierDto> result) =>
        Assert.IsType<BookAiTierDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static BookAiTierTaskDto TaskOf(BookAiTierDto dto, AiTaskType task) =>
        dto.Tasks.Single(t => t.Task == task.ToString());

    // ── The clamp ────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE MEASURED P0, CASE 1 AND 2. LineEdit and BookReview are outside <c>AiTierPolicy.TieredTasks</c>, so
    /// no tier rung exists for them at all and the book default cannot reach them. They must read "fast" -
    /// and they must not carry the fallback warning either, because nothing on their control ever claimed
    /// thinking: the readiness reason ("this task always runs on the fast tier") is the whole story there.
    /// </summary>
    [Theory]
    [InlineData(nameof(AiTaskType.LineEdit))]
    [InlineData(nameof(AiTaskType.BookReview))]
    public async Task ANonAllowlistedTask_ReadsFast_EvenWithTheBookDefaultOnThinking(string taskName)
    {
        var task = Enum.Parse<AiTaskType>(taskName);
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, "he", bookDefault: "thinking");

        var dto = Ok(await Controller(db).GetAiTier(bookId, CancellationToken.None));

        var reported = TaskOf(dto, task);
        Assert.Equal("fast", reported.EffectiveTier);
        Assert.Equal("taskNotEligible", reported.ThinkingReadiness);
        Assert.False(reported.FallbackActive);
        Assert.Null(reported.StoredTier);
        // The book default itself is untouched by the clamp - it is a book-level seed, and it really is
        // thinking. Only the per-task answer is clamped.
        Assert.Equal("thinking", dto.Tier);
        // NOT VACUOUS: a task on the SAME book that the tier can move still reads thinking.
        Assert.Equal("thinking", TaskOf(dto, AiTaskType.LinguisticAnalysis).EffectiveTier);
    }

    /// <summary>
    /// THE MEASURED P0, CASE 3. An English book's Proofread resolves <c>Proofread_en</c> because the
    /// <c>{task}_{lang}</c> rung outranks the tier rung (layer E3, the p2-4 NO-GO), so the book default cannot
    /// reach it either. Same shape as above and for a different reason, which is why the readiness token
    /// differs while the tier does not.
    /// </summary>
    [Fact]
    public async Task AnEnglishBooksProofread_ReadsFast_EvenWithTheBookDefaultOnThinking()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, "en", bookDefault: "thinking");

        var dto = Ok(await Controller(db).GetAiTier(bookId, CancellationToken.None));

        var proofread = TaskOf(dto, AiTaskType.Proofread);
        Assert.Equal("fast", proofread.EffectiveTier);
        Assert.Equal("languageAlwaysFast", proofread.ThinkingReadiness);
        Assert.False(proofread.FallbackActive);
        Assert.Null(proofread.StoredTier);
        // NOT VACUOUS, twice over: the SAME task on a HEBREW book does read thinking off the same default...
        using var hebrewDb = NewDb();
        var hebrewBookId = await SeedBookAsync(hebrewDb, "he", bookDefault: "thinking");
        Assert.Equal(
            "thinking",
            TaskOf(Ok(await Controller(hebrewDb).GetAiTier(hebrewBookId, CancellationToken.None)), AiTaskType.Proofread)
                .EffectiveTier);
        // ... and the other allowlisted task on THIS English book does too, so the clamp is per task, not
        // "English cannot think".
        Assert.Equal("thinking", TaskOf(dto, AiTaskType.LinguisticAnalysis).EffectiveTier);
    }

    /// <summary>
    /// The clamp is not a blanket "never report thinking": a task that genuinely moves reports it, ready and
    /// with no fallback warning. Without this the two tests above would pass against a service that hardcoded
    /// "fast".
    /// </summary>
    [Fact]
    public async Task ATaskWhoseRouteActuallyMoves_StillReadsThinking()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, "he", bookDefault: null,
            overrideRow: (AiTaskType.Proofread, "thinking"));

        var dto = Ok(await Controller(db).GetAiTier(bookId, CancellationToken.None));

        var proofread = TaskOf(dto, AiTaskType.Proofread);
        Assert.Equal("thinking", proofread.StoredTier);
        Assert.Equal("thinking", proofread.EffectiveTier);
        Assert.Equal("ready", proofread.ThinkingReadiness);
        Assert.False(proofread.FallbackActive);
    }

    // ── The fallback signal surviving the clamp ──────────────────────────────────────────────────────────

    /// <summary>
    /// THE TEST THAT STOPS THE CLAMP FROM REGRESSING (be-c01 step 2). A HEBREW Proofread with an explicit
    /// stored "thinking" - a legitimate opt-in the write path accepts - on a deployment whose
    /// <c>{task}_thinking</c> keys an operator has since removed. The clamp makes <c>effectiveTier</c> read
    /// "fast", which is the truth about the run, and the user must STILL be told that the setting they made is
    /// not being honoured. If `fallbackActive` is ever derived from the clamped value instead of the resolved
    /// one, this is the assertion that fails.
    /// </summary>
    [Fact]
    public async Task AStoredThinkingWhoseRouteWasRemoved_StillReportsTheFallback()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, "he", bookDefault: null,
            overrideRow: (AiTaskType.Proofread, "thinking"));

        var dto = Ok(await KillSwitchedController(db).GetAiTier(bookId, CancellationToken.None));

        var proofread = TaskOf(dto, AiTaskType.Proofread);
        Assert.Equal("thinking", proofread.StoredTier);          // what was asked for is still on the wire
        Assert.Equal("fast", proofread.EffectiveTier);            // ... and what will run is the truth
        Assert.True(proofread.FallbackActive);                    // ... and the gap between them is SAID
        Assert.Equal("routeNotConfigured", proofread.ThinkingReadiness);
        // The BOOK-LEVEL flag is a claim about the BOOK DEFAULT, which here is fast and is being honoured
        // (final-r02). It used to read true off this per-task opt-in, and the book-default toggle - the only
        // control that renders it - then said "this is set to thinking, but it is running fast" next to a
        // highlighted "Fast" pill. The signal is not lost: it is on the row above, where the stored "thinking"
        // that justifies the sentence actually lives.
        Assert.Equal("fast", dto.Tier);
        Assert.False(dto.FallbackActive);
    }

    /// <summary>
    /// The same signal through INHERITANCE rather than an override: the book default asks for thinking, the
    /// task is one the tier could move (Hebrew Proofread), and the operator's kill switch stops it. Nobody
    /// stored anything on this task, so the suppression that silences the two design verdicts must NOT reach
    /// this case - the deployment took the tier away, the user's request was real.
    /// </summary>
    [Fact]
    public async Task AnInheritedThinkingOnAKilledRoute_StillReportsTheFallback()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, "he", bookDefault: "thinking");

        var dto = Ok(await KillSwitchedController(db).GetAiTier(bookId, CancellationToken.None));

        var proofread = TaskOf(dto, AiTaskType.Proofread);
        Assert.Null(proofread.StoredTier);
        Assert.Equal("fast", proofread.EffectiveTier);
        Assert.True(proofread.FallbackActive);
        Assert.Equal("routeNotConfigured", proofread.ThinkingReadiness);
        // And HERE the book-level flag IS true, because here the BOOK DEFAULT is the thing not being honoured.
        // Asserted beside the false case in AStoredThinkingWhoseRouteWasRemoved so final-r02's clause cannot
        // be "fixed" by making the book-level flag unreachable.
        Assert.Equal("thinking", dto.Tier);
        Assert.True(dto.FallbackActive);
    }

    /// <summary>
    /// THE DORMANT OPT-IN. A Hebrew book's Proofread is opted into thinking (accepted, routes cloud), and the
    /// book's LANGUAGE is later changed to English. The language rung now outranks the tier rung, so the task
    /// runs fast - but unlike the inherited case, this user really did ask for this task, so the warning stays
    /// on. It is also the only thing on screen that explains why the follow-the-book-default link is offered.
    /// </summary>
    [Fact]
    public async Task AnOptInLeftDormantByALanguageChange_StillReportsTheFallback()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, "en", bookDefault: null,
            overrideRow: (AiTaskType.Proofread, "thinking"));

        var dto = Ok(await Controller(db).GetAiTier(bookId, CancellationToken.None));

        var proofread = TaskOf(dto, AiTaskType.Proofread);
        Assert.Equal("thinking", proofread.StoredTier);
        Assert.Equal("fast", proofread.EffectiveTier);
        Assert.True(proofread.FallbackActive);
        Assert.Equal("languageAlwaysFast", proofread.ThinkingReadiness);
        // ... and this is exactly the pair the no-override case must NOT produce - same book language, same
        // readiness, no stored choice, no warning. Asserted side by side so the two can never collapse.
        using var inheritedDb = NewDb();
        var inheritedId = await SeedBookAsync(inheritedDb, "en", bookDefault: "thinking");
        var inherited = TaskOf(
            Ok(await Controller(inheritedDb).GetAiTier(inheritedId, CancellationToken.None)), AiTaskType.Proofread);
        Assert.Equal("languageAlwaysFast", inherited.ThinkingReadiness);
        Assert.Equal("fast", inherited.EffectiveTier);
        Assert.False(inherited.FallbackActive);
    }

    // ── The rendered contradiction, as one assertion ─────────────────────────────────────────────────────

    /// <summary>
    /// THE P0 STATED AS AN INVARIANT OVER THE WHOLE PAYLOAD, because the defect was not one bad field but
    /// three fields contradicting each other in one control. Every user-facing task, both languages, book
    /// default on thinking. This is the assertion that would have caught the original bug on any of the four
    /// rows at once rather than one at a time.
    ///
    /// SCOPE, STATED PRECISELY (final-r04) - the clamp is "does this task's route MOVE", not "is the tier
    /// healthy". The three verdicts that mean it cannot move (<c>taskNotEligible</c>,
    /// <c>languageAlwaysFast</c>, <c>routeNotConfigured</c>) all force <c>effectiveTier: "fast"</c>. The two
    /// that mean it moves to a route that would then FAIL (<c>providerNotRegistered</c>,
    /// <c>providerCredentialsMissing</c>) deliberately keep <c>"thinking"</c>: the run really would go there,
    /// and the readiness line beside the pill is what says it would fail. Those two are unreachable under the
    /// shipped-shape config this test runs against, so the blanket <c>!= "ready"</c> form below is the
    /// STRONGER assertion here; it is not a claim that the clamp keys on readiness in general.
    /// </summary>
    [Theory]
    [InlineData("he")]
    [InlineData("en")]
    public async Task NoTaskEverReportsThinkingWhileItsOwnReadinessRefusesIt(string language)
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, language, bookDefault: "thinking");

        var dto = Ok(await Controller(db).GetAiTier(bookId, CancellationToken.None));

        Assert.NotEmpty(dto.Tasks);
        foreach (var reported in dto.Tasks)
        {
            if (reported.ThinkingReadiness != "ready")
                Assert.Equal("fast", reported.EffectiveTier);
            // And the toggle's two sentences can never contradict each other either: a task whose reason line
            // says it ALWAYS runs fast has nothing to fall back from unless it stored the choice itself.
            if (reported.ThinkingReadiness is "taskNotEligible" or "languageAlwaysFast" && reported.StoredTier is null)
                Assert.False(reported.FallbackActive, $"{reported.Task} warns about a setting it does not have");
        }
        // NOT VACUOUS: at least one task on this book is genuinely not-ready, and at least one IS ready and
        // reads thinking, so the loop is exercising both branches.
        Assert.Contains(dto.Tasks, t => t.ThinkingReadiness != "ready");
        Assert.Contains(dto.Tasks, t => t.ThinkingReadiness == "ready" && t.EffectiveTier == "thinking");
    }
}
