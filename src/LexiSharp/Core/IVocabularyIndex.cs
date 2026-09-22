namespace LexiSharp.Core;

/// <summary>
/// Capability for an index that can enumerate its vocabulary — the seam prefix/fuzzy query
/// expansion reads at search time.
/// </summary>
/// <remarks>
/// <para>
/// Implemented by <see cref="LexiSharp.Indexing.InMemoryTextIndex"/> over its postings keys.
/// <see cref="LexiSharp.Ranking.RankedTextSearchEngine"/> uses it to resolve
/// <c>term*</c>/<c>term~N</c> atoms against the distinct indexed terms.
/// </para>
/// <para>
/// An index that cannot answer cheaply simply does not implement this interface; the engine
/// falls back to the atom's literal base term (the behavior of a query without operators).
/// </para>
/// </remarks>
public interface IVocabularyIndex : ITextIndex
{
    /// <summary>All distinct terms known to the index, in arbitrary order.</summary>
    IEnumerable<string> Vocabulary { get; }
}
