namespace LexiSharp.Core;

/// <summary>
/// A ranked document whose final score is broken down by the source signal that produced it.
/// </summary>
/// <param name="DocumentId">Identifier of the matched document.</param>
/// <param name="Score">Final merged relevance score (same scale/convention as <see cref="SearchResult.Score"/>).</param>
/// <param name="Document">The original indexed document.</param>
/// <param name="Contributions">
/// The per-signal score each source engine assigned to this document before merging, keyed by
    /// source name (e.g. <c>"lexical"</c>, <c>"semantic"</c>, <c>"semantic-secondary"</c>). A source
    /// that did not return the document is absent from the dictionary. These are diagnostic
    /// signals, not what the merger fed its math: only score-based mergers (e.g.
    /// <see cref="Hybrid.WeightedScoreResultMerger"/>) consume them numerically, while rank-only
    /// mergers (RRF) read order — so on such a merger the raw scores need not align with
    /// <see cref="Score"/>. Treat discrepancies as expected, not as a bug.
    /// </param>
public sealed record DetailedSearchResult(
    string DocumentId,
    double Score,
    SearchDocument Document,
    IReadOnlyDictionary<string, double> Contributions)
{
    /// <summary>Projects this detailed result to the plain <see cref="SearchResult"/> shape (drops contributions).</summary>
    public SearchResult ToSearchResult() => new(DocumentId, Score, Document);
}