using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Feedback;
using Xunit;
using static Pagedraft.Api.Tests.FeedbackTestData;

namespace Pagedraft.Api.Tests;

/// <summary>
/// THE TRIAGE HALF of Show C2: the filtered list, the evidence-joining detail, the status transition
/// vocabulary, and the config flag that hides all three.
///
/// <para>EVERY FILTER CASE PROVES ITS POPULATION NON-EMPTY AND PROVES THE FILTER EXCLUDED SOMETHING. A
/// filter test that passes because the table is empty - or because everything in it matches - has bitten
/// this corpus four times, so each case asserts the unfiltered count first and then asserts both what
/// survived and what did not. The flag-off cases are seeded for the same reason: a refusal over an empty
/// table is indistinguishable from "there was nothing to return".</para>
/// </summary>
public class FeedbackTriageTests
{
    // ─── The list and its filters ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_IsNewestFirst_AndCarriesTheTriageProjection()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        var older = SeedFeedback(db, answer, createdAt: Now.AddHours(-2), installationId: "a");
        var newer = SeedFeedback(db, answer, createdAt: Now.AddHours(-1), installationId: "b", text: "A note.");
        await db.SaveChangesAsync();

        var list = await ListAsync(db);

        Assert.Equal(2, list.TotalCount);
        Assert.Equal(new[] { newer.Id, older.Id }, list.Items.Select(i => i.Id));
        Assert.Equal("A note.", list.Items[0].Text);
        Assert.Equal(FeedbackStatuses.New, list.Items[0].Status);
    }

    [Fact]
    public async Task List_FilteredByStatus_KeepsThatStatusAndExcludesTheRest()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        var fresh = SeedFeedback(db, answer, status: FeedbackStatuses.New, installationId: "a");
        SeedFeedback(db, answer, status: FeedbackStatuses.Triaged, installationId: "b");
        SeedFeedback(db, answer, status: FeedbackStatuses.Dismissed, installationId: "c");
        await db.SaveChangesAsync();

        // NON-VACUITY: all three statuses really are present, so the filtered read below excludes two
        // rows rather than querying a table that only ever held one shape.
        Assert.Equal(3, (await ListAsync(db)).TotalCount);

        var filtered = await ListAsync(db, status: FeedbackStatuses.New);

        Assert.Equal(1, filtered.TotalCount);
        Assert.Equal(fresh.Id, Assert.Single(filtered.Items).Id);
    }

    [Fact]
    public async Task List_FilteredByVerdict_KeepsThatVerdictAndExcludesTheRest()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        var down = SeedFeedback(db, answer, verdict: FeedbackVerdicts.Down, installationId: "a");
        SeedFeedback(db, answer, verdict: FeedbackVerdicts.Up, installationId: "b");
        SeedFeedback(db, answer, verdict: FeedbackVerdicts.Up, installationId: "c");
        await db.SaveChangesAsync();

        Assert.Equal(3, (await ListAsync(db)).TotalCount);

        var filtered = await ListAsync(db, verdict: FeedbackVerdicts.Down);

        Assert.Equal(1, filtered.TotalCount);
        Assert.Equal(down.Id, Assert.Single(filtered.Items).Id);
    }

    /// <summary>
    /// C3'S EXACT CONSUMPTION PREDICATE, driven through the same endpoint C3 will call:
    /// <c>Status = New AND Verdict = down</c>. Seeded with a row failing each half separately, so the
    /// combination is proved to narrow rather than either clause carrying the whole result.
    /// </summary>
    [Fact]
    public async Task List_FilteredByStatusAndVerdictTogether_IsC3sPredicate()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        var wanted = SeedFeedback(db, answer, FeedbackVerdicts.Down, FeedbackStatuses.New, installationId: "a");
        SeedFeedback(db, answer, FeedbackVerdicts.Up, FeedbackStatuses.New, installationId: "b");
        SeedFeedback(db, answer, FeedbackVerdicts.Down, FeedbackStatuses.Triaged, installationId: "c");
        await db.SaveChangesAsync();

        Assert.Equal(3, (await ListAsync(db)).TotalCount);
        // Each single filter alone still returns two - so the pair below is genuinely narrowing.
        Assert.Equal(2, (await ListAsync(db, status: FeedbackStatuses.New)).TotalCount);
        Assert.Equal(2, (await ListAsync(db, verdict: FeedbackVerdicts.Down)).TotalCount);

        var c3 = await ListAsync(db, status: FeedbackStatuses.New, verdict: FeedbackVerdicts.Down);

        Assert.Equal(1, c3.TotalCount);
        Assert.Equal(wanted.Id, Assert.Single(c3.Items).Id);
    }

    /// <summary>
    /// The <c>bookId</c> filter resolves through the EVIDENCE JOIN (<c>ConversationMessage.AskBookId</c>)
    /// rather than through a column on the feedback row, which is the same rule as everywhere else here.
    /// The seed therefore holds a row for another book and a row whose target carries NO book at all, and
    /// both are excluded.
    /// </summary>
    [Fact]
    public async Task List_FilteredByBook_ResolvesThroughTheJoin_AndExcludesOtherBooksAndBooklessTargets()
    {
        var bookA = Guid.NewGuid();
        var bookB = Guid.NewGuid();

        await using var db = NewDb();
        var conversation = SeedConversation(db, bookId: bookA);
        var inA = SeedExchange(db, conversation, firstSequence: 0, askBookId: bookA);
        var inB = SeedExchange(db, conversation, firstSequence: 2, askBookId: bookB);
        var appLevel = SeedExchange(db, conversation, firstSequence: 4, askBookId: null);

        var wanted = SeedFeedback(db, inA, installationId: "a");
        SeedFeedback(db, inB, installationId: "b");
        SeedFeedback(db, appLevel, installationId: "c");
        await db.SaveChangesAsync();

        Assert.Equal(3, (await ListAsync(db)).TotalCount);

        var filtered = await ListAsync(db, bookId: bookA);

        Assert.Equal(1, filtered.TotalCount);
        var item = Assert.Single(filtered.Items);
        Assert.Equal(wanted.Id, item.Id);
        // The list's own bookId column is composed by the join too, so the filter and the rendered value
        // cannot disagree.
        Assert.Equal(bookA, item.BookId);

        // And the excluded rows really do carry the other two shapes rather than being absent.
        var all = await ListAsync(db);
        Assert.Contains(all.Items, i => i.BookId == bookB);
        Assert.Contains(all.Items, i => i.BookId == null);
    }

    [Fact]
    public async Task List_FilteredByArea_KeepsThatAreaAndExcludesTheRest()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        var chat = SeedFeedback(db, answer, area: FeedbackAreas.ChatAnswer, installationId: "a");
        // A row from a hypothetical mount #2, written straight to storage: the COLUMN is an open
        // vocabulary even though the write endpoint's allowlist holds one value today, and the filter has
        // to work on whatever the column holds rather than on what C2 happens to write.
        SeedFeedback(db, answer, area: "suggestion-card", installationId: "b");
        await db.SaveChangesAsync();

        Assert.Equal(2, (await ListAsync(db)).TotalCount);

        var filtered = await ListAsync(db, area: FeedbackAreas.ChatAnswer);

        Assert.Equal(1, filtered.TotalCount);
        Assert.Equal(chat.Id, Assert.Single(filtered.Items).Id);
    }

    [Fact]
    public async Task List_Pages_WithoutLosingTheFilteredTotal_AndClampsAnOversizedPageSize()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        for (var i = 0; i < 5; i++)
            SeedFeedback(db, answer, createdAt: Now.AddMinutes(-i), installationId: $"install-{i}");
        await db.SaveChangesAsync();

        var page1 = await ListAsync(db, page: 1, pageSize: 2);
        var page3 = await ListAsync(db, page: 3, pageSize: 2);

        Assert.Equal(5, page1.TotalCount);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(5, page3.TotalCount);
        Assert.Single(page3.Items);
        Assert.Empty(page1.Items.Select(i => i.Id).Intersect(page3.Items.Select(i => i.Id)));

        var clamped = await ListAsync(db, pageSize: FeedbackService.MaxPageSize * 10);
        Assert.Equal(FeedbackService.MaxPageSize, clamped.PageSize);
    }

    // ─── The detail and its evidence join ───────────────────────────────────────────────────────────

    /// <summary>
    /// THE JOIN THAT IS THE POINT OF C2: the question, the answer and the grounding snapshot are composed
    /// LIVE from the target message, and none of them is stored on the feedback row.
    /// </summary>
    [Fact]
    public async Task Detail_ComposesTheQuestionAnswerAndGrounding_FromTheTargetMessage()
    {
        await using var db = NewDb();
        var book = Guid.NewGuid();
        var conversation = SeedConversation(db, "Export questions", book);
        var answer = SeedExchange(
            db, conversation,
            question: "How do I export a single chapter?",
            answer: "Open the chapter menu and choose export.",
            askBookId: book,
            grounding: new ConversationGroundingDto(
                new[] { "export" }, new[] { "chapter-brief:2" }, null, false, "retrieval: ...; answer: ..."));
        var feedback = SeedFeedback(db, answer, text: "It named a menu that does not exist.");
        await db.SaveChangesAsync();

        var detail = await DetailAsync(db, feedback.Id);

        Assert.Equal(feedback.Id, detail.Feedback.Id);
        Assert.True(detail.Evidence.Available);
        Assert.Null(detail.Evidence.UnavailableReason);
        Assert.Equal(conversation, detail.Evidence.ConversationId);
        Assert.Equal("Export questions", detail.Evidence.ConversationTitle);
        Assert.Equal("How do I export a single chapter?", detail.Evidence.Question);
        Assert.Equal("Open the chapter menu and choose export.", detail.Evidence.Answer);
        Assert.False(detail.Evidence.AnswerFailed);
        Assert.Equal(book, detail.Evidence.AskBookId);
        Assert.NotNull(detail.Evidence.Grounding);
        Assert.Equal(new[] { "export" }, detail.Evidence.Grounding!.GuideIds);
        Assert.Equal(new[] { "chapter-brief:2" }, detail.Evidence.Grounding.ArtifactRefs);

        // EVIDENCE IS JOINED, NEVER COPIED: nothing about the answer or the question landed on the row.
        var stored = await db.FeedbackItems.AsNoTracking().SingleAsync();
        Assert.Equal("It named a menu that does not exist.", stored.Text);
        Assert.Null(stored.ContextJson);
    }

    /// <summary>
    /// A FAILED answer that persisted DOES take feedback, and its evidence says so - that population is
    /// half the reason C1 stores fail-safe answers as failed rather than dropping them.
    /// </summary>
    [Fact]
    public async Task Detail_OnAFailedAnswer_ReportsTheFailure_AndCarriesNoGrounding()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation, answer: "I cannot reach your book right now.", failed: true);
        var feedback = SeedFeedback(db, answer);
        await db.SaveChangesAsync();

        var detail = await DetailAsync(db, feedback.Id);

        Assert.True(detail.Evidence.Available);
        Assert.True(detail.Evidence.AnswerFailed);
        // A fail-safe carries no grounding by construction; that is a state, not a parse failure.
        Assert.Null(detail.Evidence.Grounding);
    }

    /// <summary>
    /// An unparseable snapshot costs the triage read nothing, mirroring the transcript read's own posture:
    /// the owner came for the feedback row, and one broken diagnostic blob must not take it away.
    /// </summary>
    [Fact]
    public async Task Detail_WithAnUnparseableSnapshot_StillComposesTheRestOfTheEvidence()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        var feedback = SeedFeedback(db, answer);
        await db.SaveChangesAsync();

        (await db.ConversationMessages.SingleAsync(m => m.Id == answer)).GroundingJson = "{ this is not json";
        await db.SaveChangesAsync();

        var detail = await DetailAsync(db, feedback.Id);

        Assert.True(detail.Evidence.Available);
        Assert.Equal("Open the book menu and choose export.", detail.Evidence.Answer);
        Assert.Null(detail.Evidence.Grounding);
    }

    [Fact]
    public async Task Detail_404sOnAFeedbackIdThatDoesNotExist()
    {
        await using var db = NewDb();
        var result = await NewController(db).Detail(Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(
            FeedbackErrors.FeedbackNotFound,
            ErrorCodeOf(Assert.IsType<NotFoundObjectResult>(result.Result).Value));
    }

    // ─── Status transitions ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(FeedbackStatuses.New, FeedbackStatuses.Triaged)]
    [InlineData(FeedbackStatuses.New, FeedbackStatuses.ConfirmedBug)]
    [InlineData(FeedbackStatuses.New, FeedbackStatuses.Dismissed)]
    [InlineData(FeedbackStatuses.Triaged, FeedbackStatuses.ConfirmedBug)]
    [InlineData(FeedbackStatuses.Triaged, FeedbackStatuses.Dismissed)]
    [InlineData(FeedbackStatuses.ConfirmedBug, FeedbackStatuses.Fixed)]
    [InlineData(FeedbackStatuses.ConfirmedBug, FeedbackStatuses.Dismissed)]
    [InlineData(FeedbackStatuses.Dismissed, FeedbackStatuses.Triaged)]
    [InlineData(FeedbackStatuses.Dismissed, FeedbackStatuses.ConfirmedBug)]
    // A fix that did not hold is the same defect, not a new report.
    [InlineData(FeedbackStatuses.Fixed, FeedbackStatuses.ConfirmedBug)]
    public async Task ChangeStatus_MovesALegalTransition_AndStampsIt(string from, string to)
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        var feedback = SeedFeedback(db, answer, status: from, createdAt: Now.AddDays(-1));
        await db.SaveChangesAsync();

        var dto = await ChangeStatusOkAsync(db, feedback.Id, to);

        Assert.Equal(to, dto.Status);
        Assert.True(dto.StatusChangedAt > dto.CreatedAt);
        Assert.Equal(to, (await db.FeedbackItems.AsNoTracking().SingleAsync()).Status);
    }

    [Theory]
    // Fixed asserts a defect existed and no longer does; reaching it without a confirmation would let a
    // row claim a fix for something nobody said was broken.
    [InlineData(FeedbackStatuses.New, FeedbackStatuses.Fixed)]
    [InlineData(FeedbackStatuses.Triaged, FeedbackStatuses.Fixed)]
    [InlineData(FeedbackStatuses.Dismissed, FeedbackStatuses.Fixed)]
    // NOTHING RETURNS TO New. "New" is not "untouched", it is C3's inbox, and a hand transition back into
    // it would put an already-judged row into the automated re-check queue forever.
    [InlineData(FeedbackStatuses.Triaged, FeedbackStatuses.New)]
    [InlineData(FeedbackStatuses.ConfirmedBug, FeedbackStatuses.New)]
    [InlineData(FeedbackStatuses.Dismissed, FeedbackStatuses.New)]
    [InlineData(FeedbackStatuses.Fixed, FeedbackStatuses.New)]
    [InlineData(FeedbackStatuses.Fixed, FeedbackStatuses.Dismissed)]
    public async Task ChangeStatus_RefusesAnIllegalTransition_AndLeavesTheRowWhereItWas(string from, string to)
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        var feedback = SeedFeedback(db, answer, status: from);
        var stampBefore = feedback.StatusChangedAt;
        await db.SaveChangesAsync();

        var result = await NewController(db).ChangeStatus(
            feedback.Id, new FeedbackStatusRequest(to), CancellationToken.None);

        var body = Assert.IsType<BadRequestObjectResult>(result.Result).Value;
        Assert.Equal(FeedbackErrors.StatusTransitionNotAllowed, ErrorCodeOf(body));
        // The refusal NAMES the move, so a triage UI can say which button was wrong.
        Assert.Equal(from, StatusPropertyOf(body, "from"));
        Assert.Equal(to, StatusPropertyOf(body, "to"));

        var stored = await db.FeedbackItems.AsNoTracking().SingleAsync();
        Assert.Equal(from, stored.Status);
        Assert.Equal(stampBefore, stored.StatusChangedAt);
    }

    /// <summary>
    /// A transition to the status the row already holds is an idempotent no-op - a double-clicked button
    /// is not an event, and re-stamping would make the column lie about when a person last judged the row.
    /// </summary>
    [Fact]
    public async Task ChangeStatus_ToTheStatusItAlreadyHolds_IsANoOp_AndDoesNotRestampIt()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        var feedback = SeedFeedback(db, answer, status: FeedbackStatuses.Triaged, createdAt: Now.AddDays(-1));
        var stampBefore = feedback.StatusChangedAt;
        await db.SaveChangesAsync();

        var dto = await ChangeStatusOkAsync(db, feedback.Id, FeedbackStatuses.Triaged);

        Assert.Equal(FeedbackStatuses.Triaged, dto.Status);
        Assert.Equal(stampBefore, dto.StatusChangedAt);
    }

    [Fact]
    public async Task ChangeStatus_WithAStatusOutsideTheVocabulary_Is400_AndABlankOneToo()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        var feedback = SeedFeedback(db, answer);
        await db.SaveChangesAsync();

        var controller = NewController(db);

        var unknown = await controller.ChangeStatus(
            feedback.Id, new FeedbackStatusRequest("WontFix"), CancellationToken.None);
        Assert.Equal(
            FeedbackErrors.StatusNotRecognized,
            ErrorCodeOf(Assert.IsType<BadRequestObjectResult>(unknown.Result).Value));

        var blank = await controller.ChangeStatus(
            feedback.Id, new FeedbackStatusRequest("  "), CancellationToken.None);
        Assert.Equal(
            FeedbackErrors.StatusRequired,
            ErrorCodeOf(Assert.IsType<BadRequestObjectResult>(blank.Result).Value));

        Assert.Equal(FeedbackStatuses.New, (await db.FeedbackItems.AsNoTracking().SingleAsync()).Status);

        // NON-VACUITY: the same controller and row accept a legal move, so the two refusals above are the
        // vocabulary acting rather than a write path that refuses everything.
        Assert.Equal(FeedbackStatuses.Triaged, (await ChangeStatusOkAsync(db, feedback.Id, FeedbackStatuses.Triaged)).Status);
    }

    [Fact]
    public async Task ChangeStatus_404sOnAFeedbackIdThatDoesNotExist()
    {
        await using var db = NewDb();
        var result = await NewController(db).ChangeStatus(
            Guid.NewGuid(), new FeedbackStatusRequest(FeedbackStatuses.Triaged), CancellationToken.None);
        Assert.Equal(
            FeedbackErrors.FeedbackNotFound,
            ErrorCodeOf(Assert.IsType<NotFoundObjectResult>(result.Result).Value));
    }

    // ─── The config flag ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE FLAG ACTUALLY GATES, AND THE REFUSAL IS PROVED NOT TO BE EMPTINESS. The database is SEEDED with
    /// a row that all three gated reads would happily return - the same suite's other cases show they do -
    /// and each one still refuses. Without the seed, "404" and "there was nothing to return" would be
    /// indistinguishable, which is exactly the vacuity this codebase has been bitten by.
    ///
    /// <para>The refusal is a BODILESS 404 (<c>NotFoundResult</c>, not <c>NotFoundObjectResult</c>): the
    /// flag-off case must look identical to a route that was never registered.</para>
    /// </summary>
    [Fact]
    public async Task WithTheFlagOff_AllThreeTriageReadsRefuse_OverAPopulatedTable()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        var feedback = SeedFeedback(db, answer, text: "Something to hide.");
        await db.SaveChangesAsync();

        // NON-VACUITY, PROVED FIRST: with the flag ON, every one of these three returns real content.
        var open = NewController(db, triageEnabled: true);
        Assert.Equal(1, Assert.IsType<FeedbackListDto>(Assert.IsType<OkObjectResult>(
            (await open.List(null, null, null, null, 1, 25, CancellationToken.None)).Result).Value).TotalCount);
        Assert.IsType<OkObjectResult>((await open.Detail(feedback.Id, CancellationToken.None)).Result);

        var closed = NewController(db, triageEnabled: false);

        Assert.IsType<NotFoundResult>(
            (await closed.List(null, null, null, null, 1, 25, CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>((await closed.Detail(feedback.Id, CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>((await closed.ChangeStatus(
            feedback.Id, new FeedbackStatusRequest(FeedbackStatuses.Triaged), CancellationToken.None)).Result);

        // And the refused transition really did not happen.
        Assert.Equal(FeedbackStatuses.New, (await db.FeedbackItems.AsNoTracking().SingleAsync()).Status);
    }

    /// <summary>
    /// The availability probe is itself UNGATED and reports the flag honestly in both positions - it is
    /// what lets the client decide whether to register the triage route without calling a gated endpoint
    /// and interpreting a bodiless 404 as an answer.
    /// </summary>
    [Fact]
    public async Task Availability_IsUngated_AndReportsTheFlagInBothPositions()
    {
        await using var db = NewDb();

        var open = Assert.IsType<FeedbackAvailabilityDto>(
            Assert.IsType<OkObjectResult>(NewController(db, triageEnabled: true).Availability().Result).Value);
        Assert.True(open.TriageEnabled);

        var closed = Assert.IsType<FeedbackAvailabilityDto>(
            Assert.IsType<OkObjectResult>(NewController(db, triageEnabled: false).Availability().Result).Value);
        Assert.False(closed.TriageEnabled);
    }

    /// <summary>
    /// THE WIDGET KEEPS WORKING WHEN TRIAGE IS HIDDEN (d1 section (4)). Collecting the signal is the
    /// point; reading it is a separate privilege. Retract stays open too - the voter's own action on their
    /// own row is not a triage operation, and a widget that could vote but not un-vote would be a trap.
    /// </summary>
    [Fact]
    public async Task WithTheFlagOff_VotingAndRetractingStillWork()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        await db.SaveChangesAsync();

        var closed = NewController(db, triageEnabled: false);

        var voted = await VoteOkAsync(closed, VoteFor(answer, FeedbackVerdicts.Down, "Still collected."));
        Assert.Single(await db.FeedbackItems.ToListAsync());

        Assert.IsType<NoContentResult>(await closed.Retract(voted.Id, CancellationToken.None));
        Assert.Empty(await db.FeedbackItems.ToListAsync());
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────

    private static async Task<FeedbackListDto> ListAsync(
        Data.AppDbContext db,
        string? area = null,
        string? status = null,
        string? verdict = null,
        Guid? bookId = null,
        int page = 1,
        int pageSize = 25)
    {
        var result = await NewController(db).List(area, status, verdict, bookId, page, pageSize, CancellationToken.None);
        return Assert.IsType<FeedbackListDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    private static async Task<FeedbackDetailDto> DetailAsync(Data.AppDbContext db, Guid id)
    {
        var result = await NewController(db).Detail(id, CancellationToken.None);
        return Assert.IsType<FeedbackDetailDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    private static async Task<FeedbackDto> ChangeStatusOkAsync(Data.AppDbContext db, Guid id, string status)
    {
        var result = await NewController(db).ChangeStatus(
            id, new FeedbackStatusRequest(status), CancellationToken.None);
        return Assert.IsType<FeedbackDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
    }
}
