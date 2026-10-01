namespace LexiSharp.Core;

/// <summary>
/// Optional capability of an <see cref="ITextSearchEngine"/> that can return, alongside the
/// ranked page, the knowledge-graph facts connected to the query — the search-side half of
/// GraphRAG.
/// </summary>
/// <remarks>
/// Implementing this interface is opt-in, like <see cref="IDetailedSearchEngine"/>: a plain
/// engine keeps its single <see cref="ITextSearchEngine.Search(string, SearchOptions)"/>
/// contract. A <c>Search</c> call and a <c>SearchWithGraphFacts</c> call on the same engine
/// and options must agree on the returned results — the facts variant only adds the connected
/// facts. Detected with pattern matching (<c>engine is IGraphSearchEngine</c>).
/// </remarks>
public interface IGraphSearchEngine : ITextSearchEngine
{
    /// <summary>
    /// Same search as <see cref="ITextSearchEngine.Search(string, SearchOptions)"/>, plus the
    /// facts the <see cref="IKnowledgeGraphBridge"/> returns for the entities mined from the
    /// query.
    /// </summary>
    GraphHydratedResults SearchWithGraphFacts(string query, SearchOptions? options = null);
}