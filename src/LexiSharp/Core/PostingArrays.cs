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

    /// <summary>The first ordinal of the window after the one <paramref name="ordinal"/> falls in.</summary>
    public static int WindowEndOf(int ordinal) => (WindowOf(ordinal) + 1) * PostingBlocks.BlockSpan;

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
    /// Whether the sink has a threshold yet, which is false while its page is not full.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="CanPrune"/> because it is the cheap half: a traversal that asked the
    /// bound first and the decision second paid a contribution per term per candidate for a page that
    /// could not be beaten yet. On a 100,000-document corpus that made the three-term tail query 2.6×
    /// slower, because a tail query has fewer candidates than the page holds and every one of them was
    /// bounded to no purpose. A <c>double</c> threshold would have answered the same question, but a
    /// number invites the comparison this interface exists to prevent.
    /// </remarks>
    bool HasThreshold => false;

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

        // The bound of a term does not change from one range to the next, so it is evaluated once per
        // query instead of once per term per range. A window bound is a contribution, and a term's
        // largest one is the contribution at its largest frequency and shortest document taken over its
        // windows: the weight function is monotone in the first and anti-monotone in the second, so that
        // pair is the maximum. The sum of these over a range is then an addition each, not a division.
        // On the stack when the query is small, which it always is, so paying for the bounds does not
        // allocate. Computed on the first range decision rather than up front: a tail query fills its
        // page at its last candidate, and paying for ten terms before that was a measurable regression
        // on the one workload where the mechanism is already known to be useless.
        Span<double> bounds = prune ? stackalloc double[terms] : [];
        bool boundsReady = false;

        // The decision is made once per range, not once per document: a block bound is what the name
        // says, and recomputing it for every document inside the same block was three passes over the
        // terms per document for a verdict that could not change. The cached verdict stays sound while
        // the smallest ordinal is still inside the range it was computed for, because cursors only
        // advance — so the set of contributing terms only shrinks and the old sum only overstates.
        int rangeEnd = -1;
        bool rangeSkippable = false;

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

            // Block-max skipping: prove a whole range of ordinals cannot reach the page, then advance
            // past it without visiting the documents inside. The range ends at the earliest window end
            // among the live terms, so every term with a cursor at or before that end is still in the
            // window its cursor points into, and its window bound is a bound on any document there. A
            // term whose cursor sits past the end holds nothing inside the range at all.
            if (prune && sink.HasThreshold)
            {
                if (smallest >= rangeEnd)
                {
                    if (!boundsReady)
                    {
                        GlobalBounds(weights, postings, lengths, bounds);
                        boundsReady = true;
                    }

                    rangeEnd = int.MaxValue;

                    for (int t = 0; t < terms; t++)
                    {
                        int at = cursors[t];

                        if (at >= postings[t].Count)
                            continue;

                        int end = PostingArrays.WindowEndOf(postings[t].Ordinals[at]);

                        if (end < rangeEnd)
                            rangeEnd = end;
                    }

                    rangeSkippable = sink.CanPrune(RangeBound(bounds, postings, cursors, rangeEnd));
                }

                if (rangeSkippable)
                {
                    for (int t = 0; t < terms; t++)
                    {
                        PostingArrays term = postings[t];
                        int at = cursors[t];

                        while (at < term.Count && term.Ordinals[at] < rangeEnd)
                            at++;

                        cursors[t] = at;
                    }

                    continue;
                }
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
    /// Each term's largest single contribution, over every window of its postings.
    /// </summary>
    /// <remarks>
    /// Evaluated once per query. It is looser than the window the cursor currently sits in — which is
    /// what a block bound would use — and the measurement says that does not matter: on five query
    /// shapes the tighter bound never fell below the page either, and it cost a contribution
    /// evaluation per term per range, which is what made the traversal twice as slow as the pass it
    /// prunes. The loose bound buys the same zero skips for an addition.
    /// </remarks>
    private static void GlobalBounds<TWeight>(
        ReadOnlySpan<TWeight> weights,
        Span<PostingArrays> postings,
        int[] lengths,
        Span<double> bounds)
        where TWeight : struct, IWeightFunction
    {
        for (int t = 0; t < weights.Length; t++)
        {
            PostingArrays term = postings[t];

            if (term.Count == 0 || term.WindowMaxFrequencies is not { } frequencies || frequencies.Length == 0)
                continue;

            // Two ways to the same pair, and the corpus decides which is cheaper: a rare term has a few
            // entries spread over hundreds of mostly-empty windows, a head term has tens of thousands of
            // entries in a few windows. Scanning the wrong one is the difference between 9 iterations and
            // 782 — which, on a two-microsecond query, was a 6x regression when this scanned windows
            // unconditionally.
            int peak;
            int shortest;

            if (term.Count <= frequencies.Length)
            {
                peak = 0;
                shortest = int.MaxValue;

                for (int at = 0; at < term.Count; at++)
                {
                    if (term.Frequencies[at] > peak)
                        peak = term.Frequencies[at];

                    int length = lengths[term.Ordinals[at]];

                    if (length > 0 && length < shortest)
                        shortest = length;
                }
            }
            else
            {
                peak = Max(frequencies);
                shortest = int.MaxValue;

                for (int window = 0; window < frequencies.Length; window++)
                {
                    if (frequencies[window] == 0)
                        continue;

                    int length = term.WindowMinDocumentLengths![window];

                    if (length > 0 && length < shortest)
                        shortest = length;
                }
            }

            bounds[t] = shortest == int.MaxValue ? 0 : weights[t].Weight(peak, shortest);
        }
    }

    /// <summary>The largest window frequency in an array: the term's peak.</summary>
    private static int Max(int[] frequencies)
    {
        int max = 0;

        for (int i = 0; i < frequencies.Length; i++)
        {
            if (frequencies[i] > max)
                max = frequencies[i];
        }

        return max;
    }

    /// <summary>
    /// An upper bound on the score of any document below <paramref name="windowEnd"/>: the sum of the
    /// precomputed term bounds over the terms whose cursor sits before that end.
    /// </summary>
    private static double RangeBound(
        Span<double> bounds,
        Span<PostingArrays> postings,
        int[] cursors,
        int windowEnd)
    {
        double bound = 0;

        for (int t = 0; t < bounds.Length; t++)
        {
            int at = cursors[t];

            if (at >= postings[t].Count || postings[t].Ordinals[at] >= windowEnd)
                continue;

            bound += bounds[t];
        }

        return bound;
    }
}
