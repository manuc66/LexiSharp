namespace LexiSharp.Core;

/// <summary>
/// The role a piece of text plays when it is embedded. Several models encode queries and
/// documents differently by construction (e.g. E5 prefixes queries with <c>"query: "</c> and
/// passages with <c>"passage: "</c>); the provider needs to know which side it is encoding.
/// </summary>
public enum EmbeddingUse
{
    /// <summary>A document/text that will be indexed and searched against. Also the fallback for unspecified uses.</summary>
    Passage,

    /// <summary>The user query that will be matched against indexed passages.</summary>
    Query,
}

/// <summary>
/// Contract for producing text embeddings. <b>LexiSharp never computes embeddings itself</b>:
/// implementing this interface is up to the consumer — a local ONNX model, an HTTP call to a
/// model server, an OpenAI/Cohere-compatible API, ... — and the provider only needs to honour
/// the dimension contract. The <see cref="EmbeddingUse"/> argument tells the provider whether it
/// is encoding query text or an indexed passage, so asymmetric models (E5 and friends) can apply
/// their per-side prefixes/suffixes.
/// </summary>
/// <remarks>
/// This seam exists so that embedding-backed engines (a PostgreSQL <c>pgvector</c> ANN index,
/// an in-memory HNSW store, ...) can be integrated and merged with lexical engines through the
/// hybrid package — whose <c>ReciprocalRankFusionMerger</c> combines ranks across engines with
/// incomparable score scales.
/// <para>
/// Thread-safety is implementation-defined: an engine may call this method concurrently from
/// several search/index operations, so providers should be safe against concurrent invocation.
/// </para>
/// </remarks>
public interface IEmbeddingProvider
{
    /// <summary>Length of the returned vectors. All embeddings must match this length.</summary>
    int Dimension { get; }

    /// <summary>Embeds a single text, for the given role (query or indexed passage).</summary>
    Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
        string text,
        EmbeddingUse use,
        CancellationToken cancellationToken = default);
}

/// <summary>Backwards-compatible call shape: embeds text as a <see cref="EmbeddingUse.Passage"/>.</summary>
public static class EmbeddingProviderExtensions
{
    /// <summary>Encodes a passage (document-side) embedding. Kept so older call sites keep working.</summary>
    public static Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
        this IEmbeddingProvider provider,
        string text,
        CancellationToken cancellationToken = default) =>
        provider.GetTextEmbeddingAsync(text, EmbeddingUse.Passage, cancellationToken);
}