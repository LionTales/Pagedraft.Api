namespace Pagedraft.Api.Services.Ai.Contracts;

/// <summary>
/// SINGLE source of truth for mapping a user-facing <see cref="AnalysisType"/> to the logical
/// <see cref="AiTaskType"/> used for routing, model selection, and per-task tuning (num_ctx, temperature).
///
/// Extracted from <c>UnifiedAnalysisService.MapToTaskType</c> so other paths can reuse the SAME mapping the
/// router uses rather than re-deriving it. In particular <see cref="Analysis.BookContextAssembler"/> sizes
/// the whole-book context to the CONSUMING task's context window: the assembled text must fit the window of
/// the task that will actually consume it (e.g. QA → <see cref="AiTaskType.GenericChat"/>), not a hardcoded
/// one, or Ollama silently truncates anything past num_ctx.
/// </summary>
public static class AnalysisTaskMapping
{
    /// <summary>Maps an <see cref="AnalysisType"/> to the <see cref="AiTaskType"/> it routes to.</summary>
    public static AiTaskType ToAiTaskType(AnalysisType analysisType) => analysisType switch
    {
        AnalysisType.Proofread => AiTaskType.Proofread,
        AnalysisType.LineEdit => AiTaskType.LineEdit,
        AnalysisType.LinguisticAnalysis => AiTaskType.LinguisticAnalysis,
        AnalysisType.LiteraryAnalysis => AiTaskType.LinguisticAnalysis,
        AnalysisType.Summarization => AiTaskType.Summarization,
        AnalysisType.BookOverview => AiTaskType.LinguisticAnalysis,
        AnalysisType.Synopsis => AiTaskType.Summarization,
        AnalysisType.CharacterAnalysis => AiTaskType.LinguisticAnalysis,
        AnalysisType.StoryAnalysis => AiTaskType.LinguisticAnalysis,
        AnalysisType.BookReview => AiTaskType.BookReview,
        AnalysisType.QA => AiTaskType.GenericChat,
        AnalysisType.Custom => AiTaskType.GenericChat,
        _ => AiTaskType.GenericChat
    };
}
