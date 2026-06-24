namespace Pagedraft.Api.Services.Ai.Contracts;

/// <summary>
/// All analysis operations the unified system can perform.
/// Replaces both AiTaskType usage for analysis and the string-based template Type.
/// </summary>
public enum AnalysisType
{
    // ── Chapter / Scene level ──
    Proofread,
    LineEdit,
    LinguisticAnalysis,
    LiteraryAnalysis,

    // ── Book level ──
    BookOverview,
    Synopsis,
    CharacterAnalysis,
    StoryAnalysis,
    /// <summary>Whole-book developmental review (wb2). Routes to <see cref="AiTaskType.BookReview"/>.</summary>
    BookReview,

    // ── Cross-level ──
    Summarization,
    QA,
    Custom
}
