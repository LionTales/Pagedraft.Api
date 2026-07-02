namespace Pagedraft.Api.Models;

/// <summary>
/// Persisted HONEST coverage of the whole-book review for a (BookId, Language) pair: the two integers
/// (<see cref="ChaptersReviewed"/> / <see cref="ChaptersTotal"/>) the build computed (wb4-c06 / be-c01),
/// stored so the STATUS probe stays honest across a page reload.
///
/// WHY A DEDICATED ROW (data-c01): before this, <c>BookReviewService.GetStatusAsync</c> DERIVED coverage
/// from the live chapter count (<c>chaptersReviewed = hasReview ? chaptersTotal : 0</c>), so after a reload
/// the row ALWAYS claimed full N/N — even after a partial/degraded build where the build response honestly
/// reported reviewed &lt; total. There is no other per-book review metadata entity (status is otherwise
/// derived from <see cref="BookFinding"/> rows), so the two integers need their own home. This mirrors the
/// <see cref="BookSummaryBaseline"/> / <see cref="BookStyleBaseline"/> (BookId, Language) cache pattern: one
/// upsert-keyed row per language, refreshed inside the SAME persist step as the findings and ONLY on a build
/// that actually persisted (a total-failure / no-op / briefs-missing build leaves the prior row untouched, so
/// a bad build never degrades the stored coverage — the cache-preservation contract).
///
/// The build-time-only shape (WindowCount / RanSynthesis / RanContinuityReduce / FailedWindows) is NOT
/// persisted here — those precise per-build counts ride on <c>BookReviewBuildResult</c> only; the cached
/// status probe reports the two persisted coverage integers and leaves the shape fields at their 0/false
/// defaults.
/// </summary>
public class BookReviewCoverage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid BookId { get; set; }

    /// <summary>Analysis language the review was built for ("he" / "en"). Part of the cache key.</summary>
    public string Language { get; set; } = "he";

    /// <summary>
    /// Number of chapters actually REVIEWED in the last persisting build (the honest numerator: on the
    /// windowed path, the distinct primary chapter orders of the windows whose model call SUCCEEDED; on the
    /// legacy per-dimension path, every chapter — the whole book is reviewed in one concatenated context).
    /// Always &lt;= <see cref="ChaptersTotal"/> (the reviewed set is a subset of the reviewable set).
    /// </summary>
    public int ChaptersReviewed { get; set; }

    /// <summary>
    /// Number of chapters the last persisting build was RESPONSIBLE for (the honest denominator: the full
    /// reviewable / non-empty chapter set). A partial or degraded build leaves ChaptersReviewed below this;
    /// a fully-successful build gives ChaptersReviewed == ChaptersTotal.
    /// </summary>
    public int ChaptersTotal { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Book Book { get; set; } = null!;
}
