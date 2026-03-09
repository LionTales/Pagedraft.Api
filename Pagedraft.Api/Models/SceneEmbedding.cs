namespace Pagedraft.Api.Models;

/// <summary>
/// Stores a vector embedding for a scene's content. The embedding is stored as a byte[]
/// (varbinary(max) on SQL Server) and converted to/from float[] by the IEmbeddingStore
/// implementation. This allows universal SQL Server support now and easy migration to
/// native vector types (Azure SQL VECTOR_DISTANCE) later.
/// </summary>
public class SceneEmbedding
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SceneId { get; set; }
    public Guid ChapterId { get; set; }
    public Guid BookId { get; set; }

    /// <summary>
    /// Raw embedding vector stored as big-endian IEEE 754 floats.
    /// Maps to varbinary(max) on SQL Server. Convert to float[] via
    /// Buffer.BlockCopy or BinaryPrimitives for similarity search.
    /// </summary>
    public byte[] EmbeddingVector { get; set; } = Array.Empty<byte>();

    /// <summary>Number of dimensions in the embedding (e.g. 1536 for text-embedding-3-small).</summary>
    public int Dimensions { get; set; }

    /// <summary>Model that generated this embedding (e.g. "text-embedding-3-small").</summary>
    public string ModelName { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    // ── Navigation ──
    public Scene Scene { get; set; } = null!;
    public Chapter Chapter { get; set; } = null!;
    public Book Book { get; set; } = null!;
}
