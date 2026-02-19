namespace Pagedraft.Api.Services.Ai.Contracts;

/// <summary>Provider-agnostic AI request (input to the router).</summary>
public record AiRequest
{
    public required string InputText { get; init; }
    public string? Instruction { get; init; }
    public required AiTaskType TaskType { get; init; }
    public string Language { get; init; } = "he-IL";
    public string? UserId { get; init; }
    public string? SourceId { get; init; }
    public Dictionary<string, string>? Metadata { get; init; }
}
