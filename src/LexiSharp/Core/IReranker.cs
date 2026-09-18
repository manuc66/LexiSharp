namespace LexiSharp.Core;

/// <summary>
/// Contract for re-ordering a list of candidate results <b>after</b> retrieval — the seam for
/// second-stage ranking strategies (diversity preservation, cross-encoders, learning-to-rank
/// models, ...).
/// </summary>
/// <remarks>
/// A reranker sees only the shortlist an engine (or pipeline stage) already selected and returns
/// a better order for it. The expensive, higher-precision scoring is expected to happen here,
/// on a handful of candidates rather than the whole corpus; the standard topology is
/// <i>recall-oriented retrieval</i> → <see cref="Rerank"/> → <i>precision-oriented ordering</i>.
/// <para>
/// Contract: the returned list is ordered best-first, contains no document that was not in
/// <c>candidates</c> (it may be shorter when the strategy filters, never longer),
/// and the input list is left untouched. A strategy may replace each candidate's
/// <see cref="SearchResult.Score"/> with its own notion of relevance or keep the original score
/// and only change the order (order-only strategies, e.g. diversity-preserving ones). As
/// everywhere in LexiSharp, a final score of exactly <c>0</c> means "not a match" and NaN or
/// infinite scores are rejected by consuming engines.
/// </para>
/// </remarks>
public interface IReranker
{
    /// <summary>Human readable name of the strategy, e.g. <c>"MMR"</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Re-ranks the candidate list for a query, best-first. Must not mutate
    /// <paramref name="candidates"/>.
    /// </summary>
    /// <param name="query">The raw query text.</param>
    /// <param name="candidates">Candidates as produced by a search, ordered best-first.</param>
    /// <returns>The re-ranked list (possibly filtered down, never extended).</returns>
    IReadOnlyList<SearchResult> Rerank(string query, IReadOnlyList<SearchResult> candidates);
}
