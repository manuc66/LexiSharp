namespace LexiSharp.Core;

/// <summary>
/// Scores a (query, document) pair as a single relevance value — the job of a cross-encoder
/// (concatenate query and document into one input) or, more generally, of any pairwise
/// model (LLM judge, learned scoring function). <b>LexiSharp never runs such a model itself</b>:
/// implementing this interface is up to the consumer — a local ONNX/Runtime model, an HTTP call
/// to a model server, ... — and the reranker built on top only reads the returned score.
/// </summary>
/// <remarks>
/// This seam exists so that a precision-oriented, pairwise reranker (see the
/// <c>CrossEncoderReranker</c> in the hybrid package) can be dropped into the
/// <c>recall-oriented retrieval → rerank → precision-oriented ordering</c> topology described by
/// <see cref="IReranker"/>. Because <see cref="IReranker.Rerank"/> is synchronous, remote backends
/// block internally on their async work — the same trade-off the PostgreSQL engines already make.
/// </remarks>
public interface ICrossEncoderScorer
{
    /// <summary>Human readable name of the underlying model, e.g. <c>"cross-encoder/ms-marco-MiniLM"</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Judges how relevant <paramref name="document"/> is to <paramref name="query"/>.
    /// Higher values mean more relevant; <c>0</c> conventionally means "not a match".
    /// </summary>
    double Score(string query, SearchDocument document);
}