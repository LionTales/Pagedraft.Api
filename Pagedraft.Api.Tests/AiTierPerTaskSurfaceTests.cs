using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Analysis;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// THE PER-TASK ENDPOINTS (tier-ux-rework c1): GET reports every user-facing task, PUT writes either one
/// task or the book default, DELETE clears one task back to inheritance.
///
/// In the same environment collection as the rest of the tier surface, because the readiness checks these
/// exercise consult the process-wide <c>AI_{PROVIDER}_APIKEY</c> variables.
///
/// Class named *AiTier* so the standing deterministic filter picks it up.
/// </summary>
[Collection(AiTierEnvironmentCollection.Name)]
public class AiTierPerTaskEndpointTests
{
    private const string Local = "Ollama";

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"aitier-pertask-endpoint-{Guid.NewGuid()}").Options);

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
            scopeFactory: scopeFactory.Object,
            appLifetime: lifetime.Object,
            logger: NullLogger<BooksController>.Instance);
    }

    private static BooksController Controller(AppDbContext db) =>
        NewController(db, AiTierStatusServiceTests.Service(AiTierStatusServiceTests.ShippedShape()));

    private static async Task<Guid> SeedBookAsync(AppDbContext db, string language, string? storedTier = null)
    {
        var book = new Book { Title = "T", Language = language, AiTier = storedTier };
        db.Books.Add(book);
        await db.SaveChangesAsync();
        return book.Id;
    }

    private static BookAiTierDto Ok(ActionResult<BookAiTierDto> result) =>
        Assert.IsType<BookAiTierDto>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private static BookAiTierTaskDto TaskOf(BookAiTierDto dto, AiTaskType task) =>
        dto.Tasks.Single(t => t.Task == task.ToString());

    /// <summary>The read model covers exactly the tasks the surface offers - never more, never fewer, and in
    /// a stable order so the toggles do not reshuffle between requests.</summary>
    [Fact]
    public async Task TheReadModel_CoversExactlyTheUserFacingTasks()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, "he");

        var dto = Ok(await Controller(db).GetAiTier(bookId, CancellationToken.None));

        Assert.Equal(
            AiTierPolicy.UserFacingTasks.Select(t => t.ToString()).ToList(),
            dto.Tasks.Select(t => t.Task).ToList());
    }

    /// <summary>
    /// THE ROUND TRIP. One task opts in; it reads back as stored AND effective, and every other task is
    /// untouched with a NULL stored tier - null being "inherits", which is what makes a later default change
    /// reach it.
    /// </summary>
    [Fact]
    public async Task SettingOneTask_StoresAndReadsBackThatTaskOnly()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, "he");
        var controller = Controller(db);

        var put = Ok(await controller.UpdateAiTier(
            bookId, new UpdateBookAiTierRequest("thinking", nameof(AiTaskType.Proofread)), CancellationToken.None));

        Assert.Equal("thinking", TaskOf(put, AiTaskType.Proofread).StoredTier);
        Assert.Equal("thinking", TaskOf(put, AiTaskType.Proofread).EffectiveTier);
        Assert.Null(TaskOf(put, AiTaskType.LinguisticAnalysis).StoredTier);
        Assert.Equal("fast", TaskOf(put, AiTaskType.LinguisticAnalysis).EffectiveTier);
        // The BOOK default is untouched by a per-task write.
        Assert.Equal("fast", put.Tier);

        // ... and it survives a fresh read rather than only living in the response.
        var get = Ok(await controller.GetAiTier(bookId, CancellationToken.None));
        Assert.Equal("thinking", TaskOf(get, AiTaskType.Proofread).StoredTier);
        Assert.Equal("fast", TaskOf(get, AiTaskType.LinguisticAnalysis).EffectiveTier);
    }

    /// <summary>
    /// THE DECIDED SEMANTICS, TESTED RATHER THAN ASSUMED: setting the BOOK DEFAULT leaves explicit per-task
    /// overrides alone. The task that said "fast" keeps saying fast while the tasks that never expressed a
    /// preference follow the new default. The opposite behaviour (a default write that clears overrides) would
    /// silently discard a deliberate choice from a control the user was not touching, with no undo.
    /// </summary>
    [Fact]
    public async Task SettingTheBookDefault_DoesNotClobberPerTaskOverrides()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, "he");
        var controller = Controller(db);

        await controller.UpdateAiTier(
            bookId, new UpdateBookAiTierRequest("fast", nameof(AiTaskType.Proofread)), CancellationToken.None);

        var dto = Ok(await controller.UpdateAiTier(
            bookId, new UpdateBookAiTierRequest("thinking"), CancellationToken.None));

        Assert.Equal("thinking", dto.Tier);
        Assert.Equal("fast", TaskOf(dto, AiTaskType.Proofread).StoredTier);
        Assert.Equal("fast", TaskOf(dto, AiTaskType.Proofread).EffectiveTier);
        // The task with no override of its own DOES follow the new default - otherwise the default is inert.
        Assert.Null(TaskOf(dto, AiTaskType.LinguisticAnalysis).StoredTier);
        Assert.Equal("thinking", TaskOf(dto, AiTaskType.LinguisticAnalysis).EffectiveTier);

        // And the run-time lookup agrees with what the surface just claimed, per task.
        Assert.Equal(AiTier.Fast, await BookAiTierResolver.ResolveAsync(
            db, bookId, AiTaskType.Proofread, NullLogger.Instance, CancellationToken.None));
        Assert.Equal(AiTier.Thinking, await BookAiTierResolver.ResolveAsync(
            db, bookId, AiTaskType.LinguisticAnalysis, NullLogger.Instance, CancellationToken.None));
    }

    /// <summary>Clearing is the separate, explicit verb, and it restores INHERITANCE rather than writing
    /// "fast" - the difference shows the moment the default moves again.</summary>
    [Fact]
    public async Task ClearingATask_RestoresInheritanceOfTheBookDefault()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, "he", storedTier: "thinking");
        var controller = Controller(db);

        await controller.UpdateAiTier(
            bookId, new UpdateBookAiTierRequest("fast", nameof(AiTaskType.Proofread)), CancellationToken.None);
        Assert.Equal("fast", TaskOf(Ok(await controller.GetAiTier(bookId, CancellationToken.None)), AiTaskType.Proofread).EffectiveTier);

        var cleared = Ok(await controller.ClearAiTierTask(bookId, nameof(AiTaskType.Proofread), CancellationToken.None));

        Assert.Null(TaskOf(cleared, AiTaskType.Proofread).StoredTier);
        Assert.Equal("thinking", TaskOf(cleared, AiTaskType.Proofread).EffectiveTier);
        Assert.Empty(await db.BookAiTaskTiers.Where(t => t.BookId == bookId).ToListAsync());
    }

    /// <summary>Clearing an override that is not there is the caller's desired end state already, so it is a
    /// 200 with the unchanged read model rather than a 404.</summary>
    [Fact]
    public async Task ClearingATaskWithNoOverride_IsANoOp()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, "he");

        var dto = Ok(await Controller(db).ClearAiTierTask(bookId, nameof(AiTaskType.LineEdit), CancellationToken.None));
        Assert.Null(TaskOf(dto, AiTaskType.LineEdit).StoredTier);
    }

    /// <summary>
    /// THE 409, PER TASK. A task the allowlist does not let the tier move cannot be opted in: accepting it
    /// would store an intent that provably cannot run, which is the silent lie the endpoint exists to close.
    /// The book default is untouched by the rejection.
    /// </summary>
    [Theory]
    [InlineData(nameof(AiTaskType.LineEdit))]
    [InlineData(nameof(AiTaskType.BookReview))]
    public async Task SettingThinking_ForANonAllowlistedTask_Is409(string task)
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, "he");

        var result = await Controller(db).UpdateAiTier(
            bookId, new UpdateBookAiTierRequest("thinking", task), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Contains("thinkingTierUnavailable", System.Text.Json.JsonSerializer.Serialize(conflict.Value));
        Assert.Null((await db.Books.FindAsync(bookId))!.AiTier);
        Assert.Empty(await db.BookAiTaskTiers.ToListAsync());
    }

    /// <summary>
    /// THE Proofread_en NO-GO, ENFORCED ON THE WRITE PATH. An ENGLISH book cannot opt Proofread into the
    /// thinking tier, because the <c>Proofread_en</c> language rung outranks the tier rung and the route
    /// provably would not move. LinguisticAnalysis on the SAME book is accepted, because it does move - which
    /// is what makes this a per-task answer rather than a blanket "English cannot think".
    /// </summary>
    [Fact]
    public async Task AnEnglishBook_CannotOptProofreadIntoThinking_ButCanOptLinguisticAnalysisIn()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, "en");
        var controller = Controller(db);

        var refused = await controller.UpdateAiTier(
            bookId, new UpdateBookAiTierRequest("thinking", nameof(AiTaskType.Proofread)), CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(refused.Result);

        var accepted = Ok(await controller.UpdateAiTier(
            bookId, new UpdateBookAiTierRequest("thinking", nameof(AiTaskType.LinguisticAnalysis)), CancellationToken.None));
        Assert.Equal("thinking", TaskOf(accepted, AiTaskType.LinguisticAnalysis).StoredTier);
        Assert.Null(TaskOf(accepted, AiTaskType.Proofread).StoredTier);
    }

    /// <summary>
    /// The per-task readiness is reported on the READ model too, so the toggle can render
    /// disabled-with-reason instead of discovering the 409 by trying.
    ///
    /// TIER-UX-REWORK C2 SPLIT THE REASON. c1 answered "routeNotConfigured" for both an English Proofread and
    /// a non-allowlisted task, which is true about ROUTING and useless as COPY: "this deployment has not
    /// configured the tier" and "English proofreading always stays fast" are different sentences with
    /// different fixes, and the client renders one of them to a user. This pins that they are now different
    /// tokens - and that the ordinary configured case is still plain "ready".
    /// </summary>
    [Fact]
    public async Task TheReadModel_ReportsWhyATaskCannotThink()
    {
        using var db = NewDb();
        var english = await SeedBookAsync(db, "en");
        var hebrew = await SeedBookAsync(db, "he");
        var controller = Controller(db);

        var en = Ok(await controller.GetAiTier(english, CancellationToken.None));
        Assert.Equal("languageAlwaysFast", TaskOf(en, AiTaskType.Proofread).ThinkingReadiness);
        Assert.Equal("ready", TaskOf(en, AiTaskType.LinguisticAnalysis).ThinkingReadiness);

        var he = Ok(await controller.GetAiTier(hebrew, CancellationToken.None));
        Assert.Equal("ready", TaskOf(he, AiTaskType.Proofread).ThinkingReadiness);
        Assert.Equal("taskNotEligible", TaskOf(he, AiTaskType.LineEdit).ThinkingReadiness);
        Assert.Equal("taskNotEligible", TaskOf(he, AiTaskType.BookReview).ThinkingReadiness);
    }

    /// <summary>
    /// THE THIRD REASON, KEPT DISTINCT FROM THE OTHER TWO (c2). A HEBREW Proofread on a deployment whose tier
    /// keys have been deleted - the documented kill-switch - still reads "routeNotConfigured": the token that
    /// means an operator changed something and an operator can change it back. Without this fact the split
    /// above could pass by renaming every not-ready answer, which would trade one collapsed reason for
    /// another.
    /// </summary>
    [Fact]
    public async Task AKillSwitchedDeployment_StillReportsRouteNotConfigured_NotTheDesignReasons()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, "he");
        var killSwitched = NewController(db, AiTierStatusServiceTests.Service(AiTierStatusServiceTests.Options(
            ("LinguisticAnalysis", Local, "local-linguistic"),
            ("Proofread", Local, "local-proofread"))));

        var dto = Ok(await killSwitched.GetAiTier(bookId, CancellationToken.None));

        Assert.Equal("routeNotConfigured", TaskOf(dto, AiTaskType.Proofread).ThinkingReadiness);
        Assert.Equal("routeNotConfigured", TaskOf(dto, AiTaskType.LinguisticAnalysis).ThinkingReadiness);
        // ... and the allowlist reason still outranks it, because it holds even after the keys come back.
        Assert.Equal("taskNotEligible", TaskOf(dto, AiTaskType.LineEdit).ThinkingReadiness);
    }

    /// <summary>
    /// A PER-TASK OPT-IN MOVES EXACTLY ONE TASK. Everything on a fast Hebrew book reads fast; opting ONE task
    /// into thinking moves that task's answer and leaves every sibling on the same book alone.
    ///
    /// This asserted the <c>processingLocation</c> token until be-c03 removed that field (declared on two DTOs
    /// and in the design doc as the fact the consent copy could not be written without, read by nothing, and
    /// unable to ground that copy anyway - it described the CURRENT tier, and consent asks about the one being
    /// moved to). The behaviour it was pinning is unchanged and is now pinned through <c>effectiveTier</c>,
    /// which since be-c01 answers "did this task's route actually move" directly and is the value the toggle
    /// binds to, so the oracle got closer to what a user sees rather than weaker.
    /// </summary>
    [Fact]
    public async Task OptingOneTaskIn_MovesThatTaskOnly_AndLeavesItsSiblingsFast()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, "he");
        var controller = Controller(db);

        var fast = Ok(await controller.GetAiTier(bookId, CancellationToken.None));
        Assert.All(fast.Tasks, t => Assert.Equal("fast", t.EffectiveTier));
        Assert.All(fast.Tasks, t => Assert.False(t.FallbackActive));

        var moved = Ok(await controller.UpdateAiTier(
            bookId, new UpdateBookAiTierRequest("thinking", nameof(AiTaskType.Proofread)), CancellationToken.None));

        Assert.Equal("thinking", TaskOf(moved, AiTaskType.Proofread).EffectiveTier);
        Assert.False(TaskOf(moved, AiTaskType.Proofread).FallbackActive);
        Assert.Equal("fast", TaskOf(moved, AiTaskType.LinguisticAnalysis).EffectiveTier);
    }

    /// <summary>
    /// THE ENGLISH PROOFREAD ANSWER, END TO END ON THE READ MODEL (c2). The language rung outranks the tier
    /// rung, so even with the book default on thinking this task never moves off the fast route - and the
    /// payload says exactly that, in tokens, without naming the model that keeps it there.
    ///
    /// BE-C01 MOVED TWO OF THESE ASSERTIONS and they are the P0 itself, so the change is stated rather than
    /// quietly applied. This task used to report <c>effectiveTier: "thinking"</c> (the storage resolver's
    /// answer, passed through unclamped) AND <c>fallbackActive: true</c> beside a reason line saying it always
    /// runs fast - the toggle rendered all three at once and highlighted the wrong word. It now reports the
    /// tier that will actually route, and warns about nothing, because the book default is not a setting this
    /// task could ever honour and its readiness token is the whole explanation. The visible-fallback
    /// guarantee has NOT been weakened: the variant where the user really did opt this task in is asserted
    /// immediately below, and the killed-route variants live in <c>AiTierEffectiveTierContractTests</c>.
    /// </summary>
    [Fact]
    public async Task AnEnglishBooksProofread_StaysLocal_EvenWithTheBookDefaultOnThinking()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, "en", storedTier: "thinking");

        var dto = Ok(await Controller(db).GetAiTier(bookId, CancellationToken.None));

        var proofread = TaskOf(dto, AiTaskType.Proofread);
        Assert.Equal("languageAlwaysFast", proofread.ThinkingReadiness);
        Assert.Equal("fast", proofread.EffectiveTier);  // be-c01: what will RUN, not what the book default says
        Assert.False(proofread.FallbackActive);         // ... and nothing on this control claims otherwise
        // Not vacuous: the other allowlisted task on the SAME book does move.
        Assert.Equal("thinking", TaskOf(dto, AiTaskType.LinguisticAnalysis).EffectiveTier);
        Assert.False(TaskOf(dto, AiTaskType.LinguisticAnalysis).FallbackActive);
    }

    /// <summary>
    /// An unrecognised TASK token is a 400 and moves nothing. The dangerous alternative is treating it as
    /// absent, which would silently rewrite the BOOK DEFAULT because a task name was misspelled.
    /// </summary>
    [Theory]
    [InlineData("Proofreed")]
    [InlineData("1")]
    public async Task AnUnrecognisedTask_Is400_AndMovesNothing(string task)
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, "he");

        var result = await Controller(db).UpdateAiTier(
            bookId, new UpdateBookAiTierRequest("thinking", task), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Null((await db.Books.FindAsync(bookId))!.AiTier);
        Assert.Empty(await db.BookAiTaskTiers.ToListAsync());
    }

    /// <summary>A user-facing analysis type is accepted and normalized onto the routing task it maps to, so a
    /// client that speaks in edit types cannot create a row no run will ever read.</summary>
    [Fact]
    public async Task AnAnalysisTypeName_IsNormalizedOntoItsRoutingTask()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, "he");

        var dto = Ok(await Controller(db).UpdateAiTier(
            bookId, new UpdateBookAiTierRequest("thinking", nameof(AnalysisType.LiteraryAnalysis)), CancellationToken.None));

        Assert.Equal("thinking", TaskOf(dto, AiTaskType.LinguisticAnalysis).EffectiveTier);
        var row = Assert.Single(await db.BookAiTaskTiers.ToListAsync());
        Assert.Equal(nameof(AiTaskType.LinguisticAnalysis), row.TaskKey);
    }

    /// <summary>Repeating a per-task write updates the ONE row rather than accumulating a second one - the
    /// composite key is the identity, and a duplicate would make "which is it" a coin flip.</summary>
    [Fact]
    public async Task WritingTheSameTaskTwice_UpdatesOneRow()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, "he");
        var controller = Controller(db);

        await controller.UpdateAiTier(bookId, new UpdateBookAiTierRequest("thinking", "Proofread"), CancellationToken.None);
        await controller.UpdateAiTier(bookId, new UpdateBookAiTierRequest("fast", "Proofread"), CancellationToken.None);

        var row = Assert.Single(await db.BookAiTaskTiers.ToListAsync());
        Assert.Equal("fast", row.Tier);
    }

    /// <summary>A missing book is a 404 on the clear verb too, rather than a 200 describing nothing.</summary>
    [Fact]
    public async Task ClearingOnAMissingBook_Is404()
    {
        using var db = NewDb();
        Assert.IsType<NotFoundResult>(
            (await Controller(db).ClearAiTierTask(Guid.NewGuid(), "Proofread", CancellationToken.None)).Result);
    }

    /// <summary>
    /// A book stored as thinking whose deployment cannot route it still reads back as VISIBLY falling back,
    /// now with per-task tiers in the mix. Pinned because the fallback flag's definition had to change
    /// (per-task tiers mean "the stored tier" is no longer one value) and the visible-degradation guarantee
    /// must survive that change - and be-c01 changed the definition a SECOND time (effectiveTier is now the
    /// tier that will actually route, so it reads "fast" here), which is precisely why the guarantee is
    /// re-asserted rather than assumed.
    /// </summary>
    [Fact]
    public async Task AKillSwitchedDeployment_StillReportsAVisibleFallback_ForAPerTaskOptIn()
    {
        using var db = NewDb();
        var bookId = await SeedBookAsync(db, "he");
        db.BookAiTaskTiers.Add(new BookAiTaskTier
        {
            BookId = bookId, TaskKey = nameof(AiTaskType.Proofread), Tier = "thinking"
        });
        await db.SaveChangesAsync();

        var killSwitched = NewController(db, AiTierStatusServiceTests.Service(AiTierStatusServiceTests.Options(
            ("LinguisticAnalysis", Local, "local-linguistic"),
            ("Proofread", Local, "local-proofread"))));

        var dto = Ok(await killSwitched.GetAiTier(bookId, CancellationToken.None));

        Assert.Equal("fast", dto.Tier);                                             // the book default never moved
        Assert.Equal("thinking", TaskOf(dto, AiTaskType.Proofread).StoredTier);     // ... but this task opted in
        Assert.Equal("fast", TaskOf(dto, AiTaskType.Proofread).EffectiveTier);      // ... and is NOT running it
        // The BOOK-LEVEL flag stays FALSE (final-r02): it is a claim about the book DEFAULT, which is fast and
        // is being honoured, and the book-default toggle is the only control that renders it. The visible
        // fallback for this opt-in is the per-task flag below - the row that actually carries the "thinking".
        Assert.False(dto.FallbackActive);
        // c2: the per-task form of the same fact, which is what the toggle renders now that the route list is
        // gone from the wire - this task is set to thinking, it is not running it, and it says so.
        Assert.True(TaskOf(dto, AiTaskType.Proofread).FallbackActive);
        Assert.Equal("routeNotConfigured", TaskOf(dto, AiTaskType.Proofread).ThinkingReadiness);
    }
}

/// <summary>
/// STALENESS IS KEYED ON THE TASK THAT BUILT THE PROFILES (tier-ux-rework c1) - the invariant the resolver's
/// doc comment names as the reason it is shared code at all.
///
/// The two freshness consumers (<c>AnalysisContextService</c> for <c>ChapterStyleProfile.BuiltWithModel</c>,
/// <c>StyleBaselineService</c> for <c>BookStyleBaseline.BuiltWithModel</c>) both ask about
/// <see cref="AiTaskType.LinguisticAnalysis"/>, because that is the model those stamps name. Before per-task
/// storage the tier was one value per book, so ANY tier change invalidated everything; now a flip on an
/// unrelated task must leave these profiles alone, or every proofread setting change costs a book-wide
/// rebuild - one extra LLM call per chapter, for nothing.
///
/// Class named *AiTier* so the standing deterministic filter picks it up.
/// </summary>
public class AiTierPerTaskStalenessKeyingTests
{
    private static async Task SetTaskTierAsync(AppDbContext db, Guid bookId, AiTaskType task, string tier)
    {
        var key = AiTierPolicy.TaskKeyFor(task);
        var existing = await db.BookAiTaskTiers.FirstOrDefaultAsync(t => t.BookId == bookId && t.TaskKey == key);
        if (existing == null)
            db.BookAiTaskTiers.Add(new BookAiTaskTier { BookId = bookId, TaskKey = key, Tier = tier });
        else
            existing.Tier = tier;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// THE ASSERTION THE PLAN ASKS FOR: flipping PROOFREAD to thinking does not invalidate anything the
    /// LINGUISTICANALYSIS tier built. Then the same flip ON LinguisticAnalysis does - which is what stops this
    /// from passing because the gate broke rather than because the keying works.
    /// </summary>
    [Fact]
    public async Task AFlipOnOneTask_DoesNotInvalidateAnotherTasksProfiles()
    {
        using var provider = AiTierTestHarness.Build(out _, out _);
        var db = provider.GetRequiredService<AppDbContext>();
        var svc = provider.GetRequiredService<StyleBaselineService>();

        var (bookId, _) = await AiTierTestHarness.SeedBookAsync(db, "Keying Book", storedTier: null, chapterCount: 2);
        Assert.True((await svc.BuildBookStyleBaselineAsync(bookId, AiTierTestHarness.Lang)).Ready);

        // 1. An unrelated task moves to the paid tier. The LinguisticAnalysis profiles are untouched.
        await SetTaskTierAsync(db, bookId, AiTaskType.Proofread, "thinking");

        var afterProofreadFlip = await svc.GetStatusAsync(bookId, AiTierTestHarness.Lang);
        Assert.Equal(AiTierTestHarness.LocalLinguisticModel, afterProofreadFlip.ActiveModel);
        Assert.Equal(0, afterProofreadFlip.StaleCount);
        Assert.False(afterProofreadFlip.BuiltWithDifferentModel);

        // 2. NOT VACUOUS: the same flip on the task these profiles were BUILT under does invalidate them.
        await SetTaskTierAsync(db, bookId, AiTaskType.LinguisticAnalysis, "thinking");

        var afterLinguisticFlip = await svc.GetStatusAsync(bookId, AiTierTestHarness.Lang);
        Assert.Equal(AiTierTestHarness.CloudLinguisticModel, afterLinguisticFlip.ActiveModel);
        Assert.True(afterLinguisticFlip.BuiltWithDifferentModel);
        Assert.True(afterLinguisticFlip.StaleCount > 0);
    }

    /// <summary>
    /// The SAME keying through the book default, so the two rungs cannot drift apart: a LinguisticAnalysis
    /// override of "fast" pins these profiles fresh even after the book default moves to thinking. Without
    /// per-task precedence in the shared lookup this reads stale, which is the freshness gate firing on a tier
    /// this task never ran at.
    /// </summary>
    [Fact]
    public async Task ATaskOverrideOfFast_KeepsProfilesFresh_AcrossABookDefaultFlip()
    {
        using var provider = AiTierTestHarness.Build(out _, out _);
        var db = provider.GetRequiredService<AppDbContext>();
        var svc = provider.GetRequiredService<StyleBaselineService>();

        var (bookId, _) = await AiTierTestHarness.SeedBookAsync(db, "Pinned Book", storedTier: null, chapterCount: 2);
        Assert.True((await svc.BuildBookStyleBaselineAsync(bookId, AiTierTestHarness.Lang)).Ready);

        await SetTaskTierAsync(db, bookId, AiTaskType.LinguisticAnalysis, "fast");
        await AiTierTestHarness.SetTierAsync(db, bookId, "thinking");

        var status = await svc.GetStatusAsync(bookId, AiTierTestHarness.Lang);
        Assert.Equal(AiTierTestHarness.LocalLinguisticModel, status.ActiveModel);
        Assert.Equal(0, status.StaleCount);
        Assert.False(status.BuiltWithDifferentModel);
    }
}

/// <summary>
/// THE PRECEDENCE THIS TODO MUST NOT HAVE TOUCHED (tier-ux-rework c1). Per-task storage changes WHERE the
/// tier comes from and nothing about how it RANKS, so the four-rung order - and specifically
/// <c>{task}_{lang}</c> outranking <c>{task}_{tier}</c>, which IS the p2-4 <c>Proofread_en</c> NO-GO - is
/// re-asserted here THROUGH the new storage rather than only through a hand-passed tier.
///
/// Class named *AiRouter* so the standing deterministic filter picks it up.
/// </summary>
public class AiRouterPerTaskTierRegressionTests
{
    /// <summary>
    /// THE REGRESSION THE PLAN ASKS FOR, end to end: a per-task override of "thinking" stored DIRECTLY on an
    /// English book's Proofread - bypassing the endpoint's 409, exactly as a legacy row, a hand edit, or a
    /// later language change would - resolves as Thinking (the storage is honest) and STILL routes to the
    /// local English model, because the language rung outranks the tier rung. The per-task override is not a
    /// second way into the cloud for English proofreading.
    /// </summary>
    [Theory]
    [InlineData("en")]
    [InlineData("en-US")]
    [InlineData("EN-GB")]
    public async Task AnEnglishProofreadOverrideOfThinking_StillRoutesLocal(string language)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"aitier-pertask-regression-{Guid.NewGuid()}").Options;
        using var db = new AppDbContext(options);

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "T", Language = language, AiTier = "fast" });
        db.BookAiTaskTiers.Add(new BookAiTaskTier
        {
            BookId = bookId, TaskKey = nameof(AiTaskType.Proofread), Tier = "thinking"
        });
        await db.SaveChangesAsync();

        var tier = await BookAiTierResolver.ResolveAsync(
            db, bookId, AiTaskType.Proofread, NullLogger.Instance, CancellationToken.None);
        Assert.Equal(AiTier.Thinking, tier);

        var opt = ProviderTuningConfigParityTests.LoadShippedAiOptions();
        var selection = AiRouter.ResolveSelectionForTest(
            new AiRequest { InputText = "x", TaskType = AiTaskType.Proofread, Language = language, Tier = tier }, opt);

        Assert.Equal("Ollama", selection.Provider);
        Assert.Equal(
            LinguisticModelResolver.ResolveForTask(opt, AiTaskType.Proofread, language, AiTier.Fast),
            (selection.Provider, selection.Model));
    }

    /// <summary>
    /// The symmetric statement, so the test above cannot pass because the tier stopped working: the SAME
    /// override on a HEBREW book does reach the cloud route.
    /// </summary>
    [Fact]
    public async Task TheSameOverrideOnAHebrewBook_DoesReachTheCloudRoute()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"aitier-pertask-regression-he-{Guid.NewGuid()}").Options;
        using var db = new AppDbContext(options);

        var bookId = Guid.NewGuid();
        db.Books.Add(new Book { Id = bookId, Title = "T", Language = "he", AiTier = "fast" });
        db.BookAiTaskTiers.Add(new BookAiTaskTier
        {
            BookId = bookId, TaskKey = nameof(AiTaskType.Proofread), Tier = "thinking"
        });
        await db.SaveChangesAsync();

        var tier = await BookAiTierResolver.ResolveAsync(
            db, bookId, AiTaskType.Proofread, NullLogger.Instance, CancellationToken.None);

        var opt = ProviderTuningConfigParityTests.LoadShippedAiOptions();
        var selection = AiRouter.ResolveSelectionForTest(
            new AiRequest { InputText = "x", TaskType = AiTaskType.Proofread, Language = "he", Tier = tier }, opt);

        Assert.Equal("OpenRouter", selection.Provider);
    }

    /// <summary>
    /// A non-allowlisted task with a per-task override stored anyway (again: legacy row / hand edit) resolves
    /// as Thinking and routes IDENTICALLY to Fast, because <c>AiTierPolicy.TieredTasks</c> is still the only
    /// thing that decides whether the tier rung exists. Per-task storage is not a back door into the
    /// allowlist.
    /// </summary>
    [Fact]
    public void ANonAllowlistedTask_RoutesIdentically_EvenWithAThinkingOverride()
    {
        var opt = ProviderTuningConfigParityTests.LoadShippedAiOptions();

        foreach (var task in Enum.GetValues<AiTaskType>().Where(t => !AiTierPolicy.IsTiered(t)))
        foreach (var language in new[] { "he", "en" })
            Assert.Equal(
                LinguisticModelResolver.ResolveForTask(opt, task, language, AiTier.Fast),
                LinguisticModelResolver.ResolveForTask(opt, task, language, AiTier.Thinking));
    }
}
