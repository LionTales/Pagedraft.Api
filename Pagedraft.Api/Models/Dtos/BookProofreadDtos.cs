namespace Pagedraft.Api.Models.Dtos;

/// <summary>
/// Response of <c>POST api/books/{bookId}/proofread</c>. Started=false with SkippedFresh &gt; 0 means every
/// chapter already had a fresh Proofread result (JobId is then <c>Guid.Empty</c>). A run already active for the same
/// book and language is answered as Started=true with THAT run's JobId (the caller reattaches to it), whatever the
/// plan says, and so is the loser of a concurrent start. JSON is camelCase.
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
/// <para>COVERAGE AND STALENESS (be-c05). Every count below reads each exported UNIT's current Active Proofread
/// result in the status language through ONE rule, <c>ProofreadUnitResults</c>, which the annotated export also
/// calls: a unit is the chapter row or one scene, exactly as the export renders it, and a result keyed to no
/// unit (a frozen chapter row's result on a chapter that exports through its scenes) or in another language
/// counts for nothing. Per chapter, exactly one of: covered and fresh, <see cref="ChaptersEdited"/>,
/// <see cref="ChaptersUnreliable"/>, or not proofread (some unit has no result in this language).</para>
/// <list type="bullet">
/// <item><see cref="ChaptersWithActiveProofread"/>: chapters whose EVERY unit has a reliable result (fresh or
/// not). Before be-c05 it counted any Active result, unreliable or other-language included.</item>
/// <item><see cref="ChaptersEdited"/>: covered chapters where at least one unit was edited after its result
/// (a subset of <see cref="ChaptersWithActiveProofread"/>).</item>
/// <item><see cref="ChaptersUnreliable"/>: chapters where at least one unit's current result is flagged
/// <c>ProofreadResultUnreliable</c>; never covered. Equals <c>UnreliableChapters.Count</c>.</item>
/// <item><see cref="ChaptersStale"/>: KEPT for older clients, = <see cref="ChaptersEdited"/> +
/// <see cref="ChaptersUnreliable"/> (disjoint, so no chapter counts twice). It no longer counts a partly
/// proofread chapter or one whose only result is in another language: both read as not proofread.</item>
/// <item><see cref="LastRunAt"/>: the newest result of a covered chapter's units; null when nothing is covered.</item>
/// </list>
///
/// <para><see cref="UnreliableChapters"/>: the <see cref="ChaptersUnreliable"/> chapters, in reading order. The
/// client asks the author whether the annotated export should include or skip them
/// (<c>?unreliable=include|skip</c>); skip leaves exactly these chapters' flagged units unannotated, so the
/// export's <c>X-Export-Unreliable-Skipped-Count</c> equals this list's length for the whole book (for the
/// client's status language, the book's).</para>
///
/// <para><see cref="FailedChapters"/> (c20): chapters, in reading order, whose LATEST whole-book attempt failed or
/// produced an unreliable result and that no reliable proofread has covered since. A chapter whose attempt THREW
/// keeps its previous Active result, which still counts as fresh when it is reliable and newer than the last edit,
/// so the normal plan skips it; an unreliable result archived the previous one (its pending suggestions
/// Superseded) and is never fresh, so the normal plan re-plans that chapter too. <c>POST .../proofread?onlyFailed=true</c>
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
    int FailedEstimatedSeconds,
    int ChaptersEdited,
    int ChaptersUnreliable);

/// <summary>
/// A chapter one of whose exported units' current Active Proofread result (in the status language) is flagged
/// unreliable: the chapter <c>?unreliable=skip</c> leaves unannotated. <paramref name="Order"/> is the stored
/// 0-based <c>Chapter.Order</c>; the client owns the 1-based display number.
/// </summary>
public record BookProofreadUnreliableChapterDto(Guid ChapterId, string Title, int Order);

/// <summary>
/// A chapter whose latest whole-book proofread attempt failed or came back unreliable (c20). <paramref name="Order"/>
/// is the stored 0-based <c>Chapter.Order</c>; the client owns the 1-based display number.
/// </summary>
public record BookProofreadFailedChapterDto(Guid ChapterId, string Title, int Order);
