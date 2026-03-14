namespace Pagedraft.Api.Services.Ai.Contracts;

/// <summary>Logical AI task for routing and prompt selection.</summary>
public enum AiTaskType
{
    Proofread,
    LineEdit,
    LinguisticAnalysis,
    Summarization,
    Translation,
    GenericChat
}
