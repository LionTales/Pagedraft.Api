using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Data;
using Pagedraft.Api.Models;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// THE ONE (book id, task) -&gt; <see cref="AiTier"/> lookup (model-tier-fast-thinking plan, p3-2 introduced it
/// inside <c>UnifiedAnalysisService</c>; p3-3 lifted it here because a SECOND and THIRD caller appeared;
/// tier-ux-rework c1 made it per TASK).
///
/// WHY IT IS SHARED CODE AND NOT A COPIED SIX-LINE QUERY. Three services now need a book's tier and they need
/// the SAME answer: <c>UnifiedAnalysisService</c> stamps it on the outgoing <see cref="AiRequest"/> (what
/// actually RAN), <see cref="AnalysisContextService"/> uses it to resolve the active LinguisticAnalysis model
/// that <c>ChapterStyleProfile.BuiltWithModel</c> is compared against, and
/// <see cref="StyleBaselineService"/> uses it for <c>BookStyleBaseline.BuiltWithModel</c> plus the status
/// DTO's <c>ActiveModel</c>. If any two of those disagreed about a book's tier, the freshness gate would
/// compare a stamp from one tier against an active model from another and every profile would read
/// permanently STALE - one extra LLM call per chapter per analysis, forever. That is the exact failure mode
/// the resolver extraction exists to prevent, one layer up.
///
/// PER TASK, AND THE INVARIANT SURVIVES BECAUSE THE TASK IS A REQUIRED ARGUMENT. There is deliberately no
/// task-blind overload: a caller that forgot to say which task it is asking about would get an answer that is
/// right for some other task, which is the same disagreement with a longer fuse. The two freshness consumers
/// both ask about <see cref="AiTaskType.LinguisticAnalysis"/> - the task whose model those stamps name - so
/// they still agree with each other and with the LinguisticAnalysis runs, while a tier flip on Proofread now
/// leaves their profiles alone instead of invalidating them for nothing.
///
/// PRECEDENCE (three rungs, one query):
/// <code>
///   1. BookAiTaskTiers[(book, task)].Tier   - the explicit per-task override
///   2. Book.AiTier                          - the book-level default seed
///   3. AiTier.Fast                          - the floor
/// </code>
///
/// FAIL-SAFE IN ONE DIRECTION ONLY. A null/empty book id, a book that no longer exists, a null column, an
/// unrecognised token (in EITHER rung), and a database read that THROWS all resolve to
/// <see cref="AiTier.Fast"/> - the local, free, private tier. The thinking tier spends money and sends an
/// unpublished manuscript to a third party, so "we could not tell" must never mean "go to the cloud". Note
/// that an override row holding an UNRECOGNISED token stops at Fast rather than falling through to the book
/// default: falling through could climb to Thinking, which is the one direction doubt may not take.
///
/// OBSERVABILITY. Every doubt-driven fall to Fast logs, and every such log names the TASK, so "why did this
/// run fast?" is answerable from the log alone rather than by re-reading two tables. Nothing logged here
/// contains manuscript text - only ids, task names and tier tokens.
/// </summary>
public static class BookAiTierResolver
{
    /// <summary>Both stored rungs, as read in one pass. See <see cref="StoredTiersQuery"/>.</summary>
    /// <param name="BookDefault">The <c>Book.AiTier</c> column, null when the book never opted in.</param>
    /// <param name="Override">
    /// The task's stored token, or NULL when no override row exists. Distinct from an empty string, which
    /// means a row exists and stores nothing - the second is doubt and is logged, the first is the normal
    /// inherit-the-default case and is not.
    /// </param>
    internal sealed record StoredTiers(string? BookDefault, string? Override);

    /// <summary>
    /// ONE query for both rungs: the book row, plus a correlated lookup of the override on its composite
    /// primary key (BookId, TaskKey). Per-task storage therefore costs no extra round trip, which is what
    /// keeps the "tier resolved once per run" property of the chunked paths meaningful.
    ///
    /// Exposed (internal) as a queryable rather than inlined so a test can assert its SQL translation without
    /// re-writing the expression - the suite runs on the in-memory provider, which cannot tell a query that
    /// SQL Server would refuse to translate from one it would.
    /// </summary>
    internal static IQueryable<StoredTiers> StoredTiersQuery(AppDbContext db, Guid bookId, string taskKey) =>
        db.Books
            .AsNoTracking()
            .Where(b => b.Id == bookId)
            .Select(b => new StoredTiers(
                b.AiTier,
                db.Set<BookAiTaskTier>()
                    .Where(t => t.BookId == b.Id && t.TaskKey == taskKey)
                    .Select(t => t.Tier ?? "")
                    .FirstOrDefault()));

    /// <summary>
    /// Resolves the tier for one (book, task): the per-task override if one is stored, else
    /// <c>Book.AiTier</c>, else <see cref="AiTier.Fast"/>. Both rungs are read in ONE query and parsed
    /// through <see cref="AiTierPolicy.Parse"/>. Never throws except for cooperative cancellation, which is
    /// preserved so a cancelled analysis still stops immediately.
    /// </summary>
    public static async Task<AiTier> ResolveAsync(
        AppDbContext db, Guid? bookId, AiTaskType task, ILogger logger, CancellationToken ct)
    {
        if (bookId is null || bookId == Guid.Empty)
        {
            // NOT a warning: an analysis with no book (a bare scene/chapter run) legitimately reaches here,
            // and warning on it would train the reader to ignore the branch that IS a bug. Debug keeps the
            // answer available when someone is actually asking why a run went local.
            logger.LogDebug(
                "No book id supplied while resolving the {Task} model tier; using the local (fast) tier.",
                task);
            return AiTier.Fast;
        }

        var taskKey = AiTierPolicy.TaskKeyFor(task);

        try
        {
            var stored = await StoredTiersQuery(db, bookId.Value, taskKey).FirstOrDefaultAsync(ct);

            if (stored is null)
            {
                logger.LogWarning(
                    "Book {BookId} was not found while resolving the {Task} model tier; using the local (fast) tier.",
                    bookId, task);
                return AiTier.Fast;
            }

            if (stored.Override is not null)
            {
                if (!AiTierPolicy.IsRecognisedStoredValue(stored.Override))
                {
                    // Deliberately does NOT fall through to the book default: the default can be "thinking",
                    // and doubt must never climb.
                    logger.LogWarning(
                        "Book {BookId} stores an unrecognised {Task} tier override ({StoredTier}); using the " +
                        "local (fast) tier rather than the book default.",
                        bookId, task, stored.Override);
                    return AiTier.Fast;
                }

                return AiTierPolicy.Parse(stored.Override);
            }

            if (!string.IsNullOrWhiteSpace(stored.BookDefault)
                && !AiTierPolicy.IsRecognisedStoredValue(stored.BookDefault))
            {
                // A null/blank default is the SHIPPED state of every book and means fast, so it must stay
                // SILENT - warning on it would bury the branch below in noise. A non-blank value that parses
                // to nothing is a hand-edited or newer-build row, and worth saying out loud.
                // (An override ROW that stores blank is different and does warn: something created that row
                // on purpose and then wrote nothing into it.)
                logger.LogWarning(
                    "Book {BookId} stores an unrecognised default model tier ({StoredTier}); using the local " +
                    "(fast) tier for {Task}.",
                    bookId, stored.BookDefault, task);
                return AiTier.Fast;
            }

            return AiTierPolicy.Parse(stored.BookDefault);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Could not read the {Task} model tier for book {BookId}; falling back to the local (fast) tier.",
                task, bookId);
            return AiTier.Fast;
        }
    }
}
