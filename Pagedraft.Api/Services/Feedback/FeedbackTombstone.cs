using Microsoft.EntityFrameworkCore;
using Pagedraft.Api.Data;

namespace Pagedraft.Api.Services.Feedback;

/// <summary>
/// WHAT HAPPENS TO FEEDBACK WHEN THE THING IT POINTS AT IS DELETED (Show C2, d1 section (3)): the rows are
/// KEPT and their target is tombstoned, because the signal outlives the transcript. C3 still wants to know
/// a down-vote existed even when the conversation that produced it is gone, and the triage detail then
/// renders the stored vote-time context plus a "target deleted" notice instead of failing the read.
///
/// <para>A STATIC HELPER RATHER THAN AN INJECTED SERVICE, on purpose. The stamp has to land inside the
/// deleting endpoint's OWN <c>SaveChangesAsync</c> - a second save could commit the message removal and
/// then fail, leaving feedback rows silently pointing at nothing - so it takes the caller's
/// <see cref="AppDbContext"/> and only marks entities, never saves. Keeping it static also means
/// <c>ConversationsController</c> gains no constructor dependency, which in this codebase is a change that
/// breaks every existing test of that controller for no behavioural reason.</para>
///
/// <para>NO SINGLE HOOK CAN COVER EVERY TARGET TYPE IN ADVANCE - that is the cost of the row being
/// polymorphic. A future mount whose target type is ever hard-deleted calls this from its own delete flow
/// with its own type token; nothing here reaches out to find such flows.</para>
/// </summary>
public static class FeedbackTombstone
{
    /// <summary>
    /// Marks every feedback row pointing at one of <paramref name="targetIds"/> as having lost its target,
    /// WITHOUT saving. The caller saves, in the same unit of work as the delete that made the targets
    /// disappear.
    ///
    /// <para>The <paramref name="targetType"/> is part of the match rather than assumed: two target types
    /// could in principle mint the same <c>Guid</c>, and a tombstone stamped on the wrong row would tell
    /// the triage owner a live target was deleted.</para>
    ///
    /// <para>An ALREADY-stamped row is left alone. The stamp records when the target FIRST went away, and
    /// a re-stamp would move that date for no event; it also makes this method safe to call twice.</para>
    /// </summary>
    /// <returns>How many rows were stamped - so a caller can log it, and a test can prove it was not zero.</returns>
    public static async Task<int> StampAsync(
        AppDbContext db,
        string targetType,
        IReadOnlyCollection<Guid> targetIds,
        DateTimeOffset deletedAt,
        CancellationToken ct)
    {
        if (targetIds.Count == 0) return 0;

        // Materialised as a list so the id set is a parameter rather than a correlated subquery, matching
        // how the calling delete path already materialises its message rows.
        var ids = targetIds as IList<Guid> ?? targetIds.ToList();

        var affected = await db.FeedbackItems
            .Where(f => f.TargetType == targetType
                        && ids.Contains(f.TargetId)
                        && f.TargetDeletedAt == null)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var row in affected) row.TargetDeletedAt = deletedAt;

        return affected.Count;
    }
}
