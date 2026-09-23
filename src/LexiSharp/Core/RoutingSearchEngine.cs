namespace LexiSharp.Core;

/// <summary>
/// A named route a <see cref="RoutingSearchEngine"/> can run: an engine plus optional metadata
/// filters AND-ed onto the caller's own filters when this route is selected.
/// </summary>
/// <param name="Id">Decision label, unique within one router engine (used in errors and by the router).</param>
/// <param name="Engine">
/// The engine to run. Pre-filled by the caller: the router engine never indexes into it.
/// </param>
/// <param name="Filters">
/// Extra <see cref="SearchDocument.Fields"/> predicates applied on top of the caller's
/// <see cref="SearchOptions.Filters"/> (they are AND-ed, never substituted) when this route is
/// chosen. <c>null</c> or empty means "no extra filter".
/// </param>
public sealed record SearchRoute(
    string Id,
    ITextSearchEngine Engine,
    IReadOnlyList<MetadataFilter>? Filters = null);

/// <summary>
/// Opt-in decorator that asks an <see cref="IQueryRouter"/> which <see cref="SearchRoute"/> to run
/// each query on, then runs that route's engine with the route's filters merged into the request.
/// </summary>
/// <remarks>
/// This is the semantic counterpart of <see cref="RoutedSearchEngine"/>: the latter picks an engine
/// by <b>cost</b>, this one by an explicit <b>decision</b> (intent, question-vs-keywords, ...).
/// The engines and routes are built and populated by the caller, so the decorator owns no index:
/// its <see cref="Index"/>/<see cref="Add"/>/<see cref="Remove"/>/<see cref="Clear"/> throw
/// <see cref="NotSupportedException"/>. Exactly one route runs per query, so the returned scores are
/// that engine's own scores (never mixed or renormalized — that is the hybrid engine's job).
/// <para>
/// The router is called once per query and its decision is applied as follows: a <c>null</c>
/// decision, an unknown route id, a confidence below <see cref="MinimumConfidence"/>, or a thrown
/// exception all select the fallback route. A request that cannot produce results
/// (<see cref="SearchOptions.IsEmpty"/>) returns empty without calling the router.
/// </para>
/// <para>
/// Routing blocks on the (async) router, like the synchronous PostgreSQL engines block on
/// <see cref="IEmbeddingProvider"/>. Implementations of <see cref="IQueryRouter"/> must be
/// thread-safe, as concurrent searches may call them at once.
/// </para>
/// </remarks>
public sealed class RoutingSearchEngine : ITextSearchEngine
{
    private readonly IReadOnlyList<SearchRoute> _routes;
    private readonly Dictionary<string, SearchRoute> _routesById;
    private readonly IReadOnlyList<string> _routeIds;
    private readonly IQueryRouter _router;
    private readonly SearchRoute _fallback;

    /// <param name="router">The decision strategy (rule, classifier, model, ...).</param>
    /// <param name="routes">The candidate routes, in the order presented to the router. Must be non-empty with unique ids.</param>
    /// <param name="fallbackId">Id of the route run when the router has no confident opinion. Must match a route.</param>
    /// <param name="minimumConfidence">
    /// Confidence a decision must reach to be honoured, in <c>[0, 1]</c> (default <c>0</c> — any
    /// non-negative confidence wins). Below it, the fallback route runs.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="routes"/> is empty, holds a null route, a null/blank/duplicate id or a null
    /// engine; or <paramref name="fallbackId"/> does not match a route.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="router"/> or <paramref name="routes"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minimumConfidence"/> is outside <c>[0, 1]</c>.</exception>
    public RoutingSearchEngine(
        IQueryRouter router,
        IReadOnlyList<SearchRoute> routes,
        string fallbackId,
        double minimumConfidence = 0.0)
    {
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(routes);

        if (routes.Count == 0)
            throw new ArgumentException("A routing engine needs at least one route.", nameof(routes));

        if (minimumConfidence < 0 || minimumConfidence > 1)
            throw new ArgumentOutOfRangeException(nameof(minimumConfidence), minimumConfidence, "Confidence must be in [0, 1].");

        var byId = new Dictionary<string, SearchRoute>(routes.Count, StringComparer.Ordinal);
        var ids = new string[routes.Count];

        for (int i = 0; i < routes.Count; i++)
        {
            var route = routes[i];

            if (route is null)
                throw new ArgumentException($"Route at index {i} is null.", nameof(routes));

            if (string.IsNullOrWhiteSpace(route.Id))
                throw new ArgumentException($"Route at index {i} has a null or blank id.", nameof(routes));

            if (route.Engine is null)
                throw new ArgumentException($"Route '{route.Id}' has a null engine.", nameof(routes));

            if (!byId.TryAdd(route.Id, route))
                throw new ArgumentException($"Duplicate route id '{route.Id}'.", nameof(routes));

            ids[i] = route.Id;
        }

        if (fallbackId is null)
            throw new ArgumentNullException(nameof(fallbackId));

        if (!byId.TryGetValue(fallbackId, out var fallback))
            throw new ArgumentException($"Fallback id '{fallbackId}' does not match any route.", nameof(fallbackId));

        _router = router;
        _routes = routes;
        _routesById = byId;
        _routeIds = ids;
        _fallback = fallback;
        MinimumConfidence = minimumConfidence;
    }

    /// <summary>The candidate routes, in constructor order.</summary>
    public IReadOnlyList<SearchRoute> Routes => _routes;

    /// <summary>Confidence a router decision must reach to be honoured.</summary>
    public double MinimumConfidence { get; }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The router never writes; populate the route engines directly.</exception>
    public void Index(IEnumerable<SearchDocument> documents) => throw new NotSupportedException(WriteMessage);

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The router never writes; populate the route engines directly.</exception>
    public void Add(SearchDocument document) => throw new NotSupportedException(WriteMessage);

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The router never writes; populate the route engines directly.</exception>
    public void Remove(string documentId) => throw new NotSupportedException(WriteMessage);

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The router never writes; populate the route engines directly.</exception>
    public void Clear() => throw new NotSupportedException(WriteMessage);

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);

        var resolved = options ?? SearchOptions.Default;

        // An empty request cannot return anything: skip the router (and any model call) entirely.
        if (resolved.IsEmpty)
            return Array.Empty<SearchResult>();

        var route = SelectRoute(query);
        return route.Engine.Search(query, MergeFilters(resolved, route.Filters));
    }

    private SearchRoute SelectRoute(string query)
    {
        QueryRoute? decision;

        try
        {
            decision = _router.RouteAsync(query, _routeIds, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // A broken router must not break the search.
            return _fallback;
        }

        if (decision is null || !(decision.Confidence >= MinimumConfidence))
            return _fallback;

        return _routesById.TryGetValue(decision.RouteId, out var route) ? route : _fallback;
    }

    private static SearchOptions MergeFilters(SearchOptions options, IReadOnlyList<MetadataFilter>? routeFilters)
    {
        if (routeFilters is null || routeFilters.Count == 0)
            return options;

        var callerFilters = options.Filters;

        if (callerFilters is null || callerFilters.Count == 0)
            return options with { Filters = routeFilters };

        var merged = new List<MetadataFilter>(callerFilters.Count + routeFilters.Count);
        merged.AddRange(callerFilters);
        merged.AddRange(routeFilters);

        return options with { Filters = merged };
    }

    private const string WriteMessage =
        "RoutingSearchEngine is read-only: populate its route engines directly.";
}