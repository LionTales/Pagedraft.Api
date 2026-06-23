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
}
