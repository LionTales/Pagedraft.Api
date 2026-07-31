using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// Builds a rich <see cref="Models.AnalysisContext"/> for a given target, replacing the
/// simple text-resolution previously done by UnifiedAnalysisService.ResolveTarget().
/// Loads target text plus optional context fields (BookBible, ChunkSummary, StyleProfile)
/// from the database. All optional fields gracefully degrade to null when data hasn't
/// been generated yet, so analyses work at every stage of the pipeline build-out.
/// </summary>
public interface IAnalysisContextService
{
    /// <summary>
    /// Resolve the target text and assemble all available context for a given analysis.
    /// </summary>
    /// <param name="scope">Book, Chapter, or Scene.</param>
    /// <param name="targetId">The ID of the target entity (BookId, ChapterId, or SceneId).</param>
    /// <param name="analysisType">Which analysis is being run — determines which optional context fields to load.</param>
    /// <param name="language">
    /// The analysis language the caller will run the user-facing pass with (request override or a
    /// normalized code such as "en"/"he"). Used for the chapter style baseline so its cache key,
    /// build prompt, and [CHAPTER_STYLE_BASELINE] agree with the analysis language. Falls back to the
    /// book language when null/empty.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A fully assembled <see cref="Models.AnalysisContext"/> with TargetText always populated
    /// and optional fields (Characters, StyleProfile, ChapterBrief, BookBrief, PrecedingContext,
    /// FollowingContext) populated when the underlying data exists.
    /// </returns>
    Task<Models.AnalysisContext> BuildContextAsync(
        AnalysisScope scope,
        Guid targetId,
        AnalysisType analysisType,
        string language,
        CancellationToken ct = default);

    /// <summary>
    /// Loads the cached per-chapter style-metrics baseline for (chapterId, language), or builds and
    /// persists one when absent (cache-read-or-build, mirroring the ChunkSummary pattern). The metrics
    /// reuse the same LLM-backed linguistic computation that produces LinguisticAnalysisResult and are
    /// stored as JSON on <see cref="Models.ChapterStyleProfile.MetricsJson"/>.
    /// </summary>
    /// <param name="tier">
    /// be-c02: the book's ALREADY-RESOLVED model tier, when the caller holds one. Pass it from any caller
    /// that spans more than one profile build (the style-baseline build is the only one today) so every
    /// chapter is routed to, and stamped with, the SAME model: left to resolve for itself this method
    /// re-reads <c>Book.AiTier</c> per call, and a user flipping the tier mid-build then stamps half the
    /// chapters under one model and half under the other. <c>null</c> (the default) means "resolve from the
    /// database", which is the pre-be-c02 behaviour and the right choice for a single-shot caller.
    /// It sits after <paramref name="ct"/> deliberately, so every existing positional caller is unchanged.
    /// </param>
    /// <returns>The cached or newly built profile, or null when chapter text is unavailable or the build fails.</returns>
    Task<Models.ChapterStyleProfile?> LoadOrBuildChapterStyleProfileAsync(
        Guid bookId,
        Guid chapterId,
        string language,
        CancellationToken ct = default,
        AiTier? tier = null);

    /// <summary>
    /// Builds a synthetic book-wide style baseline: the per-metric mean of the numeric syntax/morphology
    /// fields across every chapter of (bookId, language) that ALREADY has a persisted
    /// <see cref="Models.ChapterStyleProfile"/>. Never triggers an LLM build for an unprofiled chapter
    /// (existing rows benefit from staleness self-refresh only). Used as the [CHAPTER_STYLE_BASELINE]
    /// reference at Chapter scope so a chapter is compared against the book average rather than itself.
    /// </summary>
    /// <param name="tier">
    /// be-c02: the book's ALREADY-RESOLVED model tier, when the caller holds one. This aggregator never
    /// rebuilds - it EXCLUDES rows built under a different model - so a caller that has just built profiles
    /// must average them at the tier they were built under. Resolving it again here after a mid-build tier
    /// flip drops every row the build just wrote and returns null, silently, with no error and no log.
    /// <c>null</c> (the default) means "resolve from the database", the pre-be-c02 behaviour.
    /// It sits after <paramref name="ct"/> deliberately, so every existing positional caller is unchanged.
    /// </param>
    /// <returns>
    /// A synthetic (unpersisted) profile whose MetricsJson holds the averaged metrics, or null when fewer
    /// than two chapters have a usable profile (a single-chapter "average" is not a meaningful reference).
    /// </returns>
    Task<Models.ChapterStyleProfile?> BuildBookStyleAverageProfileAsync(
        Guid bookId,
        string language,
        CancellationToken ct = default,
        AiTier? tier = null);
}
