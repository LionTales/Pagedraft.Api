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
///
/// <para><see cref="FailedChapters"/> (c20): chapters, in reading order, whose LATEST whole-book attempt failed or
/// produced an unreliable result and that no reliable proofread has covered since. A failed chapter keeps its
/// previous Active result, which counts as fresh, so the normal plan skips it; <c>POST .../proofread?onlyFailed=true</c>
/// plans exactly these (force semantics for them, every other chapter untouched), and
/// <see cref="FailedEstimatedChunks"/> / <see cref="FailedEstimatedSeconds"/> are that plan's estimate, from the
/// same <c>Plan</c>. The record behind it is IN MEMORY: it is empty after an API restart.</para>
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
    IReadOnlyList<BookProofreadUnreliableChapterDto> UnreliableChapters,
    IReadOnlyList<BookProofreadFailedChapterDto> FailedChapters,
    int FailedEstimatedChunks,
    int FailedEstimatedSeconds);

/// <summary>
/// A chapter whose Active Proofread result was flagged unreliable. <paramref name="Order"/> is the stored
/// 0-based <c>Chapter.Order</c>; the client owns the 1-based display number.
/// </summary>
public record BookProofreadUnreliableChapterDto(Guid ChapterId, string Title, int Order);

/// <summary>
/// A chapter whose latest whole-book proofread attempt failed or came back unreliable (c20). <paramref name="Order"/>
/// is the stored 0-based <c>Chapter.Order</c>; the client owns the 1-based display number.
/// </summary>
public record BookProofreadFailedChapterDto(Guid ChapterId, string Title, int Order);
