using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;

namespace Pagedraft.Api.Services.Chat;

/// <summary>
/// What one product-chat request left behind on its way in: the conversation it belongs to, the user turn
/// already written for it, and the ASK-TIME context both turns of the exchange share. <see cref="Fault"/>
/// is non-null when the write did NOT happen, which is a state the request continues through -
/// persistence must never cost the author an answer.
///
/// <para>The ask-time book travels on this record rather than being re-read from the conversation when
/// the answer lands, because a request stays tied to the book it was ASKED in and not to the book the
/// conversation started in or the one now on screen. The hydrated resend window filters on exactly that
/// value.</para>
/// </summary>
public sealed record PendingChatExchange(
    Guid? ConversationId,
    Guid? UserMessageId,
    Guid? AskBookId,
    Guid? AskChapterId,
    int? AskChapterOrder,
    string? Fault)
{
    public static PendingChatExchange Failed(string fault) => new(null, null, null, null, null, fault);

    public bool Persisted => ConversationId.HasValue && UserMessageId.HasValue;
}

/// <summary>
/// The finished exchange: the response the caller returns (carrying the threading ids when they exist)
/// and, separately, whether persistence faulted. THE FAULT IS SURFACED RATHER THAN ONLY LOGGED because a
/// nested catch that swallows to stay non-throwing blinds every layer above it - a recorded failure shape
/// in this codebase, not a hypothesis.
/// </summary>
public sealed record CompletedChatExchange(ProductChatResponseDto Response, string? Fault);

/// <summary>
/// THE ONE WRITE PATH for Show's conversation history (Show C1, d1 sections (1), (3) and (5)).
///
/// <para>WHY IT IS CALLED FROM THE CONTROLLER AND NOT FROM <c>ProductChatService.AnswerAsync</c>. That
/// method has SIX return branches - two guides fail-safes, the book fail-safe, the model-unavailable and
/// empty-completion fail-safes, the post-rewrite empty-answer guard, and the success return - so a
/// persistence call "at the point the response is finalized" inside it would be six call sites that must
/// each be remembered. The controller instead wraps the single, already-existing <c>AnswerAsync</c> call
/// exactly once and keys off the returned DTO's own <c>IsGrounded</c> / <c>FaultReason</c> /
/// <c>BookFaultReason</c>, which already distinguish every one of those branches. A fail-safe answer is
/// therefore persisted AS FAILED rather than lost, which is precisely the population C2's feedback wants.</para>
///
/// <para>IT MUST NEVER FAIL THE CHAT REQUEST, AND IT MUST NEVER FAIL SILENTLY. Both writes are wrapped,
/// each catch LOGS with the exception (an endpoint kept non-throwing by a catch that says nothing ships
/// its failures invisibly), and the fault is additionally SURFACED on the returned record so the caller
/// can see it and a test can assert it.</para>
///
/// <para>THE TWO HALVES TAKE OPPOSITE POSITIONS ON CANCELLATION, AND THE ASYMMETRY IS THE DESIGN.
/// <see cref="BeginExchangeAsync"/> runs BEFORE the answer exists, so it honours the request token and
/// lets an <c>OperationCanceledException</c> out: a cancelled request has no author left to answer, and
/// abandoning a question nobody is waiting for costs nothing. <see cref="CompleteExchangeAsync"/> runs
/// AFTER the answer exists and therefore ignores the request token entirely, writing on
/// <see cref="CancellationToken.None"/>: a cancelled request still has a ROW that must land, because its
/// user turn is already committed and dropping the answer would leave the stored conversation
/// permanently ending on an unanswered question with nothing for C2's feedback to attach to. Each method
/// states its own reasoning at its own site; neither is a copy of the other.</para>
///
/// <para>IT COMPOSES NOTHING. The client remains the sender of the history window; this class only writes
/// down what flowed through the request and reads it back. That is what keeps the composed prompt
/// byte-identical, which is the whole reason C1 needs no re-gate.</para>
/// </summary>
public sealed class ChatConversationStore
{
    /// <summary>
    /// The snapshot's serialization options, declared here rather than borrowed from MVC so the stored
    /// blob's shape is a property of this class and cannot move when a controller option changes.
    /// </summary>
    internal static readonly JsonSerializerOptions SnapshotJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly AppDbContext _db;
    private readonly ProductChatGroundingCapture _capture;
    private readonly ILogger<ChatConversationStore> _logger;

    public ChatConversationStore(
        AppDbContext db,
        ProductChatGroundingCapture capture,
        ILogger<ChatConversationStore> logger)
    {
        _db = db;
        _capture = capture;
        _logger = logger;
    }

    /// <summary>
    /// Persists the USER turn on arrival, creating the conversation when the request carried no id
    /// (implicit create, d1 section (3) - there is no "create conversation" endpoint).
    ///
    /// <para>A <c>conversationId</c> that does not resolve does NOT fail the request and is NOT a 404: the
    /// author asked a question, and the honest recovery is a new conversation with the id returned so the
    /// client re-threads. It is logged, because a client threading a deleted id repeatedly is a bug worth
    /// seeing.</para>
    ///
    /// <para>THIS HALF KEEPS THE REQUEST TOKEN, unlike <see cref="CompleteExchangeAsync"/>, and was
    /// re-examined rather than moved with it. Nothing of value exists yet at this point: the answer has
    /// not been produced, no row is committed, and a cancelled request means the author is not waiting
    /// for one. Writing a user turn for a question that will never be answered would MANUFACTURE the
    /// dangling-question state the other half exists to prevent, so honouring the token here is not
    /// merely harmless, it is the behaviour that keeps the stored transcript honest. The rethrow below
    /// stays.</para>
    /// </summary>
    public async Task<PendingChatExchange> BeginExchangeAsync(ProductChatRequest request, CancellationToken ct)
    {
        var question = (request.Question ?? string.Empty).Trim();

        try
        {
            var conversation = await ResolveOrCreateAsync(request, question, ct).ConfigureAwait(false);

            var message = new ConversationMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                Sequence = conversation.MessageCount,
                Role = ChatMessageRoles.User,
                Text = question,
                Failed = false,
                AskBookId = request.BookId,
                AskChapterId = request.AmbientChapterId,
                AskChapterOrder = request.AmbientChapterOrder
            };

            conversation.MessageCount++;
            _db.ConversationMessages.Add(message);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            return new PendingChatExchange(
                conversation.Id, message.Id,
                request.BookId, request.AmbientChapterId, request.AmbientChapterOrder,
                Fault: null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Show could not persist the USER turn of a product-chat request (conversation " +
                "{ConversationId}, book {BookId}). The question is still answered - persistence must never " +
                "cost the author an answer - but this exchange will be MISSING from the history, so a " +
                "resumed conversation will have a hole in it. This line is the only place that says so.",
                request.ConversationId, request.BookId);
            return PendingChatExchange.Failed(ChatPersistenceFaults.UserTurnWriteFailed);
        }
    }

    /// <summary>
    /// Persists the ASSISTANT turn once the response is finalized, flagged failed on every non-grounded
    /// outcome, and returns the response carrying its threading ids.
    ///
    /// <para>THIS WRITE DOES NOT RUN ON THE REQUEST TOKEN, AND THAT IS THE WHOLE POINT OF THE PARAMETER
    /// BEING IGNORED. By the time this is called the answer already exists: the GPU time is spent, the
    /// user turn is ALREADY COMMITTED, and the only thing left is bookkeeping. Cancelling here does not
    /// save any work, it destroys work already done - the stored conversation would end permanently on an
    /// unanswered question and C2 would have no row to hang feedback on, which is the exact state the
    /// Error log at the bottom of this method exists to warn about. Local-GPU answers run tens of seconds,
    /// so a browser tab closed mid-answer is the ORDINARY case here, not an exotic one. Every database
    /// call below therefore passes <see cref="CancellationToken.None"/> - the two reads as well as the
    /// save, because they are one write unit and a read cancelled halfway leaves the save with nothing to
    /// write - and there is no <c>OperationCanceledException</c> rethrow: a cancellation that somehow
    /// reached here is a failure to log and surface like any other, not a reason to drop the row.</para>
    ///
    /// <para><paramref name="requestCt"/> is kept on the signature so the caller does not have to know
    /// this, and is read for one Debug line only. Do NOT thread it into the calls below.</para>
    /// </summary>
    public async Task<CompletedChatExchange> CompleteExchangeAsync(
        PendingChatExchange pending, ProductChatResponseDto response, CancellationToken requestCt)
    {
        if (!pending.Persisted)
        {
            // The user turn never landed, so there is nothing to attach an answer to. The response goes
            // back WITHOUT ids, which is exactly what a client that cannot thread needs to see.
            return new CompletedChatExchange(response, pending.Fault);
        }

        if (requestCt.IsCancellationRequested)
        {
            _logger.LogDebug(
                "The author of conversation {ConversationId} is gone before the answer was stored (the " +
                "request token is cancelled). The assistant turn is written ANYWAY: the answer already " +
                "exists and its user turn is already committed, so abandoning the write here would leave " +
                "the stored conversation ending on an unanswered question.",
                pending.ConversationId);
        }

        try
        {
            var conversation = await _db.Conversations
                .FirstOrDefaultAsync(c => c.Id == pending.ConversationId!.Value, CancellationToken.None)
                .ConfigureAwait(false);

            if (conversation == null)
            {
                _logger.LogError(
                    "Show wrote the user turn of conversation {ConversationId} and then could not find that " +
                    "conversation to attach the answer to. The answer is returned but NOT stored.",
                    pending.ConversationId);
                return new CompletedChatExchange(response, ChatPersistenceFaults.ConversationVanished);
            }

            // THE FAILED FLAG COMES FROM THE DTO, NOT FROM A BRANCH. IsGrounded is false on every one of
            // AnswerAsync's five fail-safe returns and true only on its success return, so this single
            // expression covers all six without the controller knowing which one it got.
            var failed = !response.IsGrounded;

            var message = new ConversationMessage
            {
                Id = Guid.NewGuid(),
                ConversationId = conversation.Id,
                Sequence = conversation.MessageCount,
                Role = ChatMessageRoles.Assistant,
                Text = response.Answer ?? string.Empty,
                Failed = failed,
                // The ask-time context of the EXCHANGE, carried from the request rather than from the
                // conversation row: see PendingChatExchange's doc for why those are not the same book.
                AskBookId = pending.AskBookId,
                AskChapterId = pending.AskChapterId,
                AskChapterOrder = pending.AskChapterOrder,
                // ASSISTANT MESSAGES ONLY, AND SUCCESSFUL ONES ONLY (d1 section (1)). A fail-safe carries no
                // grounding by definition - empty guides, empty artifacts - and the Failed flag beside it is
                // what tells C2 that this turn is one of the ones it most wants feedback on.
                GroundingJson = failed ? null : SerializeGrounding(response)
            };

            conversation.MessageCount++;
            _db.ConversationMessages.Add(message);

            // BOTH TURNS OF A FAILED EXCHANGE ARE FLAGGED, not only the answer - d1's own wording is "the
            // turn(s) written for a fail-safe or error outcome". THE HYDRATION RULE DEPENDS ON IT, but not
            // in the direction it once read here: the flag on the QUESTION is what lets the client find the
            // pair, not a licence to drop it. The live client does NOT cut a failed exchange out of the
            // transcript - `ask()` appends the author's turn before the request goes out and only `retry()`
            // removes it - so hydration replays a flagged user row AS a `user` turn, which is what keeps the
            // byte-identity pin true for an un-retried failure. Only the flagged ASSISTANT row (the refusal)
            // never goes back up, and the flagged question is withheld in exactly one case: a later user
            // row carrying identical text - failed or not, because `retry()` cuts the pair out whatever
            // the second attempt then does - which is the only trace `retry()` leaves. Deriving the
            // pair instead from "the user turn immediately before a failed answer" would also work until a
            // page boundary fell between the two rows and silently split it.
            if (failed)
            {
                var userTurn = await _db.ConversationMessages
                    .FirstOrDefaultAsync(m => m.Id == pending.UserMessageId!.Value, CancellationToken.None)
                    .ConfigureAwait(false);
                if (userTurn != null) userTurn.Failed = true;
            }

            await _db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            return new CompletedChatExchange(
                response with
                {
                    ConversationId = conversation.Id,
                    UserMessageId = pending.UserMessageId,
                    AssistantMessageId = message.Id
                },
                Fault: null);
        }
        // NO OperationCanceledException RETHROW HERE, deliberately, and its absence is the other half of
        // running on CancellationToken.None. Nothing above passes a cancellable token, so the only way one
        // can arrive is from somewhere unexpected - and at that point it is a lost row like any other lost
        // row: logged with its exception and surfaced as a fault, never turned into an exception the
        // controller has to survive after the answer is already in hand.
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Show could not persist the ASSISTANT turn of conversation {ConversationId} (grounded: " +
                "{IsGrounded}, fault: {FaultReason}). The author still gets the answer, but the stored " +
                "conversation now ends on an unanswered question and C2 has no message to hang feedback on.",
                pending.ConversationId, response.IsGrounded, response.FaultReason);
            // THE THREADING IDS THAT EXIST STILL GO BACK. The conversation and the user turn were
            // committed by BeginExchangeAsync and this failure did not un-commit them, so returning
            // null ids here would tell the client the whole exchange has no home - and its NEXT question
            // would then start a duplicate conversation while this one sits ending on an unanswered
            // question. Only AssistantMessageId stays null, which is the id that truthfully does not
            // exist. The vanished-conversation branch above keeps ALL ids null, because there the rows
            // are cascade-deleted and a returned id would have the client thread a dead conversation.
            return new CompletedChatExchange(
                response with
                {
                    ConversationId = pending.ConversationId,
                    UserMessageId = pending.UserMessageId
                },
                ChatPersistenceFaults.AssistantTurnWriteFailed);
        }
    }

    /// <summary>
    /// Marks the already-committed user turn of an exchange FAILED when its answer will never arrive -
    /// the request was cancelled or died between <see cref="BeginExchangeAsync"/> and
    /// <see cref="CompleteExchangeAsync"/>, which on local-GPU timings (tens of seconds per answer) is
    /// the ordinary abandonment window, not an exotic one.
    ///
    /// <para>Without this, the committed question would sit in storage as an ordinary un-answered turn,
    /// indistinguishable from one still being answered. Flagged, it hydrates the way a live session
    /// renders the same event: the author's question with the failure UI beneath it. The window rule is
    /// unchanged either way - a flagged question is still replayed as a user turn unless a later retry
    /// superseded it, exactly like every other failed exchange.</para>
    ///
    /// <para>Runs entirely on <see cref="CancellationToken.None"/> for the same reason
    /// <see cref="CompleteExchangeAsync"/> does: the request token is cancelled by construction on this
    /// path, and this write is the record of that fact. Never throws - the request is already dying with
    /// its own exception, and this method must not replace it with a persistence one. A user turn that
    /// cannot be found is a conversation deleted mid-request: nothing to flag, cascade already took it.</para>
    /// </summary>
    public async Task AbandonExchangeAsync(PendingChatExchange pending)
    {
        if (!pending.Persisted) return;

        try
        {
            var userTurn = await _db.ConversationMessages
                .FirstOrDefaultAsync(m => m.Id == pending.UserMessageId!.Value, CancellationToken.None)
                .ConfigureAwait(false);
            if (userTurn == null) return;

            userTurn.Failed = true;
            await _db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Show could not flag the abandoned question of conversation {ConversationId} as failed. " +
                "The stored conversation will show it as an ordinary unanswered turn. The request this " +
                "belonged to is already failing with its own exception; this one is only logged.",
                pending.ConversationId);
        }
    }

    private async Task<Conversation> ResolveOrCreateAsync(
        ProductChatRequest request, string question, CancellationToken ct)
    {
        if (request.ConversationId is Guid id)
        {
            var existing = await _db.Conversations.FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false);
            if (existing != null) return existing;

            _logger.LogWarning(
                "Show was asked to continue conversation {ConversationId}, which does not exist (deleted, or " +
                "a stale id in a client that outlived it). A NEW conversation is started and its id is " +
                "returned, because refusing the question would cost the author an answer over bookkeeping.",
                id);
        }

        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            // Always null until the Pagewise-style JWT + Google login lands; the column exists so that is a
            // clean addition rather than a migration over an author's whole notebook.
            UserId = null,
            BookId = request.BookId,
            Title = ConversationTitle.FromFirstMessage(question),
            MessageCount = 0
        };

        _db.Conversations.Add(conversation);
        return conversation;
    }

    private string SerializeGrounding(ProductChatResponseDto response)
    {
        var snapshot = new ConversationGroundingDto(
            GuideIds: response.GuideIds ?? Array.Empty<string>(),
            ArtifactRefs: response.ArtifactRefs ?? Array.Empty<string>(),
            BookFaultReason: response.BookFaultReason,
            NeedsChapterClarification: response.NeedsChapterClarification,
            // Assembled at the two log call sites this request already passed through, never re-derived
            // from log text. Null is a legitimate value (a turn whose citation line was never reached).
            SelectionSummary: _capture.Compose());

        return JsonSerializer.Serialize(snapshot, SnapshotJson);
    }
}

/// <summary>The two role tokens, declared once so the writer and the hydration filter cannot disagree.</summary>
public static class ChatMessageRoles
{
    public const string User = "user";
    public const string Assistant = "assistant";
}

/// <summary>
/// Machine-readable persistence faults, surfaced on <see cref="CompletedChatExchange"/> rather than only
/// logged. None of them ever changes the answer the author receives.
/// </summary>
public static class ChatPersistenceFaults
{
    public const string UserTurnWriteFailed = "conversation-user-turn-write-failed";
    public const string AssistantTurnWriteFailed = "conversation-assistant-turn-write-failed";
    public const string ConversationVanished = "conversation-vanished";
}
