namespace Pagedraft.Api.Models;

/// <summary>Cached per-chapter summary used to build book-level intelligence.</summary>
public class ChunkSummary
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BookId { get; set; }
    public Guid ChapterId { get; set; }
    public string SummaryText { get; set; } = string.Empty;
    public string Language { get; set; } = "he";
    public DateTimeOffset CreatedAt { get; set; }

    public Book Book { get; set; } = null!;
    public Chapter Chapter { get; set; } = null!;
}
