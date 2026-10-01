namespace LexiSharp.Core;

/// <summary>
/// Optional capability of an <see cref="ITextSearchEngine"/> that knows the hierarchical
/// structure its documents live in — the « structure-aware retriever » seam. Flat retrieval
/// still finds the precise node; this surface answers the navigation question that follows:
/// "which ancestors (sections, chapters, books) does this hit belong to?"
/// </summary>
/// <remarks>
/// Implementing this interface is opt-in, like <see cref="IFacetedSearchEngine"/>: a plain
/// engine keeps its single <see cref="ITextSearchEngine.Search(string, SearchOptions)"/>
/// contract. The ancestor chain comes from a <see cref="DocumentHierarchy"/>, which the
/// engine decorator holds; writing and searching behave exactly as the wrapped engine's.
/// <para>
/// Detected with pattern matching (<c>engine is IStructureAwareSearchEngine</c>), the same
/// way the other capability surfaces are.
/// </para>
/// </remarks>
public interface IStructureAwareSearchEngine : ITextSearchEngine
{
    /// <summary>
    /// The chain of ancestor ids for <paramref name="documentId"/>, immediate parent first,
    /// ending at the root. Empty for roots and for ids unknown to the hierarchy.
    /// </summary>
    IReadOnlyList<string> GetAncestorIds(string documentId);

    /// <summary>
    /// The chain of ancestor <b>documents</b> for <paramref name="documentId" /> — the overview
    /// side of the navigation: the texts of the sections, chapters or books a hit belongs to.
    /// An ancestor the engine cannot resolve is omitted.
    /// </summary>
    /// <remarks>
    /// Resolving ids to documents needs the caller's ledger (no <see cref="ITextSearchEngine"/>
    /// contract returns a document by id), so an engine without a resolver throws
    /// <see cref="NotSupportedException"/> — call <see cref="GetAncestorIds"/> instead.
    /// </remarks>
    IReadOnlyList<SearchDocument> GetAncestors(string documentId);
}