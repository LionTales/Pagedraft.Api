using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Ai;

/// <summary>Per-provider AI analysis (Ollama, OpenAI, Azure, Anthropic).</summary>
public interface IAiAnalysisProvider
{
    /// <summary>Run completion with resolved system/instruction and selected model.</summary>
    Task<AiResponse> CompleteAsync(ResolvedAiRequest request, CancellationToken cancellationToken = default);
}
