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
/// <param name="BookId">
/// The book the user is CURRENTLY inside, or null when the drawer is open outside any book (chatbot
/// phase B). Present = the assistant may answer from that book's artifacts; absent = phase A's behavior
/// byte-for-byte, including its "answering questions about a specific book is not available yet"
/// refusal.
///
/// <para>THE PRIVACY FENCE IS THAT THIS IS A SINGLE VALUE FROM THE CLIENT'S CURRENT BOOK, never a list
/// and never inferred server-side. There is no cross-book retrieval of any kind: answering across a
/// series is phase C, and the shape of this field is what keeps that a decision rather than an
/// accident.</para>
/// </param>
/// <param name="AmbientChapterId">
/// The chapter the user has OPEN on screen right now, or null when none is (chatbot phase B, d2 section
/// (1)). Absent or null = today's behavior exactly: no chapter is ambient, and a question that says "this
/// chapter" resolves nothing.
///
/// <para>ALWAYS SENT EXPLICITLY, INCLUDING AS NULL. The client types both ambient fields as
/// <c>string | null</c> / <c>number | null</c> rather than as optional, so the key is always on the wire:
/// an ambient key that is sometimes silently absent is worse than one that is explicitly null, because
/// "the drawer is open on the dashboard" and "this client is too old to say" would otherwise be the same
/// request.</para>
///
/// <para>THE ID IS AUTHORITATIVE FOR IDENTITY and <paramref name="AmbientChapterOrder"/> is authoritative
/// for RESOLUTION, and the server reconciles them rather than trusting either alone: it looks this id up
/// against freshly-read chapter rows and uses THAT row's current order, because everything downstream
/// (selection, escalation, citation refs) is order-keyed while the client's order number is a snapshot
/// that a reorder can invalidate.</para>
/// </param>
/// <param name="AmbientChapterOrder">
/// The 0-based <c>Chapter.Order</c> of the same chapter, sent alongside the id and used ONLY as a
/// fallback for the case the id does not resolve (an older client, or a chapter deleted since the editor
/// loaded the book). Explicitly null when no chapter is open.
/// </param>
/// <param name="ConversationId">
/// The persisted conversation this question continues, or null to start one (Show C1). Absent = the
/// server creates a conversation and returns its id on the response, so the client threads from the
/// second question onward; there is deliberately no separate "create conversation" endpoint.
///
/// <para>IT IS A THREADING KEY AND NOTHING ELSE. The server does NOT read the stored transcript to
/// compose a prompt: the CLIENT remains the sender of the history window, exactly as chatbot phase B
/// shipped it, and this field only says where to WRITE. That is what keeps C1 a persistence feature
/// rather than a change to the seven-gate prompt path, and it is pinned by
/// <c>ProductChatConversationHydrationPinTests</c>.</para>
///
/// <para>An id that does not resolve is not an error: a new conversation is started and its id returned.
/// Refusing the question over a stale id would cost the author an answer for bookkeeping.</para>
/// </param>
public record ProductChatRequest(
    string? Question,
    IReadOnlyList<ProductChatTurnDto>? History = null,
    string? Language = null,
    Guid? BookId = null,
    Guid? AmbientChapterId = null,
    int? AmbientChapterOrder = null,
    Guid? ConversationId = null);

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
/// (see <c>ProductChatFaults</c>) - or, on phase B's book fail-safe alone, the <c>BookChatFaults</c>
/// code that also fills <paramref name="BookFaultReason"/>. A human-facing sentence alone would force
/// the client to match on prose, and would give a log nothing to group by.</para>
/// </summary>
/// <param name="ArtifactRefs">
/// Phase B. The BOOK-artifact references the answer cited, in the same spirit as
/// <paramref name="GuideIds"/> and subject to the same safety property: a ref appears here only when the
/// prompt actually carried that artifact AFTER the budget trim, so a chip can never point at grounding
/// the model never saw. Shapes: <c>chapter-brief:&lt;order&gt;</c>,
/// <c>chapter-summary:&lt;order&gt;</c>, <c>chapter-text:&lt;order&gt;</c>, <c>finding:&lt;guid&gt;</c>,
/// <c>register</c>, <c>book-brief</c>, <c>history</c>, <c>status:summary</c>, <c>status:review</c>,
/// <c>status:style-baseline</c>. ALWAYS EMPTY when the request carried no <c>bookId</c>.
/// </param>
/// <param name="BookFaultReason">
/// Phase B. Non-null when the BOOK half of a book-scoped turn could not be retrieved, one of the
/// <c>BookChatFaults</c> codes. DELIBERATELY SEPARATE from <paramref name="FaultReason"/>, whose phase-A
/// contract (null exactly when <paramref name="IsGrounded"/> is true) is unchanged: the two halves fail
/// independently, so a broken book lookup must not make an otherwise-fine guide-grounded answer read as
/// a failure. Null on every phase-A-shaped request.
/// </param>
/// <param name="NeedsChapterClarification">
/// Phase B, d2 section (5). True when the question was about a chapter and NOTHING about a chapter was
/// carried - no chapter resolved (explicitly or from the ambient open chapter) AND no chapter's raw text
/// escalated - so the client should offer the book's chapters as one-click chips instead of making the
/// author retype the question.
///
/// <para>COMPUTED SERVER-SIDE FROM THE SELECTION, NEVER FROM THE ANSWER'S PROSE, and that is the whole
/// mechanism. The owner's rule is that Show must never ask "which chapter?" while the chapter is open on
/// screen, and a prompt-only version of that rule would put it in exactly the place this codebase has
/// already measured the model failing to hold one under collision (g1's F-1). Here it is a boolean over
/// the two sets that carry everything an answer can be grounded in, so anything that resolved or was
/// escalated makes it false BY CONSTRUCTION rather than by the model's compliance. ALWAYS false when the
/// request carried no <c>bookId</c>, and on every fail-safe.</para>
/// </param>
/// <param name="ConversationId">
/// Show C1. The persisted conversation this exchange was written to - the one the request named, or the
/// one implicitly created for it. The client adopts an id it did not send, which is how a first question
/// starts threading.
///
/// <para>NULL MEANS THE EXCHANGE WAS NOT STORED, which happens only when the persistence write itself
/// faulted. It is a null rather than an error because an answer the author can read beats a 500 over
/// bookkeeping; the fault is logged at Error server-side, never swallowed.</para>
/// </param>
/// <param name="UserMessageId">
/// Show C1. The stored id of the question turn, null when the exchange was not stored. C2 attaches
/// feedback to these ids, which is why they are minted in the same write as the turns themselves.
/// </param>
/// <param name="AssistantMessageId">
/// Show C1. The stored id of the answer turn, null when the exchange was not stored. Present on FAIL-SAFE
/// answers too: a failed exchange is persisted flagged rather than dropped, because a thumbs-down on a
/// failure is signal and not noise.
/// </param>
public record ProductChatResponseDto(
    string Answer,
    IReadOnlyList<string> GuideIds,
    string Language,
    bool IsGrounded,
    string? FaultReason,
    IReadOnlyList<string>? ArtifactRefs = null,
    string? BookFaultReason = null,
    bool NeedsChapterClarification = false,
    Guid? ConversationId = null,
    Guid? UserMessageId = null,
    Guid? AssistantMessageId = null);
