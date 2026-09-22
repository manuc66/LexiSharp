namespace LexiSharp.Core;

/// <summary>
/// Capability for an index that can enumerate only the documents containing at least one of
/// the given query terms, instead of scanning the whole corpus.
/// </summary>
/// <remarks>
/// <para>
/// Implemented by <see cref="LexiSharp.Indexing.InMemoryTextIndex"/> over its inverted lists.
/// <see cref="LexiSharp.Ranking.RankedTextSearchEngine"/> uses it to score only candidate
/// documents when the active scorer honors the « zero without shared terms » contract of
/// <see cref="ITermOverlapScorer"/>, which is a pure performance path: the scored document set,
/// the scores and the result order are identical to a full scan.
/// </para>
/// <para>
/// An index that cannot answer cheaply simply does not implement this interface; the engine
/// falls back to iterating <see cref="ITextIndex.Documents"/>.
/// </para>
/// </remarks>
public interface ICandidateIndex : ITextIndex
{
    /// <summary>
    /// Union of the documents containing at least one of the given terms, each document
    /// appearing at most once and in the same relative order as <see cref="ITextIndex.Documents"/>.
    /// </summary>
    IEnumerable<SearchDocument> GetCandidateDocuments(IReadOnlyList<string> terms);
}
