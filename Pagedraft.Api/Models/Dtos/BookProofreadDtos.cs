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
///
/// <para>THE TWO PLANS. <see cref="PlannedChapters"/> / <see cref="EstimatedChunks"/> /
/// <see cref="EstimatedSeconds"/> are exactly what <c>POST .../proofread?force=false</c> would plan and
/// estimate right now; the <c>Force*</c> trio is the same for <c>force=true</c>. Both come from the ONE planning
/// method the start uses (<c>BookProofreadService.Plan</c>), so the consent the author reads before starting
/// cannot disagree with the run it starts. An estimate of 0 means nothing would run.</para>
///
/// <para><see cref="UnreliableChapters"/>: chapters, in reading order, where ANY Active Proofread result (Chapter
/// or Scene scope) in the status language is flagged <c>ProofreadResultUnreliable</c>. The client asks the author
/// whether the annotated export should include or skip them (<c>?unreliable=include|skip</c>).</para>
/// </summary>
public record BookProofreadStatusDto(
    Guid? ActiveJobId,
    int ChaptersWithText,
    int ChaptersWithActiveProofread,
    int ChaptersStale,
    DateTimeOffset? LastRunAt,
    int PlannedChapters,
    int EstimatedChunks,
    int EstimatedSeconds,
    int ForcePlannedChapters,
    int ForceEstimatedChunks,
    int ForceEstimatedSeconds,
    IReadOnlyList<BookProofreadUnreliableChapterDto> UnreliableChapters);

/// <summary>
/// A chapter whose Active Proofread result was flagged unreliable. <paramref name="Order"/> is the stored
/// 0-based <c>Chapter.Order</c>; the client owns the 1-based display number.
/// </summary>
public record BookProofreadUnreliableChapterDto(Guid ChapterId, string Title, int Order);
