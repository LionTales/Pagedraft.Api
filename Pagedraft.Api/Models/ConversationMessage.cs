namespace Pagedraft.Api.Models;

/// <summary>
/// ONE PERSISTED TURN of a <see cref="Conversation"/> (Show C1, d1 section (1)). Immutable once written:
/// there is no Modified arm for it in <c>AppDbContext.SaveChangesAsync</c>, matching
/// <c>AnalysisRunLog</c>/<c>DocumentVersion</c>/<c>SuggestionOutcomeRecord</c>.
///
/// <para>A FAILED EXCHANGE IS STORED, NOT DROPPED, and that is deliberate rather than incidental. It is
/// kept flagged because a thumbs-down on a failed answer is exactly the signal C2 exists to collect.
/// What the flag does NOT mean is "excluded from the resend window": the client does not cut a failed
/// exchange out of its transcript. <c>ask()</c> appends the author's turn before the request goes out
/// and only <c>retry()</c> ever removes it, so an un-retried failure's QUESTION is still in a live
/// session's transcript and still in the window it sends. What never goes back up is the REFUSAL - a
/// fault is a different entry type in the client and can never be selected as a turn - so persisting
/// the failure cannot leak the assistant's fail-safe prose into a prompt.</para>
///
/// <para>THE TEXT IS THE FULL, UNTRUNCATED TURN. The per-turn 1000-character cap lives on the server
/// (<c>ProductChatService.MaxHistoryTurnChars</c>) and is applied to whatever the client sends; a second
/// truncation site here would drift from that constant the moment it is retuned, and the hydrated
/// conversation would then compose a prompt the unbroken session would not have composed - the one thing
/// C1's byte-identity pin forbids.</para>
/// </summary>
public class ConversationMessage
{
    public Guid Id { get; set; }

    public Guid ConversationId { get; set; }

    /// <summary>
    /// THE ORDER OF RECORD, and the reason it exists rather than ordering on <see cref="CreatedAt"/>: both
    /// turns of one exchange are stamped inside a single <c>SaveChangesAsync</c>, and
    /// <c>DateTimeOffset.UtcNow</c> on Windows can return the SAME value twice in that window, which would
    /// make "user then assistant" a coin flip on read. This is the 0-based ordinal within the conversation,
    /// taken from <see cref="Conversation.MessageCount"/> at insert time, so the transcript order and the
    /// page boundaries are both deterministic.
    /// </summary>
    public int Sequence { get; set; }

    /// <summary>
    /// <c>"user"</c> or <c>"assistant"</c>. A plain string rather than an enum, mirroring
    /// <c>ProductChatTurnDto.Role</c>'s own leniency (any value other than <c>"assistant"</c> is treated
    /// as the user's) rather than imposing a stricter contract than the wire ever had.
    /// </summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>The full turn text, never a truncated copy. See the class doc.</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// True on BOTH TURNS of a failed exchange, not only the answer. Set from the response DTO's own
    /// <c>IsGrounded</c>, which already distinguishes every one of the service's six return branches.
    ///
    /// <para>THE QUESTION IS FLAGGED SO HYDRATION CAN FIND THE PAIR, NOT SO IT CAN DROP IT. A flagged
    /// user row is replayed as a <c>user</c> turn (that is what an un-retried failure leaves in a live
    /// transcript), and the flag is what tells the client which question the fault under it belongs to,
    /// and which one to WITHHOLD when a later user row carries identical text, failed or not - the only
    /// trace <c>retry()</c> leaves, and a retry that failed again is still a retry. Deriving the pair instead from "the user turn immediately before a
    /// failed answer" would break the day a page boundary fell between the two rows. It is the one field
    /// written after the row exists (in the same request, the moment the answer's outcome is known);
    /// everything else about a message is immutable.</para>
    /// </summary>
    public bool Failed { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// The book that was open when THIS turn was asked or answered, stored on every message rather than
    /// only on assistant ones: the client's resend window drops any turn whose captured book differs from
    /// the book currently open, so hydration cannot reconstruct that filter without it.
    /// </summary>
    public Guid? AskBookId { get; set; }

    /// <summary>The ambient open chapter at ask time (<c>ProductChatRequest.AmbientChapterId</c>).</summary>
    public Guid? AskChapterId { get; set; }

    /// <summary>The 0-based ambient chapter order at ask time (<c>ProductChatRequest.AmbientChapterOrder</c>).</summary>
    public int? AskChapterOrder { get; set; }

    /// <summary>
    /// The per-answer grounding snapshot, serialized <c>ConversationGroundingDto</c>. Written on SUCCESSFUL
    /// ASSISTANT MESSAGES ONLY - null on every user turn and on every failed turn, per d1 section (1).
    ///
    /// <para>JSON rather than a child table, following this schema's standing precedent for a blob nothing
    /// queries inside (<c>ChunkSummary.StructuredJson</c>, <c>BookBible.*Json</c>,
    /// <c>BookFinding.EvidenceJson</c>, <c>AnalysisRunLog.ChunkDetailsJson</c>). Nothing in C1, C2 or C3 as
    /// scoped filters or joins on a field inside it; a child table is the correct move the day something
    /// does.</para>
    /// </summary>
    public string? GroundingJson { get; set; }

    public Conversation? Conversation { get; set; }
}
