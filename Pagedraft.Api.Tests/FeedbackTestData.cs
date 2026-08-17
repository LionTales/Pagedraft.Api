using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Chat;
using Pagedraft.Api.Services.Feedback;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// Shared construction for the three Show C2 feedback suites: an isolated in-memory database, a wired
/// controller, and seed helpers for the two row shapes every case needs (a persisted Show exchange to vote
/// on, and a feedback row to read or transition).
///
/// <para>No model, no GPU, no SQL Server: rows in, rows out. The seeds go straight onto the DbSets rather
/// than through the chat path, because a case about the one-vote rule should not also be a case about
/// prompt composition.</para>
/// </summary>
internal static class FeedbackTestData
{
    internal static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    internal const string Installation = "install-a";
    internal const string OtherInstallation = "install-b";

    /// <summary>
    /// An isolated in-memory database. A NAME may be supplied so a SECOND context can be opened over the
    /// same store: that is how the book-delete cases get a controller whose change tracker has never seen
    /// the seeded conversations, exactly like the request-scoped context a real delete runs on. It matters
    /// because the in-memory provider only cascades what it has tracked, so a single shared context would
    /// remove the turns by tracker fixup and hide whether the code under test removed them at all.
    /// </summary>
    internal static AppDbContext NewDb(string? databaseName = null) => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString())
            .Options);

    internal static FeedbackService NewService(AppDbContext db) => new(
        db,
        new FeedbackEvidenceComposer(db, NullLogger<FeedbackEvidenceComposer>.Instance),
        NullLogger<FeedbackService>.Instance);

    /// <summary>
    /// The controller under the DEFAULT posture for these suites: triage ON, because the flag-off cases
    /// pass <c>triageEnabled: false</c> explicitly and are the only ones that should be reasoning about it.
    /// </summary>
    internal static FeedbackController NewController(AppDbContext db, bool triageEnabled = true)
        => new(NewService(db), Options.Create(new FeedbackOptions { TriageEnabled = triageEnabled }));

    /// <summary>
    /// The same controller with an AUTHENTICATED principal on it. Nothing in the app produces one today
    /// (no <c>AddAuthentication</c> is registered anywhere), so this exists to drive the upper half of the
    /// <c>UserId ?? InstallationId</c> key that the login will switch on.
    /// </summary>
    internal static FeedbackController NewControllerAsUser(AppDbContext db, string userId)
    {
        var controller = NewController(db);
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, authenticationType: "TestAuth");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    internal static ConversationsController NewConversationsController(AppDbContext db)
        => new(db, NullLogger<ConversationsController>.Instance);

    /// <summary>
    /// The books controller for the SECOND arrival path of the tombstone. Every dependency the delete path
    /// never reaches is left null on purpose: <c>Delete</c> reads and writes DbSets and logs, and touches
    /// no AI service, no scope factory and no application lifetime, so a mock of each would assert nothing
    /// and only obscure that.
    /// </summary>
    internal static BooksController NewBooksController(AppDbContext db)
        => new(
            db,
            bookIntelligence: null!,
            styleBaseline: null!,
            bookSummary: null!,
            bookReview: null!,
            chapterBrief: null!,
            progress: null!,
            aiTierStatus: null!,
            profileBuilds: null!,
            scopeFactory: null!,
            appLifetime: null!,
            logger: NullLogger<BooksController>.Instance);

    // ─── Seeds ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One persisted exchange - a question at <c>Sequence</c> n and its answer at n+1 - which is the shape
    /// the evidence join reads. Returns the ANSWER's id, because that is what a vote targets.
    /// </summary>
    internal static Guid SeedExchange(
        AppDbContext db,
        Guid conversationId,
        int firstSequence = 0,
        string question = "How do I export?",
        string answer = "Open the book menu and choose export.",
        Guid? askBookId = null,
        bool failed = false,
        ConversationGroundingDto? grounding = null)
    {
        db.ConversationMessages.Add(new ConversationMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            Sequence = firstSequence,
            Role = ChatMessageRoles.User,
            Text = question,
            Failed = failed,
            AskBookId = askBookId,
            CreatedAt = Now
        });

        var answerRow = new ConversationMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            Sequence = firstSequence + 1,
            Role = ChatMessageRoles.Assistant,
            Text = answer,
            Failed = failed,
            AskBookId = askBookId,
            CreatedAt = Now,
            GroundingJson = grounding == null
                ? null
                : System.Text.Json.JsonSerializer.Serialize(grounding, ChatConversationStore.SnapshotJson)
        };
        db.ConversationMessages.Add(answerRow);
        return answerRow.Id;
    }

    /// <summary>A book to hang a conversation on, for the cases about deleting one.</summary>
    internal static Guid SeedBook(AppDbContext db, string title = "A manuscript")
    {
        var book = new Book
        {
            Id = Guid.NewGuid(),
            Title = title,
            Language = "he",
            CreatedAt = Now,
            UpdatedAt = Now
        };
        db.Books.Add(book);
        return book.Id;
    }

    internal static Guid SeedConversation(AppDbContext db, string title = "A conversation", Guid? bookId = null)
    {
        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            Title = title,
            BookId = bookId,
            CreatedAt = Now,
            UpdatedAt = Now,
            MessageCount = 2
        };
        db.Conversations.Add(conversation);
        return conversation.Id;
    }

    /// <summary>A feedback row written straight to storage, for read-side cases that are not about voting.</summary>
    internal static FeedbackItem SeedFeedback(
        AppDbContext db,
        Guid targetId,
        string verdict = FeedbackVerdicts.Down,
        string status = FeedbackStatuses.New,
        string area = FeedbackAreas.ChatAnswer,
        string? text = null,
        DateTimeOffset? createdAt = null,
        string installationId = Installation)
    {
        var item = new FeedbackItem
        {
            Id = Guid.NewGuid(),
            Area = area,
            TargetType = FeedbackTargetTypes.ConversationMessage,
            TargetId = targetId,
            Verdict = verdict,
            Text = text,
            Status = status,
            InstallationId = installationId,
            CreatedAt = createdAt ?? Now,
            StatusChangedAt = createdAt ?? Now
        };
        db.FeedbackItems.Add(item);
        return item;
    }

    // ─── Request builders ───────────────────────────────────────────────────────────────────────────

    internal static FeedbackVoteRequest VoteFor(
        Guid targetId,
        string verdict = FeedbackVerdicts.Down,
        string? text = null,
        string? installationId = Installation,
        FeedbackContextDto? context = null,
        string? area = FeedbackAreas.ChatAnswer,
        string? targetType = FeedbackTargetTypes.ConversationMessage)
        => new(area, targetType, targetId, verdict, text, installationId, context);

    // ─── Assertion shortcuts ────────────────────────────────────────────────────────────────────────

    internal static async Task<FeedbackDto> VoteOkAsync(
        FeedbackController controller, FeedbackVoteRequest request)
    {
        var result = await controller.Vote(request, CancellationToken.None);
        return Assert.IsType<FeedbackDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    /// <summary>Returns the machine-readable <c>error</c> code off a 400, asserting the shape on the way.</summary>
    internal static async Task<string> VoteRejectedAsync(
        FeedbackController controller, FeedbackVoteRequest request)
    {
        var result = await controller.Vote(request, CancellationToken.None);
        var body = Assert.IsType<BadRequestObjectResult>(result.Result).Value;
        return ErrorCodeOf(body);
    }

    /// <summary>
    /// Reads the <c>error</c> property off an anonymous error body by reflection, so the assertion is
    /// about the SHAPE the client receives rather than about a substring of a serialized string.
    /// </summary>
    internal static string ErrorCodeOf(object? body)
    {
        Assert.NotNull(body);
        var property = body!.GetType().GetProperty("error");
        Assert.NotNull(property);
        return Assert.IsType<string>(property!.GetValue(body));
    }

    internal static string? StatusPropertyOf(object? body, string name)
    {
        Assert.NotNull(body);
        var property = body!.GetType().GetProperty(name);
        Assert.NotNull(property);
        return property!.GetValue(body) as string;
    }
}
