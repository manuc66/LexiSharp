namespace LexiSharp.Hybrid;

/// <summary>
/// Contract for producing text embeddings. <b>LexiSharp never computes embeddings itself</b>:
/// implementing this interface is up to the consumer — a local ONNX model, an HTTP call to a
/// model server, an OpenAI/Cohere-compatible API, ... — and the provider only needs to honour
/// the dimension contract.
/// </summary>
/// <remarks>
/// This seam exists so that the hybrid engine can later host embeddings-backed sources
/// (e.g. a PostgreSQL <c>pgvector</c> ANN index or an in-memory HNSW store) alongside the
/// lexical engines, and merge lexical + vector results. Nothing in this package consumes the
/// interface yet; it is the agreed extension point.
/// </remarks>
public interface IEmbeddingProvider
{
    /// <summary>Length of the returned vectors. All embeddings must match this length.</summary>
    int Dimension { get; }

    /// <summary>Embeds a single text (document content, query, ...).</summary>
    Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default);
}