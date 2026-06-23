namespace Pagedraft.Api.Models;

/// <summary>
/// Cached book-wide summary rollup (the L2 <see cref="BookBrief"/>) for a (BookId, Language) pair.
/// This is the persisted counterpart of the on-demand composition performed by
/// <see cref="Pagedraft.Api.Services.Analysis.BookSummaryService"/>: the service (re)builds every
/// chapter's L0 structured brief (via <see cref="Pagedraft.Api.Services.Analysis.ChapterBriefService"/>),
/// composes a per-chapter L1 <see cref="ChapterBrief"/>, rolls those up into a single L2
/// <see cref="BookBrief"/>, then caches the L2 here so callers do not pay the aggregation cost again.
///
/// CACHE HOME RATIONALE: this deliberately MIRRORS <see cref="BookStyleBaseline"/> rather than reusing
/// <see cref="BookProfile"/>/<see cref="BookBible"/> as the cache row. Those existing rows ARE read as
/// genre/themes/synopsis source data (no parallel state is invented), but they are owned by
/// BookIntelligenceService on a different freshness model and carry no Summarization-task
/// <c>BuiltWithModel</c> stamp nor a coverage signal. A dedicated (BookId, Language) row that carries the
/// model id + contributing-chapter count lets the status math, idempotency, and cross-model invalidation
/// reuse the exact same pattern the style baseline established (see docs/LINGUISTIC_SCALE_AND_REUSE.md).
///
/// The L1 per-chapter briefs are NOT stored here: they project deterministically from the L0
/// <see cref="ChunkSummary.StructuredJson"/> (which IS persisted + freshness-stamped), so L1 is recomposed
/// cheaply on demand and only the rolled-up L2 needs its own cache.
/// </summary>
public class BookSummaryBaseline
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid BookId { get; set; }

    /// <summary>Analysis language the summary was built for ("he" / "en"). Part of the cache key.</summary>
    public string Language { get; set; } = "he";

    /// <summary>
    /// Serialised L2 <see cref="BookBrief"/> JSON (genre/sub-genre/audience/literature-level/themes/
    /// synopsis rollup). camelCase, same policy the rest of the structured briefs use.
    /// </summary>
    public string BookBriefJson { get; set; } = string.Empty;

    /// <summary>Number of chapters that contributed a usable L1 brief to the rollup.</summary>
    public int BuiltChapterCount { get; set; }

    /// <summary>
    /// The Summarization model id that built the contributing L0 chapter briefs (read from AiOptions,
    /// same resolution the per-chapter builder stamps). Stored so a model change invalidates the cached
    /// rollup (cross-model cache safety); null when the model could not be resolved.
    /// </summary>
    public string? BuiltWithModel { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Book Book { get; set; } = null!;
}
