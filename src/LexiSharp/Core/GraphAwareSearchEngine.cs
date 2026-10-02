namespace LexiSharp.Core;

/// <summary>
/// A decorator that layers a <see cref="IKnowledgeGraphBridge"/> over an existing engine:
/// searching and writing are forwarded unchanged, and the <see cref="IGraphSearchEngine"/>
/// surface adds the connected facts — the search-side half of GraphRAG.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SearchWithGraphFacts"/> runs the ordinary search, mines the entities out of the
/// query through the bridge's own extractor (the same call that mines documents, so query and
/// corpus share one entity vocabulary), asks the bridge for the sub-graph around them, and
/// packages the ranked page together with the returned facts and community summaries. The
/// ranking is never touched: the facts are an addition for the application (a RAG context
/// expander, a fact box beside the hits, ...), not a re-scoring.
/// </para>
/// <para>
/// The bridge is async, and this synchronous surface blocks on it — the
/// <see cref="RoutingSearchEngine"/>/<see cref="IQueryRouter"/> trade-off. A query that mines
/// no entities skips the sub-graph request and carries an empty context. The flat
/// <see cref="Search(string, SearchOptions)"/> never touches the bridge, so a graph outage
/// degrades nothing by default; a bridge that throws fails the facts call that asked for it.
/// </para>
/// <para>
/// Writes are forwarded unchanged and this engine owns no graph: persisting the triplets
/// extraction returns is the bridge implementation's business (the application's ETL), not
/// this decorator's.
/// </para>
/// </remarks>
public sealed class GraphAwareSearchEngine : IGraphSearchEngine
{
    /// <summary>Name this engine reports to <see cref="RetrievalTelemetry"/> and its metrics sinks.</summary>
    public const string EngineName = "GraphAwareSearchEngine";

    private readonly ITextSearchEngine _inner;
    private readonly IKnowledgeGraphBridge _bridge;
    private readonly int _maxHops;

    /// <param name="inner">The engine that actually indexes and searches the documents.</param>
    /// <param name="bridge">The knowledge-graph seam (extractor and store, both consumer-provided).</param>
    /// <param name="maxHops">Neighbourhood radius passed to
    /// <see cref="IKnowledgeGraphBridge.QuerySubGraphAsync"/> when hydrating a search.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxHops"/> is not positive.</exception>
    public GraphAwareSearchEngine(
        ITextSearchEngine inner,
        IKnowledgeGraphBridge bridge,
        int maxHops = 2)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(bridge);

        if (maxHops < 1)
            throw new ArgumentOutOfRangeException(nameof(maxHops), maxHops, "maxHops must be at least 1.");

        _inner = inner;
        _bridge = bridge;
        _maxHops = maxHops;
    }

    /// <summary>The knowledge-graph bridge this engine hydrates from.</summary>
    public IKnowledgeGraphBridge Bridge => _bridge;

    /// <inheritdoc />
    public void Index(IEnumerable<SearchDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        _inner.Index(documents);
    }

    /// <inheritdoc />
    public void Add(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _inner.Add(document);
    }

    /// <inheritdoc />
    public void Remove(string documentId) => _inner.Remove(documentId);

    /// <inheritdoc />
    public void Clear() => _inner.Clear();

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        return _inner.Search(query, options);
    }

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Search(ReadOnlySpan<char> query, SearchOptions? options = null) =>
        _inner.Search(query, options);

    /// <inheritdoc />
    public GraphHydratedResults SearchWithGraphFacts(string query, SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);

        var results = _inner.Search(query, options);

        // Entities are mined with the bridge's own extractor — the same one that mined the
        // corpus — so the query is asked in the vocabulary the graph was fed.
        var triplets = _bridge
            .ExtractRelationsAsync(query, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var entities = DistinctEntities(triplets);

        if (entities.Count == 0)
            return new GraphHydratedResults(results, Array.Empty<EntityTriplet>(), Array.Empty<string>());

        var context = _bridge
            .QuerySubGraphAsync(entities, _maxHops, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        return new GraphHydratedResults(results, context.MatchedTriplets, context.CommunitySummaries);
    }

    /// <summary>
    /// The seed entities of a set of triplets: every distinct non-blank subject and object, in
    /// first-appearance order.
    /// </summary>
    private static IReadOnlyList<string> DistinctEntities(IReadOnlyList<EntityTriplet> triplets)
    {
        if (triplets.Count == 0)
            return Array.Empty<string>();

        var entities = new List<string>(triplets.Count * 2);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var triplet in triplets)
        {
            if (!string.IsNullOrWhiteSpace(triplet.Subject) && seen.Add(triplet.Subject))
                entities.Add(triplet.Subject);

            if (!string.IsNullOrWhiteSpace(triplet.Object) && seen.Add(triplet.Object))
                entities.Add(triplet.Object);
        }

        return entities;
    }
}