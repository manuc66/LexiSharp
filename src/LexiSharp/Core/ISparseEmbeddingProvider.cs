namespace LexiSharp.Core;

/// <summary>
/// Contract for producing sparse text embeddings — e.g. SPLADE/SPLADE-v2, uniCOIL, or any
/// learned sparse model. Sparse embeddings map vocabulary terms to weights, with most terms
/// absent (sparse), unlike the dense vectors of <see cref="IEmbeddingProvider"/>.
/// <b>LexiSharp never computes these embeddings itself</b>: implementing this interface is up to
/// the consumer — a local ONNX model, an HTTP call to a model server, ... — and the provider only
/// needs to return a term-to-weight mapping.
/// </summary>
/// <remarks>
/// This seam exists so that sparse-backed engines (an in-memory inverted index over learned
/// weights, a <c>pgvector</c> <c>sparsevec</c> column, ...) can be integrated and merged with
/// lexical and dense engines through the hybrid package — whose
/// <c>ReciprocalRankFusionMerger</c> combines ranks across engines with incomparable score
/// scales. Weights are expected to be non-negative (ReLU-like); non-positive weights are treated
/// as "term absent" by the built-in engines. Because a learned model is not guaranteed to produce
/// the same vocabulary as the lexical tokenizer, terms are passed as opaque strings.
/// </remarks>
public interface ISparseEmbeddingProvider
{
    /// <summary>
    /// Embeds a single text into a sparse vector, pruned or not. The returned mapping (if any) is
    /// a set of (term, weight) pairs; the same term must not appear twice.
    /// </summary>
    Task<IReadOnlyDictionary<string, float>> GetSparseEmbeddingAsync(
        string text,
        CancellationToken cancellationToken = default);
}