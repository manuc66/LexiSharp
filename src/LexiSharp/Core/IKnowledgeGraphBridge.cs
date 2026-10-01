namespace LexiSharp.Core;

/// <summary>
/// Contracts for connecting the engines to a knowledge graph — the GraphRAG seam.
/// <b>LexiSharp never runs a model or a graph itself</b>: relation extraction is the model
/// (a local ONNX model, an LLM call, a rules engine) and the graph store is the consumer's
/// (Apache AGE over PostgreSQL, Neo4j, ...). This interface only describes the two calls the
/// application's graph makes available.
/// </summary>
/// <remarks>
/// <para>
/// Two operations, one vocabulary. <see cref="ExtractRelationsAsync"/> mines the facts out of
/// a text — a document at index time (the application's ETL), or a query at search time, where
/// the resulting subjects and objects seed the sub-graph request. Using the same method for
/// both keeps the entity vocabulary aligned between what was indexed and what is asked.
/// </para>
/// <para>
/// Extraction <b>returns</b> the triplets; persisting them in the graph is the
/// implementation's own business (this contract contains no write). The engine-side decorator
/// (<see cref="GraphAwareSearchEngine"/>) is search-time only: it neither writes to the graph
/// nor touches the pages, so a graph outage degrades nothing by default.
/// </para>
/// <para>
/// The seam is asynchronous for the same reason <see cref="IQueryRouter"/> is a model-backed
/// call is naturally async; a synchronous caller blocks on the task, as
/// <see cref="RoutingSearchEngine"/> does on its router.
/// </para>
/// </remarks>
public interface IKnowledgeGraphBridge
{
    /// <summary>Human readable name of the underlying extractor/store, e.g. <c>"age-v1"</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Extracts the subject-verb-object facts <paramref name="text"/> mentions. The same call
    /// serves index-time entity mining and search-time query mining.
    /// </summary>
    /// <param name="text">A document's or a query's text.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<EntityTriplet>> ExtractRelationsAsync(
        string text,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the sub-graph around <paramref name="entities"/>, up to
    /// <paramref name="maxHops"/> hops away, with the community summaries available above it.
    /// </summary>
    /// <param name="entities">The seed entities; typically mined from a query.</param>
    /// <param name="maxHops">Neighbourhood radius, in relations. Must be positive.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<GraphContext> QuerySubGraphAsync(
        IReadOnlyCollection<string> entities,
        int maxHops = 2,
        CancellationToken cancellationToken = default);
}