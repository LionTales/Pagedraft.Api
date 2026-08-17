namespace Pagedraft.Api.Models.Dtos;

// ─── Persisted Show conversations (Show C1) ──────────────────────────────────────────────────────
//
// JSON casing is the System.Text.Json default the API already uses everywhere (camelCase); nothing here
// is an enum, matching this folder's standing convention (no JsonStringEnumConverter is registered, so an
// enum would go out as an integer and the client would have to special-case it).

/// <summary>
/// One row of <c>GET /api/conversations</c>. NO MESSAGE BODIES: <paramref name="MessageCount"/> is
/// maintained on the conversation row in the same write as each message insert precisely so the list can
/// be answered without touching the message table.
/// </summary>
public record ConversationListItemDto(
    Guid Id,
    string Title,
    Guid? BookId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int MessageCount);

/// <summary>
/// The paged list envelope. <paramref name="TotalCount"/> is the count BEFORE paging and AFTER the book
/// filter, so a client can page without re-deriving it.
///
/// <para><paramref name="NearCapWarning"/> is informational and enforced NOWHERE (d1 section (3)): the
/// retention decision is that nothing is auto-deleted, because authors keep notebooks and this feature
/// does not get to decide which of an author's conversations stop mattering. It goes true once the stored
/// count crosses <c>ConversationsController.SoftCap</c>.</para>
/// </summary>
public record ConversationListDto(
    IReadOnlyList<ConversationListItemDto> Items,
    int Page,
    int PageSize,
    int TotalCount,
    bool NearCapWarning);

/// <summary>Conversation metadata alone, for <c>GET /api/conversations/{id}</c>.</summary>
public record ConversationDto(
    Guid Id,
    string Title,
    Guid? BookId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int MessageCount);

/// <summary>
/// The per-answer grounding snapshot, captured in the SAME write as the answer it describes and stored
/// as <c>ConversationMessage.GroundingJson</c>.
///
/// <para>WHY IT IS CAPTURED HERE AND NOT RE-DERIVED LATER: C2's feedback and C3's automated re-check both
/// consume it, and a second write path built later would drift from the answer it claims to describe.
/// <paramref name="SelectionSummary"/> in particular is assembled from the SAME facts the two existing
/// retrieval/citation log lines already carry (<c>BookChatContextReader.ReadAsync</c>'s selection line and
/// <c>ProductChatService.AnswerAsync</c>'s citation line), captured at those call sites rather than parsed
/// back out of log text, so the snapshot and the log can never independently drift from one event.</para>
/// </summary>
public record ConversationGroundingDto(
    IReadOnlyList<string> GuideIds,
    IReadOnlyList<string> ArtifactRefs,
    string? BookFaultReason,
    bool NeedsChapterClarification,
    string? SelectionSummary);

/// <summary>
/// One persisted turn, for <c>GET /api/conversations/{id}/messages</c>.
///
/// <para><paramref name="Text"/> is the FULL stored text. The per-turn cap is applied server side on the
/// way IN to a prompt, never on the way out of storage - see <c>ConversationMessage.Text</c>.</para>
///
/// <para><paramref name="Grounding"/> is non-null on successful assistant turns only.</para>
/// </summary>
public record ConversationMessageDto(
    Guid Id,
    int Sequence,
    string Role,
    string Text,
    bool Failed,
    DateTimeOffset CreatedAt,
    Guid? AskBookId,
    Guid? AskChapterId,
    int? AskChapterOrder,
    ConversationGroundingDto? Grounding);

/// <summary>The paged message envelope, oldest first (transcript render order).</summary>
public record ConversationMessagesDto(
    IReadOnlyList<ConversationMessageDto> Items,
    int Page,
    int PageSize,
    int TotalCount);

/// <summary>
/// Body for <c>PATCH /api/conversations/{id}</c>. A title that is blank after trimming is a 400 rather
/// than a silent fall back to the auto-derived title, mirroring <c>ProductChatController</c>'s own
/// blank-question 400: a rename that quietly did something else is worse than one that says no.
/// </summary>
public record ConversationRenameRequest(string? Title);
