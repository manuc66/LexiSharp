namespace LexiSharp.Core;

/// <summary>
/// A named engine candidate for a <see cref="RoutedSearchEngine"/>.
/// </summary>
/// <param name="Name">Diagnostic label, unique within one router (used in errors and logs).</param>
/// <param name="Engine">
/// The engine to route to. Pre-filled by the caller: the router never indexes into it.
/// </param>
public sealed record RoutedEngine(string Name, ITextSearchEngine Engine);

/// <summary>
/// Opt-in decorator that forwards each query to one of several pre-filled engines, chosen by an
/// <see cref="IQueryCostEstimator"/> — with <see cref="CheapestByCandidateCountEstimator"/> the
/// engine whose <see cref="IQueryCostProbe"/> estimate is smallest.
/// </summary>
/// <remarks>
/// The engines are built and populated by the caller, so the router owns no index: its
/// <see cref="Index"/>/<see cref="Add"/>/<see cref="Remove"/>/<see cref="Clear"/> throw
/// <see cref="NotSupportedException"/>. Exactly one engine runs per query, so the returned
/// scores are that engine's own scores — the router never mixes or renormalizes them (merging
/// several engines' rankings is the job of the hybrid engine). A <c>null</c>-bounded or
/// unindexed engine can still be selected: decide that in a custom estimator.
/// <para>
/// Capability interfaces are surfaced too, routed to the engine the estimator selects:
/// <see cref="IFacetedSearchEngine"/>, <see cref="IDetailedSearchEngine"/> and
/// <see cref="IExplainableSearchEngine"/> delegate the whole request to that engine, and
/// <see cref="IQueryCostProbe"/> reports the smallest estimate across the engines. Because the
/// selection is per query, a capability that the selected engine lacks throws
/// <see cref="NotSupportedException"/> (the router cannot fall back to another engine without
/// changing which engine answers the query).
/// </para>
/// </remarks>
public sealed class RoutedSearchEngine : IFacetedSearchEngine, IQueryCostProbe, IDetailedSearchEngine, IExplainableSearchEngine
{
    private readonly IReadOnlyList<RoutedEngine> _engines;
    private readonly IQueryCostEstimator _estimator;

    /// <param name="engines">The candidate engines, in tie-break order. Must be non-empty.</param>
    /// <param name="estimator">The selection strategy (defaults to <see cref="CheapestByCandidateCountEstimator.Instance"/> when null).</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="engines"/> is empty, or holds a null/blank name or a null engine.
    /// </exception>
    public RoutedSearchEngine(
        IReadOnlyList<RoutedEngine> engines,
        IQueryCostEstimator? estimator = null)
    {
        ArgumentNullException.ThrowIfNull(engines);

        if (engines.Count == 0)
            throw new ArgumentException("A routed engine needs at least one candidate engine.", nameof(engines));

        for (int i = 0; i < engines.Count; i++)
        {
            var candidate = engines[i];

            if (candidate is null)
                throw new ArgumentException($"Engine at index {i} is null.", nameof(engines));

            if (string.IsNullOrWhiteSpace(candidate.Name))
                throw new ArgumentException($"Engine at index {i} has a null or blank name.", nameof(engines));

            if (candidate.Engine is null)
                throw new ArgumentException($"Engine '{candidate.Name}' is null.", nameof(engines));
        }

        _engines = engines;
        _estimator = estimator ?? CheapestByCandidateCountEstimator.Instance;
    }

    /// <summary>The candidate engines, in constructor order.</summary>
    public IReadOnlyList<RoutedEngine> Engines => _engines;

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The router never writes; populate the engines directly.</exception>
    public void Index(IEnumerable<SearchDocument> documents) => throw new NotSupportedException(WriteMessage);

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The router never writes; populate the engines directly.</exception>
    public void Add(SearchDocument document) => throw new NotSupportedException(WriteMessage);

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The router never writes; populate the engines directly.</exception>
    public void Remove(string documentId) => throw new NotSupportedException(WriteMessage);

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The router never writes; populate the engines directly.</exception>
    public void Clear() => throw new NotSupportedException(WriteMessage);

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Search(query.AsSpan(), options);
    }

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Search(ReadOnlySpan<char> query, SearchOptions? options = null)
    {
        var resolved = options ?? SearchOptions.Default;
        return SelectEngine(query, resolved).Engine.Search(query, resolved);
    }

    /// <inheritdoc />
    public FacetedSearchResult SearchWithFacets(
        string query,
        SearchOptions? options = null,
        IReadOnlyList<string>? facetFields = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        return SearchWithFacets(query.AsSpan(), options, facetFields);
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The engine selected for the query does not support facets.</exception>
    public FacetedSearchResult SearchWithFacets(
        ReadOnlySpan<char> query,
        SearchOptions? options = null,
        IReadOnlyList<string>? facetFields = null)
    {
        var resolved = options ?? SearchOptions.Default;
        var selected = SelectEngine(query, resolved);

        if (selected.Engine is not IFacetedSearchEngine faceted)
        {
            throw new NotSupportedException(
                $"Engine '{selected.Name}' selected for this query does not support facets.");
        }

        return faceted.SearchWithFacets(query, resolved, facetFields);
    }

    /// <inheritdoc />
    public IReadOnlyList<DetailedSearchResult> SearchWithDetails(string query, SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        var resolved = options ?? SearchOptions.Default;
        var selected = SelectEngine(query, resolved);

        if (selected.Engine is not IDetailedSearchEngine detailed)
        {
            throw new NotSupportedException(
                $"Engine '{selected.Name}' selected for this query does not support detailed results.");
        }

        return detailed.SearchWithDetails(query, resolved);
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The engine selected for the query does not support explanations.</exception>
    public ScoreExplanation? Explain(string documentId, string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Explain(documentId, query.AsSpan());
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The engine selected for the query does not support explanations.</exception>
    public ScoreExplanation? Explain(string documentId, ReadOnlySpan<char> query)
    {
        ArgumentNullException.ThrowIfNull(documentId);
        var selected = SelectEngine(query, SearchOptions.Default);

        if (selected.Engine is not IExplainableSearchEngine explainable)
        {
            throw new NotSupportedException(
                $"Engine '{selected.Name}' selected for this query does not support explanations.");
        }

        return explainable.Explain(documentId, query);
    }

    /// <inheritdoc />
    public long EstimateCandidateCount(ReadOnlySpan<char> query, SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        long best = long.MaxValue;

        for (int i = 0; i < _engines.Count; i++)
        {
            if (_engines[i].Engine is not IQueryCostProbe probe)
                continue;

            long cost = probe.EstimateCandidateCount(query, options);

            if (cost < 0)
                cost = 0;

            if (cost < best)
                best = cost;
        }

        return best;
    }

    private RoutedEngine SelectEngine(ReadOnlySpan<char> query, SearchOptions options)
    {
        int index = _estimator.Select(_engines, query, options);

        if (index < 0 || index >= _engines.Count)
        {
            throw new InvalidOperationException(
                $"The query estimator returned index {index}, outside the engine range [0, {_engines.Count}).");
        }

        return _engines[index];
    }

    private const string WriteMessage =
        "RoutedSearchEngine is read-only: populate its candidate engines directly.";
}
