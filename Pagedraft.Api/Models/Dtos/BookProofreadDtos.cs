namespace Pagedraft.Api.Models.Dtos;

/// <summary>
/// Response of <c>POST api/books/{bookId}/proofread</c>. Started=false with SkippedFresh &gt; 0 means every
/// chapter already had a fresh Proofread result; Started=false with an existing JobId means a run is already
/// active (the caller reattaches to it). JSON is camelCase.
/// </summary>
public record StartBookProofreadResponse(
    Guid JobId,
    bool Started,
    int SkippedFresh,
    int PlannedChapters,
    int EstimatedChunks,
    int EstimatedSeconds);

/// <summary>
/// Response of <c>GET api/books/{bookId}/proofread/status</c>. ActiveJobId is the running whole-book job (or null).
/// </summary>
public record BookProofreadStatusDto(
    Guid? ActiveJobId,
    int ChaptersWithText,
    int ChaptersWithActiveProofread,
    int ChaptersStale,
    DateTimeOffset? LastRunAt);
