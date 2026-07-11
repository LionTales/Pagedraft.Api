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
    BookReview,

    /// <summary>
    /// Value-scoped analysis-output repair (analysis-output-repair plan, p3). AnalysisRepairService tags
    /// its per-field cleanup AiRequest with this task type so the router (a) resolves the
    /// Ai:FeatureModels:AnalysisRepair key -> gemma4:12b (the "routes to itself"; there is NO
    /// AnalysisType.AnalysisRepair, so AnalysisTaskMapping is untouched), (b) sends the value-only Hebrew
    /// repair instruction VERBATIM (see AiRouter.ShouldUseUnifiedInstructionVerbatim), and (c) picks up the
    /// Ollama_AnalysisRepair tuning block (low temperature, 16k ctx). The repair pass is FAIL-SAFE: the
    /// service validates the model output and keeps the ORIGINAL value on any doubt.
    /// </summary>
    AnalysisRepair,

    /// <summary>
    /// Span-scoped dynamic term repair (dynamic-term-repair-design plan, d3). DynamicTermRepairService tags
    /// its per-run cleanup AiRequest with this task type so the router (a) resolves the
    /// Ai:FeatureModels:TermRepair key -> a local (and/or documented cloud) model (the "routes to itself";
    /// there is NO AnalysisType.TermRepair, so AnalysisTaskMapping is untouched), (b) sends the marked-span
    /// repair instruction VERBATIM (see AiRouter.ShouldUseUnifiedInstructionVerbatim), and (c) picks up the
    /// (d4) Ollama_TermRepair tuning block (low temperature, small ctx). Unlike the value/whole-JSON-scoped
    /// AnalysisRepair (whose LLM Stage-2 was turned off because it could Hebraize keys / restructure JSON),
    /// each TermRepair call marks ONE foreign run and asks the model to return only a replacement token —
    /// that tiny blast radius is the whole point. The pass is FAIL-SAFE: the service validates the model
    /// output (re-detects the foreign script) and keeps the ORIGINAL span on any doubt.
    /// </summary>
    TermRepair
}
