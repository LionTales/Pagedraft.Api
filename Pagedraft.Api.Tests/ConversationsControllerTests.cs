using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Chat;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// The five conversation endpoints the drawer's history UI reads and writes (Show C1, d1 section (5)).
///
/// <para>EVERY LIST AND FILTER CASE PROVES ITS POPULATION NON-EMPTY BEFORE ASSERTING WHAT IT CONTAINS. A
/// filter test whose seed never held a matching row passes for the wrong reason, and this codebase has
/// been bitten by exactly that; so each case asserts the count it expects to EXCLUDE as well as the
/// count it expects to keep.</para>
///
/// <para>No model, no GPU: rows in, rows out.</para>
/// </summary>
public class ConversationsControllerTests
{
    [Fact]
    public async Task List_IsNewestFirst_AndCarriesTheCheapProjection()
    {
        await using var db = NewDb();
        var older = Seed(db, "Older", bookId: null, updatedAt: Now.AddHours(-2), messageCount: 2);
        var newer = Seed(db, "Newer", bookId: null, updatedAt: Now.AddHours(-1), messageCount: 4);
        await db.SaveChangesAsync();

        var list = await ListAsync(db);

        // NON-VACUITY: both rows are really there, so "newest first" is an ordering claim and not an
        // accident of an empty page.
        Assert.Equal(2, list.TotalCount);
        Assert.Equal(2, list.Items.Count);
        Assert.Equal(new[] { newer, older }, list.Items.Select(i => i.Id));
        Assert.Equal(4, list.Items[0].MessageCount);
        Assert.False(list.NearCapWarning);
    }

    [Fact]
    public async Task List_FilteredByBook_KeepsThatBookAndExcludesTheRest()
    {
        var bookA = Guid.NewGuid();
        var bookB = Guid.NewGuid();

        await using var db = NewDb();
        var inA = Seed(db, "In book A", bookA, Now, 2);
        Seed(db, "In book B", bookB, Now, 2);
        Seed(db, "App level", null, Now, 2);
        await db.SaveChangesAsync();

        // NON-VACUITY: the unfiltered list holds all three, so the filtered list below is excluding
        // something rather than querying an empty table.
        var all = await ListAsync(db);
        Assert.Equal(3, all.TotalCount);

        var filtered = await ListAsync(db, bookId: bookA);
        Assert.Equal(1, filtered.TotalCount);
        Assert.Equal(inA, Assert.Single(filtered.Items).Id);
    }

    /// <summary>
    /// An OMITTED bookId means EVERY conversation, app-level ones included - it does not quietly mean
    /// "the app-level ones". A history list that hid the book conversations would look broken.
    /// </summary>
    [Fact]
    public async Task List_WithoutABookFilter_IncludesBookScopedAndAppLevelAlike()
    {
        await using var db = NewDb();
        Seed(db, "In a book", Guid.NewGuid(), Now, 2);
        Seed(db, "App level", null, Now, 2);
        await db.SaveChangesAsync();

        var list = await ListAsync(db);

        Assert.Equal(2, list.TotalCount);
        Assert.Contains(list.Items, i => i.BookId != null);
        Assert.Contains(list.Items, i => i.BookId == null);
    }

    [Fact]
    public async Task List_Pages_WithoutLosingTheTotal()
    {
        await using var db = NewDb();
        for (var i = 0; i < 5; i++) Seed(db, $"Conversation {i}", null, Now.AddMinutes(-i), 2);
        await db.SaveChangesAsync();

        var page1 = await ListAsync(db, page: 1, pageSize: 2);
        var page3 = await ListAsync(db, page: 3, pageSize: 2);

        Assert.Equal(5, page1.TotalCount);
        Assert.Equal(2, page1.Items.Count);
        Assert.Equal(5, page3.TotalCount);
        Assert.Single(page3.Items);
        Assert.Empty(page1.Items.Select(i => i.Id).Intersect(page3.Items.Select(i => i.Id)));
    }

    // ─── The soft cap: informational, and counted over the WHOLE notebook ───────────────────────────

    /// <summary>
    /// Below the cap the warning stays down. Seeded straight onto the DbSet rather than through the chat
    /// path: 199 conversations through the controller would be 199 round trips to prove one boolean.
    /// </summary>
    [Fact]
    public async Task List_JustBelowTheSoftCap_RaisesNoWarning()
    {
        await using var db = NewDb();
        for (var i = 0; i < ConversationsController.SoftCap - 1; i++)
            Seed(db, $"Conversation {i}", null, Now.AddMinutes(-i), 2);
        await db.SaveChangesAsync();

        var list = await ListAsync(db);

        // NON-VACUITY: the population really is one row short of the cap, so "no warning" is a boundary
        // claim and not the answer an empty table would have given.
        Assert.Equal(ConversationsController.SoftCap - 1, list.TotalCount);
        Assert.Equal(25, list.Items.Count);
        Assert.False(list.NearCapWarning);
    }

    /// <summary>
    /// AT the cap it goes up, and it stays up above it. The flag is informational only - nothing is
    /// deleted, ever - so the only thing that can be wrong here is the boundary, which is why the case
    /// sits exactly on it and then steps one past.
    /// </summary>
    [Fact]
    public async Task List_AtTheSoftCap_RaisesTheWarning_AndKeepsItAbove()
    {
        await using var db = NewDb();
        for (var i = 0; i < ConversationsController.SoftCap; i++)
            Seed(db, $"Conversation {i}", null, Now.AddMinutes(-i), 2);
        await db.SaveChangesAsync();

        var atTheCap = await ListAsync(db);
        Assert.Equal(ConversationsController.SoftCap, atTheCap.TotalCount);
        Assert.Equal(25, atTheCap.Items.Count);
        Assert.True(atTheCap.NearCapWarning);

        Seed(db, "One more", null, Now, 2);
        await db.SaveChangesAsync();

        var aboveTheCap = await ListAsync(db);
        Assert.Equal(ConversationsController.SoftCap + 1, aboveTheCap.TotalCount);
        Assert.True(aboveTheCap.NearCapWarning);

        // And nothing was deleted on the way past the cap: the retention decision is that the cap is a
        // notice, not an eviction policy.
        Assert.Equal(ConversationsController.SoftCap + 1, await db.Conversations.CountAsync());
    }

    /// <summary>
    /// THE ASYMMETRY, PINNED BECAUSE IT IS A DECISION AND NOT AN OVERSIGHT. <c>TotalCount</c> is counted
    /// AFTER the book filter (it is what the client pages on), but <c>nearCapWarning</c> is counted over
    /// EVERY stored conversation. The warning is about the author's whole notebook filling up, which is
    /// what the storage actually holds; deriving it from the filtered count would hide the notice behind
    /// whichever book happened to be open.
    /// </summary>
    [Fact]
    public async Task List_NearCapWarning_CountsTheWholeNotebook_EvenWhenFilteredToOneBook()
    {
        var bookA = Guid.NewGuid();

        await using var db = NewDb();
        for (var i = 0; i < ConversationsController.SoftCap; i++)
            Seed(db, $"App level {i}", null, Now.AddMinutes(-i), 2);
        for (var i = 0; i < 3; i++) Seed(db, $"In book A {i}", bookA, Now.AddMinutes(-i), 2);
        await db.SaveChangesAsync();

        // NON-VACUITY: both populations exist, and the filtered one is FAR below the cap - so a warning on
        // the filtered read can only have come from the unfiltered count.
        var all = await ListAsync(db);
        Assert.Equal(ConversationsController.SoftCap + 3, all.TotalCount);
        Assert.True(all.NearCapWarning);

        var filtered = await ListAsync(db, bookId: bookA);
        Assert.Equal(3, filtered.TotalCount);
        Assert.Equal(3, filtered.Items.Count);
        Assert.True(filtered.TotalCount < ConversationsController.SoftCap);
        Assert.True(filtered.NearCapWarning);
    }

    [Fact]
    public async Task Get_ReturnsMetadata_And404sOnAnUnknownId()
    {
        await using var db = NewDb();
        var id = Seed(db, "A conversation", null, Now, 6);
        await db.SaveChangesAsync();

        var controller = NewController(db);

        var found = await controller.Get(id, CancellationToken.None);
        var dto = Assert.IsType<ConversationDto>(Assert.IsType<OkObjectResult>(found.Result).Value);
        Assert.Equal("A conversation", dto.Title);
        Assert.Equal(6, dto.MessageCount);

        var missing = await controller.Get(Guid.NewGuid(), CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(missing.Result);
    }

    [Fact]
    public async Task Messages_AreOldestFirstBySequence_AndCarryTheFullStoredText()
    {
        await using var db = NewDb();
        var id = Seed(db, "A conversation", null, Now, 3);
        var longText = new string('x', ProductChatService.MaxHistoryTurnChars * 2);

        // Inserted OUT of sequence order on purpose: the read must sort, not rely on insertion order.
        SeedMessage(db, id, sequence: 2, ChatMessageRoles.User, "third");
        SeedMessage(db, id, sequence: 0, ChatMessageRoles.User, longText);
        SeedMessage(db, id, sequence: 1, ChatMessageRoles.Assistant, "second");
        await db.SaveChangesAsync();

        var page = await MessagesAsync(db, id);

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(new[] { 0, 1, 2 }, page.Items.Select(m => m.Sequence));
        Assert.Equal(longText, page.Items[0].Text);
        Assert.True(page.Items[0].Text.Length > ProductChatService.MaxHistoryTurnChars);
    }

    /// <summary>
    /// THE LOAD-BEARING PAGING CASE. <c>ConversationService.allMessages</c> on the client walks this
    /// endpoint page by page and hands the concatenation to hydration, so a page 2 that skipped a turn,
    /// repeated one, or restarted the ordering would rebuild a transcript missing exactly the turns the
    /// next question is composed from - and C1's byte-identity property would be false on every
    /// conversation longer than one page, which is every conversation an author actually keeps.
    ///
    /// <para>The walk is therefore asserted as a WALK: the pages are concatenated and the result is
    /// checked for contiguity and distinctness, not just page 2 read in isolation.</para>
    /// </summary>
    [Fact]
    public async Task Messages_PagesWalkTheWholeTranscript_WithNoOverlapNoGap_AndAPrePagingTotal()
    {
        const int turns = 7;

        await using var db = NewDb();
        var id = Seed(db, "A long conversation", null, Now, turns);
        for (var i = 0; i < turns; i++)
        {
            SeedMessage(
                db, id, i,
                i % 2 == 0 ? ChatMessageRoles.User : ChatMessageRoles.Assistant,
                $"turn {i}");
        }
        await db.SaveChangesAsync();

        var page1 = await MessagesAsync(db, id, page: 1, pageSize: 3);
        var page2 = await MessagesAsync(db, id, page: 2, pageSize: 3);
        var page3 = await MessagesAsync(db, id, page: 3, pageSize: 3);

        // NON-VACUITY: there really are three populated pages, so everything below is a claim about
        // paging and not about three empty reads agreeing with each other.
        Assert.Equal(3, page1.Items.Count);
        Assert.Equal(3, page2.Items.Count);
        Assert.Single(page3.Items);

        // TotalCount is the PRE-paging total on every page - it is what the client's page-walk uses to
        // decide there is a page 2 at all.
        Assert.Equal(turns, page1.TotalCount);
        Assert.Equal(turns, page2.TotalCount);
        Assert.Equal(turns, page3.TotalCount);
        Assert.Equal(2, page2.Page);
        Assert.Equal(3, page2.PageSize);

        // Page 2 CONTINUES the sequence rather than restarting it.
        Assert.Equal(new[] { 0, 1, 2 }, page1.Items.Select(m => m.Sequence));
        Assert.Equal(new[] { 3, 4, 5 }, page2.Items.Select(m => m.Sequence));
        Assert.Equal(new[] { 6 }, page3.Items.Select(m => m.Sequence));

        var walked = page1.Items.Concat(page2.Items).Concat(page3.Items).ToList();
        Assert.Equal(turns, walked.Count);
        // No overlap (every row appears once) and no gap (0..6, in order, nothing missing).
        Assert.Equal(turns, walked.Select(m => m.Id).Distinct().Count());
        Assert.Equal(Enumerable.Range(0, turns), walked.Select(m => m.Sequence));
        // And the TEXT is what hydration would rebuild, not merely a set of ids in the right order.
        Assert.Equal(
            Enumerable.Range(0, turns).Select(i => $"turn {i}"),
            walked.Select(m => m.Text));
        Assert.Equal(
            Enumerable.Range(0, turns)
                .Select(i => i % 2 == 0 ? ChatMessageRoles.User : ChatMessageRoles.Assistant),
            walked.Select(m => m.Role));
    }

    /// <summary>
    /// The page size is clamped at <c>MaxMessagePageSize</c>, so no caller can ask for a transcript of
    /// unbounded size in one read. The clamp is proved to have CUT something: the seed holds more turns
    /// than the maximum, and the remainder is still reachable on the next page.
    /// </summary>
    [Fact]
    public async Task Messages_PageSize_IsClampedAtTheMaximum_AndTheRemainderStaysWalkable()
    {
        const int overflow = 5;
        var turns = ConversationsController.MaxMessagePageSize + overflow;

        await using var db = NewDb();
        var id = Seed(db, "A very long conversation", null, Now, turns);
        for (var i = 0; i < turns; i++)
            SeedMessage(db, id, i, ChatMessageRoles.User, $"turn {i}");
        await db.SaveChangesAsync();

        var page1 = await MessagesAsync(db, id, page: 1, pageSize: turns * 10);

        // NON-VACUITY: the conversation really does hold more turns than the clamp, so the short page
        // below is the clamp acting rather than the transcript running out.
        Assert.Equal(turns, page1.TotalCount);
        Assert.True(page1.TotalCount > ConversationsController.MaxMessagePageSize);

        Assert.Equal(ConversationsController.MaxMessagePageSize, page1.PageSize);
        Assert.Equal(ConversationsController.MaxMessagePageSize, page1.Items.Count);
        Assert.Equal(0, page1.Items[0].Sequence);
        Assert.Equal(ConversationsController.MaxMessagePageSize - 1, page1.Items[^1].Sequence);

        // The clamped-away tail is not lost, only deferred: page 2 of the CLAMPED size carries it.
        var page2 = await MessagesAsync(db, id, page: 2, pageSize: turns * 10);
        Assert.Equal(overflow, page2.Items.Count);
        Assert.Equal(ConversationsController.MaxMessagePageSize, page2.Items[0].Sequence);
        Assert.Equal(turns - 1, page2.Items[^1].Sequence);
    }

    [Fact]
    public async Task Messages_DeserializeTheGroundingSnapshot_OnTheAnswerTurnOnly()
    {
        await using var db = NewDb();
        var id = Seed(db, "A conversation", null, Now, 2);

        SeedMessage(db, id, 0, ChatMessageRoles.User, "A question");
        var answer = SeedMessage(db, id, 1, ChatMessageRoles.Assistant, "An answer");
        answer.GroundingJson = JsonSerializer.Serialize(
            new ConversationGroundingDto(
                new[] { "export" }, new[] { "chapter-brief:2" }, null, false, "retrieval: ...; answer: ..."),
            ChatConversationStore.SnapshotJson);
        await db.SaveChangesAsync();

        var page = await MessagesAsync(db, id);

        Assert.Null(page.Items[0].Grounding);
        var snapshot = page.Items[1].Grounding;
        Assert.NotNull(snapshot);
        Assert.Equal(new[] { "export" }, snapshot!.GuideIds);
        Assert.Equal(new[] { "chapter-brief:2" }, snapshot.ArtifactRefs);
    }

    /// <summary>
    /// An unreadable snapshot costs the transcript nothing. The author came for the conversation; one
    /// broken diagnostic blob must not take it away, and the failure is logged rather than swallowed.
    /// </summary>
    [Fact]
    public async Task Messages_WithAnUnparseableSnapshot_StillReturnTheTranscript()
    {
        await using var db = NewDb();
        var id = Seed(db, "A conversation", null, Now, 1);
        var answer = SeedMessage(db, id, 0, ChatMessageRoles.Assistant, "An answer");
        answer.GroundingJson = "{ this is not json";
        await db.SaveChangesAsync();

        var page = await MessagesAsync(db, id);

        Assert.Equal("An answer", Assert.Single(page.Items).Text);
        Assert.Null(page.Items[0].Grounding);
    }

    [Fact]
    public async Task Messages_404OnAnUnknownConversation()
    {
        await using var db = NewDb();
        var result = await NewController(db).Messages(Guid.NewGuid(), ct: CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task Rename_StoresTheAuthorsOwnTitle()
    {
        await using var db = NewDb();
        var id = Seed(db, "How do I export?", null, Now, 2);
        await db.SaveChangesAsync();

        var result = await NewController(db).Rename(
            id, new ConversationRenameRequest("  Export questions  "), CancellationToken.None);

        var dto = Assert.IsType<ConversationDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("Export questions", dto.Title);
        Assert.Equal("Export questions", (await db.Conversations.SingleAsync()).Title);
    }

    [Fact]
    public async Task Rename_BlankTitleIsA400_RatherThanASilentFallBackToTheAutoTitle()
    {
        await using var db = NewDb();
        var id = Seed(db, "How do I export?", null, Now, 2);
        await db.SaveChangesAsync();

        var result = await NewController(db).Rename(
            id, new ConversationRenameRequest("   "), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("How do I export?", (await db.Conversations.SingleAsync()).Title);
    }

    [Fact]
    public async Task Rename_404sOnAnUnknownId()
    {
        await using var db = NewDb();
        var result = await NewController(db).Rename(
            Guid.NewGuid(), new ConversationRenameRequest("Anything"), CancellationToken.None);
        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task Delete_RemovesTheConversationAndItsTurns_AndLeavesTheRestAlone()
    {
        await using var db = NewDb();
        var doomed = Seed(db, "Doomed", null, Now, 2);
        var kept = Seed(db, "Kept", null, Now, 2);
        SeedMessage(db, doomed, 0, ChatMessageRoles.User, "A question");
        SeedMessage(db, doomed, 1, ChatMessageRoles.Assistant, "An answer");
        SeedMessage(db, kept, 0, ChatMessageRoles.User, "Another question");
        await db.SaveChangesAsync();

        // NON-VACUITY: there really are rows to delete, and rows that must survive.
        Assert.Equal(2, await db.Conversations.CountAsync());
        Assert.Equal(3, await db.ConversationMessages.CountAsync());

        var controller = NewController(db);
        Assert.IsType<NoContentResult>(await controller.Delete(doomed, CancellationToken.None));

        Assert.Equal(kept, (await db.Conversations.SingleAsync()).Id);
        Assert.Equal(kept, (await db.ConversationMessages.SingleAsync()).ConversationId);

        Assert.IsType<NotFoundObjectResult>(await controller.Delete(doomed, CancellationToken.None));
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────

    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private static AppDbContext NewDb() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ConversationsController NewController(AppDbContext db)
        => new(db, NullLogger<ConversationsController>.Instance);

    private static Guid Seed(
        AppDbContext db, string title, Guid? bookId, DateTimeOffset updatedAt, int messageCount)
    {
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            Title = title,
            BookId = bookId,
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt,
            MessageCount = messageCount
        };
        db.Conversations.Add(conversation);
        return conversation.Id;
    }

    private static ConversationMessage SeedMessage(
        AppDbContext db, Guid conversationId, int sequence, string role, string text)
    {
        var message = new ConversationMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            Sequence = sequence,
            Role = role,
            Text = text,
            CreatedAt = Now
        };
        db.ConversationMessages.Add(message);
        return message;
    }

    private static async Task<ConversationListDto> ListAsync(
        AppDbContext db, Guid? bookId = null, int page = 1, int pageSize = 25)
    {
        var result = await NewController(db).List(bookId, page, pageSize, CancellationToken.None);
        return Assert.IsType<ConversationListDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    private static async Task<ConversationMessagesDto> MessagesAsync(
        AppDbContext db, Guid id, int page = 1, int pageSize = ConversationsController.DefaultMessagePageSize)
    {
        var result = await NewController(db).Messages(id, page, pageSize, CancellationToken.None);
        return Assert.IsType<ConversationMessagesDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
    }
}
