namespace LexiSharp.Core;

/// <summary>
/// Optional capability of an <see cref="ITextSearchEngine"/> that can estimate, cheaply and
/// without running a search, how many documents a query is likely to touch — the signal a
/// <see cref="RoutedSearchEngine"/> uses to pick the cheapest engine for a query.
/// </summary>
/// <remarks>
/// Implementing this interface is opt-in; engines without it are treated as a last resort by
/// the built-in estimator. The estimate is a <b>heuristic</b> — it must never be used as a
/// result count, only to compare engines against each other. Implemented by the stock
/// <c>RankedTextSearchEngine</c> as the sum of the query terms' document frequencies (an
/// upper bound of the candidate union; expansion/synonym atoms resolve at search time and are
/// deliberately not counted).
/// </remarks>
public interface IQueryCostProbe
{
    /// <summary>
    /// Estimates how many candidate documents <paramref name="query"/> will touch under
    /// <paramref name="options"/>. Smaller is cheaper. May be zero (empty request or empty
    /// index) or an upper bound, but must be non-negative and deterministic for the same inputs.
    /// </summary>
    /// <param name="query">Raw query text, as passed to <see cref="ITextSearchEngine.Search(string, SearchOptions)"/>.</param>
    /// <param name="options">The options the search would run with.</param>
    long EstimateCandidateCount(ReadOnlySpan<char> query, SearchOptions options);
}
