using System.Text.Json;
using Pagedraft.Api.Models;

namespace Pagedraft.Api.Services.Analysis;

/// <summary>
/// SINGLE source of truth for deserializing a persisted L0 structured-brief JSON
/// (<see cref="ChunkSummary.StructuredJson"/>) into <see cref="StructuredChunkSummaryData"/>, and for
/// deciding whether a chapter has a USABLE structured brief.
///
/// A brief is usable only when its JSON deserializes to a non-null value; a non-empty but UNPARSEABLE
/// StructuredJson is NOT usable. Every path that decides "does this chapter have a usable structured brief?"
/// — the freshness gate (<see cref="ChapterBriefService"/>), the coverage/status count
/// (<see cref="BookSummaryService.GetStatusAsync"/>), and the L1 composition
/// (<see cref="BookSummaryService.ComposeChapterBriefsAsync"/>) — resolves it through HERE so they cannot
/// disagree. They previously diverged: the freshness/status checks tested only for a NON-EMPTY string while
/// composition actually parsed, so an unparseable brief was counted "built" by status yet skipped by
/// composition. That left the chapter built-but-uncomposed, the freshness gate returned it as fresh (so it
/// was never rebuilt), and the book summary stayed permanently not-ready because
/// <see cref="BookSummaryStatus.SummaryCoversBuiltChapters"/> could never match.
/// </summary>
public static class StructuredChunkSummaryParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Parses the StructuredJson blob into the typed brief; null on null/blank/invalid JSON.</summary>
    public static StructuredChunkSummaryData? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<StructuredChunkSummaryData>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when <paramref name="json"/> deserializes to a usable structured brief (non-empty AND parseable).
    /// This is the SINGLE "has a usable structured brief" predicate the freshness gate and the status count
    /// must use so they agree with <see cref="Parse"/>-based composition.
    /// </summary>
    public static bool IsUsable(string? json) => Parse(json) != null;

    /// <summary>
    /// Phase 4c-16: True when <paramref name="brief"/> carries NO usable content — every list
    /// (PlotEvents/CharacterStates/ThematicMarkers/OpenThreads) is empty AND ToneNotes is blank. This is the
    /// DEGENERATE shape an empty payload like <c>"{}"</c> deserializes into: <see cref="Parse"/> returns a
    /// NON-null record (so <see cref="IsUsable"/> is true), yet it conveys nothing about the chapter — the
    /// documented num_ctx-truncation/empty-payload failure on a small GPU.
    ///
    /// This is a SEPARATE notion from <see cref="IsUsable"/>/<see cref="Parse"/> on purpose. Those decide
    /// "is the PERSISTED StructuredJson parseable?" for the freshness gate, the status count, and L1
    /// composition — semantics those paths depend on and which MUST NOT change (a degenerate-but-parseable
    /// persisted row still reads "built" there). This predicate is used only at BUILD time to reject a
    /// freshly produced empty brief BEFORE it is allowed to overwrite a previously-good one.
    /// </summary>
    public static bool IsDegenerate(StructuredChunkSummaryData brief) =>
        brief.PlotEvents.Count == 0
        && brief.CharacterStates.Count == 0
        && brief.ThematicMarkers.Count == 0
        && brief.OpenThreads.Count == 0
        && string.IsNullOrWhiteSpace(brief.ToneNotes);
}
