using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Feedback;
using Xunit;
using static Pagedraft.Api.Tests.FeedbackTestData;

namespace Pagedraft.Api.Tests;

/// <summary>
/// WHAT HAPPENS TO A VOTE WHEN THE THING IT POINTS AT IS DELETED (Show C2, d1 section (3)). The decision is
/// KEEP THE ROW AND TOMBSTONE THE TARGET, because the signal outlives the transcript: C3 still wants to
/// know a down-vote existed even when the conversation that produced it is gone.
///
/// <para>This is the pairing that would be easiest to get wrong quietly. A cascade would have deleted the
/// feedback (the plan's decision reversed, invisibly); a missing stamp would leave rows pointing at nothing
/// with the triage detail unable to say why; and a stamp written in a SECOND save could commit the
/// conversation delete and then fail. Each of those is a case below.</para>
/// </summary>
public class FeedbackTargetLifecycleTests
{
    [Fact]
    public async Task DeletingAConversation_KeepsItsFeedback_AndStampsTheTombstone()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        var feedback = SeedFeedback(db, answer, FeedbackVerdicts.Down, text: "The answer was wrong.");
        await db.SaveChangesAsync();

        // NON-VACUITY: there really is a vote attached to a real transcript before the delete.
        Assert.Single(await db.FeedbackItems.ToListAsync());
        Assert.Null((await db.FeedbackItems.AsNoTracking().SingleAsync()).TargetDeletedAt);
        Assert.Equal(2, await db.ConversationMessages.CountAsync());

        Assert.IsType<NoContentResult>(
            await NewConversationsController(db).Delete(conversation, CancellationToken.None));

        // The transcript is gone...
        Assert.Empty(await db.ConversationMessages.ToListAsync());
        Assert.Empty(await db.Conversations.ToListAsync());

        // ...and the SIGNAL is not. That is the decision, and a cascade would have silently reversed it.
        var kept = Assert.Single(await db.FeedbackItems.AsNoTracking().ToListAsync());
        Assert.Equal(feedback.Id, kept.Id);
        Assert.Equal(FeedbackVerdicts.Down, kept.Verdict);
        Assert.Equal("The answer was wrong.", kept.Text);
        Assert.NotNull(kept.TargetDeletedAt);
    }

    /// <summary>
    /// The stamp is SCOPED to the deleted conversation's own turns. A tombstone on somebody else's live
    /// feedback would tell the triage owner a target had been deleted when it is still readable, which is
    /// worse than no tombstone at all.
    /// </summary>
    [Fact]
    public async Task DeletingOneConversation_DoesNotTombstoneAnothersFeedback()
    {
        await using var db = NewDb();
        var doomed = SeedConversation(db, "Doomed");
        var kept = SeedConversation(db, "Kept");
        var doomedAnswer = SeedExchange(db, doomed);
        var keptAnswer = SeedExchange(db, kept);
        var doomedFeedback = SeedFeedback(db, doomedAnswer, installationId: "a");
        var keptFeedback = SeedFeedback(db, keptAnswer, installationId: "b");
        await db.SaveChangesAsync();

        // NON-VACUITY: two conversations, two votes, so "left the other alone" is a claim.
        Assert.Equal(2, await db.FeedbackItems.CountAsync());

        await NewConversationsController(db).Delete(doomed, CancellationToken.None);

        var rows = await db.FeedbackItems.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.NotNull(rows.Single(f => f.Id == doomedFeedback.Id).TargetDeletedAt);
        Assert.Null(rows.Single(f => f.Id == keptFeedback.Id).TargetDeletedAt);
    }

    /// <summary>
    /// THE SECOND ARRIVAL PATH. Deleting a BOOK takes every turn of every conversation in it - both FKs
    /// cascade (<c>Conversation.BookId</c> and <c>ConversationMessage.ConversationId</c>, and the
    /// <c>AddShowConversationHistory</c> migration's own ON DELETE CASCADE on each) - so the invariant has
    /// to hold on this path too. Before be-c01 the rows survived UNSTAMPED and the triage detail answered
    /// <c>targetMissing</c>, the reason reserved for a row that left without going through a delete path:
    /// an ordinary book delete rendered the owner's own defect indicator.
    ///
    /// <para>WHICH CODE PATH THIS EXERCISES, stated because the provider makes it ambiguous otherwise: the
    /// controller runs on its OWN context, opened over the same in-memory store as the seed. That tracker
    /// has never seen these conversations or turns, and the in-memory provider only cascades what it has
    /// tracked, so nothing but <c>BooksController.Delete</c> can remove them here. The turns go because it
    /// removes them, and the stamp lands because it stamps.</para>
    /// </summary>
    [Fact]
    public async Task DeletingABook_KeepsItsFeedback_StampsTheTombstone_AndTheEvidenceSaysDeletedNotMissing()
    {
        var store = $"book-delete-{Guid.NewGuid()}";
        Guid book, feedbackId;

        await using (var seed = NewDb(store))
        {
            book = SeedBook(seed);
            var conversation = SeedConversation(seed, "About this book", book);
            var answer = SeedExchange(seed, conversation, askBookId: book);
            feedbackId = SeedFeedback(seed, answer, FeedbackVerdicts.Down, text: "The answer was wrong.").Id;
            await seed.SaveChangesAsync();
        }

        // NON-VACUITY: a real vote on a real transcript whose evidence really composes, before the delete.
        await using (var before = NewDb(store))
        {
            Assert.Equal(2, await before.ConversationMessages.CountAsync());
            var detail = await DetailOf(before, feedbackId);
            Assert.True(detail.Evidence.Available);
            Assert.Equal("Open the book menu and choose export.", detail.Evidence.Answer);
            Assert.Null(detail.Feedback.TargetDeletedAt);
        }

        await using (var deleting = NewDb(store))
        {
            Assert.IsType<NoContentResult>(
                await NewBooksController(deleting).Delete(book, CancellationToken.None));
        }

        await using (var after = NewDb(store))
        {
            // The transcript went with the book...
            Assert.Empty(await after.Books.ToListAsync());
            Assert.Empty(await after.Conversations.ToListAsync());
            Assert.Empty(await after.ConversationMessages.ToListAsync());

            // ...and the signal did not, and it carries the stamp.
            var kept = Assert.Single(await after.FeedbackItems.AsNoTracking().ToListAsync());
            Assert.Equal(feedbackId, kept.Id);
            Assert.Equal(FeedbackVerdicts.Down, kept.Verdict);
            Assert.Equal("The answer was wrong.", kept.Text);
            Assert.NotNull(kept.TargetDeletedAt);

            // The reason is the whole point: DELETED, the ordinary consequence of a decision - not MISSING,
            // which means a row vanished outside any delete path and is a defect worth seeing.
            var detail = await DetailOf(after, feedbackId);
            Assert.False(detail.Evidence.Available);
            Assert.Equal(FeedbackEvidenceComposer.ReasonTargetDeleted, detail.Evidence.UnavailableReason);
        }
    }

    /// <summary>
    /// The book delete's stamp is SCOPED to that book's own conversations. Two exclusions here, and a fix
    /// that stamped indiscriminately would fail both: another book's live feedback, and the feedback on a
    /// BOOKLESS conversation - <c>Conversation.BookId</c> is nullable because app-level product Q&amp;A
    /// belongs to no book, and no book delete may take it.
    /// </summary>
    [Fact]
    public async Task DeletingOneBook_LeavesAnotherBooksFeedbackAndABooklessConversationsAlone()
    {
        var store = $"book-delete-scope-{Guid.NewGuid()}";
        Guid doomedBook, doomedFeedback, keptFeedback, booklessFeedback;

        await using (var seed = NewDb(store))
        {
            doomedBook = SeedBook(seed, "Doomed");
            var keptBook = SeedBook(seed, "Kept");

            var doomed = SeedConversation(seed, "Doomed book chat", doomedBook);
            var kept = SeedConversation(seed, "Kept book chat", keptBook);
            var bookless = SeedConversation(seed, "Product Q and A", bookId: null);

            doomedFeedback = SeedFeedback(seed, SeedExchange(seed, doomed, askBookId: doomedBook)).Id;
            keptFeedback = SeedFeedback(seed, SeedExchange(seed, kept, askBookId: keptBook)).Id;
            booklessFeedback = SeedFeedback(seed, SeedExchange(seed, bookless)).Id;
            await seed.SaveChangesAsync();
        }

        // NON-VACUITY: three votes on three transcripts, so "left the other two alone" is a claim.
        await using (var before = NewDb(store))
        {
            Assert.Equal(3, await before.FeedbackItems.CountAsync());
            Assert.Equal(6, await before.ConversationMessages.CountAsync());
        }

        await using (var deleting = NewDb(store))
        {
            Assert.IsType<NoContentResult>(
                await NewBooksController(deleting).Delete(doomedBook, CancellationToken.None));
        }

        await using (var after = NewDb(store))
        {
            var rows = await after.FeedbackItems.AsNoTracking().ToListAsync();
            Assert.Equal(3, rows.Count);
            Assert.NotNull(rows.Single(f => f.Id == doomedFeedback).TargetDeletedAt);
            Assert.Null(rows.Single(f => f.Id == keptFeedback).TargetDeletedAt);
            Assert.Null(rows.Single(f => f.Id == booklessFeedback).TargetDeletedAt);

            // The other two transcripts are not merely unstamped, they are still readable - which is what
            // makes an unstamped row here correct rather than a second bug of the opposite sign.
            Assert.Equal(4, await after.ConversationMessages.CountAsync());
            Assert.Equal(2, await after.Conversations.CountAsync());
            Assert.True((await DetailOf(after, keptFeedback)).Evidence.Available);
            Assert.True((await DetailOf(after, booklessFeedback)).Evidence.Available);
        }
    }

    /// <summary>
    /// The stamp records when the target FIRST went away, so a second pass over the same rows must not
    /// move it. Driven through <see cref="FeedbackTombstone.StampAsync"/> directly, because the endpoint
    /// cannot delete the same conversation twice.
    /// </summary>
    [Fact]
    public async Task StampingTwice_LeavesTheOriginalDateAndReportsNothingStampedTheSecondTime()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        SeedFeedback(db, answer);
        await db.SaveChangesAsync();

        var first = await FeedbackTombstone.StampAsync(
            db, FeedbackTargetTypes.ConversationMessage, new[] { answer }, Now, CancellationToken.None);
        await db.SaveChangesAsync();
        Assert.Equal(1, first);

        var second = await FeedbackTombstone.StampAsync(
            db, FeedbackTargetTypes.ConversationMessage, new[] { answer }, Now.AddDays(1), CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(0, second);
        Assert.Equal(Now, (await db.FeedbackItems.AsNoTracking().SingleAsync()).TargetDeletedAt);
    }

    /// <summary>
    /// The target type is part of the match, not assumed: two target types could in principle mint the
    /// same <c>Guid</c>, and a tombstone stamped on the wrong row would misreport a live target as deleted.
    /// </summary>
    [Fact]
    public async Task Stamping_MatchesOnTheTargetTypeAsWellAsTheId()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        var message = SeedFeedback(db, answer, installationId: "a");

        // A row of a different target type that happens to carry the SAME id.
        var otherType = SeedFeedback(db, answer, installationId: "b");
        otherType.TargetType = "suggestion";
        await db.SaveChangesAsync();

        Assert.Equal(2, await db.FeedbackItems.CountAsync());

        var stamped = await FeedbackTombstone.StampAsync(
            db, FeedbackTargetTypes.ConversationMessage, new[] { answer }, Now, CancellationToken.None);
        await db.SaveChangesAsync();

        Assert.Equal(1, stamped);
        var rows = await db.FeedbackItems.AsNoTracking().ToListAsync();
        Assert.NotNull(rows.Single(f => f.Id == message.Id).TargetDeletedAt);
        Assert.Null(rows.Single(f => f.Id == otherType.Id).TargetDeletedAt);
    }

    /// <summary>
    /// THE TOMBSTONE IS WHAT MAKES THE KEPT ROW READABLE. The triage detail does not 404 a row whose
    /// target is gone - that would defeat the decision to keep it - it comes back with the stored
    /// vote-time context and a machine-readable reason for the missing evidence.
    /// </summary>
    [Fact]
    public async Task Detail_AfterTheConversationIsDeleted_RendersTheContextAndSaysWhyTheEvidenceIsGone()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var voted = await VoteOkAsync(
            controller,
            VoteFor(answer, FeedbackVerdicts.Down, "Wrong.",
                context: new FeedbackContextDto("/books/1/chat", null, null, "he", null)));

        // NON-VACUITY: before the delete the evidence really is composable.
        var before = Assert.IsType<FeedbackDetailDto>(Assert.IsType<OkObjectResult>(
            (await controller.Detail(voted.Id, CancellationToken.None)).Result).Value);
        Assert.True(before.Evidence.Available);
        Assert.Equal("Open the book menu and choose export.", before.Evidence.Answer);

        await NewConversationsController(db).Delete(conversation, CancellationToken.None);

        var after = Assert.IsType<FeedbackDetailDto>(Assert.IsType<OkObjectResult>(
            (await controller.Detail(voted.Id, CancellationToken.None)).Result).Value);

        Assert.False(after.Evidence.Available);
        Assert.Equal(FeedbackEvidenceComposer.ReasonTargetDeleted, after.Evidence.UnavailableReason);
        Assert.Null(after.Evidence.Answer);
        Assert.Null(after.Evidence.Question);

        // The vote itself, and everything a join cannot recover, is still there to read.
        Assert.Equal(FeedbackVerdicts.Down, after.Feedback.Verdict);
        Assert.Equal("Wrong.", after.Feedback.Text);
        Assert.Equal("/books/1/chat", after.Feedback.Context!.Route);
        Assert.NotNull(after.Feedback.TargetDeletedAt);
    }

    /// <summary>
    /// A target that vanished WITHOUT a tombstone reports a different reason. It means a row left the
    /// database without going through the delete path that owns the stamp, which is a defect worth being
    /// able to see rather than one worth smoothing over into "deleted".
    /// </summary>
    [Fact]
    public async Task Detail_WhenTheTargetVanishedWithoutATombstone_SaysMissingRatherThanDeleted()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        var feedback = SeedFeedback(db, answer);
        await db.SaveChangesAsync();

        db.ConversationMessages.RemoveRange(await db.ConversationMessages.ToListAsync());
        await db.SaveChangesAsync();

        var detail = Assert.IsType<FeedbackDetailDto>(Assert.IsType<OkObjectResult>(
            (await NewController(db).Detail(feedback.Id, CancellationToken.None)).Result).Value);

        Assert.False(detail.Evidence.Available);
        Assert.Equal(FeedbackEvidenceComposer.ReasonTargetMissing, detail.Evidence.UnavailableReason);
        Assert.Null(detail.Feedback.TargetDeletedAt);
    }

    /// <summary>
    /// A tombstoned row is still fully triageable - it is a signal the owner deliberately kept, so it must
    /// still list and still transition. This is the case that would fail if "keep it" had quietly become
    /// "keep it, but unusable".
    /// </summary>
    [Fact]
    public async Task ATombstonedRow_StillListsAndStillTransitions()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        var feedback = SeedFeedback(db, answer, FeedbackVerdicts.Down);
        await db.SaveChangesAsync();

        await NewConversationsController(db).Delete(conversation, CancellationToken.None);

        var controller = NewController(db);
        var list = Assert.IsType<FeedbackListDto>(Assert.IsType<OkObjectResult>(
            (await controller.List(null, FeedbackStatuses.New, FeedbackVerdicts.Down, null, 1, 25, CancellationToken.None))
            .Result).Value);

        Assert.Equal(1, list.TotalCount);
        var item = Assert.Single(list.Items);
        Assert.Equal(feedback.Id, item.Id);
        Assert.NotNull(item.TargetDeletedAt);
        // The book column resolves through the join, which no longer has a row to resolve - honestly null
        // rather than a stale copy.
        Assert.Null(item.BookId);

        var moved = Assert.IsType<FeedbackDto>(Assert.IsType<OkObjectResult>(
            (await controller.ChangeStatus(
                feedback.Id, new FeedbackStatusRequest(FeedbackStatuses.ConfirmedBug), CancellationToken.None))
            .Result).Value);
        Assert.Equal(FeedbackStatuses.ConfirmedBug, moved.Status);
    }

    /// <summary>
    /// VOTING ON AN ALREADY-DELETED TARGET IS STILL REFUSED. The tombstone keeps EXISTING signal readable;
    /// it does not make a dead id a legitimate new target, which would put an unactionable row into C3's
    /// inbox.
    /// </summary>
    [Fact]
    public async Task VotingOnAMessageFromADeletedConversation_IsStill400TargetNotFound()
    {
        await using var db = NewDb();
        var conversation = SeedConversation(db);
        var answer = SeedExchange(db, conversation);
        await db.SaveChangesAsync();

        var controller = NewController(db);
        // NON-VACUITY: the very same target accepted a vote before the delete.
        await VoteOkAsync(controller, VoteFor(answer, installationId: "a"));

        await NewConversationsController(db).Delete(conversation, CancellationToken.None);

        Assert.Equal(
            FeedbackErrors.TargetNotFound,
            await VoteRejectedAsync(controller, VoteFor(answer, installationId: "b")));
    }

    // ─── The tombstone LOG reports a committed outcome, not an intended one ─────────────────────────

    /// <summary>
    /// BOTH POLES OF ONE INVARIANT, in this test and the next: the book-delete log line claims feedback rows
    /// "were KEPT and tombstoned", which is a statement about what is IN THE DATABASE, so it may only be
    /// written once <c>SaveChangesAsync</c> has committed it.
    ///
    /// <para>This pole is the ordinary one: a delete that succeeds says so. Alone it is worthless as a pin -
    /// it passed before the fix too, because a log emitted early is still emitted. It earns its place only
    /// beside the failure pole below, which is the one that discriminates.</para>
    /// </summary>
    [Fact]
    public async Task DeletingABook_ReportsTheTombstoneCount_WhenTheSaveCommits()
    {
        var store = $"book-delete-log-ok-{Guid.NewGuid()}";
        Guid book;

        await using (var seed = NewDb(store))
        {
            book = SeedBook(seed);
            var conversation = SeedConversation(seed, "About this book", book);
            SeedFeedback(seed, SeedExchange(seed, conversation, askBookId: book), FeedbackVerdicts.Down);
            await seed.SaveChangesAsync();
        }

        var log = new RecordingLogger();
        await using (var deleting = NewDb(store))
        {
            Assert.IsType<NoContentResult>(
                await NewBooksController(deleting, log).Delete(book, CancellationToken.None));
        }

        Assert.Contains(log.Messages, m => m.Contains("KEPT and") && m.Contains("tombstoned"));
    }

    /// <summary>
    /// THE POLE THAT DISCRIMINATES, and the defect Bugbot found on `Pagedraft.Api#63`: the log used to be
    /// emitted from inside the conversation block, BEFORE the method's single
    /// <c>SaveChangesAsync</c>. A save that then threw rolled the whole delete back and left a log line
    /// asserting a durable outcome that never happened - the same class of lie as a fail-safe that swallows
    /// its own fault, because the record becomes evidence for something the database never did.
    ///
    /// <para>Reverting the fix (moving the log back above the save) fails THIS test and passes the one
    /// above, which is what makes the pair a pin rather than a pair of green checkmarks.
    /// <c>ConversationsController.Delete</c> always logged after its own save; this is the second path
    /// being held to the rule instead of nearly matching it.</para>
    /// </summary>
    [Fact]
    public async Task ABookDeleteThatFailsToSave_ClaimsNothingAboutTombstones()
    {
        var store = $"book-delete-log-fail-{Guid.NewGuid()}";
        Guid book;

        await using (var seed = NewDb(store))
        {
            book = SeedBook(seed);
            var conversation = SeedConversation(seed, "About this book", book);
            SeedFeedback(seed, SeedExchange(seed, conversation, askBookId: book), FeedbackVerdicts.Down);
            await seed.SaveChangesAsync();
        }

        var log = new RecordingLogger();
        await using var failing = new SaveFailingDb(InMemoryOptions(store));

        await Assert.ThrowsAsync<DbUpdateException>(
            () => NewBooksController(failing, log).Delete(book, CancellationToken.None));

        // NON-VACUITY: the run really did reach the tombstone work - it stamped rows in the change tracker
        // and only the COMMIT failed - so an empty log here is the fix holding, not a test that never got
        // near the code under test.
        Assert.True(failing.SaveWasAttempted);
        Assert.DoesNotContain(log.Messages, m => m.Contains("tombstoned"));
    }

    private static DbContextOptions<AppDbContext> InMemoryOptions(string store) =>
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(store).Options;

    /// <summary>An <see cref="AppDbContext"/> whose commit always fails, so the rollback branch is reachable.</summary>
    private sealed class SaveFailingDb : AppDbContext
    {
        internal bool SaveWasAttempted { get; private set; }

        internal SaveFailingDb(DbContextOptions<AppDbContext> options) : base(options) { }

        public override Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            SaveWasAttempted = true;
            throw new DbUpdateException("The commit failed after the tombstone was staged.");
        }
    }

    /// <summary>Keeps every formatted message so a test can assert one was NOT written.</summary>
    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger<BooksController>
    {
        internal readonly System.Collections.Generic.List<string> Messages = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    /// <summary>The triage detail for one row, asserting the response shape on the way.</summary>
    private static async Task<FeedbackDetailDto> DetailOf(AppDbContext db, Guid feedbackId)
        => Assert.IsType<FeedbackDetailDto>(Assert.IsType<OkObjectResult>(
            (await NewController(db).Detail(feedbackId, CancellationToken.None)).Result).Value);
}
