namespace Pagedraft.Api.Services.Ai.Contracts;

/// <summary>Request with resolved system/instruction from PromptFactory, passed to providers.</summary>
public record ResolvedAiRequest
{
    public required string SystemMessage { get; init; }
    public required string Instruction { get; init; }
    public required string InputText { get; init; }
    public string Language { get; init; } = "he-IL";
    public required AiModelSelection Selection { get; init; }
}
