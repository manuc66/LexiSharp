namespace LexiSharp.Core;

/// <summary>
/// Optional capability of an <see cref="ITextSearchEngine"/> that can return, alongside the merged
/// ranking, the per-source score breakdown that fed it (see <see cref="DetailedSearchResult.Contributions"/>).
/// </summary>
/// <remarks>
/// Implementing this interface is opt-in — plain engines keep their single
/// <see cref="ITextSearchEngine.Search(string, SearchOptions)"/> contract. It is naturally implemented by federated
/// engines (<c>HybridTextSearchEngine</c>) that know each delegate engine's own score; detecting
/// it is done with pattern matching (<c>engine is IDetailedSearchEngine</c>) or by reading an
/// interface-typed field. A <c>Search</c> call and a <c>SearchWithDetails</c> call on the same
/// engine must agree on the returned <see cref="SearchResult.Score"/> ordering — the details
/// variant only adds diagnostics.
/// </remarks>
public interface IDetailedSearchEngine
{
    /// <summary>
    /// Same search as <see cref="ITextSearchEngine.Search(string, SearchOptions)"/>, but every result also carries the
    /// per-source score breakdown that fed the final rank.
    /// </summary>
    IReadOnlyList<DetailedSearchResult> SearchWithDetails(string query, SearchOptions? options = null);
}