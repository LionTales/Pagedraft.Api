namespace Pagedraft.Api.Models.Dtos;

// ─── Feedback infrastructure (Show C2) ───────────────────────────────────────────────────────────
//
// JSON casing is the System.Text.Json default this API already uses everywhere (camelCase). NOTHING HERE
// IS A CLR ENUM, matching this folder's standing convention and the entity's own choice of plain strings:
// no JsonStringEnumConverter is registered, so an enum would go out as an integer and every client would
// have to special-case it. Verdict / Status / Area / TargetType therefore travel as the exact stored
// tokens ("up", "down", "New", "Triaged", "ConfirmedBug", "Dismissed", "Fixed", "chat-answer",
// "conversation-message") in that exact casing.

/// <summary>
/// The vote-time context that NO JOIN CAN RECOVER LATER (d1 section (2)). Every field is here for that
/// reason and no other; the answer, the question and the grounding refs are deliberately absent, because
/// they are joined at read time and a copy would freeze at vote time and drift.
///
/// <para>NULLABLE THROUGHOUT, and the DTO itself is nullable on the request, because
/// <c>System.Text.Json</c> nulls out an <c>= new()</c> property on an explicit JSON <c>null</c> - the
/// null-guard shape this codebase already standardised on. A caller sending <c>"context": null</c> and a
/// caller omitting the property entirely must land in the same place, and they do: both leave the stored
/// blob unchanged on a re-vote and absent on a first vote.</para>
/// </summary>
/// <param name="Route">The client route open at vote time. No joined row records it.</param>
/// <param name="BookId">
/// The book open at VOTE time. Captured even though <c>ConversationMessage.AskBookId</c> already carries
/// the ASK-time book for mount #1: a reader can move between receiving an answer and voting on it, and
/// this is the one field guaranteed to exist for a future target type whose entity carries no book.
/// </param>
/// <param name="ChapterId">The chapter open at vote time, same reasoning as <paramref name="BookId"/>.</param>
/// <param name="UiLanguage">
/// The reader's UI chrome locale (<c>he</c>/<c>en</c>) at vote time. No joined row records which locale
/// rendered the page, and it is what tells the triage owner whether a complaint is locale-specific.
/// </param>
/// <param name="AppBuild">
/// RESERVED, not populated in v1: no cheap build stamp exists in this client or API today
/// (<c>package.json</c> pins a never-bumped <c>"0.0.0"</c> and no CI build id reaches the client). The
/// field stays in the shape so wiring a real stamp later needs no schema change; c2-client sends it
/// absent rather than inventing a value.
/// </param>
public record FeedbackContextDto(
    string? Route,
    Guid? BookId,
    Guid? ChapterId,
    string? UiLanguage,
    string? AppBuild);

/// <summary>
/// Body of <c>POST /api/feedback</c> - the one-vote create-or-update.
///
/// <para><paramref name="Text"/> NULL AND <paramref name="Text"/> EMPTY MEAN DIFFERENT THINGS, and this is
/// the one place a c2-client author must read carefully. Null (or absent) means "leave the note alone",
/// which is what makes d1's "a verdict flip KEEPS the existing text" rule expressible without a second
/// endpoint. A non-null value REPLACES the note, and a value that is empty after trimming clears it -
/// that is the reader deliberately revising their note down to nothing, not an accident.</para>
///
/// <para><paramref name="InstallationId"/> is the pre-login voter identity and is REQUIRED today: with no
/// <c>[Authorize]</c> anywhere the server has no other way to key the one-vote rule, so a request
/// carrying neither it nor an authenticated user is <c>400 voterIdentityRequired</c> rather than a vote
/// nobody can dedupe. There is deliberately no <c>userId</c> field: a client-supplied user id would be
/// unauthenticated and therefore meaningless, so the server resolves that half from the request principal
/// (null today) and never from the body.</para>
/// </summary>
public record FeedbackVoteRequest(
    string? Area,
    string? TargetType,
    Guid TargetId,
    string? Verdict,
    string? Text,
    string? InstallationId,
    FeedbackContextDto? Context);

/// <summary>Body of <c>PATCH /api/feedback/{id}/status</c>.</summary>
public record FeedbackStatusRequest(string? Status);

/// <summary>
/// One feedback row as the wire sees it - the vote endpoint's response, the triage detail's
/// <c>feedback</c> half, and the shape the widget reconciles its optimistic state against.
///
/// <para>The voter identity is NOT on this DTO. <c>InstallationId</c> and <c>UserId</c> are keying
/// material, not something a reading tool needs, and the triage view is read by the owner rather than
/// operated as a product.</para>
/// </summary>
public record FeedbackDto(
    Guid Id,
    string Area,
    string TargetType,
    Guid TargetId,
    string Verdict,
    string? Text,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset StatusChangedAt,
    DateTimeOffset? TargetDeletedAt,
    FeedbackContextDto? Context);

/// <summary>
/// One row of the triage list. <paramref name="BookId"/> is COMPOSED BY THE JOIN
/// (<c>ConversationMessage.AskBookId</c> for mount #1), never read off the feedback row, which is the
/// same rule the list's <c>bookId</c> filter follows - it is null when the target no longer resolves or
/// when the target type carries no book.
///
/// <para>The full <paramref name="Text"/> travels rather than a preview: it is capped at 2000 characters
/// by construction, and v1 is a reading tool.</para>
/// </summary>
public record FeedbackListItemDto(
    Guid Id,
    string Area,
    string TargetType,
    Guid TargetId,
    string Verdict,
    string? Text,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset StatusChangedAt,
    DateTimeOffset? TargetDeletedAt,
    Guid? BookId);

/// <summary>
/// The paged list envelope. <paramref name="TotalCount"/> is the count AFTER the filters and BEFORE
/// paging, so a client can page without re-deriving it - the same shape <c>ConversationListDto</c> uses.
/// No aggregate counts beyond this: v1 is a reading tool, not a dashboard (d1 section (4)).
/// </summary>
public record FeedbackListDto(
    IReadOnlyList<FeedbackListItemDto> Items,
    int Page,
    int PageSize,
    int TotalCount);

/// <summary>
/// THE EVIDENCE, COMPOSED BY A LIVE JOIN EVERY TIME (d1 section (2)). Nothing in here is stored on the
/// feedback row: for mount #1 it is read from <c>ConversationMessage</c> - the target's own text and
/// grounding snapshot, plus the paired question at <c>(ConversationId, Sequence - 1)</c>.
///
/// <para>A MISS IS A STATE, NOT A FAILURE. When the joined row no longer resolves this DTO still comes
/// back with <paramref name="Available"/> false and a machine-readable
/// <paramref name="UnavailableReason"/>, so the triage detail renders the stored context plus a "target
/// deleted" notice instead of 404-ing a row the owner deliberately kept.</para>
///
/// <para>PRIVACY: this is the manuscript-bearing half of the feature. It is composed for the triage read
/// and nothing else; it is never written into a feedback row, and C3's tickets carry ids and summaries,
/// never this prose.</para>
/// </summary>
/// <param name="Available">False when the target row could not be joined.</param>
/// <param name="UnavailableReason">
/// <c>"targetDeleted"</c> (the tombstone is stamped - the conversation was deleted),
/// <c>"targetMissing"</c> (no tombstone, yet nothing resolves - a row that vanished without going through
/// its delete path, which is worth seeing rather than smoothing over), or
/// <c>"targetTypeNotComposable"</c> (a target type with no evidence composer yet). Null when available.
/// </param>
public record FeedbackEvidenceDto(
    bool Available,
    string? UnavailableReason,
    Guid? ConversationId,
    string? ConversationTitle,
    string? Question,
    string? Answer,
    bool? AnswerFailed,
    DateTimeOffset? AnsweredAt,
    Guid? AskBookId,
    Guid? AskChapterId,
    int? AskChapterOrder,
    ConversationGroundingDto? Grounding);

/// <summary>
/// <c>GET /api/feedback/{id}</c> - the triage detail, composing the row and its joined evidence in ONE
/// response so the detail view makes one request rather than reconstructing a join client-side.
/// </summary>
public record FeedbackDetailDto(
    FeedbackDto Feedback,
    FeedbackEvidenceDto Evidence);

/// <summary>
/// <c>GET /api/feedback/availability</c> - whether this deployment serves the triage surface at all.
///
/// <para>ADDED BEYOND d1, and it is a client-contract convenience rather than a security surface. Without
/// it a client can only learn the flag's value by CALLING a gated endpoint and reading a bodiless 404,
/// which is indistinguishable from a transport failure and is an odd thing to do just to decide whether to
/// register a route. It leaks nothing d1 did not already accept as public: d1's own reasoning for
/// answering 404 rather than 403 is that this app exposes its whole route table through Swagger with
/// nothing in front of it, so a 403 "would leak exactly as much as a 200".</para>
///
/// <para>It is itself UNGATED, necessarily - a gated availability check could never answer the question
/// it exists to answer - and it carries no feedback data of any kind.</para>
/// </summary>
public record FeedbackAvailabilityDto(bool TriageEnabled);

/// <summary>
/// The machine-readable rejection codes, declared once so the server, the docs and the client's error
/// copy cannot drift. Every one of them travels as <c>400 { "error": "&lt;code&gt;" }</c>, the shape this
/// codebase already uses (<c>titleRequired</c>, <c>questionRequired</c>).
/// </summary>
public static class FeedbackErrors
{
    public const string AreaRequired = "areaRequired";
    public const string AreaNotRecognized = "areaNotRecognized";
    public const string TargetTypeRequired = "targetTypeRequired";
    public const string TargetTypeNotRecognized = "targetTypeNotRecognized";
    public const string TargetIdRequired = "targetIdRequired";
    public const string VerdictRequired = "verdictRequired";
    public const string VerdictNotRecognized = "verdictNotRecognized";

    /// <summary>d1 section (3). A feedback row pointing at nothing is unactionable.</summary>
    public const string TargetNotFound = "targetNotFound";

    /// <summary>d1 section (1). An unkeyable vote cannot be deduped and defeats the one-vote rule.</summary>
    public const string VoterIdentityRequired = "voterIdentityRequired";

    public const string TextTooLong = "textTooLong";
    public const string InstallationIdTooLong = "installationIdTooLong";
    public const string ContextFieldTooLong = "contextFieldTooLong";

    public const string FeedbackNotFound = "feedbackNotFound";
    public const string StatusRequired = "statusRequired";
    public const string StatusNotRecognized = "statusNotRecognized";
    public const string StatusTransitionNotAllowed = "statusTransitionNotAllowed";
}
