using Pagedraft.Api.Services.Analysis;

namespace Pagedraft.Api.Models.Dtos;

public record AnalysisProgressDto(
    Guid JobId,
    string AnalysisType,
    string Scope,
    Guid? BookId,
    Guid? ChapterId,
    Guid? SceneId,
    string Status,
    int CurrentChunk,
    int TotalChunks,
    int CompletedChunks,
    string Message,
    int EstimatedCompletionPercent,
    // ── Whole-book REVIEW build-shape (wb4-c06). Populated ONLY on a BookReview build's terminal poll so the FE
    //    can render the "N windows[, continuity pass]" detail + the "N windows failed" partial warning right
    //    after a build. Null for every other analysis-progress route and for a review job that has not yet
    //    reached its terminal. This is the LIVE build-completion channel the persisted status probe does not
    //    carry (it reports these as 0/false). ──
    int? BookReviewWindowCount = null,
    bool? BookReviewRanContinuityReduce = null,
    int? BookReviewFailedWindows = null);

