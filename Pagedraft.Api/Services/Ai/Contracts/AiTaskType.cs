namespace Pagedraft.Api.Services.Ai.Contracts;

/// <summary>Logical AI task for routing and prompt selection.</summary>
public enum AiTaskType
{
    Proofread,
    LineEdit,
    LinguisticAnalysis,
    Summarization,
    Translation,

    /// <summary>
    /// Free-form chapter-scoped work on the user's OWN text. CALLER SET, re-inventoried 2026-08-15
    /// (c2-genericchat-adjudication), because every written record of this rung named only the two callers
    /// Wave 3's <c>w7</c> stripped of their UI, which makes a live rung read as a dead one:
    /// <list type="number">
    ///   <item><description><c>UnifiedAnalysisService.ExplainSuggestionAsync</c> - the suggestion <b>Why?</b>
    ///     button. Builds its <see cref="AiRequest"/> with this task type directly, still has a live client
    ///     caller, was named in none of the old breadcrumbs, and is untouched by <c>w7</c>. It is the ONLY
    ///     GenericChat consumer with a user-visible surface today, and the reason this rung is KEPT.</description></item>
    ///   <item><description><see cref="AnalysisType.Custom"/> - mapped by <c>AnalysisTaskMapping</c>. The API
    ///     still accepts a <c>customPrompt</c> / <c>"Custom"</c> type; <c>w7</c> removed the client surface
    ///     only, so this is API-reachable only.</description></item>
    ///   <item><description><see cref="AnalysisType.QA"/> - <c>POST /api/books/{bookId}/ask</c>, still served;
    ///     <c>w7</c> removed the dashboard ask card, so this too is API-reachable only.</description></item>
    /// </list>
    /// <see cref="Translation"/> shares this task's prompt arm in <c>PromptFactory.GetPrompt</c> but has no
    /// live producer: its only construction site is <c>AiAnalysisService.TemplateTypeToTaskType</c>, and that
    /// service is registered in DI and injected nowhere (<c>UnifiedAnalysisService</c> superseded it).
    ///
    /// All three live surfaces are pinned by <c>Pagedraft.Api.Tests/GenericChatCallerSetTests</c>, so a
    /// retirement pass has to turn them red rather than conclude from a stale comment that nothing calls this.
    /// RETIREMENT WOULD BE A BEHAVIOR CHANGE and needs its own decision, not a cleanup batch.
    ///
    /// Tuned by <c>Ai:ProviderSettings:Ollama_GenericChat</c> (NumCtx 16384). Do NOT widen that window
    /// without a measurement plan - chatbot phase B's standing caveat binds this rung too.
    /// </summary>
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
    TermRepair,

    /// <summary>
    /// Grounded product Q&amp;A over the shipped guides corpus (chatbot phase A, d1 item 4 / c1).
    /// ProductChatService tags its AiRequest with this task type so the router (a) resolves the
    /// Ai:FeatureModels:ProductChat key -> Ollama/qwen3.5:9b (the "routes to itself"; there is NO
    /// AnalysisType.ProductChat, so AnalysisTaskMapping is untouched), (b) sends the composed
    /// grounding+guides instruction VERBATIM (see AiRouter.ShouldUseUnifiedInstructionVerbatim - the
    /// legacy pipeline instruction would contradict the grounding contract), and (c) picks up the
    /// Ai:ProviderSettings:Ollama_ProductChat tuning block (Temperature 0.1, NumPredict 2048, NumCtx
    /// 16384 - d1's whole-file retrieval token math is against that 16384 window, so do not lower it).
    ///
    /// DELIBERATELY NOT GenericChat, even though that value exists and would "work". GenericChat is a
    /// live route today (see <see cref="GenericChat"/> for the caller set, re-inventoried after Wave 3's
    /// w7), it has its own tuned rung, and its system message
    /// (PromptFactory's HebrewAssistantSystem/EnglishAssistantSystem) carries no grounding, citation
    /// or refusal semantics at all. Sharing the value would either bolt this feature's grounding
    /// contract onto chapter QA and Translation, or force a branch on some other signal inside
    /// GetPrompt - and it would mean any tuning change made FOR chapter QA silently moved product
    /// chat's behaviour, which is the exact coupling a per-task rung exists to prevent.
    ///
    /// NOT TIERED. It is absent from AiTierPolicy.TieredTasks and UserFacingTasks by decision: the
    /// tier is per BOOK and this feature is app-level, so there is no book id to resolve a tier from
    /// and no surface that could move it. ProductChatService resolves through the router's ordinary
    /// Fast rung and never calls BookAiTierResolver. Do NOT add an Ai:FeatureModels:ProductChat_thinking
    /// key; AiTierConfigParityTests fails on a tier key for a task outside the allowlist.
    ///
    /// FAIL-SAFE: retrieval failure, an empty corpus, an unreachable model or an empty completion all
    /// produce an honest "I cannot reach the guides right now" with a machine-readable fault reason,
    /// never an answer from the model's own priors.
    /// </summary>
    ProductChat
}
