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
/// <para>PHASE B, IN ONE LINE: <c>bookId</c> absent is phase A byte-for-byte, including its
/// "answering questions about a specific book is not available yet" refusal; <c>bookId</c> present
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

    public ProductChatController(ProductChatService chat) => _chat = chat;

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
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ProductChatResponseDto>> Ask(
        [FromBody] ProductChatRequest req,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req?.Question))
            return BadRequest(new { error = "questionRequired" });

        return Ok(await _chat.AnswerAsync(req, ct));
    }
}
