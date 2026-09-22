namespace LexiSharp.Core;

/// <summary>
/// Picks which of several engines a <see cref="RoutedSearchEngine"/> should run a query on.
/// </summary>
/// <remarks>
/// The default is <see cref="CheapestByCandidateCountEstimator"/>; supply a custom one to route
/// on a different signal (latency history, engine priority, query shape, ...).
/// </remarks>
public interface IQueryCostEstimator
{
    /// <summary>
    /// Returns the index of the engine in <paramref name="engines"/> to use for
    /// <paramref name="query"/>, in <c>[0, engines.Count)</c>.
    /// </summary>
    /// <param name="engines">The candidate engines, in constructor order.</param>
    /// <param name="query">The raw query about to be searched.</param>
    /// <param name="options">The options the search will run with.</param>
    int Select(IReadOnlyList<RoutedEngine> engines, ReadOnlySpan<char> query, SearchOptions options);
}
