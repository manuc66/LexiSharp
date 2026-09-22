namespace LexiSharp.Core;

/// <summary>
/// Optional capability of an <see cref="ITextSearchEngine"/> that can explain why a document
/// received the score it did for a query, when its scorer is an <see cref="IScoreExplainer"/>.
/// </summary>
/// <remarks>
/// Implementing this interface is opt-in. It exists so capability-preserving decorators (e.g.
/// <see cref="RoutedSearchEngine"/>) can be probed the same way as
/// <see cref="IFacetedSearchEngine"/>/<see cref="IDetailedSearchEngine"/>; the stock
/// <c>RankedTextSearchEngine</c> implements it.
/// </remarks>
public interface IExplainableSearchEngine
{
    /// <summary>
    /// Explains the document's score, or returns <c>null</c> when the active scorer cannot
    /// explain itself or the document is unknown.
    /// </summary>
    /// <param name="documentId">Id of the document to explain.</param>
    /// <param name="query">The raw query, parsed and resolved like <see cref="ITextSearchEngine.Search(string, SearchOptions)"/>.</param>
    ScoreExplanation? Explain(string documentId, string query);

    /// <summary>
    /// Span-first counterpart of <see cref="Explain(string, string)"/>; the default implementation
    /// copies the span into a string and forwards.
    /// </summary>
    /// <param name="documentId">Id of the document to explain.</param>
    /// <param name="query">The raw query, parsed and resolved like <see cref="ITextSearchEngine.Search(ReadOnlySpan{char}, SearchOptions)"/>.</param>
    ScoreExplanation? Explain(string documentId, ReadOnlySpan<char> query) =>
        Explain(documentId, query.ToString());
}
