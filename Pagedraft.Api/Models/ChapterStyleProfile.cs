namespace Pagedraft.Api.Models;

/// <summary>Cached per-chapter style metrics used for style-consistency analysis.</summary>
public class ChapterStyleProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BookId { get; set; }
    public Guid ChapterId { get; set; }
    public string Language { get; set; } = "he";

    /// <summary>Serialised <see cref="Pagedraft.Api.Services.Analysis.LinguisticAnalysisResult"/> JSON emitted by linguistic analysis.</summary>
    public string MetricsJson { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Book Book { get; set; } = null!;
    public Chapter Chapter { get; set; } = null!;
}
