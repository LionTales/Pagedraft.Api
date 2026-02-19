namespace Pagedraft.Api.Services.Ai.Contracts;

/// <summary>Chosen provider and model for a single request.</summary>
public record AiModelSelection
{
    public required string Provider { get; init; }
    public required string Model { get; init; }
}
