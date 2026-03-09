namespace Pagedraft.Api.Services.Ai;

/// <summary>
/// Pure storage abstraction for vector embeddings. Decouples the vector backend
/// from domain logic so we can swap implementations without touching analysis code.
/// Plan 0: StubEmbeddingStore (throws NotSupportedException).
/// Plan 5 MVP: SqlServerEmbeddingStore (store in SceneEmbeddings table, in-memory cosine similarity).
/// Future: AzureSqlVectorStore (native VECTOR_DISTANCE), QdrantEmbeddingStore, etc.
/// </summary>
public interface IEmbeddingStore
{
    Task StoreAsync(
        Guid sceneId, Guid chapterId, Guid bookId,
        float[] embedding, string modelName,
        CancellationToken ct = default);

    Task<IReadOnlyList<(Guid SceneId, float Score)>> SearchAsync(
        float[] queryEmbedding, Guid bookId,
        int topK = 5,
        CancellationToken ct = default);

    Task DeleteByBookAsync(Guid bookId, CancellationToken ct = default);

    Task DeleteBySceneAsync(Guid sceneId, CancellationToken ct = default);
}
