using System;
using System.Threading;
using System.Threading.Tasks;

namespace Pagedraft.Api.Services.Ai;

/// <summary>
/// Plan 0 stub implementation of <see cref="IEmbeddingService"/>. Throws by default so
/// any accidental usage clearly signals that embeddings are not yet configured.
/// </summary>
public sealed class StubEmbeddingService : IEmbeddingService
{
    public Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default) =>
        throw new NotSupportedException("Embedding service not yet configured");
}

