using LexiSharp.Indexing;

namespace LexiSharp.Core;

/// <summary>
/// One term's posting entries as a document-at-a-time pass needs them: the ordinals and term
/// frequencies of the entries, addressable at the same time as every other term's.
/// </summary>
/// <remarks>
/// <para>
/// The term-at-a-time pass resolves one term, walks its list, and moves on; it can afford to hand the
/// list out as a <see cref="PostingView"/> over whatever memory the index owns. A document-at-a-time
/// pass needs every term's list open at once, so it needs references it can keep in an array and index
/// repeatedly — <see cref="PostingView"/> is a <c>ref struct</c> and cannot be stored, and the arrays
/// behind it are what an index can actually lend. This is that plate: the arrays themselves, not a
/// view over them.
/// </para>
/// <para>
/// The ordinal is the index's own, so it indexes <see cref="IPostingArrayIndex.LengthsByOrdinal"/>
/// directly. The entries ascend by ordinal and hold one entry per document, which is the layout the
/// index's flat postings already have.
/// </para>
/// </remarks>
internal readonly struct PostingArrays
{
    public PostingArrays(int[] ordinals, int[] frequencies)
        : this(ordinals, frequencies, null, null)
    {
    }

    /// <param name="ordinals">Document ordinals, ascending, one per document carrying the term.</param>
    /// <param name="frequencies">Term frequency at the same position in <paramref name="ordinals"/>.</param>
    /// <param name="windowMaxFrequencies">
    /// Per-ordinal-window largest term frequency, one entry per <c>ordinal / BlockSpan</c>, or
    /// <c>null</c> when the index cannot say. Together with
    /// <paramref name="windowMinDocumentLengths"/> this is the true block bound
    /// <see cref="PostingBlocks"/> already writes in every block header.
    /// </param>
    /// <param name="windowMinDocumentLengths">Per-ordinal-window shortest document length, or <c>null</c>.</param>
    public PostingArrays(
        int[] ordinals,
        int[] frequencies,
        int[]? windowMaxFrequencies,
        int[]? windowMinDocumentLengths)
    {
        Ordinals = ordinals;
        Frequencies = frequencies;
        WindowMaxFrequencies = windowMaxFrequencies;
        WindowMinDocumentLengths = windowMinDocumentLengths;
    }

    /// <summary>Document ordinals, ascending, one per document carrying the term.</summary>
    public int[] Ordinals { get; }

    /// <summary>Term frequency at the same position in <see cref="Ordinals"/>.</summary>
    public int[] Frequencies { get; }

    /// <summary>
    /// Largest term frequency per ordinal window, aligned with <see cref="WindowOf"/>, or <c>null</c>.
    /// </summary>
    public int[]? WindowMaxFrequencies { get; }

    /// <summary>Shortest document length per ordinal window, or <c>null</c>.</summary>
    public int[]? WindowMinDocumentLengths { get; }

    /// <summary>
    /// Whether this plate carries what a pruning traversal needs: both halves of the bound, covering
    /// the windows its entries fall in.
    /// </summary>
    public bool HasWindowBounds =>
        WindowMaxFrequencies is not null
        && WindowMinDocumentLengths is not null
        && WindowMaxFrequencies.Length == WindowMinDocumentLengths.Length;

    /// <summary>The ordinal window an ordinal belongs to, the alignment the segment format writes.</summary>
    public static int WindowOf(int ordinal) => ordinal / PostingBlocks.BlockSpan;

    /// <summary>Number of posting entries.</summary>
    public int Count => Ordinals.Length;

    /// <summary>A term the index does not hold: nothing to walk, and nothing to contribute.</summary>
    public static PostingArrays Empty => new([], []);
}

/// <summary>
/// Capability of an index that can hand a whole query's posting arrays out at once, which is what a
/// document-at-a-time traversal needs and a term-at-a-time fold does not.
/// </summary>
/// <remarks>
/// <para>
/// The traversal sums one document at a time and keeps only the page window, so it never needs a
/// score buffer as long as the ordinal space — the buffer whose <c>Array.Clear</c> is measured at
/// 9.1-9.6 µs of a 13-21 µs tail query at 500,000 documents. What it does need is a cursor per term,
/// and a cursor is a position in an array that outlives the walk.
/// </para>
/// <para>
/// Internal, like <see cref="IAccumulatingIndex"/>: an index that cannot lend its postings as arrays
/// — one that decodes them a block at a time, or holds them behind a dictionary of occurrences — does
/// not implement this, and the engine keeps the accumulator for it rather than failing.
/// </para>
/// </remarks>
internal interface IPostingArrayIndex
{
    /// <summary>
    /// Token count per ordinal, in the layout the accumulation loops read, or <c>null</c> when this
    /// index cannot expose one array covering its whole ordinal space.
    /// </summary>
    int[]? LengthsByOrdinal { get; }

    /// <summary>
    /// The posting arrays for <paramref name="term"/>, or <c>false</c> when the term is out of
    /// vocabulary. The arrays stay valid for the lifetime of the index, which is what lets a caller
    /// keep one per query term for the whole traversal.
    /// </summary>
    bool TryResolvePostingArrays(string term, out PostingArrays postings);
}

/// <summary>
/// An <see cref="IPostingArrayIndex"/> that can also resolve a term given as characters, so a query
/// term that is its own normalized form never becomes a string on the cursor path either.
/// </summary>
internal interface ISpanPostingArrayIndex : IPostingArrayIndex, IReadOnlyTextIndex
{
    /// <inheritdoc cref="IPostingArrayIndex.TryResolvePostingArrays(string, out PostingArrays)"/>
    bool TryResolvePostingArrays(ReadOnlySpan<char> term, out PostingArrays postings);
}

/// <summary>
/// The destination of a document-at-a-time traversal: one call per document holding at least one
/// query term, with the score already summed in query order.
/// </summary>
/// <remarks>
/// It is an interface rather than a window because the engine, not the traversal, owns what a
/// candidate costs: the finalisation step, the metadata filter, the facet counter and the
/// minimum-score gate all live on the same object here, exactly as they do on the accumulator's
/// finish loop.
/// </remarks>
internal interface IDocumentScoreSink
{
    /// <summary>Offers the document at <paramref name="ordinal"/> with its summed score.</summary>
    void Score(int ordinal, double score);

    /// <summary>
    /// Whether this sink will ever answer <see cref="CanPrune"/> with true, so a traversal can skip
    /// computing bounds entirely when it will not.
    /// </summary>
    bool Pruning => false;

    /// <summary>
    /// Whether a document whose summed contribution cannot exceed <paramref name="upperBound"/> may be
    /// skipped instead of offered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A predicate rather than a threshold number because the comparison lives in a different space
    /// from the bound: <see cref="Score"/> receives the raw sum and the sink finalises it before the
    /// window sees it, and finalisation is not the identity — BM25 in single precision rounds the sum
    /// to <c>float</c>, which can round <em>up</em>. Comparing a raw bound against a finalised
    /// threshold would therefore skip a document that finalises into the page. The sink owns both
    /// numbers, so the sink does the comparison.
    /// </para>
    /// <para>
    /// The default is false: a traversal may not prune for a sink that has not thought about it.
    /// </para>
    /// </remarks>
    bool CanPrune(double upperBound) => false;
}

/// <summary>One term's contribution to a document, from that document's term frequency and length.</summary>
/// <remarks>
/// The common half of <see cref="IPostingWeight"/> (which also names its term) and
/// <see cref="IResolvedWeight"/> (whose postings the caller already holds), so one traversal can be
/// generic over either.
/// </remarks>
internal interface IWeightFunction
{
    /// <summary>The term's contribution to a document with this frequency and token count.</summary>
    double Weight(int termFrequency, int documentLength);
}

/// <summary>
/// The document-at-a-time scoring pass: one cursor per query term, the smallest current ordinal
/// scored, and the window updated as each document is finished.
/// </summary>
/// <remarks>
/// <para>
/// A document's score is the sum of its terms in <b>query order</b>, written as a write for the first
/// term that holds it and an add for every term after — the same sequence of operations, on the same
/// <see cref="double"/>s, that the term-at-a-time accumulator performs when it folds the terms one
/// list at a time. That is what makes the page bit-identical, ties included, and the parity tests
/// assert it rather than infer it from a matching ranking.
/// </para>
/// <para>
/// Nothing here is allocated at corpus size: the cursor array is one <c>int</c> per query term, and
/// the only per-document state is the sum being built. The price of that is the per-document scan of
/// the cursors, which is why the engine takes this path when the corpus is large enough for the
/// accumulator's fixed cost to matter and leaves small corpora on the term-at-a-time pass.
/// </para>
/// </remarks>
internal static class DocumentAtATime
{
    /// <summary>
    /// Scores every document holding at least one of <paramref name="postings"/>' terms, in ascending
    /// ordinal order, and offers each to <paramref name="sink"/>.
    /// </summary>
    /// <param name="weights">One per posting array, in query order.</param>
    /// <param name="postings">The query terms' posting arrays, aligned with <paramref name="weights"/>.</param>
    /// <param name="lengths">Token count per ordinal; must cover every ordinal in the postings.</param>
    /// <param name="sink">Receives each document's ordinal and score.</param>
    public static void Score<TWeight>(
        ReadOnlySpan<TWeight> weights,
        Span<PostingArrays> postings,
        int[] lengths,
        IDocumentScoreSink sink)
        where TWeight : struct, IWeightFunction
    {
        int terms = weights.Length;

        if (terms == 0)
            return;

        var cursors = new int[terms];

        // Pruning needs every term that holds anything to carry its window bounds; a term with no
        // entries contributes nothing and needs none.
        bool prune = sink.Pruning;

        for (int t = 0; prune && t < terms; t++)
        {
            if (postings[t].Count > 0 && !postings[t].HasWindowBounds)
                prune = false;
        }

        while (true)
        {
            int smallest = int.MaxValue;

            for (int t = 0; t < terms; t++)
            {
                int[] ordinals = postings[t].Ordinals;
                int at = cursors[t];

                if (at < ordinals.Length && ordinals[at] < smallest)
                    smallest = ordinals[at];
            }

            if (smallest == int.MaxValue)
                return;

            // Before scoring: every term holding this document is bounded by its window's largest
            // frequency and shortest document, so the sum of those contributions is an upper bound on
            // the score. The sink compares it against the page, in the space its own finalisation
            // produces; strictly, because an equal score can still be ordered by its identifier.
            if (prune && sink.CanPrune(UpperBound(weights, postings, cursors, smallest)))
            {
                for (int t = 0; t < terms; t++)
                {
                    if (cursors[t] < postings[t].Count && postings[t].Ordinals[cursors[t]] == smallest)
                        cursors[t]++;
                }

                continue;
            }

            double score = 0;
            bool first = true;

            for (int t = 0; t < terms; t++)
            {
                PostingArrays term = postings[t];
                int at = cursors[t];

                if (at >= term.Count || term.Ordinals[at] != smallest)
                    continue;

                double contribution = weights[t].Weight(term.Frequencies[at], lengths[smallest]);

                // The accumulator writes on a document's first touch and adds on every later one. The
                // difference matters for a contribution of zero, and copying the behaviour is cheaper
                // than proving no weight can ever return one.
                if (first)
                {
                    score = contribution;
                    first = false;
                }
                else
                {
                    score += contribution;
                }

                cursors[t] = at + 1;
            }

            sink.Score(smallest, score);
        }
    }

    /// <summary>
    /// An upper bound on the score of <paramref name="ordinal"/> from the window bounds of the terms
    /// that hold it, or positive infinity when a window is out of the bound's range.
    /// </summary>
    /// <remarks>
    /// A term whose cursor sits past the ordinal does not hold it — postings ascend and carry one entry
    /// per document — so it contributes nothing and is not bounded by anything. Every contribution is
    /// non-negative, so the sum is monotone under addition and rounding cannot make it fall below the
    /// score the same terms produce.
    /// </remarks>
    private static double UpperBound<TWeight>(
        ReadOnlySpan<TWeight> weights,
        Span<PostingArrays> postings,
        int[] cursors,
        int ordinal)
        where TWeight : struct, IWeightFunction
    {
        int window = PostingArrays.WindowOf(ordinal);
        double bound = 0;

        for (int t = 0; t < weights.Length; t++)
        {
            PostingArrays term = postings[t];
            int at = cursors[t];

            if (at >= term.Count || term.Ordinals[at] != ordinal)
                continue;

            if (term.WindowMaxFrequencies is not { } frequencies || window >= frequencies.Length)
                return double.PositiveInfinity;

            bound += weights[t].Weight(frequencies[window], term.WindowMinDocumentLengths![window]);
        }

        return bound;
    }
}
