using Pagedraft.Api.Services.Ai.Contracts;

namespace Pagedraft.Api.Services.Ai;

/// <summary>Optional streaming support (e.g. Ollama).</summary>
public interface IStreamingAiAnalysisProvider : IAiAnalysisProvider
{
    IAsyncEnumerable<string> StreamCompleteAsync(ResolvedAiRequest request, CancellationToken cancellationToken = default);
}
