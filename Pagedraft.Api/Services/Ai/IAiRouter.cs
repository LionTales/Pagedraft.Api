using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Ai;

/// <summary>Single entry-point for AI: resolves provider/model and delegates to the chosen provider.</summary>
public interface IAiRouter
{
    Task<AiResponse> CompleteAsync(AiRequest request, CancellationToken cancellationToken = default);

    /// <summary>Stream tokens when the selected provider supports it (e.g. Ollama).</summary>
    IAsyncEnumerable<string> StreamCompleteAsync(AiRequest request, CancellationToken cancellationToken = default);
}
