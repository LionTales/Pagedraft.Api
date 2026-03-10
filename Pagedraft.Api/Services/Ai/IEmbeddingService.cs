namespace Pagedraft.Api.Services.Ai;

/// <summary>
/// Orchestration layer for generating vector embeddings from text via an AI model.
/// Decoupled from storage — call <see cref="IEmbeddingStore"/> to persist results.
/// Plan 0: StubEmbeddingService (throws NotSupportedException).
/// Plan 5: Real implementation backed by OpenAI / Azure / local model.
/// </summary>
public interface IEmbeddingService
{
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default);
}
