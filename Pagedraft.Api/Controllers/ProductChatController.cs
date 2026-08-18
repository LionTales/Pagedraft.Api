using Microsoft.AspNetCore.Mvc;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Chat;

namespace Pagedraft.Api.Controllers;

/// <summary>
/// The product assistant: questions about PageDraft itself, answered from the shipped guides corpus
/// with citations (chatbot phase A, c1).
///
/// <para>Deliberately its own controller and its own route family rather than a method on
/// <c>AnalysisController</c>: the endpoint is APP-LEVEL, and stays app-level in phase B. The book
/// arrives as an OPTIONAL <c>bookId</c> on the body rather than as a route segment, because the drawer
/// that calls it is app chrome that sometimes happens to be open inside a book - a
/// <c>/books/{id}/chat</c> route would make the book mandatory and force a second route for the case
/// the client actually starts in.</para>
///
/// <para>PHASE B, IN ONE LINE: <c>bookId</c> absent is phase A, whose book question is met with "I can
/// only see a book while it is open" (g3 replaced the false "not available yet and is coming" that
/// sentence used to be, in code and in the prompt alike); <c>bookId</c> present
/// lets the assistant answer from THAT book's artifacts (briefs, findings, register, statuses, and
/// question-escalated raw chapter text), cited by artifact. There is no cross-book retrieval of any
/// kind: the field is a single value from the client's current book, never a list and never inferred
/// server-side.</para>
///
/// <para>Thin by design: retrieval, prompt composition, the history cap and every fail-safe live in
/// <see cref="ProductChatService"/> and the pure helpers beside it, so the endpoint cannot drift from
/// what the deterministic tests pin.</para>
/// </summary>
[ApiController]
[Route("api/product-chat")]
public class ProductChatController : ControllerBase
{
    private readonly ProductChatService _chat;
    private readonly ChatConversationStore _conversations;

    public ProductChatController(ProductChatService chat, ChatConversationStore conversations)
    {
        _chat = chat;
        _conversations = conversations;
    }

    /// <summary>
    /// POST a question plus the client-held conversation history; get back the answer and the guide
    /// ids it was grounded in.
    ///
    /// <para>400 only when the question itself is blank. Everything else is 200, INCLUDING the
    /// fail-safe states (an unreachable guides corpus, an unreachable model, and phase B's unreadable
    /// book): the response carries <c>isGrounded=false</c> and a machine-readable <c>faultReason</c> so
    /// the client renders an honest failure rather than an assistant answer. A 5xx would be the wrong
    /// shape here because the endpoint DID do its job, which in that situation is to refuse.</para>
    ///
    /// <para>A bookId naming a book that does not exist is NOT a 404 either, for the same reason: the
    /// endpoint answers, and what it answers is the honest "I cannot see your book right now" with
    /// <c>bookFaultReason=book-unavailable</c>. Turning it into a 404 would make the drawer show a
    /// transport error where the user asked a question and deserves a sentence.</para>
    ///
    /// <para>Streaming is deliberately not offered in phase A. The answer's citation is part of the
    /// contract and is only known once the answer is complete, so a streamed reply would render prose
    /// the user could act on before anything said where it came from.</para>
    ///
    /// <para>SHOW C1 ADDED A DUAL-WRITE AROUND THIS ONE CALL, and nowhere else. The user turn is written
    /// on arrival (creating the conversation when the request carried no id) and the assistant turn once
    /// <c>AnswerAsync</c> returns, keyed off the DTO's own <c>IsGrounded</c> - which already distinguishes
    /// all six of that method's return branches, so a fail-safe answer is persisted AS FAILED rather than
    /// lost. Persisting inside the service would mean six call sites that each have to remember.</para>
    ///
    /// <para>PERSISTENCE NEVER CHANGES THE ANSWER AND NEVER FAILS THE REQUEST. A write that faults leaves
    /// the response's threading ids null, logs at Error inside <see cref="ChatConversationStore"/> and
    /// surfaces a fault code on the returned record; it does not turn a good answer into a 500. It also
    /// changes nothing the model sees: the history window is still composed and sent by the CLIENT, so the
    /// composed prompt is byte-identical to what it was before C1 - the property
    /// <c>ProductChatConversationHydrationPinTests</c> pins.</para>
    ///
    /// <para>THE TWO PERSISTENCE CALLS TREAT <c>ct</c> DIFFERENTLY ON PURPOSE, and the reason is written
    /// out at each call below. The user turn is written on the request token, because a question nobody
    /// is waiting for is worth nothing. The assistant turn is written on
    /// <c>CancellationToken.None</c> inside <see cref="ChatConversationStore.CompleteExchangeAsync"/>,
    /// because by then the answer already exists and its user turn is already committed - and a
    /// local-GPU answer takes tens of seconds, so a client that walked away during it is ordinary rather
    /// than exotic. Cancellation is therefore absorbed by exactly one of the two writes. The window
    /// BETWEEN them - cancelled mid-answer, user turn committed, no answer ever coming - is closed by
    /// <see cref="ChatConversationStore.AbandonExchangeAsync"/>: the committed question is flagged
    /// failed so storage records the exchange the way it actually ended, and the cancellation still
    /// propagates.</para>
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ProductChatResponseDto>> Ask(
        [FromBody] ProductChatRequest req,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req?.Question))
            return BadRequest(new { error = "questionRequired" });

        // ct is honoured: nothing has been produced yet, so a request the author abandoned here is a
        // request worth dropping - and writing its user turn anyway would leave a question in the stored
        // transcript that no answer is ever coming for.
        var pending = await _conversations.BeginExchangeAsync(req, ct);

        ProductChatResponseDto answer;
        try
        {
            answer = await _chat.AnswerAsync(req, ct);
        }
        catch
        {
            // The answer will never arrive - the author cancelled mid-GPU-call (the ordinary abandonment,
            // AnswerAsync rethrows it) or the service died unexpectedly - but the user turn IS already
            // committed. Left alone it would sit in storage as a question still waiting for an answer;
            // flagged, it hydrates as the failed exchange it actually was, with the failure UI under the
            // author's question, exactly as a live session renders the same event. The exception then
            // continues out unchanged: this write records the death, it must not mask it.
            await _conversations.AbandonExchangeAsync(pending);
            throw;
        }

        // ct is passed but deliberately NOT honoured by the write (see CompleteExchangeAsync). The answer
        // now exists and the user turn is committed, so cancelling would destroy work rather than save it.
        var completed = await _conversations.CompleteExchangeAsync(pending, answer, ct);

        return Ok(completed.Response);
    }
}
