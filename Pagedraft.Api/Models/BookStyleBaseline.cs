namespace Pagedraft.Api.Models;

/// <summary>
/// Cached book-wide style baseline: the per-metric average of every chapter's
/// <see cref="ChapterStyleProfile"/> for a (BookId, Language) pair. This is the persisted
/// counterpart of the SYNTHETIC average produced by
/// <see cref="Pagedraft.Api.Services.Analysis.AnalysisContextService.BuildBookStyleAverageProfileAsync"/>:
/// the synthetic version is recomputed on demand (read-only, never persisted), while THIS row is the
/// result of the heavy "fill coverage" build that (re)builds every chapter profile first, then caches
/// the average so callers do not pay the aggregation cost again.
///
/// REUSE INTENT: this is the first of a family of book-level cached builds. Future book-level builders
/// (BookBible, Literary baseline) should follow the same shape - keyed by (BookId, Language), carrying
/// the model id that built it, and a coverage/freshness signal - so they can share the async-job +
/// progress-polling contract this baseline establishes (see docs/LINGUISTIC_SCALE_AND_REUSE.md).
/// </summary>
public class BookStyleBaseline
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid BookId { get; set; }

    /// <summary>Analysis language the baseline was built for ("he" / "en"). Part of the cache key.</summary>
    public string Language { get; set; } = "he";

    /// <summary>
    /// Serialised <see cref="Pagedraft.Api.Services.Analysis.LinguisticAnalysisResult"/> JSON holding the
    /// per-metric MEAN across the book's chapter profiles (same shape as ChapterStyleProfile.MetricsJson).
    /// </summary>
    public string MetricsJson { get; set; } = string.Empty;

    /// <summary>Number of chapter profiles that contributed to the cached average.</summary>
    public int BuiltChapterCount { get; set; }

    /// <summary>
    /// The LinguisticAnalysis model id that built the contributing chapter profiles (read from
    /// AiOptions). Stored so a later pass can verify there was no cross-model mixing of the cached
    /// average; null when the model could not be resolved.
    /// </summary>
    public string? BuiltWithModel { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Book Book { get; set; } = null!;
}
