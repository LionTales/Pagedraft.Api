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
    /// by later analysis passes (wb1-c01 fills it via ChapterBriefService).
    /// </summary>
    public string? StructuredJson { get; set; }

    /// <summary>
    /// The resolved Summarization model id that built <see cref="StructuredJson"/> (the model the
    /// structured-brief request was actually routed to). Null on legacy rows created before this column
    /// existed, or rows that only ever carried the flat <see cref="SummaryText"/>. When it does not match
    /// the active Summarization model the structured brief is treated as STALE and rebuilt, so a structured
    /// brief is never served from a different model than the one now configured (cross-model cache safety).
    /// Mirrors <see cref="ChapterStyleProfile.BuiltWithModel"/>.
    /// </summary>
    public string? BuiltWithModel { get; set; }

    public string Language { get; set; } = "he";

    /// <summary>
    /// When <see cref="CreatedAt"/> was first stamped. This row is shared by two writers — the flat
    /// <see cref="SummaryText"/> path (BookIntelligenceService.SummarizeChaptersAsync) and the structured
    /// <see cref="StructuredJson"/> path (ChapterBriefService). The flat path bumps <see cref="CreatedAt"/>
    /// to record its own re-summary freshness, which is fine for the flat surface but MUST NOT be read as
    /// the structured brief's build time (a flat re-summary would otherwise mask a stale structured brief).
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When the STRUCTURED brief in <see cref="StructuredJson"/> was last (re)built (wb1-r02). This is the
    /// build timestamp the structured freshness gate reads — NOT <see cref="CreatedAt"/>, which the flat
    /// re-summary path also bumps and would otherwise mask a stale structured brief. Stamped by
    /// ChapterBriefService whenever it writes <see cref="StructuredJson"/>. Null on legacy rows created
    /// before this column existed, or rows that only ever carried the flat <see cref="SummaryText"/>; a null
    /// is treated as STALE so the brief self-heals (rebuilds) on next access, matching the
    /// graceful-degradation posture of the rest of Phase 1.
    /// </summary>
    public DateTimeOffset? StructuredBuiltAt { get; set; }

    /// <summary>
    /// True once the user has manually edited the flat <see cref="SummaryText"/> (wb3-c04). The flat
    /// <see cref="SummaryText"/> is the user's OWN authoritative understanding of the chapter, distinct from
    /// the AI-generated <see cref="StructuredJson"/>. This flag is the clobber guard: the automatic flat
    /// re-summary path (BookIntelligenceService.SummarizeChaptersAsync) SKIPS a row where this is true so it
    /// never silently overwrites the user's edit. A subsequent automatic re-summary is gated behind an
    /// explicit user action (the re-derive endpoint) that consumes the edited summary rather than discarding
    /// it. Default false (legacy + AI-built rows are not user-edited).
    /// </summary>
    public bool SummaryUserEdited { get; set; }

    /// <summary>
    /// When the user last edited the flat <see cref="SummaryText"/> (wb3-c04). This is the freshness stamp
    /// for the USER-EDIT on the flat surface; it is owned by the PUT-summary path and is INDEPENDENT of both
    /// <see cref="CreatedAt"/> (the AI flat re-summary stamp) and <see cref="StructuredBuiltAt"/> (the
    /// structured-brief stamp), so a user edit never masks structured staleness and vice-versa (dual-surface
    /// trap). Null until the user edits the summary at least once.
    /// </summary>
    public DateTimeOffset? SummaryUserEditedAt { get; set; }

    public Book Book { get; set; } = null!;
    public Chapter Chapter { get; set; } = null!;
}
