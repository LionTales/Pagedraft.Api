using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Chat;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// The dual-write that gives a Show conversation a durable existence (Show C1, c1).
///
/// <para>THE THREE PROPERTIES THESE DEFEND. (1) One write path: the controller wraps the single existing
/// <c>AnswerAsync</c> call and keys off the returned DTO, so a fail-safe is persisted AS FAILED rather
/// than lost - which is exactly the population C2's feedback wants. (2) The grounding snapshot is written
/// in the SAME write as the answer it describes, because a second path built later would drift. (3) A
/// persistence failure never costs the author an answer, and never fails silently - a nested catch that
/// swallows to stay non-throwing blinds every layer above it, a recorded failure shape in this
/// codebase.</para>
///
/// <para>No model, no GPU, no network: the database is in-memory and the DTOs are synthetic where the
/// service is not the thing under test.</para>
/// </summary>
public class ChatConversationStoreTests
{
    // ─── The dual-write ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Exchange_PersistsBothTurns_WithAskContextAndThreadingIds()
    {
        var bookId = Guid.NewGuid();
        var chapterId = Guid.NewGuid();
        await using var db = NewDb();
        var store = NewStore(db, out _, out _);

        var request = new ProductChatRequest(
            "  How do I export?  ", Language: "en", BookId: bookId,
            AmbientChapterId: chapterId, AmbientChapterOrder: 3);

        var pending = await store.BeginExchangeAsync(request, CancellationToken.None);
        Assert.Null(pending.Fault);
        Assert.True(pending.Persisted);

        var completed = await store.CompleteExchangeAsync(
            pending, Grounded("Use the export panel."), CancellationToken.None);

        Assert.Null(completed.Fault);
        Assert.Equal(pending.ConversationId, completed.Response.ConversationId);
        Assert.Equal(pending.UserMessageId, completed.Response.UserMessageId);
        Assert.NotNull(completed.Response.AssistantMessageId);
        Assert.NotEqual(completed.Response.UserMessageId, completed.Response.AssistantMessageId);

        var conversation = await db.Conversations.SingleAsync();
        Assert.Equal(2, conversation.MessageCount);
        Assert.Equal(bookId, conversation.BookId);
        Assert.Null(conversation.UserId);
        Assert.True(conversation.UpdatedAt >= conversation.CreatedAt);

        var messages = await db.ConversationMessages.OrderBy(m => m.Sequence).ToListAsync();
        Assert.Equal(2, messages.Count);

        Assert.Equal(ChatMessageRoles.User, messages[0].Role);
        Assert.Equal(0, messages[0].Sequence);
        Assert.Equal("How do I export?", messages[0].Text);
        Assert.False(messages[0].Failed);
        Assert.Null(messages[0].GroundingJson);
        Assert.Equal(bookId, messages[0].AskBookId);
        Assert.Equal(chapterId, messages[0].AskChapterId);
        Assert.Equal(3, messages[0].AskChapterOrder);

        Assert.Equal(ChatMessageRoles.Assistant, messages[1].Role);
        Assert.Equal(1, messages[1].Sequence);
        Assert.Equal("Use the export panel.", messages[1].Text);
        Assert.False(messages[1].Failed);
        // The ask-time context rides on BOTH turns of the exchange: the resend window filters on it, and a
        // window that could only see the question's book would drop every answer.
        Assert.Equal(bookId, messages[1].AskBookId);
        Assert.Equal(3, messages[1].AskChapterOrder);
    }

    /// <summary>
    /// THE POINT OF PERSISTING AT THE CONTROLLER RATHER THAN INSIDE THE SERVICE. Every fail-safe is a
    /// plain 200 with <c>isGrounded=false</c>, so one keyed expression covers all five of the service's
    /// fail-safe returns; C2 wants feedback on exactly these, so they are stored flagged rather than
    /// dropped.
    /// </summary>
    [Theory]
    [InlineData("guides-unavailable")]
    [InlineData("guides-empty")]
    [InlineData("model-unavailable")]
    [InlineData("empty-answer")]
    [InlineData("book-unavailable")]
    public async Task FailSafe_IsPersistedAsFailed_OnBothTurns(string fault)
    {
        await using var db = NewDb();
        var store = NewStore(db, out _, out _);

        var pending = await store.BeginExchangeAsync(
            new ProductChatRequest("What happened?", Language: "en"), CancellationToken.None);

        var completed = await store.CompleteExchangeAsync(
            pending,
            new ProductChatResponseDto(
                "I cannot reach the guides right now.", Array.Empty<string>(), "en",
                IsGrounded: false, FaultReason: fault),
            CancellationToken.None);

        Assert.Null(completed.Fault);
        // THE IDS ARE STILL RETURNED. A failed answer that cannot be pointed at is a failed answer C2
        // cannot collect feedback on, which is the opposite of what C1 exists to enable.
        Assert.NotNull(completed.Response.AssistantMessageId);

        var messages = await db.ConversationMessages.OrderBy(m => m.Sequence).ToListAsync();
        Assert.Equal(2, messages.Count);
        Assert.All(messages, m => Assert.True(m.Failed));
        // No snapshot on a failure: there is no grounding to describe, and the flag beside it is the fact
        // that matters.
        Assert.All(messages, m => Assert.Null(m.GroundingJson));
    }

    [Fact]
    public async Task GroundingSnapshot_IsWrittenOnTheAnswerTurn_FromTheCapturedFacts()
    {
        await using var db = NewDb();
        var store = NewStore(db, out var capture, out _);

        // The two halves the snapshot is assembled from, captured where the two existing log lines are
        // written. Populated here directly; ServiceFillsTheCapture below proves the service does it live.
        capture.CaptureRetrieval(
            Guid.NewGuid(), "he", "en", new[] { 2, 3 }, 1, new[] { "pacing" },
            2, "Exact", 2, false, new[] { 2 }, Array.Empty<int>(), 5, Array.Empty<string>());
        capture.CaptureCitation(
            "en", new[] { "export" }, new[] { "export", "faq" }, new[] { "chapter-brief:2" },
            new[] { "chapter-brief:2", "register" }, "ollama", "gemma4:12b", 4, 6, 9000, 3500, 14080);

        var pending = await store.BeginExchangeAsync(
            new ProductChatRequest("What happens in chapter three?", Language: "en"), CancellationToken.None);

        await store.CompleteExchangeAsync(
            pending,
            new ProductChatResponseDto(
                "Chapter three moves the plot along.", new[] { "export" }, "en",
                IsGrounded: true, FaultReason: null,
                ArtifactRefs: new[] { "chapter-brief:2" },
                BookFaultReason: null,
                NeedsChapterClarification: false),
            CancellationToken.None);

        var answer = await db.ConversationMessages
            .SingleAsync(m => m.Role == ChatMessageRoles.Assistant);

        Assert.NotNull(answer.GroundingJson);
        var snapshot = JsonSerializer.Deserialize<ConversationGroundingDto>(
            answer.GroundingJson!, ChatConversationStore.SnapshotJson)!;

        Assert.Equal(new[] { "export" }, snapshot.GuideIds);
        Assert.Equal(new[] { "chapter-brief:2" }, snapshot.ArtifactRefs);
        Assert.Null(snapshot.BookFaultReason);
        Assert.False(snapshot.NeedsChapterClarification);

        // Both halves reached the one stored line, so C3's re-check can see what was retrieved AND what
        // was cited without joining the log.
        Assert.NotNull(snapshot.SelectionSummary);
        Assert.Contains("retrieval:", snapshot.SelectionSummary!);
        Assert.Contains("answer:", snapshot.SelectionSummary!);
        Assert.Contains("chapters=[2, 3]", snapshot.SelectionSummary!);
        Assert.Contains("citedGuides=[export]", snapshot.SelectionSummary!);
        Assert.Contains("history=4/6", snapshot.SelectionSummary!);
    }

    /// <summary>
    /// The capture is filled by the SERVICE at its own log site, not by the store guessing. Without this,
    /// the snapshot test above would pass over a capture nothing in production ever writes to.
    /// </summary>
    [Fact]
    public async Task Service_FillsTheCapture_AtItsCitationLogSite()
    {
        var capture = new ProductChatGroundingCapture();
        Assert.Null(capture.CitationSummary);

        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AiResponse { Content = "An answer.", Provider = "test", Model = "test-model" });

        var service = new ProductChatService(
            new GuidesCorpusReader(
                ProductChatCorpusTests.RealGuidesDirectory(),
                ProductChatCorpusTests.NullLoggerFor<GuidesCorpusReader>()),
            router.Object,
            Options.Create(ProductChatBudgetTests.AiConfig()),
            new ProductChatBudgetTests.ThrowingBookChatContextReader(),
            ProductChatCorpusTests.NullLoggerFor<ProductChatService>(),
            capture);

        var response = await service.AnswerAsync(
            new ProductChatRequest("How do I export a book?", Language: "en"), CancellationToken.None);

        Assert.True(response.IsGrounded);
        Assert.NotNull(capture.CitationSummary);
        Assert.Contains("instructionChars=", capture.CitationSummary!);
        // The book half never ran, so it is absent rather than blank - a book-less turn is the normal case.
        Assert.Null(capture.RetrievalSummary);
        Assert.StartsWith("answer:", capture.Compose());
    }

    /// <summary>
    /// THE CONTAINER FILLS THE OPTIONAL CAPTURE PARAMETER, AND FILLS IT WITH THE SCOPE'S OWN INSTANCE.
    ///
    /// <para>This is the one thing about C1's wiring that is not a compile error when it is wrong. The
    /// capture is an OPTIONAL constructor parameter (so the composed-prompt pin tests construct the service
    /// unchanged), which means a missing registration would not fail startup - the container would quietly
    /// pass the default null and every stored snapshot would carry a null <c>selectionSummary</c> forever.
    /// So the resolution is exercised through a real container, and the instance the SERVICE was given is
    /// proved to be the instance the CONTROLLER would read by pulling it out of the same scope after the
    /// answer.</para>
    ///
    /// <para>The registration lines themselves are checked below, because this test mirrors Program.cs
    /// rather than executing it.</para>
    /// </summary>
    [Fact]
    public async Task TheContainer_FillsTheOptionalCapture_WithTheScopesOwnInstance()
    {
        var router = new Mock<IAiRouter>();
        router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(new AiResponse { Content = "An answer.", Provider = "test", Model = "test-model" });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(_ => new GuidesCorpusReader(
            ProductChatCorpusTests.RealGuidesDirectory(),
            ProductChatCorpusTests.NullLoggerFor<GuidesCorpusReader>()));
        services.AddSingleton(router.Object);
        services.Configure<AiOptions>(o =>
        {
            var config = ProductChatBudgetTests.AiConfig();
            o.DefaultProvider = config.DefaultProvider;
            o.DefaultModel = config.DefaultModel;
            o.FeatureModels = config.FeatureModels;
            o.ProviderSettings = config.ProviderSettings;
        });
        services.AddScoped<IBookChatContextReader, ProductChatBudgetTests.ThrowingBookChatContextReader>();
        // The two lines under test, exactly as Program.cs registers them.
        services.AddScoped<ProductChatGroundingCapture>();
        services.AddScoped<ProductChatService>();

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var service = scope.ServiceProvider.GetRequiredService<ProductChatService>();
        var response = await service.AnswerAsync(
            new ProductChatRequest("How do I export a book?", Language: "en"), CancellationToken.None);

        Assert.True(response.IsGrounded);

        var capture = scope.ServiceProvider.GetRequiredService<ProductChatGroundingCapture>();
        Assert.NotNull(capture.CitationSummary);
    }

    /// <summary>
    /// Program.cs really does register both, so the mechanism proved above is the mechanism production
    /// uses. A text check is a weak instrument on its own; paired with the resolution test above it covers
    /// the only two ways this wiring can be wrong.
    /// </summary>
    [Fact]
    public void ProgramCs_RegistersTheCaptureAndTheStore()
    {
        var program = System.IO.File.ReadAllText(
            ProductChatCorpusTests.FindUpward(System.IO.Path.Combine("Pagedraft.Api", "Program.cs")));

        Assert.Contains("AddScoped<ProductChatGroundingCapture>()", program);
        Assert.Contains("AddScoped<ChatConversationStore>()", program);
    }

    // ─── Lifecycle ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NoConversationId_CreatesOne_WithADeterministicTitle()
    {
        await using var db = NewDb();
        var store = NewStore(db, out _, out _);

        var pending = await store.BeginExchangeAsync(
            new ProductChatRequest("How do I split a chapter?", Language: "en"), CancellationToken.None);

        var conversation = await db.Conversations.SingleAsync();
        Assert.Equal(pending.ConversationId, conversation.Id);
        Assert.Equal("How do I split a chapter?", conversation.Title);
    }

    [Fact]
    public async Task ThreadedConversationId_AppendsToTheSameConversation()
    {
        await using var db = NewDb();
        var store = NewStore(db, out _, out _);

        var first = await store.BeginExchangeAsync(
            new ProductChatRequest("First question", Language: "en"), CancellationToken.None);
        await store.CompleteExchangeAsync(first, Grounded("First answer"), CancellationToken.None);

        var second = await store.BeginExchangeAsync(
            new ProductChatRequest("Second question", Language: "en", ConversationId: first.ConversationId),
            CancellationToken.None);
        await store.CompleteExchangeAsync(second, Grounded("Second answer"), CancellationToken.None);

        Assert.Equal(first.ConversationId, second.ConversationId);

        var conversation = await db.Conversations.SingleAsync();
        Assert.Equal(4, conversation.MessageCount);

        var sequences = await db.ConversationMessages.OrderBy(m => m.Sequence)
            .Select(m => m.Sequence).ToListAsync();
        Assert.Equal(new[] { 0, 1, 2, 3 }, sequences);
        // The title is derived ONCE, from the first message, and a later question does not rewrite it.
        Assert.Equal("First question", conversation.Title);
    }

    [Fact]
    public async Task StaleConversationId_StartsANewConversation_RatherThanRefusingTheQuestion()
    {
        await using var db = NewDb();
        var store = NewStore(db, out _, out var logs);

        var stale = Guid.NewGuid();
        var pending = await store.BeginExchangeAsync(
            new ProductChatRequest("A question", Language: "en", ConversationId: stale),
            CancellationToken.None);

        Assert.True(pending.Persisted);
        Assert.NotEqual(stale, pending.ConversationId);
        Assert.Contains(logs.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("does not exist"));
    }

    // ─── The failure posture ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A BROKEN WRITE MUST NOT COST THE AUTHOR AN ANSWER, AND MUST NOT BE SILENT. Both halves are
    /// asserted: the call returns rather than throwing, AND the fault is surfaced on the returned record
    /// AND the log line carries the exception. Surfacing only one of the three is the recorded failure
    /// shape where a fail-safe swallow blinds the outer error path.
    /// </summary>
    [Fact]
    public async Task BrokenUserTurnWrite_IsNonFatal_ButSurfacedAndLoggedWithItsException()
    {
        var db = NewDb();
        var store = NewStore(db, out _, out var logs);
        await db.DisposeAsync();

        var pending = await store.BeginExchangeAsync(
            new ProductChatRequest("A question", Language: "en"), CancellationToken.None);

        Assert.False(pending.Persisted);
        Assert.Equal(ChatPersistenceFaults.UserTurnWriteFailed, pending.Fault);

        var errors = logs.Entries.Where(e => e.Level == LogLevel.Error).ToList();
        Assert.NotEmpty(errors);
        Assert.All(errors, e => Assert.NotNull(e.Exception));
        Assert.Contains(errors, e => e.Message.Contains("could not persist the USER turn"));
    }

    [Fact]
    public async Task BrokenAssistantTurnWrite_StillReturnsTheAnswer_AndSurfacesTheFault()
    {
        var db = NewDb();
        var store = NewStore(db, out _, out var logs);

        var pending = await store.BeginExchangeAsync(
            new ProductChatRequest("A question", Language: "en"), CancellationToken.None);
        Assert.True(pending.Persisted);

        await db.DisposeAsync();

        var completed = await store.CompleteExchangeAsync(
            pending, Grounded("An answer."), CancellationToken.None);

        Assert.Equal("An answer.", completed.Response.Answer);
        Assert.Equal(ChatPersistenceFaults.AssistantTurnWriteFailed, completed.Fault);
        Assert.Null(completed.Response.AssistantMessageId);
        Assert.Contains(logs.Entries, e =>
            e.Level == LogLevel.Error &&
            e.Exception != null &&
            e.Message.Contains("could not persist the ASSISTANT turn"));
    }

    // ─── Cancellation: absorbed by exactly one of the two writes ────────────────────────────────────

    /// <summary>
    /// THE ORDINARY CASE THAT HAD NO TEST AT ALL: the author closes the tab while a local-GPU answer is
    /// still generating, which takes tens of seconds. Everything after <c>AnswerAsync</c> returns is
    /// bookkeeping over an answer that ALREADY EXISTS and whose user turn is ALREADY COMMITTED, so
    /// honouring the request token there would not save work, it would destroy it - leaving the stored
    /// conversation permanently ending on an unanswered question, which is the exact state
    /// <c>CompleteExchangeAsync</c>'s own Error log warns about, and leaving C2 no row to hang feedback on.
    /// </summary>
    [Fact]
    public async Task CancelledRequest_StillPersistsTheAssistantTurn_BecauseTheAnswerAlreadyExists()
    {
        await using var db = NewDb();
        var store = NewStore(db, out _, out _);
        using var cts = new CancellationTokenSource();

        var pending = await store.BeginExchangeAsync(
            new ProductChatRequest("How do I export a book?", Language: "en"), cts.Token);
        Assert.True(pending.Persisted);

        // The author walks away. The answer is produced anyway - AnswerAsync has already returned by the
        // time CompleteExchangeAsync is reached.
        cts.Cancel();

        CompletedChatExchange? completed = null;
        Exception? escaped = null;
        try
        {
            completed = await store.CompleteExchangeAsync(
                pending, Grounded("Use the export panel."), cts.Token);
        }
        catch (Exception ex)
        {
            escaped = ex;
        }

        // THE ROW IS ASSERTED FIRST, ON PURPOSE. A red here must name the product defect - the answer was
        // lost - rather than whatever the abandoned call happened to throw on its way out, which is a
        // symptom and could just as easily be a broken test.
        var messages = await db.ConversationMessages.OrderBy(m => m.Sequence).ToListAsync();
        var assistant = messages.SingleOrDefault(m => m.Role == ChatMessageRoles.Assistant);
        Assert.True(assistant != null,
            "the ASSISTANT turn was not persisted after the request was cancelled, so the stored " +
            "conversation ends permanently on an unanswered question and C2 has no row to hang feedback " +
            "on. The answer already existed; only the bookkeeping was dropped" +
            (escaped == null ? "." : $", abandoned by {escaped.GetType().Name}."));

        Assert.Equal("Use the export panel.", assistant!.Text);
        Assert.False(assistant.Failed);
        // Non-vacuity: the user turn really did land before the cancellation, so the assertion above is
        // about the ANSWER and not about an empty conversation that never started.
        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatMessageRoles.User, messages[0].Role);
        Assert.Equal(2, (await db.Conversations.SingleAsync()).MessageCount);

        // And the caller is handed a completed exchange rather than an exception, carrying the threading
        // ids - the response a client that reconnects needs, and the ids C2 keys feedback on.
        Assert.Null(escaped);
        Assert.NotNull(completed);
        Assert.Null(completed!.Fault);
        Assert.Equal(pending.ConversationId, completed.Response.ConversationId);
        Assert.Equal(pending.UserMessageId, completed.Response.UserMessageId);
        Assert.Equal(assistant.Id, completed.Response.AssistantMessageId);
    }

    /// <summary>
    /// The failed-exchange arm re-READS the user turn to flag it, and that read is part of the same write
    /// unit - a read cancelled halfway would leave the save with a half-built exchange. This is the cell
    /// that catches it being left on the request token when the save was moved off it.
    /// </summary>
    [Fact]
    public async Task CancelledRequest_StillFlagsBOTHTurnsOfAFailedExchange()
    {
        await using var db = NewDb();
        var store = NewStore(db, out _, out _);
        using var cts = new CancellationTokenSource();

        var pending = await store.BeginExchangeAsync(
            new ProductChatRequest("What happened?", Language: "en"), cts.Token);
        Assert.True(pending.Persisted);

        cts.Cancel();

        CompletedChatExchange? completed = null;
        Exception? escaped = null;
        try
        {
            completed = await store.CompleteExchangeAsync(
                pending,
                new ProductChatResponseDto(
                    "I cannot reach the model right now.", Array.Empty<string>(), "en",
                    IsGrounded: false, FaultReason: "model-unavailable"),
                cts.Token);
        }
        catch (Exception ex)
        {
            escaped = ex;
        }

        var messages = await db.ConversationMessages.OrderBy(m => m.Sequence).ToListAsync();
        Assert.True(messages.Count == 2,
            $"a cancelled request left {messages.Count} of the exchange's 2 turns stored, so the failed " +
            "exchange is half-written and the hydration rule that both turns carry Failed cannot hold" +
            (escaped == null ? "." : $" (abandoned by {escaped.GetType().Name})."));

        Assert.All(messages, m => Assert.True(m.Failed,
            $"the {m.Role} turn of a failed exchange was stored UNFLAGGED after a cancellation, so a " +
            "resumed conversation would resend a question the live session had cut."));

        Assert.Null(escaped);
        Assert.NotNull(completed);
        Assert.Null(completed!.Fault);
        Assert.NotNull(completed.Response.AssistantMessageId);
    }

    /// <summary>
    /// THE OTHER HALF OF THE ASYMMETRY, PINNED SO IT READS AS A DECISION. The USER turn is written BEFORE
    /// the answer exists, so a cancelled request there is worth dropping: nobody is waiting for an answer,
    /// and writing the question anyway would MANUFACTURE the dangling-question state the assistant-turn
    /// write goes out of its way to prevent. This case exists so a future reader who sees
    /// <c>CancellationToken.None</c> in one method does not "make it consistent" in the other.
    /// </summary>
    [Fact]
    public async Task CancelledBeforeTheAnswerExists_DropsTheUserTurn_RatherThanStoringADanglingQuestion()
    {
        await using var db = NewDb();
        var store = NewStore(db, out _, out _);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.BeginExchangeAsync(
                new ProductChatRequest("A question nobody is waiting for", Language: "en"), cts.Token));

        Assert.Empty(await db.ConversationMessages.ToListAsync());
    }

    [Fact]
    public async Task UnpersistedExchange_LeavesTheResponseUnthreaded_RatherThanInventingIds()
    {
        await using var db = NewDb();
        var store = NewStore(db, out _, out _);

        var completed = await store.CompleteExchangeAsync(
            PendingChatExchange.Failed(ChatPersistenceFaults.UserTurnWriteFailed),
            Grounded("An answer."),
            CancellationToken.None);

        Assert.Equal(ChatPersistenceFaults.UserTurnWriteFailed, completed.Fault);
        Assert.Null(completed.Response.ConversationId);
        Assert.Null(completed.Response.UserMessageId);
        Assert.Null(completed.Response.AssistantMessageId);
        Assert.Empty(await db.ConversationMessages.ToListAsync());
    }

    // ─── The (ConversationId, Sequence) unique index ────────────────────────────────────────────────

    /// <summary>
    /// THE INDEX IS A DELIBERATE DESIGN AND THIS IS THE MODEL-LEVEL HALF OF PINNING IT.
    /// <c>AppDbContext</c> declares <c>(ConversationId, Sequence)</c> unique because "two turns claiming
    /// the same slot is the shape a lost increment would take" - <see cref="ConversationMessage.Sequence"/>
    /// is the ordinal read off <c>Conversation.MessageCount</c> at insert time, so a duplicate means the
    /// count went backwards and the transcript's order of record is no longer an order.
    ///
    /// <para>This asserts the DECLARATION, which is the only part of it a test on an in-memory provider
    /// can reach. See the collision case below for what is and is not covered about ENFORCEMENT.</para>
    /// </summary>
    [Fact]
    public void TheModel_DeclaresConversationIdAndSequence_UNIQUE()
    {
        using var db = NewDb();

        var entity = db.Model.FindEntityType(typeof(ConversationMessage));
        Assert.NotNull(entity);

        var indexes = entity!.GetIndexes().ToList();
        // NON-VACUITY: the entity really does declare indexes, so a null match below means THIS index is
        // missing rather than that the whole entity fell out of the model.
        Assert.NotEmpty(indexes);

        var slot = indexes.SingleOrDefault(i => i.Properties.Select(p => p.Name).SequenceEqual(
            new[] { nameof(ConversationMessage.ConversationId), nameof(ConversationMessage.Sequence) }));

        Assert.True(slot != null,
            "the (ConversationId, Sequence) index is gone from the model, so nothing at the database level " +
            "stops two turns claiming the same ordinal - the shape a lost MessageCount increment takes.");
        Assert.True(slot!.IsUnique,
            "the (ConversationId, Sequence) index is no longer UNIQUE, so a lost MessageCount increment " +
            "would write a second turn into an occupied slot and the transcript's order of record would " +
            "silently stop being an order.");
    }

    /// <summary>
    /// TWO TURNS COLLIDING ON THE SAME SLOT MUST BEHAVE LIKE EVERY OTHER PERSISTENCE FAILURE HERE: caught,
    /// surfaced on <see cref="CompletedChatExchange"/>, logged at Error WITH the exception, and the answer
    /// still handed back. The author asked a question and got one; a bookkeeping clash is not theirs to pay
    /// for.
    ///
    /// <para><b>WHAT THIS DOES NOT COVER, STATED PLAINLY.</b> The EF Core IN-MEMORY provider does NOT
    /// enforce unique indexes. That was MEASURED here rather than assumed: run against a plain
    /// <see cref="NewDb"/>, the staged duplicate below saves cleanly and <c>Fault</c> comes back null, so
    /// the provider accepted two turns in slot 1 without complaint. A collision test written that way
    /// would therefore be measuring nothing about the index - it would behave identically with the index
    /// deleted from the model. So the enforcement is supplied by
    /// <see cref="UniqueSequenceEnforcingDbContext"/>, which raises the same <see cref="DbUpdateException"/>
    /// SQL Server would raise, at the same moment (inside <c>SaveChangesAsync</c>), for exactly the
    /// duplicate <c>(ConversationId, Sequence)</c> pair the real index would reject. What is therefore
    /// pinned is the STORE'S HANDLING of the collision and the collision-shaped stimulus that provokes it;
    /// what is NOT pinned is that the database itself rejects the row. Covering that would need this test
    /// to run against a real SQL Server / LocalDB instance (an <c>EnsureCreated</c> against the migration,
    /// not an in-memory context), which no test in this suite does today. The declaration half is pinned
    /// by <see cref="TheModel_DeclaresConversationIdAndSequence_UNIQUE"/> above; together they cover the
    /// index being declared and the fault being handled, and leave only "the provider honours what is
    /// declared" to the database.</para>
    /// </summary>
    [Fact]
    public async Task SequenceCollision_IsCaught_SurfacedAsAFault_AndStillReturnsTheAnswer()
    {
        await using var db = NewUniqueSequenceEnforcingDb();
        var store = NewStore(db, out _, out var logs);

        var pending = await store.BeginExchangeAsync(
            new ProductChatRequest("How do I export a book?", Language: "en"), CancellationToken.None);
        Assert.True(pending.Persisted);

        // THE LOST INCREMENT, STAGED. A concurrent turn has already claimed slot 1 - the ordinal this
        // exchange's answer is about to take, because the MessageCount this context read is still 1. That
        // is precisely the state the unique index exists to refuse.
        db.ConversationMessages.Add(new ConversationMessage
        {
            Id = Guid.NewGuid(),
            ConversationId = pending.ConversationId!.Value,
            Sequence = 1,
            Role = ChatMessageRoles.Assistant,
            Text = "An answer that got to the slot first",
            Failed = false
        });
        await db.SaveChangesAsync();

        // NON-VACUITY: the slot really is occupied before the write under test, so the failure below is a
        // collision and not an empty-database accident.
        var before = await db.ConversationMessages.AsNoTracking()
            .Where(m => m.ConversationId == pending.ConversationId!.Value)
            .OrderBy(m => m.Sequence).ToListAsync();
        Assert.Equal(new[] { 0, 1 }, before.Select(m => m.Sequence));

        var completed = await store.CompleteExchangeAsync(
            pending, Grounded("Use the export panel."), CancellationToken.None);

        // (1) The author still gets the answer. The call returns; it does not throw.
        Assert.Equal("Use the export panel.", completed.Response.Answer);
        // (2) The fault is SURFACED, not only logged - a caller that cannot see it is a caller that ships
        // the failure invisibly.
        Assert.Equal(ChatPersistenceFaults.AssistantTurnWriteFailed, completed.Fault);
        // (3) And no id is invented for a row that does not exist.
        Assert.Null(completed.Response.AssistantMessageId);

        // (4) Logged at Error, WITH the exception, and the exception is the collision rather than some
        // other write failure that happened to land in the same catch.
        var error = Assert.Single(
            logs.Entries,
            e => e.Level == LogLevel.Error && e.Message.Contains("could not persist the ASSISTANT turn"));
        Assert.NotNull(error.Exception);
        Assert.IsType<DbUpdateException>(error.Exception);
        Assert.Contains("ConversationId", error.Exception!.Message);
        Assert.Contains("Sequence", error.Exception.Message);

        // (5) Nothing landed in the contested slot: the transcript is exactly what it was, so the store
        // failed the write rather than overwriting the turn that got there first.
        var after = await db.ConversationMessages.AsNoTracking()
            .Where(m => m.ConversationId == pending.ConversationId!.Value)
            .OrderBy(m => m.Sequence).ToListAsync();
        Assert.Equal(new[] { 0, 1 }, after.Select(m => m.Sequence));
        Assert.Equal("An answer that got to the slot first", after[1].Text);
    }

    // ─── Title derivation (pure) ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Title_ShortMessage_IsUsedWhole()
        => Assert.Equal("How do I export?", ConversationTitle.FromFirstMessage("  How do I export?  "));

    [Fact]
    public void Title_CollapsesInteriorWhitespace_BecauseItRendersOnOneLine()
        => Assert.Equal("How do I export?", ConversationTitle.FromFirstMessage("How do\n I    export?"));

    [Fact]
    public void Title_LongMessage_IsCutAtAWordBoundary_AndMarkedAsCut()
    {
        var question = string.Join(' ', Enumerable.Repeat("export", 40));
        var title = ConversationTitle.FromFirstMessage(question);

        Assert.True(question.Length > ConversationTitle.MaxLength);
        Assert.EndsWith(ConversationTitle.Ellipsis, title);
        Assert.True(title.Length <= ConversationTitle.MaxLength + ConversationTitle.Ellipsis.Length);
        Assert.DoesNotContain("expo...", title);
    }

    [Fact]
    public void Title_LongUnbrokenMessage_TakesTheHardCut_RatherThanEmptyingItself()
    {
        var question = new string('x', 300);
        var title = ConversationTitle.FromFirstMessage(question);

        Assert.Equal(new string('x', ConversationTitle.MaxLength) + ConversationTitle.Ellipsis, title);
    }

    [Fact]
    public void Title_BlankMessage_FallsBackRatherThanStoringAnEmptyTitle()
        => Assert.Equal(ConversationTitle.Untitled, ConversationTitle.FromFirstMessage("   "));

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────

    private static ProductChatResponseDto Grounded(string answer) => new(
        answer, new[] { "export" }, "en", IsGrounded: true, FaultReason: null,
        ArtifactRefs: Array.Empty<string>());

    private static AppDbContext NewDb() => new(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static AppDbContext NewUniqueSequenceEnforcingDb() => new UniqueSequenceEnforcingDbContext(
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    /// <summary>
    /// THE ONE INDEX THE IN-MEMORY PROVIDER WILL NOT KEEP FOR US, kept here instead.
    ///
    /// <para>EF Core's in-memory provider holds no indexes and enforces no uniqueness, so a duplicate
    /// <c>(ConversationId, Sequence)</c> inserted against a plain <see cref="NewDb"/> saves cleanly. A
    /// collision test written that way would pass with the index deleted from the model, which is the
    /// worst kind of green. This context raises the same <see cref="DbUpdateException"/> SQL Server would
    /// raise, at the same point (inside the save), for exactly the pairs the declared unique index would
    /// reject - both duplicates WITHIN one batch and duplicates against already-stored rows.</para>
    ///
    /// <para>It is a stand-in for the database, not a proof of it. See
    /// <c>SequenceCollision_IsCaught_SurfacedAsAFault_AndStillReturnsTheAnswer</c> for what that does and
    /// does not cover.</para>
    /// </summary>
    private sealed class UniqueSequenceEnforcingDbContext : AppDbContext
    {
        public UniqueSequenceEnforcingDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            var added = ChangeTracker.Entries<ConversationMessage>()
                .Where(e => e.State == EntityState.Added)
                .Select(e => e.Entity)
                .ToList();

            (Guid ConversationId, int Sequence)? clash = null;

            var withinTheBatch = added
                .GroupBy(m => (m.ConversationId, m.Sequence))
                .FirstOrDefault(g => g.Count() > 1);
            if (withinTheBatch != null) clash = withinTheBatch.Key;

            if (clash == null)
            {
                var againstTheStore = added.FirstOrDefault(m => ConversationMessages
                    .AsNoTracking()
                    .Any(s => s.ConversationId == m.ConversationId && s.Sequence == m.Sequence && s.Id != m.Id));
                if (againstTheStore != null)
                    clash = (againstTheStore.ConversationId, againstTheStore.Sequence);
            }

            if (clash != null)
            {
                throw new DbUpdateException(
                    "Cannot insert duplicate key row in object 'dbo.ConversationMessages' with unique index " +
                    "'IX_ConversationMessages_ConversationId_Sequence'. The duplicate key value is " +
                    $"({clash.Value.ConversationId}, {clash.Value.Sequence}).",
                    new InvalidOperationException("Violation of UNIQUE KEY constraint."));
            }

            return base.SaveChangesAsync(cancellationToken);
        }
    }

    private static ChatConversationStore NewStore(
        AppDbContext db, out ProductChatGroundingCapture capture, out ExceptionCapturingLogger logs)
    {
        capture = new ProductChatGroundingCapture();
        logs = new ExceptionCapturingLogger();
        return new ChatConversationStore(db, capture, logs);
    }

    /// <summary>
    /// Captures the EXCEPTION as well as the message, because "it logged something" is not the property
    /// under test: a catch that logs a sentence without the exception it caught is the same blind spot as
    /// one that logs nothing.
    /// </summary>
    internal sealed class ExceptionCapturingLogger : ILogger<ChatConversationStore>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
