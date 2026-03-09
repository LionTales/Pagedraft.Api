namespace Pagedraft.Api.Models;

/// <summary>Cached per-chapter summary used to build book-level intelligence.</summary>
public class ChunkSummary
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BookId { get; set; }
    public Guid ChapterId { get; set; }

    /// <summary>Flat natural-language summary used by existing features.</summary>
    public string SummaryText { get; set; } = string.Empty;

    /// <summary>
    /// Structured JSON representation of the chapter, matching
    /// <see cref="StructuredChunkSummaryData"/> schema. Optional in Plan 0 and populated
    /// by later analysis passes.
    /// </summary>
    public string? StructuredJson { get; set; }

    public string Language { get; set; } = "he";
    public DateTimeOffset CreatedAt { get; set; }

    public Book Book { get; set; } = null!;
    public Chapter Chapter { get; set; } = null!;
}
