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
    /// <returns>The cached or newly built profile, or null when chapter text is unavailable or the build fails.</returns>
    Task<Models.ChapterStyleProfile?> LoadOrBuildChapterStyleProfileAsync(
        Guid bookId,
        Guid chapterId,
        string language,
        CancellationToken ct = default);
}
