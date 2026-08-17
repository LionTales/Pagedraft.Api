namespace Pagedraft.Api.Models;

/// <summary>
/// ONE PERSISTED SHOW CONVERSATION (Show C1, d1 section (1)). Until C1 a conversation existed only in the
/// client's memory: a refresh, a tab close or the "new conversation" reset destroyed the only copy and an
/// answer had no durable identity. This row is that identity, and it is the FLOOR under C2 (feedback needs
/// a message id to attach to) and C3 (the automated re-check needs the grounding snapshot captured at
/// answer time).
///
/// <para>PERSISTENCE ONLY, NEVER PROMPT COMPOSITION. C1's one architectural rule is that the composed
/// prompt does not change by a single byte: the CLIENT still composes and sends the history window
/// exactly as chatbot phase B shipped it, and the server merely writes down what flowed through it. The
/// hydration path therefore has to replay the FULL stored turn text - the 1000-character cap is server
/// side (<c>ProductChatService.MaxHistoryTurnChars</c>, applied to whatever the client sends), so storing
/// a pre-truncated copy would silently drift from that constant the day it is retuned.</para>
///
/// <para><see cref="UserId"/> IS SHAPED FOR A LOGIN THAT DOES NOT EXIST YET, and is always null today.
/// PageDraft has no <c>[Authorize]</c>, no <c>AddAuthentication</c> and no Identity anywhere; the owner's
/// decision is that it will get the Pagewise-style JWT + Google login, whose user key is
/// <c>ApplicationUser.Id</c>, an Identity string key conventionally <c>nvarchar(450)</c>. Carrying the
/// column from day one makes that a clean addition rather than a data migration over an author's whole
/// notebook.</para>
///
/// <para><see cref="BookId"/> is NULLABLE because Show is app chrome that sometimes happens to be open
/// inside a book: a conversation held on the dashboard is app-level product Q&amp;A and belongs to no
/// book. It records where the conversation STARTED; each message additionally records the book it was
/// asked in (<see cref="ConversationMessage.AskBookId"/>), because a request stays tied to the book it
/// was asked in rather than the one on screen.</para>
/// </summary>
public class Conversation
{
    public Guid Id { get; set; }

    /// <summary>
    /// The owning user, once a login exists. Indexed non-uniquely (one user has many conversations) and
    /// unfilled today. See the class doc: this is shaped to Identity's string key, not to a guess.
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>The book this conversation was started in, or null for an app-level conversation.</summary>
    public Guid? BookId { get; set; }

    /// <summary>
    /// DETERMINISTIC, NEVER MODEL-AUTHORED (d1 section (2)). Derived from the first user message by
    /// <c>ConversationTitle.FromFirstMessage</c>. A title model call would add a GPU cost and a brand new
    /// prompt surface to a persistence plan, which is exactly what C1's byte-identity rule exists to
    /// prevent. The column is wider than the derivation's 80-character budget so a user's own rename has
    /// headroom.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Bumped every time a message is appended (and on a rename), because the list is ordered by it. The
    /// dual-write marks this row Modified in the SAME save that inserts the messages, so the ordering can
    /// never lag the content.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// The cheap list projection: incremented in the same write as each message insert, so the list
    /// endpoint never joins or counts <see cref="ConversationMessage"/> rows. It is also the source of
    /// each message's <see cref="ConversationMessage.Sequence"/>.
    /// </summary>
    public int MessageCount { get; set; }

    public Book? Book { get; set; }
}
