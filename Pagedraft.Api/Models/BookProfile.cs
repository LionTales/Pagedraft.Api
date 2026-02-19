namespace Pagedraft.Api.Models;

/// <summary>Cached book-level intelligence derived from chapter summaries.</summary>
public class BookProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BookId { get; set; }
    public string? Genre { get; set; }
    public string? SubGenre { get; set; }
    public string? Synopsis { get; set; }
    public string? TargetAudience { get; set; }
    public int? LiteratureLevel { get; set; }
    public string? LanguageRegister { get; set; }

    /// <summary>JSON-serialized CharacterAnalysisResult.</summary>
    public string? CharactersJson { get; set; }

    /// <summary>JSON-serialized StoryAnalysisResult.</summary>
    public string? StoryStructureJson { get; set; }

    public string Language { get; set; } = "he";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Book Book { get; set; } = null!;
}
