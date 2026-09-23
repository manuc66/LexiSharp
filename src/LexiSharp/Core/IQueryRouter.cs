namespace LexiSharp.Core;

/// <summary>
/// The decision an <see cref="IQueryRouter"/> returns for a query: which route to run, and how
/// confident the router is.
/// </summary>
/// <param name="RouteId">Id of the selected route — one of the candidate ids passed to the router.</param>
/// <param name="Confidence">
/// Router confidence in <c>[0, 1]</c> (1 = certain). Compared against
/// <see cref="RoutingSearchEngine.MinimumConfidence"/>: below it, the configured fallback route
/// runs. A <c>NaN</c> confidence is treated as below any threshold.
/// </param>
public sealed record QueryRoute(string RouteId, double Confidence);

/// <summary>
/// Picks which route (engine + optional filters) a <see cref="RoutingSearchEngine"/> should run a
/// query on — a semantic/intent decision, as opposed to <see cref="IQueryCostEstimator"/> which
/// routes on candidate cost.
/// </summary>
/// <remarks>
/// Implementations are consumer code — a rule, a small classifier, or a model exposed over HTTP;
/// <b>LexiSharp never runs a model itself</b>. The router only chooses among the ids it is given:
/// it does not build filters, build queries or touch indexes. Returning <c>null</c>, an unknown id,
/// a confidence below the threshold, or throwing makes the engine fall back to its fallback route,
/// so a router can never break a search.
/// <para>
/// The method is asynchronous because a model-backed router is naturally async. A synchronous
/// caller (e.g. <see cref="RoutingSearchEngine.Search(string, SearchOptions)"/>) blocks on it,
/// the same trade-off the PostgreSQL engines and the rerankers already make.
/// </para>
/// </remarks>
public interface IQueryRouter
{
    /// <summary>
    /// Returns the route to run for <paramref name="query"/>, or <c>null</c> for "no opinion".
    /// </summary>
    /// <param name="query">The raw query about to be searched.</param>
    /// <param name="candidateRouteIds">Ids of the routes the engine can run, in configuration order.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<QueryRoute?> RouteAsync(
        string query,
        IReadOnlyList<string> candidateRouteIds,
        CancellationToken cancellationToken = default);
}