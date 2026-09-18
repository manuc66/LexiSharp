namespace LexiSharp.Core;

/// <summary>
/// Computes how relevant a single document is with respect to a tokenized query,
/// given a shared <see cref="ITextIndex"/> for corpus statistics.
/// </summary>
/// <remarks>
/// A scorer is a pure strategy: it reads statistics from the index and never mutates it,
/// which makes swapping ranking algorithms (BM25, TF-IDF, boolean, ...) trivial.
/// </remarks>
public interface ITextScorer
{
    /// <summary>Human readable name of the algorithm, e.g. <c>"BM25"</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Computes the relevance score of a document for a query. Higher values mean more relevant.
    /// </summary>
    /// <param name="documentId">Id of the document to score.</param>
    /// <param name="queryTerms">Terms of the query, already tokenized.</param>
    /// <param name="index">The shared index holding corpus statistics.</param>
    double Score(string documentId, IReadOnlyList<string> queryTerms, ITextIndex index);
}