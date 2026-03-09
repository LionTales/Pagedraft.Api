using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Models;

/// <summary>
/// Rich context assembled by IAnalysisContextService and passed to PromptFactory
/// for context-aware prompt generation. All optional fields are null until the
/// corresponding data pipelines populate them (Plans 1-5).
/// This is an in-memory DTO — not an EF entity.
/// </summary>
public record AnalysisContext
{
    public required string TargetText { get; init; }
    public string? PrecedingContext { get; init; }
    public string? FollowingContext { get; init; }

    public CharacterRegister? Characters { get; init; }
    public StyleProfileData? StyleProfile { get; init; }
    public ChapterBrief? ChapterBrief { get; init; }
    public BookBrief? BookBrief { get; init; }

    public AnalysisScope Scope { get; init; }
    public AnalysisType AnalysisType { get; init; }
    public Guid? BookId { get; init; }
    public Guid? ChapterId { get; init; }
    public Guid? SceneId { get; init; }
}

// ─── Character Register ─────────────────────────────────────────────

/// <summary>Known characters in the book, assembled from BookBible.CharacterRegisterJson.</summary>
public record CharacterRegister
{
    public IReadOnlyList<CharacterRegisterEntry> Characters { get; init; } = Array.Empty<CharacterRegisterEntry>();
}

public record CharacterRegisterEntry
{
    public required string Name { get; init; }
    public string? Gender { get; init; }

    /// <summary>"protagonist", "antagonist", "supporting", "minor"</summary>
    public string? Role { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();
}

// ─── Style Profile ──────────────────────────────────────────────────

/// <summary>
/// Book-level writing style fingerprint, deserialized from BookBible.StyleProfileJson.
/// Captures the author's dominant patterns so analyses can respect them.
/// </summary>
public record StyleProfileData
{
    /// <summary>"lyrical", "dark", "humorous", "neutral", etc.</summary>
    public string? DominantTone { get; init; }

    /// <summary>"first-person", "third-limited", "third-omniscient", "second-person", "mixed"</summary>
    public string? Pov { get; init; }

    /// <summary>"past", "present", "mixed"</summary>
    public string? TensePattern { get; init; }

    /// <summary>"simple", "moderate", "literary", "academic"</summary>
    public string? VocabularyLevel { get; init; }

    /// <summary>"natural", "formal", "dialect", "minimal"</summary>
    public string? DialogueStyle { get; init; }

    public IReadOnlyList<string> RecurringMotifs { get; init; } = Array.Empty<string>();

    public double? AverageSentenceLength { get; init; }

    /// <summary>0.0 (very informal) to 1.0 (very formal).</summary>
    public double? FormalityScore { get; init; }
}

// ─── Chapter Brief ──────────────────────────────────────────────────

/// <summary>
/// Structured summary of a single chapter, deserialized from ChunkSummary.StructuredJson.
/// Provides narrative context for scene/chapter-level analyses.
/// </summary>
public record ChapterBrief
{
    public required string Title { get; init; }
    public int Order { get; init; }
    public string? Summary { get; init; }
    public IReadOnlyList<string> PlotEvents { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ChapterCharacterState> CharacterStates { get; init; } = Array.Empty<ChapterCharacterState>();
    public IReadOnlyList<string> ThematicMarkers { get; init; } = Array.Empty<string>();
    public string? ToneNotes { get; init; }
    public IReadOnlyList<string> OpenThreads { get; init; } = Array.Empty<string>();
}

public record ChapterCharacterState
{
    public required string Name { get; init; }
    public string? State { get; init; }
    public string? EmotionalArc { get; init; }
}

// ─── Book Brief ─────────────────────────────────────────────────────

/// <summary>
/// High-level book metadata assembled from BookBible + BookProfile.
/// Gives analyses global story awareness.
/// </summary>
public record BookBrief
{
    public string? Genre { get; init; }
    public string? SubGenre { get; init; }
    public string? TargetAudience { get; init; }

    /// <summary>1 (very simple) to 10 (high literature).</summary>
    public int? LiteratureLevel { get; init; }
    public IReadOnlyList<string> Themes { get; init; } = Array.Empty<string>();
    public string? Synopsis { get; init; }
}

// ─── Structured Chunk Summary ───────────────────────────────────────

/// <summary>
/// JSON schema for ChunkSummary.StructuredJson column. Provides machine-readable
/// chapter summary data that feeds into ChapterBrief assembly.
/// </summary>
public record StructuredChunkSummaryData
{
    public IReadOnlyList<string> PlotEvents { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ChapterCharacterState> CharacterStates { get; init; } = Array.Empty<ChapterCharacterState>();
    public IReadOnlyList<string> ThematicMarkers { get; init; } = Array.Empty<string>();
    public string? ToneNotes { get; init; }
    public IReadOnlyList<string> OpenThreads { get; init; } = Array.Empty<string>();
}
