using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Ranking;

/// <summary>
/// Okapi BM25 relevance — the standard non-semantic ranking model for full-text search.
/// It blends term saturation (<c>k1</c>) with document-length normalization (<c>b</c>).
/// </summary>
/// <remarks>
/// <c>score(q,d) = Σ_t idf(t) * tf(t,d) * (k1 + 1) / (tf(t,d) + k1 * (1 − b + b · |d| / avgdl))</c>
/// with <c>idf(t) = ln(1 + (N − df(t) + 0.5) / (df(t) + 0.5))</c>.
/// <para>
/// The scorer also implements <see cref="IScoreExplainer"/>, so every ranking decision can be
/// audited term by term (see <see cref="Explain"/>).
/// </para>
/// <para>
/// Three of its inputs are conventions rather than mathematics, and each is an option because a
/// published figure or a recorded run was produced under one of them and not the other:
/// <see cref="IReadOnlyTextIndex.StatisticDocumentCount"/> fixes which documents the <c>N</c> of the idf counts,
/// <c>saturationConstant</c> fixes whether the numerator carries <c>k1 + 1</c>, and
/// <c>arithmetic</c> fixes the precision. All three default to this library's own conventions.
/// </para>
/// </remarks>
public sealed class Bm25Scorer : IScoreExplainer, ITermOverlapScorer, IQueryPlannableScorer
{
    private readonly double _k1;
    private readonly double _b;
    private readonly QueryTermWeighting _queryTerms;

    /// <summary>
    /// The saturation constant <c>k1 + 1</c>, or 1 when the caller asked for the formula without it.
    /// It scales every score of a query by the same factor, so it changes no ranking — but it changes
    /// every score, and a score is what gets compared.
    /// </summary>
    private readonly double _saturation;

    private readonly Bm25Arithmetic _arithmetic;

    /// <summary>
    /// The single-precision reciprocal table and the average length it was built for, held together so
    /// they cannot be read apart.
    /// </summary>
    /// <remarks>
    /// The table is what makes the single-precision path differ from the same formula evaluated on the
    /// spot: a reciprocal rounded to single precision carries its rounding into every document of that
    /// length, where evaluating the expression does not.
    /// <para>
    /// It cannot be built in the constructor, because every entry depends on the corpus's average
    /// document length and the scorer does not see the index until it scores something.
    /// </para>
    /// <para>
    /// It is read through <see cref="Volatile"/> and written under a lock, because one scorer is shared
    /// by every query an engine runs and those run concurrently. Two ordinary fields would be a data
    /// race: one thread could publish a reference to an array another is still filling. Holding the two
    /// together in one immutable object and publishing it once removes that — the reference and the
    /// length it belongs to are read or replaced, never written piecemeal.
    /// </para>
    /// </remarks>
    private NormInverseTable? _normInverse;

    private readonly Lock _normInverseLock = new();

    /// <param name="k1">Term-frequency saturation: higher values let frequent terms contribute more.</param>
    /// <param name="b">Document-length normalization, in <c>[0, 1]</c>. <c>0</c> disables normalization.</param>
    /// <param name="queryTermWeighting">
    /// How a term repeated in the query is treated. Default
    /// <see cref="QueryTermWeighting.Distinct"/>. Pass
    /// <see cref="QueryTermWeighting.QueryFrequency"/> to reproduce a published BM25 figure, which
    /// counts one scoring clause per query-token occurrence.
    /// </param>
    /// <param name="saturationConstant">
    /// Whether the numerator carries <c>k1 + 1</c> (the default) or 1. Most statements of BM25 include it
    /// and one implementation of it does not: that implementation scores
    /// <c>Σ_t qf(t) · idf(t) · tf / (tf + k1(1 − b + b·dl/avgdl))</c> with the boost on the query side,
    /// which is the same ranking multiplied by <c>1 / (k1 + 1)</c>. Pass <c>false</c> to reproduce a figure
    /// or a run file produced there — the scores then match, not only the order.
    /// </param>
    /// <param name="arithmetic">
    /// The precision the arithmetic is carried out in. <see cref="Bm25Arithmetic.Double"/> — the
    /// default — computes in double precision. <see cref="Bm25Arithmetic.SinglePrecision"/> reproduces
    /// the single-precision per-term contributions and reciprocal table one implementation uses, which is
    /// what a raw-score comparison against it needs; see that value's remarks for why it is a trade and
    /// not an improvement.
    /// </param>
    public Bm25Scorer(
        double k1 = 1.5,
        double b = 0.75,
        QueryTermWeighting queryTermWeighting = QueryTermWeighting.Distinct,
        bool saturationConstant = true,
        Bm25Arithmetic arithmetic = Bm25Arithmetic.Double)
    {
        if (double.IsNaN(k1) || double.IsInfinity(k1) || k1 < 0)
            throw new ArgumentOutOfRangeException(nameof(k1), k1, "k1 must be non-negative and finite.");
        if (double.IsNaN(b) || double.IsInfinity(b) || b is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(b), b, "b must be within [0, 1] and finite.");

        _k1 = k1;
        _b = b;
        _queryTerms = queryTermWeighting;
        _saturation = saturationConstant ? k1 + 1.0 : 1.0;
        _arithmetic = arithmetic;
    }

    /// <summary>Builds a scorer from a preset or tuned <see cref="Bm25Parameters"/> profile.</summary>
    public Bm25Scorer(Bm25Parameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        if (double.IsNaN(parameters.K1) || double.IsInfinity(parameters.K1) || parameters.K1 < 0)
            throw new ArgumentOutOfRangeException(nameof(parameters), parameters, "k1 must be non-negative and finite.");
        if (double.IsNaN(parameters.B) || double.IsInfinity(parameters.B) || parameters.B is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(parameters), parameters, "b must be within [0, 1] and finite.");

        _k1 = parameters.K1;
        _b = parameters.B;
        _queryTerms = QueryTermWeighting.Distinct;
        _saturation = parameters.K1 + 1.0;
        _arithmetic = Bm25Arithmetic.Double;
    }

    /// <inheritdoc />
    public string Name => "BM25";

    /// <inheritdoc />
    public double Score(string documentId, IReadOnlyList<string> queryTerms, IReadOnlyTextIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(queryTerms);

        int documentCount = index.StatisticDocumentCount;
        int documentLength = index.DocumentLength(documentId);
        double averageLength = index.AverageDocumentLength;

        if (documentCount == 0 || documentLength == 0 || averageLength <= 0)
            return 0;

        var shape = new Bm25Shape(_k1, _b, _saturation, averageLength, Table(averageLength));

        // Loop-invariant: the document is the same for every term.
        double normalization = shape.Normalization(documentLength);
        double score = 0;

        // Indexed loop over the IReadOnlyList<string> interface: a foreach would box the
        // enumerator once per scored document.
        var terms = Terms(queryTerms);

        for (int i = 0; i < terms.Count; i++)
        {
            string term = terms[i];
            int tf = index.TermFrequency(documentId, term);

            if (tf == 0)
                continue;

            int df = index.DocumentFrequency(term);

            double idf = Math.Log(1.0 + ((documentCount - df + 0.5) / (df + 0.5)));

            score += shape.Contribution(idf, tf, documentLength, normalization);
        }

        return shape.Round(score);
    }

    /// <summary>
    /// The query terms as this scorer reads them: deduplicated, or kept whole so a repeated term is
    /// scored once per occurrence.
    /// </summary>
    private IReadOnlyList<string> Terms(IReadOnlyList<string> queryTerms)
    {
        if (_queryTerms == QueryTermWeighting.QueryFrequency)
            return queryTerms;

        // The engine already deduplicated, and DistinctTermList is the marker saying so. Deduplicating
        // again would hash every term to arrive at the list this was handed.
        return queryTerms is DistinctTermList ? queryTerms : TermDeduplicator.Distinct(queryTerms);
    }

    ISearchQueryPlan IQueryPlannableScorer.CreatePlan(IReadOnlyList<string> queryTerms, IReadOnlyTextIndex index)
    {
        ArgumentNullException.ThrowIfNull(queryTerms);
        ArgumentNullException.ThrowIfNull(index);

        // The plan builds its own table rather than taking the scorer's: it is given the index and so
        // knows the average length at construction, and a plan outlives the query that made it.
        double averageLength = index.AverageDocumentLength;
        float[]? table = _arithmetic == Bm25Arithmetic.SinglePrecision
            ? BuildNormInverseTable(_k1, _b, averageLength)
            : null;

        return new Bm25QueryPlan(
            Terms(queryTerms), index, new Bm25Shape(_k1, _b, _saturation, averageLength, table));
    }

    ISpanAccumulatingQueryPlan? IQueryPlannableScorer.CreateSpanPlan(
        ReadOnlySpan<NormalizedTerm> terms,
        ReadOnlySpan<char> source,
        IReadOnlyTextIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);

        // Only an index that resolves a term by span can run this plan, and the caller asks for the
        // capability before it asks the scorer.
        if (index is not ISpanAccumulatingIndex spanIndex)
            return null;

        // Single precision collapses a repeated query term into one clause whose weight carries the
        // repetition, and that fold is a property of the string plan's term list. Reproducing it here
        // would mean reimplementing it over slices to serve one reproduction, so this plan declines
        // and the string path takes that query.
        if (_arithmetic == Bm25Arithmetic.SinglePrecision)
            return null;

        double averageLength = index.AverageDocumentLength;
        int documentCount = index.StatisticDocumentCount;
        var idf = new double[terms.Length];

        for (int i = 0; i < terms.Length; i++)
        {
            if (documentCount == 0)
                continue;

            int df = spanIndex.SpanDocumentFrequency(terms[i].View(source));
            idf[i] = Math.Log(1.0 + ((documentCount - df + 0.5) / (df + 0.5)));
        }

        return new Bm25SpanQueryPlan(
            new Bm25Shape(_k1, _b, _saturation, averageLength, null),
            idf,
            distinct: _queryTerms != QueryTermWeighting.QueryFrequency);
    }

    /// <summary>Largest stored length the reciprocal table is indexed by.</summary>
    /// <remarks>
    /// The table stands in for one the reference's scorer builds with 256 entries, because the length
    /// reaches it as a single stored byte and a byte has 256 values. An index that stores exact lengths
    /// can exceed it, so the table is not the only route: <see cref="Bm25Shape.Contribution"/> evaluates the same
    /// expression in single precision for a length past the end, which is the same number the table
    /// would hold — the table is a cache of that expression, not a different computation.
    /// </remarks>
    private const int MaxStoredLength = 255;

    /// <summary>
    /// The single-precision reciprocal table for this corpus's average length, built on first use and
    /// rebuilt when the average moves.
    /// </summary>
    private float[]? Table(double averageLength)
    {
        if (_arithmetic != Bm25Arithmetic.SinglePrecision)
            return null;

        NormInverseTable? current = Volatile.Read(ref _normInverse);

        if (TableFor(current, averageLength) is { } served)
            return served.Entries;

        lock (_normInverseLock)
        {
            current = _normInverse;

            if (TableFor(current, averageLength) is { } rechecked)
                return rechecked.Entries;

            var built = new NormInverseTable(averageLength, BuildNormInverseTable(_k1, _b, averageLength));

            // Published only once the array is complete, which is the whole reason this is one field
            // written under a lock rather than two written in sequence.
            Volatile.Write(ref _normInverse, built);

            return built.Entries;
        }
    }

    /// <summary>A reciprocal table and the average document length it was computed for.</summary>
    private sealed record NormInverseTable(double For, float[] Entries);

    /// <summary>
    /// The table built for exactly this average document length, or null. Bitwise equality on
    /// purpose: the table is a function of the average, not an approximation of it, so the cache is
    /// keyed on the exact value the table was built for. An epsilon here would serve a table built
    /// for a different average to scores that claim the new one.
    /// </summary>
    private static NormInverseTable? TableFor(NormInverseTable? table, double averageLength) =>
        table is not null && table.For == averageLength ? table : null; // NOSONAR:S1244

    /// <summary>
    /// The single-precision reciprocal table, one entry per stored length, each entry built by the same
    /// expression evaluated in single precision so that the rounding is the one being reproduced.
    /// </summary>
    private static float[] BuildNormInverseTable(double k1, double b, double averageLength)
    {
        float k = (float)k1;
        float bSingle = (float)b;
        float average = (float)averageLength;
        float oneMinus = 1f - bSingle;

        // Entry zero is filled like the rest so the table has no special case in it. A document with no
        // term has no posting entries and so never contributes, but a table with a hole in it would be a
        // branch on the scoring path.
        float[] table = new float[MaxStoredLength + 1];

        for (int i = 0; i < table.Length; i++)
        {
            float scaled = bSingle * i;
            float normalised = scaled / average;
            table[i] = 1f / (k * (oneMinus + normalised));
        }

        return table;
    }

    /// <summary>
    /// The part of the formula that is fixed once the scorer and the corpus are: the two BM25
    /// parameters, the numerator multiplier, the corpus average length, and the reciprocal table
    /// when the arithmetic is single precision.
    /// </summary>
    /// <remarks>
    /// Held together because that is how it is read — every caller had all of it and passed all of
    /// it — and because the table and the average length it was built for cannot be told apart: a
    /// table paired with another average length indexes a normalization nobody computed.
    /// </remarks>
    private readonly struct Bm25Shape(double k1, double b, double saturation, double averageLength, float[]? normInverse)
    {
        /// <summary>The corpus average length these constants were read against.</summary>
        public double AverageLength => averageLength;

        /// <summary>Whether the arithmetic is the single-precision one, announced by the table.</summary>
        public bool IsSinglePrecision => normInverse is not null;

        /// <summary>
        /// The length normalization of a document of <paramref name="documentLength"/> tokens:
        /// <c>1 − b + b · |d| / avgdl</c>, spelled once because every caller wrote it out and
        /// they have to agree to the bit.
        /// </summary>
        public double Normalization(int documentLength) =>
            1.0 - b + (b * documentLength / averageLength);

        /// <summary>
        /// One term's contribution, in whichever precision the table's presence announces.
        /// </summary>
        /// <remarks>
        /// The two are the same expression written twice rather than one written once and converted: the
        /// grouping is part of what is being reproduced, and evaluating the double form and narrowing it
        /// afterwards would round once, at the wrong places.
        /// </remarks>
        public double Contribution(double idf, int termFrequency, int documentLength, double normalization)
        {
            if (normInverse is null)
                return idf * termFrequency * saturation / (termFrequency + (k1 * normalization));

            float entry = documentLength <= MaxStoredLength
                ? normInverse[documentLength]
                : 1f / ((float)k1 * (1f - (float)b + ((float)b * documentLength / (float)averageLength)));

            // weight - weight / (1 + tf·normInverse), which is what the reference's scorer evaluates. The
            // algebraically equal form — weight·tf / (tf + k1·normInverse) — divides by a different quantity
            // and rounds differently, and the rounding is the thing being reproduced here.
            float weight = (float)idf * (float)saturation;

            return weight - (weight / (1f + (termFrequency * entry)));
        }

        /// <summary>The total, narrowed once when the scorer was built for single precision.</summary>
        /// <remarks>
        /// One rounding, at the end, on the sum rather than on each addition: the per-term contributions are
        /// single precision but they accumulate in double, so the total is rounded exactly once. Rounding
        /// each partial sum instead would be a different arithmetic and would not reproduce anything.
        /// </remarks>
        public double Round(double score) => normInverse is null ? score : (float)score;
    }

    /// <summary>
    /// The query-bound half of BM25: everything that does not depend on the document, so the
    /// accumulation loop is left with the same arithmetic and in the same order as
    /// <see cref="Bm25QueryPlan.Score"/>.
    /// </summary>
    private sealed class Bm25QueryPlan : IAccumulatingQueryPlan
    {
        private readonly IReadOnlyTextIndex _index;
        private readonly Bm25Shape _shape;

        private IReadOnlyList<string> _terms;
        private double[] _idf;

        public Bm25QueryPlan(IReadOnlyList<string> queryTerms, IReadOnlyTextIndex index, Bm25Shape shape)
        {
            ArgumentNullException.ThrowIfNull(queryTerms);

            _index = index;
            _shape = shape;

            // The plan holds the caller's list rather than a copy of it: it is created and discarded
            // inside one search, so the list outlives it by construction, and the terms the caller
            // tokenized for that query are the terms the plan wants. Copying them into a `string[]`
            // buys nothing but the array.
            _terms = queryTerms;
            _idf = new double[queryTerms.Count];

            int documentCount = index.StatisticDocumentCount;

            for (int i = 0; i < queryTerms.Count; i++)
            {
                if (documentCount == 0)
                    continue;

                int df = index.DocumentFrequency(queryTerms[i]);
                _idf[i] = Math.Log(1.0 + ((documentCount - df + 0.5) / (df + 0.5)));
            }

            // Only single precision folds, because only there the two forms differ.
            if (shape.IsSinglePrecision)
                FoldQueryFrequencies();
        }

        /// <summary>
        /// Collapses repeated query terms into one clause whose weight carries the repetition.
        /// </summary>
        /// <remarks>
        /// Only in single precision, and only because of what it changes there. Scoring a repeated term
        /// once per occurrence and scoring it once with the count in the weight are the same sum in
        /// double — the terms are identical and the doubles are exact — but in single precision they are
        /// not: the reference multiplies the weight by the count before it subtracts, where repeating a
        /// clause multiplies an already-rounded contribution. The difference is a unit in the last place,
        /// which is invisible in a ranking and decides a fourth-decimal rounding often enough to be worth
        /// the fold.
        /// <para>
        /// First-occurrence order is kept. The contributions are summed in double, where the order of the
        /// additions moves the result by about one part in 10^16 and the rounding that follows is on the
        /// total, so the order is not part of what is being reproduced.
        /// </para>
        /// </remarks>
        private void FoldQueryFrequencies()
        {
            // One pass: where each term first appears, and how often it appears at all.
            var firstOf = new Dictionary<string, int>(_terms.Count, StringComparer.Ordinal);
            var occurrences = new Dictionary<string, int>(_terms.Count, StringComparer.Ordinal);
            var order = new List<string>(_terms.Count);

            for (int i = 0; i < _terms.Count; i++)
            {
                string term = _terms[i];

                if (occurrences.TryGetValue(term, out int seen))
                {
                    occurrences[term] = seen + 1;
                    continue;
                }

                firstOf[term] = i;
                occurrences[term] = 1;
                order.Add(term);
            }

            if (order.Count == _terms.Count)
                return;

            string[] foldedTerms = new string[order.Count];
            double[] foldedIdf = new double[order.Count];

            for (int i = 0; i < order.Count; i++)
            {
                string term = order[i];
                foldedTerms[i] = term;

                // Rounded to single precision here, not later: the reference narrows the idf first and
                // then multiplies the count into it, and narrowing a product of two doubles is not the
                // same number. Both roundings are single precision and both are part of what is being
                // reproduced.
                foldedIdf[i] = (float)_idf[firstOf[term]] * (float)occurrences[term];
            }

            _terms = foldedTerms;
            _idf = foldedIdf;
        }

        /// <inheritdoc />
        public double Score(string documentId)
        {
            int documentLength = _index.DocumentLength(documentId);
            double averageLength = _shape.AverageLength;

            if (documentLength == 0 || averageLength <= 0)
                return 0;

            double normalization = _shape.Normalization(documentLength);
            double score = 0;

            for (int i = 0; i < _terms.Count; i++)
            {
                int tf = _index.TermFrequency(documentId, _terms[i]);

                if (tf == 0)
                    continue;

                score += _shape.Contribution(_idf[i], tf, documentLength, normalization);
            }

            return _shape.Round(score);
        }

        /// <inheritdoc />
        public double Finalise(double accumulated) => _shape.Round(accumulated);

        /// <inheritdoc />
        public bool TryAccumulate(IAccumulatingIndex index, ScoreAccumulator accumulator)
        {
            // The guards Score applies per document, hoisted: with no corpus or no average length
            // every score is 0, and with a zero length the normalization is not a number. A
            // document of length 0 has no posting entries at all, so it never reaches the loop.
            if (_index.Count == 0 || _shape.AverageLength <= 0)
                return false;

            for (int i = 0; i < _terms.Count; i++)
                index.Accumulate(new Bm25Weight(_terms[i], _idf[i], _shape), accumulator);

            return true;
        }
    }

    /// <summary>
    /// The query-bound half of BM25 for terms given as slices of the query text.
    /// </summary>
    /// <remarks>
    /// Same arithmetic and same order as <see cref="Bm25QueryPlan"/>: the terms are folded in query
    /// order, one posting entry at a time, and the per-term weight is the same expression over the
    /// same shape. What differs is only how a term reaches the index — by characters instead of by a
    /// string — which is why the two plans have to produce the same doubles and the parity tests and
    /// the golden master check it.
    /// </remarks>
    private sealed class Bm25SpanQueryPlan(Bm25Shape shape, double[] idf, bool distinct) : ISpanAccumulatingQueryPlan
    {
        /// <inheritdoc />
        public double Finalise(double accumulated) => shape.Round(accumulated);

        /// <inheritdoc />
        public bool TryAccumulateSpans(
            ISpanAccumulatingIndex index,
            ReadOnlySpan<NormalizedTerm> terms,
            ReadOnlySpan<char> source,
            ScoreAccumulator accumulator)
        {
            // The guards Score applies per document, hoisted exactly as the string plan hoists them:
            // with no corpus or no average length every score is 0, and a term the index cannot
            // resolve contributes nothing, which is what the string plan's fold does with it too.
            if (index.Count == 0 || shape.AverageLength <= 0)
                return false;

            for (int i = 0; i < terms.Length; i++)
            {
                // `Terms` collapses a repeated query term unless the scorer counts occurrences, and
                // this plan carries that decision rather than re-deriving it: the rule is the same
                // one, applied to slices.
                if (distinct && NormalizedTerm.IsRepeated(terms, i, source))
                    continue;

                if (!index.TryResolvePostings(terms[i].View(source), out var postings))
                    continue;

                index.AccumulateResolved(postings, new Bm25ResolvedWeight(idf[i], shape), accumulator);
            }

            return true;
        }
    }

    /// <summary>
    /// The per-term weight for a term whose postings the index has already resolved: the same
    /// contribution as <see cref="Bm25Weight"/>, over the same shape, naming no term.
    /// </summary>
    private readonly struct Bm25ResolvedWeight(double idf, Bm25Shape shape) : IResolvedWeight
    {
        public double Weight(int termFrequency, int documentLength) =>
            shape.Contribution(idf, termFrequency, documentLength, shape.Normalization(documentLength));
    }

    /// <summary>
    /// The query-bound half of BM25 as the accumulating index consumes it: the same arithmetic as
    /// <see cref="Bm25QueryPlan.Score"/>, one posting entry at a time.
    /// </summary>
    private readonly struct Bm25Weight(string term, double idf, Bm25Shape shape) : IPostingWeight
    {
        public string Term => term;

        /// <remarks>
        /// The accumulator sums in double whatever this returns, so narrowing the contribution here and
        /// narrowing the total afterwards is the same arithmetic as narrowing each term and summing in
        /// double — which is what it has to be.
        /// </remarks>
        /// <remarks>
        /// The length normalisation is recomputed here rather than taken from the caller: the
        /// accumulating index hands this one posting entry at a time and has nowhere to keep it, and
        /// recomputing it costs a divide the caller would otherwise have done once per document.
        /// </remarks>
        public double Weight(int termFrequency, int documentLength) =>
            shape.Contribution(idf, termFrequency, documentLength, shape.Normalization(documentLength));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Terms absent from the document contribute nothing and are omitted from
    /// <see cref="ScoreExplanation.Terms"/>. The reported
    /// <see cref="ScoreExplanation.TotalScore"/> always equals <see cref="Score"/> for the
    /// same inputs.
    /// </remarks>
    public ScoreExplanation Explain(string documentId, IReadOnlyList<string> queryTerms, IReadOnlyTextIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(queryTerms);

        int documentCount = index.StatisticDocumentCount;
        int documentLength = index.DocumentLength(documentId);
        double averageLength = index.AverageDocumentLength;

        // Written out rather than taken from the shape's Normalization, which divides by the
        // average length: a corpus without one reports 1 − b here, which is what the explanation
        // says, and the shape's would not be a number.
        double lengthRatio = averageLength > 0 ? documentLength / averageLength : 0;
        double normalization = 1.0 - _b + (_b * lengthRatio);

        var shape = new Bm25Shape(_k1, _b, _saturation, averageLength, Table(averageLength));
        var contributions = new List<TermContribution>();
        double total = 0;

        if (documentCount > 0 && documentLength > 0 && averageLength > 0)
        {
            var terms = Terms(queryTerms);

            for (int i = 0; i < terms.Count; i++)
            {
                string term = terms[i];
                int tf = index.TermFrequency(documentId, term);

                if (tf == 0)
                    continue;

                int df = index.DocumentFrequency(term);
                double idf = Math.Log(1.0 + ((documentCount - df + 0.5) / (df + 0.5)));
                double termScore = shape.Contribution(idf, tf, documentLength, normalization);

                contributions.Add(new TermContribution(term, tf, df, idf, termScore));
                total += termScore;
            }
        }

        var parameters = new Dictionary<string, double>
        {
            ["k1"] = _k1,
            ["b"] = _b,

            // The multiplier as the scorer applies it, not a fraction of it: the reader needs to be able
            // to divide the reported total by it and get back the same form of the formula the score was
            // computed from. 1.9 with the constant and 1 without it.
            ["saturationConstant"] = _saturation,
        };

        return new ScoreExplanation(
            documentId,
            Name,
            shape.Round(total),
            documentLength,
            averageLength,
            lengthRatio,
            normalization,
            contributions,
            parameters);
    }
}