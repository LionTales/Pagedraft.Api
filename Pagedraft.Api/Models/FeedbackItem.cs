namespace Pagedraft.Api.Models;

/// <summary>
/// ONE READER'S CURRENT OPINION ABOUT ONE THING (Show C2, d1 section (1)). Polymorphic from the first
/// row: <see cref="Area"/> and <see cref="TargetType"/> are open string vocabularies, so mounting the
/// widget on a proofread suggestion card later is a code change to an allowlist and a target-existence
/// arm, never an <c>ALTER TABLE</c>. C2 writes exactly two values into them
/// (<c>"chat-answer"</c> / <c>"conversation-message"</c>).
///
/// <para>IT IS THE CURRENT OPINION, NOT A HISTORY. The one-vote rule is an UPSERT keyed on
/// <c>(Area, TargetType, TargetId, UserId ?? InstallationId)</c>: a re-vote rewrites this row rather
/// than appending a second one, because C3's automated re-check consumes what a reader thinks NOW and
/// an append-only table would force every reader - triage and C3 alike - to resolve "which row is live"
/// on every read instead of once at write time. Retract is a hard delete, matching this app's standing
/// posture (<c>Conversation</c> has no soft-delete flag either).</para>
///
/// <para>NO EVIDENCE IS STORED HERE, EVER. The answer, the question and the grounding snapshot are
/// composed by a live JOIN from <see cref="TargetId"/> to <c>ConversationMessage.Id</c> at read time
/// (d1 section (2)). A copy would freeze at vote time and drift from the row it claims to describe, and
/// evidence contains manuscript text: it lives in this database, the triage view reads it in place, and
/// C3's tickets carry ids and summaries only.</para>
///
/// <para>MUTABLE, unlike <c>ConversationMessage</c>: <see cref="Verdict"/>, <see cref="Text"/> and
/// <see cref="ContextJson"/> move on a re-vote, and <see cref="Status"/> moves on a triage transition -
/// but only ever through <c>PATCH /api/feedback/{id}/status</c>. The two writers are deliberately
/// disjoint: a re-vote NEVER regresses <see cref="Status"/>, so a reader flipping their vote after the
/// owner (or C3) already confirmed a bug cannot silently erase the triage state.</para>
/// </summary>
public class FeedbackItem
{
    public Guid Id { get; set; }

    /// <summary>
    /// WHAT PART OF THE PRODUCT this is feedback about - <c>"chat-answer"</c> for mount #1. An open
    /// vocabulary validated at the application layer against <c>FeedbackAreas.All</c>, never by a CLR
    /// enum or a DB <c>CHECK</c>: mount #2 adds one constant, not a migration.
    /// </summary>
    public string Area { get; set; } = string.Empty;

    /// <summary>
    /// WHAT KIND OF THING <see cref="TargetId"/> points at - <c>"conversation-message"</c> for mount #1.
    /// Same open-vocabulary rule as <see cref="Area"/>, and the discriminator both the target-existence
    /// check and the evidence join switch on.
    /// </summary>
    public string TargetType { get; set; } = string.Empty;

    /// <summary>
    /// The thing itself. A <c>Guid</c> because every id in this schema is one (<c>Book</c>,
    /// <c>Chapter</c>, <c>Conversation</c>, <c>ConversationMessage</c>); an int-keyed target type would
    /// need this column revisited, and nothing in this codebase is int-keyed today.
    ///
    /// <para>NO FOREIGN KEY, and that is the point of a polymorphic target: a FK would bind this table
    /// to one target table forever, and it would also cascade-delete the very rows d1 section (3)
    /// decided to KEEP. Referential honesty is enforced instead by validating the id on write
    /// (<c>400 targetNotFound</c>) and by <see cref="TargetDeletedAt"/> on delete.</para>
    /// </summary>
    public Guid TargetId { get; set; }

    /// <summary>
    /// <c>"up"</c> or <c>"down"</c>. A plain string rather than a CLR enum, mirroring
    /// <c>ConversationMessage.Role</c>'s own leniency: this table imposes no stricter a contract than the
    /// wire has ever had, and no enum goes out over JSON in this API (no
    /// <c>JsonStringEnumConverter</c> is registered, so one would serialize as an integer).
    /// </summary>
    public string Verdict { get; set; } = string.Empty;

    /// <summary>
    /// The reader's optional note, capped at <c>FeedbackCaps.TextChars</c> (2000). Commentary about an
    /// answer, not a second manuscript-length payload: unlike <c>ConversationMessage.Text</c>
    /// (deliberately unbounded, because a second truncation site there would drift from the server-side
    /// history cap), nothing downstream composes a prompt from this field.
    ///
    /// <para>It is the one field here capable of carrying prose, so it is bound by the same privacy rule
    /// as the joined evidence: it lives in this database, and it is never forwarded verbatim anywhere.</para>
    /// </summary>
    public string? Text { get; set; }

    /// <summary>
    /// Serialized <c>FeedbackContextDto</c> - route, bookId/chapterId, uiLanguage, appBuild - and NOTHING
    /// a join could recover (d1 section (2)). Every field in it exists precisely because no joined row
    /// records it: which client route was open, which locale rendered the page, and what the reader's
    /// context was at VOTE time rather than at ask time.
    ///
    /// <para><c>nvarchar(max)</c> JSON rather than columns or a child table, following this schema's
    /// standing precedent for a blob nothing queries inside (<c>ConversationMessage.GroundingJson</c>,
    /// <c>ChunkSummary.StructuredJson</c>, <c>BookBible.*Json</c>, <c>AnalysisRunLog.ChunkDetailsJson</c>).
    /// The list's <c>bookId</c> filter deliberately resolves through the evidence JOIN and not through
    /// this blob, so nothing filters inside it; a child table is the correct move the day something does.</para>
    /// </summary>
    public string? ContextJson { get; set; }

    /// <summary>
    /// The local, pre-login voter identity: a client-generated GUID persisted in <c>localStorage</c> the
    /// first time the widget fires, mirroring the local-identity pattern this client already uses. It is
    /// the LOWER half of the one-vote key - <see cref="UserId"/> wins when it exists - and a request
    /// carrying neither is rejected <c>400 voterIdentityRequired</c>, because an unkeyable vote cannot be
    /// deduped and defeats the whole rule.
    /// </summary>
    public string? InstallationId { get; set; }

    /// <summary>
    /// The owning user, once a login exists. Shaped to <c>ApplicationUser.Id</c>'s Identity string key
    /// (<c>nvarchar(450)</c>), the same convention as <c>Conversation.UserId</c>, and indexed
    /// non-uniquely (one user has many votes). ALWAYS NULL TODAY: PageDraft has no <c>[Authorize]</c>,
    /// <c>AddAuthentication</c> or Identity anywhere, so the resolver that fills it reads an
    /// unauthenticated principal and gets nothing. Carrying the column from day one makes the login a
    /// clean addition rather than a data migration.
    /// </summary>
    public string? UserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// <c>New</c> / <c>Triaged</c> / <c>ConfirmedBug</c> / <c>Dismissed</c> / <c>Fixed</c>. Plain string
    /// for the same reason as <see cref="Verdict"/>. THIS IS C3'S INTERFACE: it consumes rows where
    /// <c>Status = "New"</c> and <c>Verdict = "down"</c> and moves them on through the same PATCH
    /// endpoint the owner's triage UI uses, so nothing else in this codebase may move it.
    /// </summary>
    public string Status { get; set; } = FeedbackStatuses.New;

    /// <summary>
    /// Equals <see cref="CreatedAt"/> at insert and moves on every legal transition. It does NOT move on
    /// a re-vote (see the class doc: the two writers are disjoint) and it does not move on a transition
    /// to the status the row already holds, which is a no-op rather than an event.
    /// </summary>
    public DateTimeOffset StatusChangedAt { get; set; }

    /// <summary>
    /// THE TOMBSTONE (d1 section (3)). Stamped when the thing this row points at is hard-deleted -
    /// today only by <c>ConversationsController.Delete</c>, in the same <c>SaveChangesAsync</c> that
    /// removes the message rows. The feedback row itself is KEPT, because the signal outlives the
    /// transcript: C3 still wants to know a down-vote existed even when the conversation that produced it
    /// is gone, and the triage detail then renders the stored context plus a "target deleted" notice
    /// instead of failing the read.
    ///
    /// <para>A future mount whose target type is ever hard-deleted owns stamping this the same way. No
    /// single hook can cover every target type in advance - that is the cost of the row being
    /// polymorphic, and it is stated here rather than discovered later.</para>
    /// </summary>
    public DateTimeOffset? TargetDeletedAt { get; set; }
}
