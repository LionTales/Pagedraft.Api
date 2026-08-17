using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;

namespace Pagedraft.Api.Services.Feedback;

/// <summary>How a write ended, so the controller can pick a status code without re-deriving it.</summary>
public enum FeedbackOutcome
{
    Ok,

    /// <summary>The request was understood and refused - a 400 carrying <c>Error</c>.</summary>
    Rejected,

    /// <summary>The addressed row does not exist - a 404.</summary>
    NotFound
}

/// <summary>The result of a vote (create-or-update) or a retract.</summary>
public sealed record FeedbackWriteResult(FeedbackOutcome Outcome, FeedbackDto? Item, string? Error)
{
    public static FeedbackWriteResult Ok(FeedbackDto item) => new(FeedbackOutcome.Ok, item, null);
    public static FeedbackWriteResult Rejected(string error) => new(FeedbackOutcome.Rejected, null, error);
    public static readonly FeedbackWriteResult NotFound = new(FeedbackOutcome.NotFound, null, null);
}

/// <summary>
/// The result of a status transition. <see cref="From"/>/<see cref="To"/> are carried so a rejection can
/// tell the caller WHICH move was refused rather than only that one was.
/// </summary>
public sealed record FeedbackStatusResult(
    FeedbackOutcome Outcome, FeedbackDto? Item, string? Error, string? From, string? To);

/// <summary>
/// EVERY WRITE AND READ OF <see cref="FeedbackItem"/> (Show C2, d1). The controller is thin on purpose:
/// the one-vote upsert rule, the target validation, the caps and the transition graph are the parts that
/// must not drift, so they live here where the tests drive them directly.
///
/// <para>THE ONE-VOTE RULE, IN ONE SENTENCE: a row is keyed on
/// <c>(Area, TargetType, TargetId, UserId ?? InstallationId)</c> and a second vote on the same key
/// REWRITES it. That is an upsert and not an append, because C3's re-check consumes the CURRENT opinion
/// about a target rather than a history of opinions, and an append-only table would make every reader -
/// triage and C3 alike - resolve "which row is live" on every read instead of once at write time.</para>
///
/// <para>THE ONE-VOTE KEY IS ENFORCED HERE AND NOT BY A UNIQUE INDEX, deliberately, and this is the one
/// place that says so. The key is a COALESCE (<c>UserId ?? InstallationId</c>), which cannot be expressed
/// as a unique index over d1's frozen column set without adding a derived column d1 did not authorise. A
/// unique index over the raw <c>(Area, TargetType, TargetId, UserId, InstallationId)</c> tuple would
/// enforce a DIFFERENT rule - one vote per (user, device) rather than per user - so it would be correct
/// only for exactly as long as <c>UserId</c> stays null, and would start rejecting writes the day the
/// login lands. The residual exposure is a genuine race (two simultaneous votes from one voter on one
/// target creating two rows), which on a single-author app with no auth is theoretical; the read side is
/// ordered deterministically below so even then it resolves the same row every time rather than
/// alternating. Revisit this together with the <c>[Authorize]</c> retrofit, not before.</para>
///
/// <para>STATUS HAS EXACTLY ONE WRITER, <see cref="ChangeStatusAsync"/>. A re-vote never touches it - see
/// <see cref="VoteAsync"/> - which is what stops a reader flipping their vote from silently erasing a
/// <c>ConfirmedBug</c> the owner or C3 already produced.</para>
///
/// <para>NO MANUSCRIPT PROSE, NO FEEDBACK TEXT AND NO ANSWER TEXT IS EVER LOGGED by this class. Evidence
/// never leaves the database, and a log file is somewhere it would have left to. Every diagnostic line
/// below carries ids, vocabulary tokens and lengths only.</para>
/// </summary>
public sealed class FeedbackService
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    /// <summary>
    /// The context blob's serialization options, declared here rather than borrowed from MVC so the
    /// stored shape is a property of this class and cannot move when a controller option changes - the
    /// same reasoning (and the same camelCase shape) as <c>ChatConversationStore.SnapshotJson</c>.
    /// </summary>
    internal static readonly JsonSerializerOptions ContextJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly AppDbContext _db;
    private readonly FeedbackEvidenceComposer _evidence;
    private readonly ILogger<FeedbackService> _logger;

    public FeedbackService(
        AppDbContext db,
        FeedbackEvidenceComposer evidence,
        ILogger<FeedbackService> logger)
    {
        _db = db;
        _evidence = evidence;
        _logger = logger;
    }

    // ─── Write: the vote ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Create-or-update one voter's opinion about one target.
    ///
    /// <para><paramref name="userId"/> is resolved by the CALLER from the request principal, never from
    /// the request body: a client-supplied user id would be unauthenticated and therefore meaningless. It
    /// is null on every deployment today, so the key falls through to the installation id.</para>
    ///
    /// <para>THREE UPDATE RULES, each of which is a decision rather than an implementation detail:</para>
    /// <list type="number">
    /// <item><b>A verdict flip KEEPS the existing note</b> unless the request supplies a new one. Null
    /// text means "leave it alone" and non-null text REPLACES it, which is how one endpoint expresses
    /// both "I changed my mind" and "I revised what I wrote". A supplied value that is empty after
    /// trimming clears the note - a reader deliberately revising it down to nothing.</item>
    /// <item><b>Context is replaced when supplied</b>, because it describes VOTE time and this is a new
    /// vote; when the request carries none, the stored blob is left rather than blanked.</item>
    /// <item><b>Status is never touched.</b> See the class doc.</item>
    /// </list>
    /// </summary>
    public async Task<FeedbackWriteResult> VoteAsync(
        FeedbackVoteRequest? request, string? userId, CancellationToken ct)
    {
        var req = request ?? new FeedbackVoteRequest(null, null, Guid.Empty, null, null, null, null);

        var area = Trim(req.Area);
        var targetType = Trim(req.TargetType);
        var verdict = Trim(req.Verdict);
        var voterUser = Trim(userId);
        var voterInstallation = Trim(req.InstallationId);

        // Validation runs cheapest-first and stops at the FIRST failure, so the rejection log below names
        // one cause rather than a list the caller has to interpret.
        if (area == null) return Reject(FeedbackErrors.AreaRequired, targetType, req.TargetId);
        if (!FeedbackAreas.IsKnown(area)) return Reject(FeedbackErrors.AreaNotRecognized, targetType, req.TargetId);
        if (targetType == null) return Reject(FeedbackErrors.TargetTypeRequired, null, req.TargetId);
        if (!FeedbackTargetTypes.IsKnown(targetType))
            return Reject(FeedbackErrors.TargetTypeNotRecognized, targetType, req.TargetId);
        if (req.TargetId == Guid.Empty) return Reject(FeedbackErrors.TargetIdRequired, targetType, req.TargetId);
        if (verdict == null) return Reject(FeedbackErrors.VerdictRequired, targetType, req.TargetId);
        if (!FeedbackVerdicts.IsKnown(verdict))
            return Reject(FeedbackErrors.VerdictNotRecognized, targetType, req.TargetId);
        if (voterUser == null && voterInstallation == null)
            return Reject(FeedbackErrors.VoterIdentityRequired, targetType, req.TargetId);
        if (voterInstallation is { Length: > FeedbackCaps.InstallationIdChars })
            return Reject(FeedbackErrors.InstallationIdTooLong, targetType, req.TargetId);
        // Measured AFTER trimming, the same value that would be stored: refusing a note that is only over
        // the cap because of trailing whitespace would be a cap on something the reader cannot see.
        if ((req.Text?.Trim().Length ?? 0) > FeedbackCaps.TextChars)
            return Reject(FeedbackErrors.TextTooLong, targetType, req.TargetId);
        if (ExceedsContextFieldCap(req.Context))
            return Reject(FeedbackErrors.ContextFieldTooLong, targetType, req.TargetId);

        // LAST, because it is the only check that costs a query: a feedback row pointing at nothing is
        // unactionable, so an unresolvable target is a 400 rather than a stored row nobody can triage.
        if (!await TargetExistsAsync(targetType, req.TargetId, ct).ConfigureAwait(false))
            return Reject(FeedbackErrors.TargetNotFound, targetType, req.TargetId);

        var existing = await FindExistingVoteAsync(
            area, targetType, req.TargetId, voterUser, voterInstallation, ct).ConfigureAwait(false);

        FeedbackItem item;
        if (existing == null)
        {
            item = new FeedbackItem
            {
                Id = Guid.NewGuid(),
                Area = area,
                TargetType = targetType,
                TargetId = req.TargetId,
                Verdict = verdict,
                Text = NormalizeText(req.Text),
                ContextJson = SerializeContext(req.Context),
                InstallationId = voterInstallation,
                UserId = voterUser,
                Status = FeedbackStatuses.New
            };
            _db.FeedbackItems.Add(item);
        }
        else
        {
            item = existing;
            item.Verdict = verdict;
            if (req.Text != null) item.Text = NormalizeText(req.Text);
            if (req.Context != null) item.ContextJson = SerializeContext(req.Context);
            // Keep the freshest device stamp when the row was matched on the USER half of the key: the
            // same person voting from a second browser is still one vote, and the newer installation id
            // is the one a later retract will arrive with.
            if (voterInstallation != null) item.InstallationId = voterInstallation;
            // Status and StatusChangedAt are DELIBERATELY absent from this block.
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return FeedbackWriteResult.Ok(ToDto(item));
    }

    /// <summary>
    /// Retract - a HARD delete of the row, matching this app's standing posture (<c>Conversation</c> has
    /// no soft-delete flag either). Retract is the voter's own action on their own row, so it is not gated
    /// by the triage flag; ownership is not enforced because there is no identity to enforce it against
    /// yet, and it arrives with the same <c>[Authorize]</c> retrofit as every other endpoint here.
    /// </summary>
    public async Task<FeedbackWriteResult> RetractAsync(Guid id, CancellationToken ct)
    {
        var item = await _db.FeedbackItems.FirstOrDefaultAsync(f => f.Id == id, ct).ConfigureAwait(false);
        if (item == null) return FeedbackWriteResult.NotFound;

        _db.FeedbackItems.Remove(item);
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Feedback {FeedbackId} was retracted ({Area} / {TargetType} {TargetId}, verdict {Verdict}, " +
            "status {Status}). The row is hard-deleted; nothing about the target changes.",
            item.Id, item.Area, item.TargetType, item.TargetId, item.Verdict, item.Status);

        return new FeedbackWriteResult(FeedbackOutcome.Ok, null, null);
    }

    // ─── Write: the triage transition ───────────────────────────────────────────────────────────────

    /// <summary>
    /// THE ONLY WRITE PATH FOR <c>Status</c>, for the owner's triage buttons and for C3 alike - C3 is not
    /// a special caller with its own path (d1 section (5)).
    ///
    /// <para>A transition to the status the row already holds is an idempotent NO-OP: it saves nothing and
    /// leaves <c>StatusChangedAt</c> where it was, because a double-clicked button is not an event and
    /// re-stamping would make the column lie about when a person last judged this row.</para>
    /// </summary>
    public async Task<FeedbackStatusResult> ChangeStatusAsync(Guid id, string? status, CancellationToken ct)
    {
        var to = Trim(status);
        if (to == null)
            return new FeedbackStatusResult(
                FeedbackOutcome.Rejected, null, FeedbackErrors.StatusRequired, null, null);

        if (!FeedbackStatuses.IsKnown(to))
        {
            _logger.LogWarning(
                "A triage transition on feedback {FeedbackId} was refused: the requested status is not in " +
                "the vocabulary (requested {RequestedStatus}, known: {KnownStatuses}).",
                id, to, string.Join(", ", FeedbackStatuses.All));
            return new FeedbackStatusResult(
                FeedbackOutcome.Rejected, null, FeedbackErrors.StatusNotRecognized, null, to);
        }

        var item = await _db.FeedbackItems.FirstOrDefaultAsync(f => f.Id == id, ct).ConfigureAwait(false);
        if (item == null)
            return new FeedbackStatusResult(FeedbackOutcome.NotFound, null, null, null, to);

        var from = item.Status;

        if (string.Equals(from, to, StringComparison.Ordinal))
        {
            _logger.LogDebug(
                "Feedback {FeedbackId} is already {Status}; the transition is a no-op and StatusChangedAt " +
                "is left where it was.",
                item.Id, to);
            return new FeedbackStatusResult(FeedbackOutcome.Ok, ToDto(item), null, from, to);
        }

        if (!FeedbackStatuses.IsLegalTransition(from, to))
        {
            _logger.LogWarning(
                "A triage transition on feedback {FeedbackId} was refused as illegal: {FromStatus} -> " +
                "{ToStatus}. The legal moves from that status are: {LegalMoves}.",
                item.Id, from, to,
                FeedbackStatuses.LegalTransitions.TryGetValue(from, out var legal)
                    ? string.Join(", ", legal)
                    : "(none)");
            return new FeedbackStatusResult(
                FeedbackOutcome.Rejected, null, FeedbackErrors.StatusTransitionNotAllowed, from, to);
        }

        item.Status = to;
        item.StatusChangedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct).ConfigureAwait(false);

        // THE TRANSITION LEDGER. There is no status-history table (v1 is a reading tool), so this line is
        // the only durable record that a row moved and when. Ids and vocabulary tokens only - no note, no
        // answer, no manuscript.
        _logger.LogInformation(
            "Feedback {FeedbackId} moved {FromStatus} -> {ToStatus} ({Area} / {TargetType} {TargetId}, " +
            "verdict {Verdict}).",
            item.Id, from, to, item.Area, item.TargetType, item.TargetId, item.Verdict);

        return new FeedbackStatusResult(FeedbackOutcome.Ok, ToDto(item), null, from, to);
    }

    // ─── Read: the triage surface ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The paged triage list, newest first. Every filter is optional and an omitted one means EVERYTHING -
    /// it never quietly means a default subset.
    ///
    /// <para><paramref name="bookId"/> RESOLVES THROUGH THE EVIDENCE JOIN
    /// (<c>ConversationMessage.AskBookId</c>) rather than through a column on the feedback row, which is
    /// the same rule as everywhere else here: evidence is never copied. A consequence worth stating
    /// because it is behaviour and not an accident - filtering by book excludes rows whose target type
    /// has no book at all, and excludes rows whose target has been deleted, since neither can be shown to
    /// belong to that book.</para>
    /// </summary>
    public async Task<FeedbackListDto> ListAsync(
        string? area,
        string? status,
        string? verdict,
        Guid? bookId,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var safePage = page < 1 ? 1 : page;
        var safeSize = pageSize < 1 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);

        var query = _db.FeedbackItems.AsNoTracking();

        var areaFilter = Trim(area);
        if (areaFilter != null) query = query.Where(f => f.Area == areaFilter);

        var statusFilter = Trim(status);
        if (statusFilter != null) query = query.Where(f => f.Status == statusFilter);

        var verdictFilter = Trim(verdict);
        if (verdictFilter != null) query = query.Where(f => f.Verdict == verdictFilter);

        if (bookId.HasValue)
        {
            var book = bookId.Value;
            query = query.Where(f =>
                f.TargetType == FeedbackTargetTypes.ConversationMessage &&
                _db.ConversationMessages.Any(m => m.Id == f.TargetId && m.AskBookId == book));
        }

        var total = await query.CountAsync(ct).ConfigureAwait(false);

        var rows = await query
            // Id breaks the tie: two votes cast inside one tick would otherwise page nondeterministically
            // and a client walking the pages could see one row twice and another never.
            .OrderByDescending(f => f.CreatedAt)
            .ThenByDescending(f => f.Id)
            .Skip((safePage - 1) * safeSize)
            .Take(safeSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // The list's book column is composed by ONE extra query bounded by the page size, rather than by a
        // per-row join or by denormalising the book onto the feedback row.
        var messageIds = rows
            .Where(r => r.TargetType == FeedbackTargetTypes.ConversationMessage)
            .Select(r => r.TargetId)
            .Distinct()
            .ToList();

        var bookByMessage = messageIds.Count == 0
            ? new Dictionary<Guid, Guid?>()
            : (await _db.ConversationMessages.AsNoTracking()
                    .Where(m => messageIds.Contains(m.Id))
                    .Select(m => new { m.Id, m.AskBookId })
                    .ToListAsync(ct)
                    .ConfigureAwait(false))
                .ToDictionary(x => x.Id, x => x.AskBookId);

        var items = rows
            .Select(r => new FeedbackListItemDto(
                r.Id, r.Area, r.TargetType, r.TargetId, r.Verdict, r.Text, r.Status,
                r.CreatedAt, r.StatusChangedAt, r.TargetDeletedAt,
                bookByMessage.TryGetValue(r.TargetId, out var b) ? b : null))
            .ToList();

        return new FeedbackListDto(items, safePage, safeSize, total);
    }

    /// <summary>
    /// The triage detail: the row plus its joined evidence, composed in ONE response so the detail view
    /// makes one request instead of reconstructing the join client-side. Null when the feedback row does
    /// not exist - a MISSING TARGET is not this method's 404 (see <see cref="FeedbackEvidenceComposer"/>).
    /// </summary>
    public async Task<FeedbackDetailDto?> GetDetailAsync(Guid id, CancellationToken ct)
    {
        var item = await _db.FeedbackItems.AsNoTracking()
            .FirstOrDefaultAsync(f => f.Id == id, ct)
            .ConfigureAwait(false);
        if (item == null) return null;

        var evidence = await _evidence.ComposeAsync(item, ct).ConfigureAwait(false);
        return new FeedbackDetailDto(ToDto(item), evidence);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The one-vote lookup. Two branches rather than one coalescing predicate, because
    /// <c>UserId ?? InstallationId</c> in a LINQ tree translates badly and reads worse: when a user id
    /// exists it is the WHOLE key (the same person on a second device is one vote), and when it does not,
    /// the match additionally requires <c>UserId == null</c> so an anonymous vote can never hijack a
    /// signed-in reader's row.
    /// </summary>
    private async Task<FeedbackItem?> FindExistingVoteAsync(
        string area, string targetType, Guid targetId,
        string? voterUser, string? voterInstallation, CancellationToken ct)
    {
        var query = _db.FeedbackItems
            .Where(f => f.Area == area && f.TargetType == targetType && f.TargetId == targetId);

        query = voterUser != null
            ? query.Where(f => f.UserId == voterUser)
            : query.Where(f => f.UserId == null && f.InstallationId == voterInstallation);

        // Oldest first, then by id: if the race the class doc names ever produced two rows, every read
        // resolves the SAME one rather than alternating between them.
        return await query
            .OrderBy(f => f.CreatedAt)
            .ThenBy(f => f.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The target-existence arm per target type (d1 section (3)). Mount #2 adds one case here; an
    /// unrecognised type never reaches this method (it is refused above), so the default is unreachable
    /// today and returns false rather than true - an unvalidatable target must not be storable.
    /// </summary>
    private async Task<bool> TargetExistsAsync(string targetType, Guid targetId, CancellationToken ct)
        => targetType switch
        {
            FeedbackTargetTypes.ConversationMessage =>
                await _db.ConversationMessages.AnyAsync(m => m.Id == targetId, ct).ConfigureAwait(false),
            _ => false
        };

    private FeedbackWriteResult Reject(string error, string? targetType, Guid targetId)
    {
        // THE REJECTED-VOTE LINE. It names WHICH validation failed and WHAT was being voted on, by id -
        // never the note, never the answer, never any prose. Every code but TargetNotFound stays at
        // Warning because the client counts characters and picks its own vocabulary tokens, so a
        // rejection on one of those means the two halves of the contract have drifted. TargetNotFound is
        // different: the target existed when the reader's page loaded and the client cannot know it
        // stopped existing before the vote landed, so this one code is an ordinary race (the target's
        // conversation was deleted in another tab or window) and not evidence of a defect - it logs at
        // Information with wording that says so.
        if (error == FeedbackErrors.TargetNotFound)
        {
            _logger.LogInformation(
                "A feedback vote was rejected: {RejectionReason} (target type {TargetType}, target {TargetId}). " +
                "The target most likely disappeared after the client loaded it (e.g. deleted in another tab); " +
                "no row was written or changed.",
                error, targetType ?? "(none)", targetId);
        }
        else
        {
            _logger.LogWarning(
                "A feedback vote was rejected: {RejectionReason} (target type {TargetType}, target {TargetId}). " +
                "No row was written or changed.",
                error, targetType ?? "(none)", targetId);
        }
        return FeedbackWriteResult.Rejected(error);
    }

    private static string? Trim(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    /// Null in means null out ("leave the note alone" - the caller checks for null BEFORE calling this).
    /// An empty-after-trim value becomes null, which is the reader clearing their note.
    /// </summary>
    private static string? NormalizeText(string? text) => Trim(text);

    private static bool ExceedsContextFieldCap(FeedbackContextDto? context)
        => context != null &&
           (context.Route is { Length: > FeedbackCaps.ContextFieldChars } ||
            context.UiLanguage is { Length: > FeedbackCaps.ContextFieldChars } ||
            context.AppBuild is { Length: > FeedbackCaps.ContextFieldChars });

    private static string? SerializeContext(FeedbackContextDto? context)
        => context == null ? null : JsonSerializer.Serialize(context, ContextJson);

    /// <summary>
    /// The stored context, or null. A blob that will not parse does NOT fail the read - the owner came
    /// for the feedback row, and one unreadable metadata blob must not cost it - and the fault is logged
    /// rather than swallowed.
    /// </summary>
    private FeedbackContextDto? DeserializeContext(Guid feedbackId, string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<FeedbackContextDto>(json, ContextJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "The vote-time context stored on feedback {FeedbackId} did not parse ({Chars} chars). The " +
                "row is returned WITHOUT it rather than failing the read, but the triage owner loses the " +
                "route and locale this vote was cast in.",
                feedbackId, json.Length);
            return null;
        }
    }

    private FeedbackDto ToDto(FeedbackItem item) => new(
        item.Id, item.Area, item.TargetType, item.TargetId, item.Verdict, item.Text, item.Status,
        item.CreatedAt, item.StatusChangedAt, item.TargetDeletedAt,
        DeserializeContext(item.Id, item.ContextJson));
}
