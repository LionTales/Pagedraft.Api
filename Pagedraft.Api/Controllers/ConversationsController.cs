using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Chat;
using Pagedraft.Api.Services.Feedback;

namespace Pagedraft.Api.Controllers;

/// <summary>
/// The persisted Show conversations (Show C1). List, read, rename, delete - everything the drawer's
/// history UI needs, and nothing that composes a prompt.
///
/// <para>THIS CONTROLLER NEVER TOUCHES THE MODEL. It reads and writes rows. C1's one architectural rule
/// is that the composed prompt does not change by a byte, and the way that is guaranteed is that the
/// server has no path from stored history into a prompt at all: the CLIENT still composes and sends the
/// resend window, and hydration is a client-side reconstruction from what
/// <see cref="Messages"/> returns.</para>
///
/// <para>NO AUTH TODAY, BY THE PLAN'S OWN DECISION - PageDraft has no <c>[Authorize]</c>,
/// <c>AddAuthentication</c> or Identity anywhere yet. Every endpoint here, plus the
/// <c>conversationId</c>-bearing branch of <c>POST /api/product-chat</c>, gains <c>[Authorize]</c> and a
/// per-request ownership check (<c>Conversation.UserId == current user id</c>) the day the Pagewise-style
/// JWT + Google login ships. The column already exists and is always written null, so that is an addition
/// rather than a retrofit over an author's whole notebook.</para>
/// </summary>
[ApiController]
[Route("api/conversations")]
public class ConversationsController : ControllerBase
{
    /// <summary>
    /// The retention SOFT cap (d1 section (3)). Purely informational: nothing is auto-deleted, ever.
    /// Authors keep notebooks, and this feature does not get to decide which of an author's conversations
    /// stop mattering. Crossing it only sets <c>nearCapWarning</c> on the list response.
    /// </summary>
    public const int SoftCap = 200;

    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;
    public const int DefaultMessagePageSize = 100;
    public const int MaxMessagePageSize = 500;

    private readonly AppDbContext _db;
    private readonly ILogger<ConversationsController> _logger;

    public ConversationsController(AppDbContext db, ILogger<ConversationsController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// GET /api/conversations - newest first by <c>UpdatedAt</c>, optionally filtered to one book.
    ///
    /// <para>An OMITTED <c>bookId</c> means EVERY conversation, app-level ones included; it does not mean
    /// "the app-level ones". A history list that silently hid the book conversations would make the
    /// feature look broken to the only author using it.</para>
    ///
    /// <para>No message bodies: <c>MessageCount</c> is maintained on the row in the same write as each
    /// message insert precisely so this query never touches the message table.</para>
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ConversationListDto>> List(
        [FromQuery] Guid? bookId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        CancellationToken ct = default)
    {
        var (safePage, safeSize) = Paging(page, pageSize, DefaultPageSize, MaxPageSize);

        var query = _db.Conversations.AsNoTracking();
        if (bookId.HasValue) query = query.Where(c => c.BookId == bookId.Value);

        var total = await query.CountAsync(ct);
        var storedTotal = bookId.HasValue ? await _db.Conversations.CountAsync(ct) : total;

        var items = await query
            .OrderByDescending(c => c.UpdatedAt)
            .ThenByDescending(c => c.CreatedAt)
            .Skip((safePage - 1) * safeSize)
            .Take(safeSize)
            .Select(c => new ConversationListItemDto(
                c.Id, c.Title, c.BookId, c.CreatedAt, c.UpdatedAt, c.MessageCount))
            .ToListAsync(ct);

        return Ok(new ConversationListDto(items, safePage, safeSize, total, storedTotal >= SoftCap));
    }

    /// <summary>GET /api/conversations/{id} - metadata alone. 404 when the id does not resolve.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ConversationDto>> Get(Guid id, CancellationToken ct)
    {
        var c = await _db.Conversations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c == null) return NotFound(new { error = "conversationNotFound" });

        return Ok(new ConversationDto(c.Id, c.Title, c.BookId, c.CreatedAt, c.UpdatedAt, c.MessageCount));
    }

    /// <summary>
    /// GET /api/conversations/{id}/messages - the paged transcript, OLDEST FIRST (render order), ordered
    /// by <c>Sequence</c>.
    ///
    /// <para>ORDERED BY SEQUENCE AND NOT BY <c>CreatedAt</c>, deliberately: both turns of one exchange are
    /// stamped inside a single save and <c>DateTimeOffset.UtcNow</c> can hand them the same value, which
    /// would make "question then answer" a coin flip on read.</para>
    ///
    /// <para>The text returned is the FULL stored turn. The per-turn character cap is a SERVER-side
    /// property of prompt composition (<c>ProductChatService.MaxHistoryTurnChars</c>), applied to whatever
    /// the client sends; truncating here would give the hydrated conversation a different window from the
    /// unbroken one the moment that constant is retuned.</para>
    /// </summary>
    [HttpGet("{id:guid}/messages")]
    public async Task<ActionResult<ConversationMessagesDto>> Messages(
        Guid id,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultMessagePageSize,
        CancellationToken ct = default)
    {
        var exists = await _db.Conversations.AsNoTracking().AnyAsync(c => c.Id == id, ct);
        if (!exists) return NotFound(new { error = "conversationNotFound" });

        var (safePage, safeSize) = Paging(page, pageSize, DefaultMessagePageSize, MaxMessagePageSize);

        var query = _db.ConversationMessages.AsNoTracking().Where(m => m.ConversationId == id);
        var total = await query.CountAsync(ct);

        var rows = await query
            .OrderBy(m => m.Sequence)
            .Skip((safePage - 1) * safeSize)
            .Take(safeSize)
            .ToListAsync(ct);

        var items = rows
            .Select(m => new ConversationMessageDto(
                m.Id, m.Sequence, m.Role, m.Text, m.Failed, m.CreatedAt,
                m.AskBookId, m.AskChapterId, m.AskChapterOrder,
                DeserializeGrounding(m.Id, m.GroundingJson)))
            .ToList();

        return Ok(new ConversationMessagesDto(items, safePage, safeSize, total));
    }

    /// <summary>
    /// PATCH /api/conversations/{id} - the author's own title (d1 section (2)).
    ///
    /// <para>A blank title after trimming is a 400 rather than a silent fall back to the auto-derived one,
    /// mirroring <c>ProductChatController</c>'s blank-question 400: a rename that quietly did something
    /// else is worse than one that says no.</para>
    /// </summary>
    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<ConversationDto>> Rename(
        Guid id, [FromBody] ConversationRenameRequest req, CancellationToken ct)
    {
        var title = (req?.Title ?? string.Empty).Trim();
        if (title.Length == 0) return BadRequest(new { error = "titleRequired" });
        if (title.Length > MaxTitleLength) title = title[..MaxTitleLength];

        var c = await _db.Conversations.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c == null) return NotFound(new { error = "conversationNotFound" });

        c.Title = title;
        await _db.SaveChangesAsync(ct);

        return Ok(new ConversationDto(c.Id, c.Title, c.BookId, c.CreatedAt, c.UpdatedAt, c.MessageCount));
    }

    /// <summary>
    /// DELETE /api/conversations/{id} - HARD delete (d1 section (3)). No soft-delete flag and no undo:
    /// this is the owner's own machine and database today, and a delete the author asked for that leaves
    /// the row behind is the kind of thing that gets rediscovered under auth as a privacy defect.
    ///
    /// <para>The message rows are removed EXPLICITLY before the conversation even though the FK cascades,
    /// matching how every other book-scoped table is deleted in this codebase: the cascade is the database
    /// keeping its promise, not the reason the rows go.</para>
    ///
    /// <para>SHOW C2 ADDED ONE THING HERE AND ONLY ONE: the feedback rows pointing at these messages are
    /// KEPT and TOMBSTONED (d1 section (3)) - the signal outlives the transcript, because C3 still wants to
    /// know a down-vote existed even when the conversation that produced it is gone. The stamp lands in the
    /// SAME <c>SaveChangesAsync</c> as the removal, deliberately: a second save could commit the delete and
    /// then fail, leaving feedback rows silently pointing at nothing with no record that anything happened.
    /// It is why the id set is taken from the already-materialised <c>messages</c> list rather than
    /// re-queried after the rows are gone.</para>
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var c = await _db.Conversations.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c == null) return NotFound(new { error = "conversationNotFound" });

        var messages = await _db.ConversationMessages.Where(m => m.ConversationId == id).ToListAsync(ct);

        var tombstoned = await FeedbackTombstone.StampAsync(
            _db,
            FeedbackTargetTypes.ConversationMessage,
            messages.Select(m => m.Id).ToList(),
            DateTimeOffset.UtcNow,
            ct);

        _db.ConversationMessages.RemoveRange(messages);
        _db.Conversations.Remove(c);
        await _db.SaveChangesAsync(ct);

        if (tombstoned > 0)
        {
            _logger.LogInformation(
                "Conversation {ConversationId} was deleted with {MessageCount} turns; {TombstonedCount} " +
                "feedback row(s) pointing at those turns were KEPT and tombstoned. Their triage detail now " +
                "renders the stored vote-time context instead of the transcript.",
                id, messages.Count, tombstoned);
        }

        return NoContent();
    }

    /// <summary>Mirrors the <c>Conversation.Title</c> column width.</summary>
    private const int MaxTitleLength = 200;

    private static (int Page, int PageSize) Paging(int page, int pageSize, int fallback, int max)
    {
        var safePage = page < 1 ? 1 : page;
        var safeSize = pageSize < 1 ? fallback : Math.Min(pageSize, max);
        return (safePage, safeSize);
    }

    /// <summary>
    /// The stored snapshot, or null. A blob that cannot be parsed does NOT fail the read: the transcript
    /// is what the author came for, and one unreadable diagnostic must not cost them the conversation. It
    /// IS logged, because a snapshot that stopped parsing is a schema drift C3 will trip over later.
    ///
    /// <para>The parse itself lives in <see cref="ConversationGroundingSnapshot"/>, extracted when Show
    /// C2's triage evidence became its second reader: one place holds the writer's serializer options and
    /// the catch-everything posture, and it hands the exception BACK rather than swallowing it, so this
    /// method still logs the fault with the id that makes it findable.</para>
    /// </summary>
    private ConversationGroundingDto? DeserializeGrounding(Guid messageId, string? json)
    {
        if (ConversationGroundingSnapshot.TryRead(json, out var snapshot, out var error)) return snapshot;

        _logger.LogError(error,
            "The grounding snapshot stored on conversation message {MessageId} did not parse ({Chars} " +
            "chars). The message is returned WITHOUT its snapshot rather than failing the transcript " +
            "read, but C3's automated re-check has nothing to work from for this answer.",
            messageId, json!.Length);
        return null;
    }
}
