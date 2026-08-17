using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Pagedraft.Api.Controllers;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;
using Pagedraft.Api.Services.Chat;
using Xunit;

namespace Pagedraft.Api.Tests;

/// <summary>
/// THE PIN THAT MAKES "NO RE-GATE NEEDED" A PROOF RATHER THAN AN ASSUMPTION (Show C1, d1 section (4)).
///
/// <para>The acceptance criterion, verbatim from the plan: <i>the composed prompt sent for the NEXT
/// question in a conversation hydrated from storage must be byte-identical to the composed prompt an
/// unbroken client session would have sent for that same next question.</i> The chat prompt path is the
/// subject of seven live gates and a measured verdict stack; C1 is a persistence feature, and the only
/// thing that makes it safe to ship without re-measuring any of that is that the prompt does not move by
/// one byte. These tests are the instrument that says so.</para>
///
/// <para>WHAT IS COMPARED IS THE WHOLE COMPOSED <c>instruction</c> STRING captured off the
/// <see cref="IAiRouter"/> seam - the exact bytes the model would receive - not a substring, not a token
/// count, and not the answer. A substring proxy would pass while the history block differed.</para>
///
/// <para>NON-VACUITY IS ASSERTED, NOT ASSUMED. Every case proves (a) that the hydrated window it built is
/// non-empty and actually contains the turns it claims, and (b) that a window built WITHOUT the exclusion
/// under test composes a DIFFERENT prompt - so a green run cannot mean "history never reached the
/// prompt".</para>
///
/// <para>WHERE THE CRITERION DOES NOT HOLD, so this file is not read as claiming more than it proves.
/// A retry is recorded nowhere - neither <c>ProductChatRequest</c> nor <c>ConversationMessage</c> carries
/// a "re-asks turn N" reference - so the client DERIVES it from stored text and cannot tell a retry from
/// an author who retyped the same question by hand. In that one input the resumed window carries the
/// question once where the live session carried it twice: a strict SUBSET of live, which drops context
/// rather than inflating what the model reads. The full cell matrix is in
/// <c>show-c1-history-fixes-2026-08-16</c> (<c>final-r02</c>), which also shows why the divergence can
/// only ever go that way. Nothing here or in the client claims byte-identity for that cell.</para>
///
/// <para>NO MODEL, NO GPU, NO NETWORK: the router is a stub and the database is in-memory.</para>
/// </summary>
public class ProductChatConversationHydrationPinTests
{
    private const string Q1 = "How do I export a book to DOCX?";
    private const string Q2 = "And can I export a single chapter?";
    private const string QOtherBook = "What is the review status of this manuscript?";
    private const string QFailing = "What happened to my last analysis run?";
    private const string QNext = "Where do the exported files end up?";

    /// <summary>
    /// THE ACCEPTANCE CRITERION ITSELF. A conversation is held live (two good exchanges, one exchange
    /// asked inside a DIFFERENT book, one exchange that fail-safes), then the NEXT question is composed
    /// twice: once from the transcript an unbroken session would still be holding, and once from a window
    /// hydrated out of the persisted rows. The two composed prompts must be byte-identical.
    /// </summary>
    [Fact]
    public async Task NextQuestion_ComposedFromHydratedStorage_IsByteIdenticalToUnbrokenSession()
    {
        var otherBookId = Guid.NewGuid();
        await using var harness = new Harness();

        // ─── The live session ────────────────────────────────────────────────────────────────────
        var a1 = await harness.AskAsync(Q1, answer: "Use the export panel.");
        var a2 = await harness.AskAsync(Q2, answer: "Yes, chapter export is its own action.");
        var a3 = await harness.AskAsync(QOtherBook, answer: "The review has not run.", bookId: otherBookId);
        var failed = await harness.AskFailingAsync(QFailing);

        Assert.True(a1.IsGrounded);
        Assert.True(a2.IsGrounded);
        Assert.True(a3.IsGrounded);
        Assert.False(failed.IsGrounded);
        Assert.NotNull(a1.ConversationId);
        Assert.Equal(a1.ConversationId, failed.ConversationId);

        // The transcript an unbroken session still holds, composed by the CLIENT's rules exactly as
        // chatbot phase B shipped them: the REFUSAL contributes no turn, and a turn captured in another
        // book is retained on screen but never sent.
        //
        // QFailing IS IN THIS WINDOW, LAST, and that is the correction this literal needed. The client's
        // `ask()` appends the author's turn before the request goes out and `acceptFault` never removes
        // it - only `retry()` cuts the pair - so a failure the author did not retry leaves its question
        // in the live transcript and `historyForServer()` sends it. Omitting it here was a wrong model of
        // the client, and it is how a false byte-identity criterion passed: the hydration under
        // comparison was matched against a window no live session ever holds.
        var unbrokenWindow = new List<ProductChatTurnDto>
        {
            new(ChatMessageRoles.User, Q1),
            new(ChatMessageRoles.Assistant, a1.Answer),
            new(ChatMessageRoles.User, Q2),
            new(ChatMessageRoles.Assistant, a2.Answer),
            new(ChatMessageRoles.User, QFailing)
        };

        // ─── The hydrated session ────────────────────────────────────────────────────────────────
        var stored = await harness.ReadMessagesAsync(a1.ConversationId!.Value);

        // VACUITY GUARD ON THE POPULATION: everything the exclusions below are supposed to remove has to
        // be in the store in the first place, or "the windows match" would say nothing.
        Assert.Equal(8, stored.Count);
        Assert.Equal(2, stored.Count(m => m.AskBookId == otherBookId));
        Assert.Equal(2, stored.Count(m => m.Failed));
        Assert.Contains(stored, m => m.Role == ChatMessageRoles.User && m.Text == QFailing);

        var hydratedWindow = Hydrate(stored, currentBookId: null);

        // VACUITY GUARD ON THE WINDOW: it is non-empty and holds the turns it claims to. The two halves
        // of the failed exchange are asserted FIRST and by content, so a helper that drops the wrong one
        // fails on a message naming the turn rather than on a bare count.
        Assert.Contains(hydratedWindow, t => t.Content == QFailing);
        // The refusal is the half that never goes back up. Read off the STORED row rather than the DTO,
        // so this cannot pass by comparing against something the store never wrote.
        var storedRefusal = Assert.Single(stored, m => m.Failed && m.Role == ChatMessageRoles.Assistant);
        Assert.False(string.IsNullOrWhiteSpace(storedRefusal.Text));
        Assert.DoesNotContain(hydratedWindow, t => t.Content == storedRefusal.Text);
        Assert.Equal(5, hydratedWindow.Count);
        Assert.Equal(unbrokenWindow.Select(t => t.Content), hydratedWindow.Select(t => t.Content));
        Assert.Equal(unbrokenWindow.Select(t => t.Role), hydratedWindow.Select(t => t.Role));

        var fromUnbroken = await harness.ComposeAsync(QNext, unbrokenWindow);
        var fromHydrated = await harness.ComposeAsync(QNext, hydratedWindow);

        Assert.Equal(fromUnbroken, fromHydrated);

        // ─── The control: the comparison above is capable of failing ─────────────────────────────
        //
        // Composing from the UNFILTERED stored rows (failed exchange and other-book exchange included)
        // must produce a DIFFERENT prompt. Without this, a bug that dropped history from the prompt
        // entirely would make every assertion above pass.
        var unfiltered = stored
            .Select(m => new ProductChatTurnDto(m.Role, m.Text))
            .ToList();
        Assert.Equal(8, unfiltered.Count);
        var fromUnfiltered = await harness.ComposeAsync(QNext, unfiltered);
        Assert.NotEqual(fromUnbroken, fromUnfiltered);
    }

    /// <summary>
    /// THE FULL TURN TEXT SURVIVES STORAGE. The per-turn 1000-character cap is a SERVER-side property of
    /// prompt composition (<c>ProductChatService.MaxHistoryTurnChars</c>), applied to whatever the client
    /// sends; storing a pre-truncated copy would be a second truncation site that drifts from that
    /// constant the day it is retuned, and the hydrated window would then compose a prompt the unbroken
    /// session never would have.
    /// </summary>
    [Fact]
    public async Task StoredTurnText_IsFullAndUntruncated_SoTheHydratedPromptStillMatches()
    {
        var longQuestion = "How do I export? " + new string('x', ProductChatService.MaxHistoryTurnChars * 2);
        await using var harness = new Harness();

        var first = await harness.AskAsync(longQuestion, answer: "Use the export panel.");
        Assert.NotNull(first.ConversationId);

        var stored = await harness.ReadMessagesAsync(first.ConversationId!.Value);
        var storedQuestion = Assert.Single(stored.Where(m => m.Role == ChatMessageRoles.User));

        // NON-VACUITY: the fixture really is longer than the cap, so "stored in full" is a claim about a
        // string the cap would otherwise have cut.
        Assert.True(longQuestion.Length > ProductChatService.MaxHistoryTurnChars);
        Assert.Equal(longQuestion.Length, storedQuestion.Text.Length);
        Assert.Equal(longQuestion, storedQuestion.Text);

        var unbrokenWindow = new List<ProductChatTurnDto>
        {
            new(ChatMessageRoles.User, longQuestion),
            new(ChatMessageRoles.Assistant, first.Answer)
        };
        var hydratedWindow = Hydrate(stored, currentBookId: null);
        Assert.Equal(2, hydratedWindow.Count);

        Assert.Equal(
            await harness.ComposeAsync(QNext, unbrokenWindow),
            await harness.ComposeAsync(QNext, hydratedWindow));
    }

    /// <summary>
    /// A RETRIED FAILURE IS CARRIED ONCE, NOT TWICE - the mirror of the case above, on a conversation
    /// short enough that the 8-turn slice cannot hide the difference.
    ///
    /// <para>The client's <c>retry()</c> removes the user turn AND the fault before re-asking, so a live
    /// transcript that retried holds ONE copy of that question. The server keeps the flagged pair as well
    /// as the retry's fresh pair, so the store holds the same question TWICE, byte-equal (the re-ask goes
    /// through the ordinary path with <c>entry.question</c> verbatim, and both writes are trimmed the same
    /// way by <c>ChatConversationStore.BeginExchangeAsync</c>). Replaying both would make the resumed
    /// window a SUPERSET of the live one, which inflates what the model reads rather than merely dropping
    /// context.</para>
    /// </summary>
    [Fact]
    public async Task HydratedWindow_ForARetriedFailure_CarriesTheQuestionOnce_LikeTheLiveTranscript()
    {
        await using var harness = new Harness();

        var failed = await harness.AskFailingAsync(QFailing);
        var retried = await harness.AskAsync(QFailing, answer: "The run finished at 10:04.");

        Assert.False(failed.IsGrounded);
        Assert.True(retried.IsGrounded);
        Assert.NotNull(failed.ConversationId);
        Assert.Equal(failed.ConversationId, retried.ConversationId);

        var stored = await harness.ReadMessagesAsync(failed.ConversationId!.Value);

        // VACUITY GUARD ON THE POPULATION: the store really does hold that question twice, byte-equal,
        // and really does flag both halves of the failed pair. Without this the comparison below could
        // pass over a store that never wrote the duplicate at all.
        Assert.Equal(4, stored.Count);
        Assert.Equal(2, stored.Count(m => m.Role == ChatMessageRoles.User && m.Text == QFailing));
        Assert.Equal(2, stored.Count(m => m.Failed));

        // The transcript the live session holds after `retry()`: the flagged pair is gone from it, so the
        // question appears once, followed by the answer the retry got.
        var unbrokenWindow = new List<ProductChatTurnDto>
        {
            new(ChatMessageRoles.User, QFailing),
            new(ChatMessageRoles.Assistant, retried.Answer)
        };

        var hydratedWindow = Hydrate(stored, currentBookId: null);

        // Asserted BY CONTENT first, so a hydration that replays both copies fails on a message naming
        // the duplicated question rather than on a bare count.
        Assert.Equal(
            new[] { QFailing },
            hydratedWindow.Where(t => t.Role == ChatMessageRoles.User).Select(t => t.Content).ToArray());
        var storedRefusal = Assert.Single(stored, m => m.Failed && m.Role == ChatMessageRoles.Assistant);
        Assert.False(string.IsNullOrWhiteSpace(storedRefusal.Text));
        Assert.DoesNotContain(hydratedWindow, t => t.Content == storedRefusal.Text);
        Assert.Equal(2, hydratedWindow.Count);
        Assert.Equal(unbrokenWindow.Select(t => t.Content), hydratedWindow.Select(t => t.Content));
        Assert.Equal(unbrokenWindow.Select(t => t.Role), hydratedWindow.Select(t => t.Role));

        var fromUnbroken = await harness.ComposeAsync(QNext, unbrokenWindow);
        var fromHydrated = await harness.ComposeAsync(QNext, hydratedWindow);
        Assert.Equal(fromUnbroken, fromHydrated);

        // ─── The control: carrying the question twice really does move the prompt ────────────────
        //
        // Without this, a bug that dropped history from the composition entirely would make the equality
        // above pass and the duplicate would cost nothing to allow.
        var doubled = new List<ProductChatTurnDto> { new(ChatMessageRoles.User, QFailing) };
        doubled.AddRange(hydratedWindow);
        var fromDoubled = await harness.ComposeAsync(QNext, doubled);
        Assert.NotEqual(fromUnbroken, fromDoubled);
    }

    /// <summary>
    /// A FAILURE RETRIED INTO A SECOND FAILURE IS STILL CARRIED ONCE - the cell between the two cases
    /// above, and the one a suppression keyed on a later UN-FAILED row cannot see.
    ///
    /// <para><c>retry()</c> cuts the user turn and the fault out before re-asking whatever the second
    /// attempt then does, so the live transcript holds ONE failed pair however many attempts failed. The
    /// store holds one flagged pair per attempt, so replaying each of them would send the question twice:
    /// the SUPERSET divergence again, in the shape it actually has in the field, since a model that is
    /// unreachable for the first attempt is usually unreachable for the retry.</para>
    /// </summary>
    [Fact]
    public async Task HydratedWindow_ForAFailureRetriedIntoASecondFailure_CarriesTheQuestionOnce()
    {
        await using var harness = new Harness();

        var first = await harness.AskFailingAsync(QFailing);
        var second = await harness.AskFailingAsync(QFailing);

        Assert.False(first.IsGrounded);
        Assert.False(second.IsGrounded);
        Assert.NotNull(first.ConversationId);
        Assert.Equal(first.ConversationId, second.ConversationId);

        var stored = await harness.ReadMessagesAsync(first.ConversationId!.Value);

        // VACUITY GUARD ON THE POPULATION: the store really holds that question twice, byte-equal, with
        // every one of the four rows flagged - which is what makes "carried once" a claim about a
        // duplicate that exists rather than about a store that never wrote one.
        Assert.Equal(4, stored.Count);
        Assert.Equal(2, stored.Count(m => m.Role == ChatMessageRoles.User && m.Text == QFailing));
        Assert.Equal(4, stored.Count(m => m.Failed));

        // What the live transcript is holding after the second failure: the first pair was cut out by
        // `retry()`, so one question and no assistant turn at all.
        var unbrokenWindow = new List<ProductChatTurnDto> { new(ChatMessageRoles.User, QFailing) };

        var hydratedWindow = Hydrate(stored, currentBookId: null);

        // By CONTENT first, so replaying both attempts fails on a message naming the duplicated question
        // rather than on a bare count.
        Assert.Equal(
            new[] { QFailing },
            hydratedWindow.Select(t => t.Content).ToArray());
        Assert.Equal(unbrokenWindow.Select(t => t.Role), hydratedWindow.Select(t => t.Role));

        var fromUnbroken = await harness.ComposeAsync(QNext, unbrokenWindow);
        var fromHydrated = await harness.ComposeAsync(QNext, hydratedWindow);
        Assert.Equal(fromUnbroken, fromHydrated);

        // ─── The control: carrying it twice really does move the prompt ──────────────────────────
        var doubled = new List<ProductChatTurnDto> { new(ChatMessageRoles.User, QFailing) };
        doubled.AddRange(hydratedWindow);
        Assert.NotEqual(fromUnbroken, await harness.ComposeAsync(QNext, doubled));
    }

    /// <summary>
    /// A conversation resumed INSIDE the book it was held in keeps its book-scoped turns, which is the
    /// other half of the book filter: the exclusion above must not turn into "book turns are never
    /// resent".
    /// </summary>
    [Fact]
    public async Task HydratedWindow_InsideTheSameBook_KeepsThatBooksTurns()
    {
        var bookId = Guid.NewGuid();
        await using var harness = new Harness();

        var a1 = await harness.AskAsync(Q1, answer: "Use the export panel.", bookId: bookId);
        var a2 = await harness.AskAsync(Q2, answer: "Yes.", bookId: bookId);
        Assert.True(a1.IsGrounded && a2.IsGrounded);

        var stored = await harness.ReadMessagesAsync(a1.ConversationId!.Value);
        Assert.Equal(4, stored.Count);
        Assert.All(stored, m => Assert.Equal(bookId, m.AskBookId));

        var hydratedWindow = Hydrate(stored, currentBookId: bookId);
        Assert.Equal(4, hydratedWindow.Count);

        var unbrokenWindow = new List<ProductChatTurnDto>
        {
            new(ChatMessageRoles.User, Q1),
            new(ChatMessageRoles.Assistant, a1.Answer),
            new(ChatMessageRoles.User, Q2),
            new(ChatMessageRoles.Assistant, a2.Answer)
        };

        Assert.Equal(
            await harness.ComposeAsync(QNext, unbrokenWindow, bookId: bookId),
            await harness.ComposeAsync(QNext, hydratedWindow, bookId: bookId));
    }

    // ─── The hydration rules, replicated from the CLIENT they belong to ─────────────────────────────
    //
    // These are `historyForServer()`'s three rules (pagedraft-client
    // src/app/shared/product-chat/product-chat.component.ts, the `historyForServer` method; rule 1b lives
    // one layer earlier, in src/app/shared/product-chat/conversation-hydration.ts), stated here against the stored
    // rows because c2 implements them in the client and c1 has to prove the STORE holds everything they
    // need. They are deliberately not server code: the client remains the sender of the window, which is
    // exactly why C1 needs no re-gate.
    //
    //   1. Only user/assistant turns are sent (a fault is a different entry type and can never be
    //      selected). The two halves of a failed exchange are NOT symmetrical here, and that asymmetry
    //      is the client's, not this helper's invention: `conversation-hydration.ts` rebuilds a flagged
    //      exchange as the author's `user` turn with a fault under it, which is exactly the pair a live
    //      session holds - `ask()` appends the turn before the request goes out and only `retry()` ever
    //      removes it. So a flagged USER row is a turn and goes up; a flagged ASSISTANT row is the
    //      refusal, becomes the fault, and never does.
    //   1b. UNLESS THE AUTHOR RETRIED IT. `retry()` cuts the user turn and the fault out before
    //      re-asking, so a live transcript holds ONE copy of a retried question while storage holds two
    //      (the flagged pair, and the retry's fresh pair). The client withholds the flagged copy when a
    //      LATER `user` row carries identical text - FAILED OR NOT, because `retry()` removes the pair
    //      whatever the second attempt then does, so a question that failed twice is held once live as
    //      well. It is a derivation from stored text and not a recorded fact: nothing on the wire or in
    //      the schema marks a turn as a retry. Mirrored here for the same reason the rest of this helper
    //      exists.
    //   1c. ONE CLIENT RULE IS DELIBERATELY NOT MIRRORED: `conversation-hydration.ts` emits no `user`
    //      turn for a flagged question whose text is EMPTY (its stand-in for a flagged answer row that
    //      arrived with no question above it). This helper models what the WRITER can produce, and it
    //      cannot produce that row - `ProductChatController.Ask` 400s a blank question before any turn is
    //      written, and this harness drives that real endpoint - so mirroring it would add a branch no
    //      case here can reach. Recorded rather than mirrored, because an unexercised branch in a model
    //      is the drift these comments exist to prevent.
    //   2. Only the last MaxSentTurns are sent, by COUNT, with their FULL text.
    //   3. A turn captured in a book other than the one currently open is retained on screen but not sent.
    //
    // WHAT THIS HELPER IS NOT: a mechanically bound mirror. It is hand-written C# modelling TypeScript in
    // another repository, and it has already been wrong twice in step with the client, both times because
    // one author wrote both sides in one sitting. Nothing compiles them against each other and no test
    // fails if one moves alone; the cost of closing that properly is recorded under
    // `## Closing review findings - deferred` in `show-c1-history-fixes-2026-08-16`. The practical
    // mitigation is to know what it does NOT reach, so its green is not over-read:
    //   - The ENTRY SHAPES. It maps stored rows straight to turns and never builds a fault, a book marker
    //     or a user entry, so nothing about what the author SEES is pinned here. That is
    //     `conversation-hydration.spec.ts`.
    //   - The cells where the derivation is knowingly WRONG (a hand-retyped repeat, in this book or
    //     through another one). Those are pinned as deviations on the client, where the live window they
    //     deviate from can be built and compared. Reproducing them here would model the deviation twice
    //     and bind it no better.
    //   - Everything downstream of the window: `historyForServer()` runs over live entries too, so only
    //     the client suite can compare a resumed window against the live one it must equal.
    private static List<ProductChatTurnDto> Hydrate(
        IReadOnlyList<ConversationMessageDto> stored, Guid? currentBookId)
    {
        var ordered = stored.OrderBy(m => m.Sequence).ToList();

        // The LAST position at which ANY USER row carries each exact text, failed rows included (rule 1b).
        var lastAskedAt = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < ordered.Count; i++)
        {
            var row = ordered[i];
            if (row.Role == ChatMessageRoles.Assistant) continue;
            if (!string.IsNullOrEmpty(row.Text)) lastAskedAt[row.Text] = i;
        }

        var turns = new List<ProductChatTurnDto>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var row = ordered[i];
            if (row.Failed && row.Role == ChatMessageRoles.Assistant) continue;
            if (row.Failed
                && lastAskedAt.TryGetValue(row.Text ?? string.Empty, out var resentAt)
                && resentAt > i)
            {
                continue;
            }
            if (row.AskBookId != null && row.AskBookId != currentBookId) continue;
            turns.Add(new ProductChatTurnDto(row.Role, row.Text));
        }

        return turns.TakeLast(ClientSentTurnCap).ToList();
    }

    /// <summary>
    /// Mirrors the client's own <c>ProductChatService.MaxSentTurns</c>
    /// (pagedraft-client src/app/core/services/product-chat.service.ts, applied in <c>ask()</c> as
    /// <c>history.slice(-MaxSentTurns)</c>). Nothing mechanical keeps the two in step; a change there is a
    /// change here.
    /// </summary>
    private const int ClientSentTurnCap = 8;

    // ─── Harness ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The real controller over the real service, the real store and an in-memory database, with the
    /// router stubbed. Composition, the budget, the history cap and the persistence all run for real; only
    /// the model is absent.
    /// </summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly AppDbContext _db;
        private readonly ProductChatController _controller;
        private readonly List<AiRequest> _sent = new();
        private bool _failNextCall;

        public Harness()
        {
            _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

            var router = new Mock<IAiRouter>();
            router.Setup(r => r.CompleteAsync(It.IsAny<AiRequest>(), It.IsAny<CancellationToken>()))
                  .Returns<AiRequest, CancellationToken>((req, _) =>
                  {
                      _sent.Add(req);
                      if (_failNextCall)
                      {
                          _failNextCall = false;
                          throw new InvalidOperationException("The routed model is unreachable (stub).");
                      }
                      return Task.FromResult(new AiResponse
                      {
                          Content = NextAnswer,
                          Provider = "test",
                          Model = "test-model"
                      });
                  });

            var capture = new ProductChatGroundingCapture();
            var service = new ProductChatService(
                new GuidesCorpusReader(
                    ProductChatCorpusTests.RealGuidesDirectory(),
                    ProductChatCorpusTests.NullLoggerFor<GuidesCorpusReader>()),
                router.Object,
                Options.Create(ProductChatBudgetTests.AiConfig()),
                // A book-scoped turn reads an EMPTY-but-not-blind context, so it takes the ordinary
                // success path without needing a database full of artifacts. What this test pins is the
                // history block, and an empty book context is identical on both compositions.
                new ProductChatBudgetTests.StubBookChatContextReader(BookChatContext.None),
                ProductChatCorpusTests.NullLoggerFor<ProductChatService>(),
                capture);

            var store = new ChatConversationStore(
                _db, capture, NullLogger<ChatConversationStore>.Instance);

            _controller = new ProductChatController(service, store);
            _messages = new ConversationsController(_db, NullLogger<ConversationsController>.Instance);
        }

        private readonly ConversationsController _messages;

        private string NextAnswer { get; set; } = "An answer.";

        public Guid? ConversationId { get; private set; }

        /// <summary>One live exchange through the real endpoint, threading the conversation id.</summary>
        public async Task<ProductChatResponseDto> AskAsync(
            string question, string answer, Guid? bookId = null)
        {
            NextAnswer = answer;
            return await PostAsync(question, bookId);
        }

        /// <summary>One live exchange whose model call throws, i.e. the model-unavailable fail-safe.</summary>
        public async Task<ProductChatResponseDto> AskFailingAsync(string question, Guid? bookId = null)
        {
            _failNextCall = true;
            return await PostAsync(question, bookId);
        }

        private async Task<ProductChatResponseDto> PostAsync(string question, Guid? bookId)
        {
            var result = await _controller.Ask(
                new ProductChatRequest(
                    question,
                    History: Array.Empty<ProductChatTurnDto>(),
                    Language: "en",
                    BookId: bookId,
                    ConversationId: ConversationId),
                CancellationToken.None);

            var dto = Assert.IsType<ProductChatResponseDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
            ConversationId ??= dto.ConversationId;
            return dto;
        }

        /// <summary>The stored transcript, read through the real endpoint rather than the DbSet.</summary>
        public async Task<IReadOnlyList<ConversationMessageDto>> ReadMessagesAsync(Guid conversationId)
        {
            var result = await _messages.Messages(conversationId, ct: CancellationToken.None);
            var dto = Assert.IsType<ConversationMessagesDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
            return dto.Items;
        }

        /// <summary>
        /// Composes one prompt for <paramref name="question"/> over <paramref name="history"/> and returns
        /// the EXACT instruction string the router was handed. Deliberately does NOT persist: this is the
        /// composition under comparison, not another exchange.
        /// </summary>
        public async Task<string> ComposeAsync(
            string question, IReadOnlyList<ProductChatTurnDto> history, Guid? bookId = null)
        {
            var before = _sent.Count;
            NextAnswer = "Composed.";

            var result = await _controller.Ask(
                new ProductChatRequest(question, history, Language: "en", BookId: bookId,
                    ConversationId: Guid.NewGuid()),
                CancellationToken.None);
            Assert.IsType<OkObjectResult>(result.Result);

            Assert.Equal(before + 1, _sent.Count);
            return _sent[^1].Instruction!;
        }

        public async ValueTask DisposeAsync() => await _db.DisposeAsync();
    }
}
