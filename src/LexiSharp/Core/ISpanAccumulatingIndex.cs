namespace LexiSharp.Core;

/// <summary>
/// An accumulating index that can resolve a term given as characters rather than as a string, so a
/// query term the source already holds never has to be materialized to be scored.
/// </summary>
/// <remarks>
/// <see cref="IAccumulatingIndex"/> resolves the term it is handed through
/// <see cref="IPostingWeight.Term"/>, which is a string, and a string is what a term that is its own
/// normalized form should not need. This capability is separate rather than added to
/// <see cref="IAccumulatingIndex"/> because the decorators that implement that interface forward to
/// an inner index and do not resolve postings themselves: a default implementation would be a
/// capability they only appear to have, and the engine asks for the capability instead.
/// </remarks>
internal interface ISpanAccumulatingIndex : IAccumulatingIndex, IReadOnlyTextIndex
{
    /// <summary>
    /// Document frequency of <paramref name="term"/>, or <c>0</c> when it is out of vocabulary.
    /// </summary>
    int SpanDocumentFrequency(ReadOnlySpan<char> term);

    /// <summary>
    /// Resolves <paramref name="term"/> to its postings, the same ones
    /// <see cref="IAccumulatingIndex.Accumulate{TWeight}"/> would fold for that term. Returns
    /// <c>false</c>, and leaves <paramref name="postings"/> default, when the term is out of
    /// vocabulary.
    /// </summary>
    /// <param name="term">The term to resolve.</param>
    /// <param name="withPositions">
    /// Whether to fill <see cref="PostingView.Positions"/>. Only a segment write reads them, and
    /// materializing them costs a walk of the term's occurrences, so the accumulation path — which
    /// never touches the field — asks for them not to be.
    /// </param>
    /// <param name="postings">The resolved postings.</param>
    bool TryResolvePostings(ReadOnlySpan<char> term, bool withPositions, out PostingView postings);

    /// <summary>
    /// Folds <paramref name="weight"/> over the posting entries of an already-resolved term, one
    /// entry at a time, in the same order and with the same arithmetic as
    /// <see cref="IAccumulatingIndex.Accumulate{TWeight}"/>.
    /// </summary>
    void AccumulateResolved<TWeight>(in PostingView postings, TWeight weight, ScoreAccumulator accumulator)
        where TWeight : struct, IResolvedWeight;
}
