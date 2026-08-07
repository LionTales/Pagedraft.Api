namespace Pagedraft.Api.Models.Dtos;

// ─── Grounded product Q&A over the shipped guides (chatbot phase A, c1) ──────────────────────────
//
// JSON casing throughout is the System.Text.Json default the API already uses everywhere
// (camelCase) - the app calls AddControllers() with no naming-policy override. Nothing here is an
// enum, matching this folder's standing convention: no JsonStringEnumConverter is registered, so an
// enum would go out as an integer and the client would have to special-case it.

/// <summary>
/// Body for <c>POST /api/product-chat</c>. JSON (camelCase): <c>{ "question": "...",
/// "history": [ { "role": "user"|"assistant", "content": "..." } ], "language": "he"|"en" }</c>.
///
/// <para><paramref name="Question"/> is required; a blank one is a 400.</para>
///
/// <para><paramref name="History"/> is the prior transcript the CLIENT holds. Phase A keeps no
/// server-side conversation state at all (that belongs with phase C's history and quota surface,
/// which needs a user model that does not exist yet), so continuity is per request. The server does
/// NOT trust this list to be bounded: it forwards only the last
/// <c>ProductChatService.MaxHistoryTurns</c> turns and truncates each one, because the retrieval
/// budget math assumes a bounded history and an unbounded client list would silently overrun the
/// context window instead of failing.</para>
///
/// <para><paramref name="Language"/> is a HINT, not an instruction. The answer language is detected
/// from the question itself (Hebrew and English use disjoint alphabets, so one Hebrew letter is
/// decisive) and this field is consulted only when the question carries no letters at all. A client
/// that mislabels its locale must not be able to get a Hebrew question answered in English.</para>
/// </summary>
public record ProductChatRequest(
    string? Question,
    IReadOnlyList<ProductChatTurnDto>? History = null,
    string? Language = null);

/// <summary>
/// One prior turn. JSON (camelCase): role, content.
///
/// <para><paramref name="Role"/> is <c>"assistant"</c> for the assistant's turns; ANY other value
/// (including null) is treated as the user's. Deliberately lenient rather than a 400: a mislabelled
/// role degrades a history line, it does not make the request meaningless, and rejecting the whole
/// question over it would be worse for the author than answering.</para>
/// </summary>
public record ProductChatTurnDto(string? Role, string? Content);

/// <summary>
/// Response for <c>POST /api/product-chat</c>. JSON (camelCase): answer, guideIds, language,
/// isGrounded, faultReason. Always 200 once the question itself is valid, including on the
/// fail-safe paths below.
///
/// <para><paramref name="Answer"/> is the prose to render. On a fail-safe it is an honest sentence
/// saying the guides could not be reached, NEVER an answer assembled from the model's own knowledge
/// of the product. That is the one property this whole phase exists to guarantee: a bot that
/// confidently describes a setting which does not exist is worse than no bot.</para>
///
/// <para><paramref name="GuideIds"/> are frontmatter <c>id</c> values (<c>export</c>, <c>faq</c>,
/// <c>guides-index</c> ...), not filenames and not titles. They are LANGUAGE-NEUTRAL by construction:
/// an en/he pair shares one id, so a Hebrew answer grounded in an English guide still cites the same
/// id an English answer would. The list is the ids the answer actually cited when the model's
/// citation line parsed and named guides it was given; otherwise it is every guide the answer was
/// grounded in. Empty on a fail-safe.</para>
///
/// <para><paramref name="Language"/> is the language the answer is written in ("he" or "en"), so the
/// client can set <c>dir</c> without re-detecting.</para>
///
/// <para><paramref name="IsGrounded"/> is the flag the client branches on: true means a normal
/// answer traceable to <paramref name="GuideIds"/>, false means the fail-safe state and should be
/// rendered as such (not as an assistant answer). <paramref name="FaultReason"/> is null exactly when
/// <paramref name="IsGrounded"/> is true, and otherwise one of the machine-readable codes
/// <c>guides-unavailable</c>, <c>guides-empty</c>, <c>model-unavailable</c>, <c>empty-answer</c>
/// (see <c>ProductChatFaults</c>). A human-facing sentence alone would force the client to match on
/// prose, and would give a log nothing to group by.</para>
/// </summary>
public record ProductChatResponseDto(
    string Answer,
    IReadOnlyList<string> GuideIds,
    string Language,
    bool IsGrounded,
    string? FaultReason);
