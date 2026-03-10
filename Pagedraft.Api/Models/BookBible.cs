namespace Pagedraft.Api.Models;

/// <summary>
/// Extended book-level intelligence that complements BookProfile.
/// Stores rich structured JSON for style, characters, themes, timeline, and world-building.
/// One-to-one with Book (unique index on BookId).
/// </summary>
public class BookBible
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BookId { get; set; }

    /// <summary>JSON matching <see cref="StyleProfileData"/> schema.</summary>
    public string? StyleProfileJson { get; set; }

    /// <summary>JSON matching <see cref="CharacterRegister"/> schema.</summary>
    public string? CharacterRegisterJson { get; set; }

    /// <summary>JSON array of theme strings with optional descriptions.</summary>
    public string? ThemesJson { get; set; }

    /// <summary>JSON array of timeline entries (event, chapter, approximate position).</summary>
    public string? TimelineJson { get; set; }

    /// <summary>JSON object with world-building details (setting, rules, factions, etc.).</summary>
    public string? WorldBuildingJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // ── Navigation ──
    public Book Book { get; set; } = null!;
}
