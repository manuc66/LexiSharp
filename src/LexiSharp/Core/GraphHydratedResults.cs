namespace LexiSharp.Core;

/// <summary>
/// The outcome of <see cref="IGraphSearchEngine.SearchWithGraphFacts"/>: the ordinary ranked
/// page plus the facts the <see cref="IKnowledgeGraphBridge"/> returned for the query's
/// entities.
/// </summary>
/// <param name="Results">
/// Exactly the page <see cref="ITextSearchEngine.Search(string, SearchOptions)"/> would return
/// for the same query and options — the ranking is untouched; the facts are an addition.
/// </param>
/// <param name="Facts">The triplets the graph connected to the query's entities.</param>
/// <param name="CommunitySummaries">
/// The community summaries the graph stands above the facts, when it provides any.
/// </param>
public sealed record GraphHydratedResults(
    IReadOnlyList<SearchResult> Results,
    IReadOnlyList<EntityTriplet> Facts,
    IReadOnlyList<string> CommunitySummaries);