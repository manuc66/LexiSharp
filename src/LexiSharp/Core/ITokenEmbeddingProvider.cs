namespace LexiSharp.Core;

/// <summary>
/// Produces a sequence of token-level embeddings for a text — the building block of late
/// interaction (ColBERT-style) ranking, where a query is matched to a document token by token.
/// <b>LexiSharp never runs such a model itself</b>: implementing this interface is up to the
/// consumer — a local ONNX/Runtime model, an HTTP call to a model server, ... — and the reranker
/// built on top only reads the returned vectors.
/// </summary>
/// <remarks>
/// This seam mirrors <see cref="IEmbeddingProvider"/> but for <i>several</i> vectors per text:
/// every token of the input gets its own embedding, so documents need their token vectors
/// pre-computed at index time (with <c>EmbeddingUse.Passage</c>) and the query is embedded again
/// with <c>EmbeddingUse.Query</c> for each search — the same role distinction the dense and sparse
/// seams carry. Because <see cref="IReranker.Rerank"/> is synchronous, remote backends block
/// internally on their async work — the same trade-off the PostgreSQL engines and
/// <c>ICrossEncoderScorer</c> already make.
/// </remarks>
public interface ITokenEmbeddingProvider
{
    /// <summary>Human readable name of the underlying model, e.g. <c>"colbert-ir/colbertv2.0"</c>.</summary>
    string Name { get; }

    /// <summary>Embeds every token of <paramref name="text"/>; returns one vector per token.</summary>
    IReadOnlyList<ReadOnlyMemory<float>> GetTokenEmbeddings(string text, EmbeddingUse use);
}

/// <summary>Backwards-compatible call shape: embeds text as a <see cref="EmbeddingUse.Passage"/>.</summary>
public static class TokenEmbeddingProviderExtensions
{
    /// <summary>Encodes passage-side (index) token embeddings. Kept so older call sites keep working.</summary>
    public static IReadOnlyList<ReadOnlyMemory<float>> GetTokenEmbeddings(
        this ITokenEmbeddingProvider provider,
        string text) =>
        provider.GetTokenEmbeddings(text, EmbeddingUse.Passage);
}