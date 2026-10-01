namespace LexiSharp.Core;

/// <summary>
/// Optional capability of an <see cref="ITextSearchEngine"/> that can return, alongside the
/// ranked page, each document compressed to its query-relevant content — the surface a RAG
/// pipeline calls just before handing the page to a generation model.
/// </summary>
/// <remarks>
/// Implementing this interface is opt-in, like <see cref="IDetailedSearchEngine"/>: a plain
/// engine keeps its single <see cref="ITextSearchEngine.Search(string, SearchOptions)"/>
/// contract, and <c>Search</c> returns the full text unchanged — the compressed variant is a
/// separate surface, not a mutation of <see cref="SearchResult.Document"/>. A <c>Search</c>
/// call and a <c>SearchCompressed</c> call on the same engine and options must agree on the
/// ranking (ids and scores); compression only changes the text carried beside it. Detected
/// with pattern matching (<c>engine is ICompressingSearchEngine</c>).
/// </remarks>
public interface ICompressingSearchEngine : ITextSearchEngine
{
    /// <summary>
    /// Same search as <see cref="ITextSearchEngine.Search(string, SearchOptions)"/>, but each
    /// hit carries its text compressed to the query-relevant content (see
    /// <see cref="IContextCompressor"/>).
    /// </summary>
    IReadOnlyList<CompressedHit> SearchCompressed(string query, SearchOptions? options = null);
}