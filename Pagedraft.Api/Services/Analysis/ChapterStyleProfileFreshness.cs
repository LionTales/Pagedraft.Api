namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// SINGLE source of truth for "is this cached ChapterStyleProfile fresh, or must it be (re)built?".
/// A profile is fresh only when BOTH hold:
///   • it was built at/after the chapter's last edit (timestamp freshness), AND
///   • it was built under the active LinguisticAnalysis model (cross-model cache safety).
/// Anything else - missing, timestamp-stale, OR built under a different model (including a legacy row
/// whose BuiltWithModel is null) - is STALE and must be rebuilt.
///
/// Defined once and called from both:
///   • <see cref="AnalysisContextService.LoadOrBuildChapterStyleProfileAsync"/> (the (re)build gate), and
///   • <see cref="StyleBaselineService.GetStatusAsync"/> (the stale-count predicate),
/// so the two cannot drift into different staleness definitions. Operates on primitive inputs so the
/// status path can call it from a projected (ChapterId, UpdatedAt, BuiltWithModel) row without loading
/// the full entity.
/// </summary>
public static class ChapterStyleProfileFreshness
{
    /// <summary>
    /// True when a profile built at <paramref name="profileUpdatedAt"/> under
    /// <paramref name="profileBuiltWithModel"/> is fresh relative to a chapter last edited at
    /// <paramref name="chapterUpdatedAt"/> and the current <paramref name="activeModel"/>.
    /// </summary>
    public static bool IsFresh(
        DateTimeOffset profileUpdatedAt,
        string? profileBuiltWithModel,
        DateTimeOffset chapterUpdatedAt,
        string? activeModel)
    {
        if (profileUpdatedAt < chapterUpdatedAt)
            return false; // timestamp stale: chapter changed after the profile was built
        // Model match uses ordinal equality. A legacy null (or any mismatch) is stale → rebuild once.
        return string.Equals(profileBuiltWithModel, activeModel, StringComparison.Ordinal);
    }
}
