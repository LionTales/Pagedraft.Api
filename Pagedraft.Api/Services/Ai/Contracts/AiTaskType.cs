namespace Pagedraft.Api.Services.Ai.Contracts;

/// <summary>Logical AI task for routing and prompt selection.</summary>
public enum AiTaskType
{
    Proofread,
    LineEdit,
    LinguisticAnalysis,
    Summarization,
    Translation,
    GenericChat,

    /// <summary>
    /// Whole-book developmental review (wb2-c02). The orchestrator (BookReviewService) tags every
    /// per-dimension AiRequest with this task type so the router resolves the (future)
    /// Ai:FeatureModels:BookReview key and the call is correctly labelled/capped.
    ///
    /// SEAM with wb2-c03: c03 owns attaching AnalysisType.BookReview -> AiTaskType.BookReview in
    /// AnalysisTaskMapping AND the Ai:FeatureModels:BookReview appsettings key (+ breadcrumb). Until c03
    /// sets that key, LinguisticModelResolver.ResolveForTask(BookReview) falls back to the default model
    /// (both-non-empty predicate) -- which is fine: no real model call happens in c02's tests.
    /// </summary>
    BookReview
}
