namespace LexiSharp.Core;

/// <summary>
/// Internal capability for an index that can hand a query plan its inverted lists to walk, so the
/// plan can score a whole query term-at-a-time instead of document-at-a-time.
/// </summary>
/// <remarks>
/// <para>
/// The document-at-a-time loop this replaces asks the index for <c>(document, term)</c>
/// frequencies, and every one of those is a posting-list lookup keyed on a document id — a string
/// hash. With <c>m</c> query terms over a candidate set of <c>n</c> documents that is
/// <c>2 · m · n</c> string hashes (one to resolve the term's list, one to resolve the document
/// inside it), and the term half of that is the same lookup repeated for the same <c>m</c> values
/// on every one of the <c>n</c> documents.
/// </para>
/// <para>
/// This interface lets the index expose the loop instead: the plan supplies a
/// <see cref="IPostingWeight"/> per term and the index walks each term's list once, folding into a
/// <see cref="ScoreAccumulator"/>. The work becomes proportional to the posting entries the query
/// has, with no hashing in it at all.
/// </para>
/// <para>
/// Internal on purpose, for the same reason as <c>IUnorderedCandidateIndex</c>: it is a
/// performance capability, not a promise to library consumers. An index that does not implement it
/// keeps working through the per-document path, and a scorer that has no separable contribution
/// keeps the per-document path even on an index that does.
/// </para>
/// </remarks>
internal interface IAccumulatingIndex
{
    /// <summary>
    /// Number of ordinal slots the index uses, including any freed by removals. Every live
    /// document's ordinal is below this, so an accumulator rented at this size covers the corpus.
    /// </summary>
    int OrdinalSpace { get; }

    /// <summary>
    /// The document at <paramref name="ordinal"/>, or <c>null</c> when that slot is free.
    /// </summary>
    SearchDocument? DocumentAt(int ordinal);

    /// <summary>
    /// Folds <paramref name="weight"/> into <paramref name="accumulator"/> for every document
    /// containing <see cref="IPostingWeight.Term"/>. A no-op when the term is out of vocabulary.
    /// </summary>
    /// <param name="weight">The term's contribution function, specialized and inlined by the JIT.</param>
    /// <param name="accumulator">Destination buffer; must be rented at <see cref="OrdinalSpace"/>.</param>
    void Accumulate<TWeight>(TWeight weight, ScoreAccumulator accumulator)
        where TWeight : struct, IPostingWeight;
}
