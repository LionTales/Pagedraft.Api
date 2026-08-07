using Microsoft.AspNetCore.Mvc;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Chat;

namespace Pagedraft.Api.Controllers;

/// <summary>
/// The product assistant: questions about PageDraft itself, answered from the shipped guides corpus
/// with citations (chatbot phase A, c1).
///
/// <para>Deliberately its own controller and its own route family rather than a method on
/// <c>AnalysisController</c>: this endpoint is APP-LEVEL, not book-scoped and not chapter-scoped. It
/// takes no bookId, reads no database, and answers only about the product. Book-aware answers are
/// phase B and will need a different route shape (and a different grounding contract) when they
/// arrive.</para>
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
    /// fail-safe states (an unreachable guides corpus, an unreachable model): the response carries
    /// <c>isGrounded=false</c> and a machine-readable <c>faultReason</c> so the client renders an
    /// honest failure rather than an assistant answer. A 5xx would be the wrong shape here because
    /// the endpoint DID do its job, which in that situation is to refuse.</para>
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
