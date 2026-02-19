namespace Pagedraft.Api.Services.Ai.Contracts;

/// <summary>Provider-agnostic AI response.</summary>
public record AiResponse
{
    public required string Content { get; init; }
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public TimeSpan? Duration { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public decimal? ApproxCostUsd { get; init; }
    public Dictionary<string, string>? Extra { get; init; }
}
