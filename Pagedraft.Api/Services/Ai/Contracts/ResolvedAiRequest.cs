namespace Pagedraft.Api.Services.Ai.Contracts;

/// <summary>Request with resolved system/instruction from PromptFactory, passed to providers.</summary>
public record ResolvedAiRequest
{
    public required string SystemMessage { get; init; }
    public required string Instruction { get; init; }
    public required string InputText { get; init; }
    public string Language { get; init; } = "he-IL";
    public required AiModelSelection Selection { get; init; }
    /// <summary>Task type (e.g. Proofread) so providers can apply task-specific limits (e.g. higher NumPredict).</summary>
    public AiTaskType TaskType { get; init; }
    /// <summary>When true, providers should enforce structured JSON output (e.g. Ollama format:"json").</summary>
    public bool JsonMode { get; init; }
}
