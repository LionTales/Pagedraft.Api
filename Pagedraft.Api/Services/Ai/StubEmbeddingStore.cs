using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Pagedraft.Api.Services.Ai;

/// <summary>
/// Plan 0 stub implementation of <see cref="IEmbeddingStore"/>. All operations throw
/// so callers know embeddings persistence/search is not wired up yet.
/// </summary>
public sealed class StubEmbeddingStore : IEmbeddingStore
{
    public Task StoreAsync(
        Guid sceneId,
        Guid chapterId,
        Guid bookId,
        float[] embedding,
        string modelName,
        CancellationToken ct = default) =>
        throw new NotSupportedException("Embedding store not yet configured");

    public Task<IReadOnlyList<(Guid SceneId, float Score)>> SearchAsync(
        float[] queryEmbedding,
        Guid bookId,
        int topK = 5,
        CancellationToken ct = default) =>
        throw new NotSupportedException("Embedding store not yet configured");

    public Task DeleteByBookAsync(Guid bookId, CancellationToken ct = default) =>
        throw new NotSupportedException("Embedding store not yet configured");

    public Task DeleteBySceneAsync(Guid sceneId, CancellationToken ct = default) =>
        throw new NotSupportedException("Embedding store not yet configured");
}

