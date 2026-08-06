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

    /// <summary>
    /// Cached per-chapter style metrics baseline for the chapter under analysis.
    /// Populated for LinguisticAnalysis when a chapter is in scope; null otherwise or
    /// when chapter text is unavailable.
    /// </summary>
    public ChapterStyleProfile? ChapterStyleBaseline { get; init; }

    /// <summary>
    /// Book-wide style averages sourced from BookBible.StyleProfileJson (same shape as
    /// <see cref="StyleProfile"/>). Provided for LinguisticAnalysis so the prompt can
    /// compare the chapter baseline against the whole book. Null when no profile exists.
    /// </summary>
    public StyleProfileData? BookStyleAverages { get; init; }

    public AnalysisScope Scope { get; init; }
    public AnalysisType AnalysisType { get; init; }
    public Guid? BookId { get; init; }
    public Guid? ChapterId { get; init; }
    public Guid? SceneId { get; init; }
}

// ─── Character Register ─────────────────────────────────────────────

/// <summary>
/// Known characters in the book, assembled from BookBible.CharacterRegisterJson.
///
/// PROVENANCE (character-register-editing plan, d1): the entries carry per-field
/// author-confirmation flags so a re-extraction can preserve what a human blessed. See
/// <see cref="CharacterRegisterEntry"/> and <c>CharacterRegisterMerge</c> (the single place the
/// merge rule lives).
/// </summary>
public record CharacterRegister
{
    private readonly IReadOnlyList<CharacterRegisterEntry> _characters = Array.Empty<CharacterRegisterEntry>();

    /// <summary>
    /// NULL-GUARD (see Services/Analysis/RepairableFields.cs for the canonical statement of this
    /// trap): a plain <c>= Array.Empty&lt;...&gt;()</c> initializer does NOT survive an EXPLICIT
    /// <c>"characters": null</c> in the persisted JSON — System.Text.Json writes the null straight
    /// through and the collection becomes null despite the initializer. The init accessor coerces it
    /// back, so no consumer (PromptFactory.FormatCharacters, the merge, the endpoints) can ever see
    /// a null here.
    /// </summary>
    public IReadOnlyList<CharacterRegisterEntry> Characters
    {
        get => _characters;
        init => _characters = value ?? Array.Empty<CharacterRegisterEntry>();
    }

    /// <summary>
    /// Last time this register's content changed, by EITHER a re-extraction or an author edit
    /// (d1 §4 invalidation stamp). Deliberately NOT <c>BookBible.UpdatedAt</c>, which is one column
    /// shared by every sibling JSON blob on the bible — a write to StyleProfileJson would bump it
    /// and falsely read as "the register changed" (the dual-surface trap already paid for once on
    /// ChunkSummary).
    ///
    /// Null on every register persisted before provenance shipped, and on a register nothing has
    /// touched since. A null MUST be read as "no staleness signal", never as "everything referencing
    /// this register is stale" — otherwise every pre-existing AnalysisResult on every book lights up
    /// as stale purely because the feature shipped.
    /// </summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>
    /// THE SCAN LEDGER (automatic-coverage plan, d1 §1): which chapters have already contributed to
    /// this register, and against which version of their text. It is what replaced the one-shot gate
    /// that used to freeze the register after a single extraction.
    ///
    /// <para>A chapter is COVERED-AND-FRESH iff an entry for it exists here AND its
    /// <see cref="ScannedChapterEntry.SourceStamp"/> still equals the chapter's current
    /// <c>Chapter.UpdatedAt</c>. Any other state (no entry, or an older stamp) is a miss and the next
    /// analysis of that chapter re-contributes it.</para>
    ///
    /// <para>It lives INSIDE this record, in the one JSON column, deliberately: the register has one
    /// storage shape, one serializer and two writers, and a ledger table would be a third writer
    /// surface for data that is small in every real book (one entry per chapter, never one per scan -
    /// a re-scan REPLACES its chapter's entry).</para>
    ///
    /// <para>It carries NO staleness meaning: grafting an entry on must not move
    /// <see cref="UpdatedAt"/>, or improving coverage would mark every prior AnalysisResult on the
    /// book stale without any character FACT having changed. Coverage-freshness and content-freshness
    /// are two questions on two signals.</para>
    ///
    /// <para>NULL-GUARD: same trap as <see cref="Characters"/> - an explicit
    /// <c>"scannedChapters": null</c> in the persisted JSON overwrites a plain initializer, so the
    /// init accessor coerces it back to empty.</para>
    /// </summary>
    public IReadOnlyList<ScannedChapterEntry> ScannedChapters
    {
        get => _scannedChapters;
        init => _scannedChapters = value ?? Array.Empty<ScannedChapterEntry>();
    }

    private readonly IReadOnlyList<ScannedChapterEntry> _scannedChapters = Array.Empty<ScannedChapterEntry>();
}

/// <summary>
/// One chapter's line in <see cref="CharacterRegister.ScannedChapters"/> (d1 §1).
///
/// <para><see cref="SourceStamp"/> is the chapter's <c>Chapter.UpdatedAt</c> AT SCAN TIME and is the
/// re-scan key: when the author edits the chapter its UpdatedAt moves, the stamps stop matching, and
/// the chapter re-contributes on its next analysis. <see cref="ScannedAt"/> is the wall clock of the
/// scan and is reporting only (be-c03) - never compared against anything to decide freshness.</para>
///
/// <para>An entry whose <see cref="ChapterId"/> no longer resolves (the chapter was deleted) is
/// harmless: it names nothing, matches nothing by name, and costs bytes rather than correctness.</para>
/// </summary>
public record ScannedChapterEntry
{
    public required Guid ChapterId { get; init; }
    public DateTimeOffset ScannedAt { get; init; }

    /// <summary>The chapter's <c>Chapter.UpdatedAt</c> when it was scanned. The re-scan key.</summary>
    public DateTimeOffset SourceStamp { get; init; }
}

/// <summary>
/// One character. Provenance is PER-FIELD and scoped to exactly the three fields an author can
/// edit (d1 §1): Gender, Aliases, IsCharacter. Role/Description carry no flag — they are
/// always-extracted, always-replaceable, because no UI exposes an edit path for them.
///
/// BACKWARD COMPATIBILITY INVARIANT: every provenance field defaults so that a register persisted
/// BEFORE this shipped (none of these properties present in its JSON) deserializes as
/// EXTRACTED-and-a-character — never as author-confirmed. Defaulting the other way would freeze
/// every currently-guessed gender as if a human had blessed it. Pinned by
/// <c>CharacterRegisterProvenanceTests</c> from a literal pre-change JSON string.
/// </summary>
public record CharacterRegisterEntry
{
    private readonly IReadOnlyList<string> _aliases = Array.Empty<string>();

    public required string Name { get; init; }
    public string? Gender { get; init; }

    /// <summary>"protagonist", "antagonist", "supporting", "minor" — extracted-only, no provenance flag.</summary>
    public string? Role { get; init; }

    /// <summary>Extracted-only, no provenance flag: same reason as <see cref="Role"/>.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// NULL-GUARD, same trap as <see cref="CharacterRegister.Characters"/>: an explicit
    /// <c>"aliases": null</c> overwrites the initializer with null, and
    /// <c>PromptFactory.FormatCharacters</c> reads <c>c.Aliases.Count</c> unguarded. The init
    /// accessor coerces null back to empty so that read can never NRE.
    /// </summary>
    public IReadOnlyList<string> Aliases
    {
        get => _aliases;
        init => _aliases = value ?? Array.Empty<string>();
    }

    /// <summary>
    /// False once the author marks this entry as not-a-character (suppressed). Default TRUE so a
    /// legacy row with this field absent means "yes, a character" — matching what the extractor
    /// already implied simply by including the entry.
    /// </summary>
    public bool IsCharacter { get; init; } = true;

    /// <summary>
    /// True when the WHOLE entry was hand-added by the author rather than produced by extraction.
    /// An author-added entry is exempt from the extracted-only replace step on EVERY field, not just
    /// the three provenance-flagged ones (d1 §3, row 3).
    /// </summary>
    public bool IsAuthorAdded { get; init; }

    // ── Per-field provenance (d1 §1). Each MUST default to false so a legacy row with the field
    // absent deserializes as EXTRACTED, never as author-confirmed. ──────────────────────────────

    /// <summary>The author explicitly set <see cref="Gender"/>; a re-extraction must not overwrite it.</summary>
    public bool GenderConfirmed { get; init; }

    /// <summary>The author explicitly set <see cref="Aliases"/>; a re-extraction must not overwrite them.</summary>
    public bool AliasesConfirmed { get; init; }

    /// <summary>
    /// The author explicitly decided <see cref="IsCharacter"/>. Combined with
    /// <c>IsCharacter == false</c> this is the PERMANENT SUPPRESSION marker: a re-extraction that
    /// proposes a matching entry must drop it rather than resurrect the character.
    /// </summary>
    public bool IsCharacterConfirmed { get; init; }
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
