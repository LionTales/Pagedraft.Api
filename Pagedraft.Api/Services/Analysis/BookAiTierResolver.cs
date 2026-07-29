using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pagedraft.Api.Data;
using Pagedraft.Api.Services.Ai;
using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// THE ONE book id -&gt; <see cref="AiTier"/> lookup (model-tier-fast-thinking plan, p3-2 introduced it inside
/// <c>UnifiedAnalysisService</c>; p3-3 lifted it here because a SECOND and THIRD caller appeared).
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
/// FAIL-SAFE IN ONE DIRECTION ONLY. A null/empty book id, a book that no longer exists, a null column, an
/// unrecognised token, and a database read that THROWS all resolve to <see cref="AiTier.Fast"/> - the local,
/// free, private tier. The thinking tier spends money and sends an unpublished manuscript to a third party,
/// so "we could not tell" must never mean "go to the cloud".
/// </summary>
public static class BookAiTierResolver
{
    /// <summary>
    /// Reads <c>Book.AiTier</c> and parses it through <see cref="AiTierPolicy.Parse"/>. Never throws except
    /// for cooperative cancellation, which is preserved so a cancelled analysis still stops immediately.
    /// </summary>
    public static async Task<AiTier> ResolveAsync(
        AppDbContext db, Guid? bookId, ILogger logger, CancellationToken ct)
    {
        if (bookId is null || bookId == Guid.Empty)
            return AiTier.Fast;

        try
        {
            var stored = await db.Books
                .AsNoTracking()
                .Where(b => b.Id == bookId.Value)
                .Select(b => b.AiTier)
                .FirstOrDefaultAsync(ct);
            return AiTierPolicy.Parse(stored);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Could not read the model tier for book {BookId}; falling back to the local (fast) tier.",
                bookId);
            return AiTier.Fast;
        }
    }
}
