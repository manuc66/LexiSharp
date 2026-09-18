namespace LexiSharp.Core;

/// <summary>
/// Contract for producing text embeddings. <b>LexiSharp never computes embeddings itself</b>:
/// implementing this interface is up to the consumer — a local ONNX model, an HTTP call to a
/// model server, an OpenAI/Cohere-compatible API, ... — and the provider only needs to honour
/// the dimension contract.
/// </summary>
/// <remarks>
/// This seam exists so that embedding-backed engines (a PostgreSQL <c>pgvector</c> ANN index,
/// an in-memory HNSW store, ...) can be integrated and merged with lexical engines through the
/// hybrid package — whose <c>ReciprocalRankFusionMerger</c> combines ranks across engines with
/// incomparable score scales.
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