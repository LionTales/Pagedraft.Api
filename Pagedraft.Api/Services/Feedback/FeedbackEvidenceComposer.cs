using Microsoft.EntityFrameworkCore;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Models.Dtos;
using Pagedraft.Api.Services.Chat;

namespace Pagedraft.Api.Services.Feedback;

/// <summary>
/// THE JOIN THAT IS THE WHOLE POINT OF C2 (Show C2, d1 section (2)). A thumbs-down with no evidence is a
/// mood; this class is what turns one into something C3 can mechanically re-check - the question that was
/// asked, the answer that was given, and the grounding snapshot the answer was built from.
///
/// <para>IT COMPOSES, IT NEVER COPIES. Every field below is read LIVE from
/// <c>ConversationMessage</c> at triage-read time, and nothing about the answer, the question or the
/// grounding refs is ever written into a <see cref="FeedbackItem"/> at vote time. A copy would freeze at
/// the moment of the vote and drift from the row it claims to describe; a join is honest on every read.</para>
///
/// <para>PRIVACY, stated at the site rather than only in the plan: this is the manuscript-bearing half of
/// the feature. It is composed for the flag-gated triage read and for nothing else, it is never persisted
/// onto a feedback row, and C3's tickets carry ids and summaries - never this prose.</para>
///
/// <para>A MISS IS A STATE, NOT AN ERROR. d1 section (3) decided that deleting a conversation KEEPS its
/// feedback rows and tombstones their target, so an unresolvable target is the ordinary consequence of a
/// decision rather than a fault: the reason is named on the DTO and the caller renders the stored context
/// alone. Failing the read here would 404 exactly the rows the owner deliberately chose to keep.</para>
/// </summary>
public sealed class FeedbackEvidenceComposer
{
    /// <summary>The target row is gone and its tombstone says why - the conversation was deleted.</summary>
    public const string ReasonTargetDeleted = "targetDeleted";

    /// <summary>
    /// Nothing resolves and NO tombstone was stamped. Distinguished from
    /// <see cref="ReasonTargetDeleted"/> on purpose: it means a row left the database without going
    /// through the delete path that owns the stamp, which is a defect worth being able to see rather than
    /// one worth smoothing over into "deleted".
    /// </summary>
    public const string ReasonTargetMissing = "targetMissing";

    /// <summary>
    /// A target type with no evidence composer yet. Mount #2 adds an arm here; until it does, its rows
    /// still read - with their stored context and this reason - instead of throwing.
    /// </summary>
    public const string ReasonTargetTypeNotComposable = "targetTypeNotComposable";

    private readonly AppDbContext _db;
    private readonly ILogger<FeedbackEvidenceComposer> _logger;

    public FeedbackEvidenceComposer(AppDbContext db, ILogger<FeedbackEvidenceComposer> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Composes the evidence for one feedback row. Never throws for a missing target and never returns
    /// null - the DTO always comes back, carrying <c>Available = false</c> and a reason when it could not
    /// be filled.
    /// </summary>
    public async Task<FeedbackEvidenceDto> ComposeAsync(FeedbackItem feedback, CancellationToken ct)
    {
        if (!string.Equals(feedback.TargetType, FeedbackTargetTypes.ConversationMessage, StringComparison.Ordinal))
            return Unavailable(ReasonTargetTypeNotComposable);

        var answer = await _db.ConversationMessages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == feedback.TargetId, ct)
            .ConfigureAwait(false);

        if (answer == null)
        {
            return Unavailable(feedback.TargetDeletedAt.HasValue
                ? ReasonTargetDeleted
                : ReasonTargetMissing);
        }

        var conversationTitle = await _db.Conversations.AsNoTracking()
            .Where(c => c.Id == answer.ConversationId)
            .Select(c => c.Title)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // THE PAIRED QUESTION IS FOUND BY SEQUENCE, not by CreatedAt and not by "the previous row I
        // happened to fetch": both turns of one exchange are stamped inside a single SaveChangesAsync and
        // can share a timestamp to the tick, which is the same reason the transcript itself is read in
        // Sequence order. Sequence 0 has no predecessor, and an assistant turn without one is a shape this
        // read tolerates (question stays null) rather than one it asserts against.
        var question = answer.Sequence > 0
            ? await _db.ConversationMessages.AsNoTracking()
                .Where(m => m.ConversationId == answer.ConversationId && m.Sequence == answer.Sequence - 1)
                .Select(m => m.Text)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false)
            : null;

        if (!ConversationGroundingSnapshot.TryRead(answer.GroundingJson, out var grounding, out var error))
        {
            // The evidence read survives an unreadable snapshot for the same reason the transcript read
            // does - the owner came for the feedback - but it is logged, because a snapshot that stopped
            // parsing is a schema drift C3's automated re-check will trip over with nothing to work from.
            _logger.LogError(error,
                "The grounding snapshot stored on conversation message {MessageId} did not parse, so the " +
                "triage evidence for feedback {FeedbackId} is composed WITHOUT it. C3's automated re-check " +
                "has nothing to re-check this answer against.",
                answer.Id, feedback.Id);
        }

        return new FeedbackEvidenceDto(
            Available: true,
            UnavailableReason: null,
            ConversationId: answer.ConversationId,
            ConversationTitle: conversationTitle,
            Question: question,
            Answer: answer.Text,
            AnswerFailed: answer.Failed,
            AnsweredAt: answer.CreatedAt,
            AskBookId: answer.AskBookId,
            AskChapterId: answer.AskChapterId,
            AskChapterOrder: answer.AskChapterOrder,
            Grounding: grounding);
    }

    private static FeedbackEvidenceDto Unavailable(string reason) => new(
        Available: false,
        UnavailableReason: reason,
        ConversationId: null,
        ConversationTitle: null,
        Question: null,
        Answer: null,
        AnswerFailed: null,
        AnsweredAt: null,
        AskBookId: null,
        AskChapterId: null,
        AskChapterOrder: null,
        Grounding: null);
}
