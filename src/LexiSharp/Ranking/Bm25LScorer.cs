using LexiSharp.Core;

namespace LexiSharp.Ranking;

/// <summary>
/// BM25L relevance: Okapi BM25 with a <b>compressed</b> term frequency in the numerator and a lower
/// bound on it, which lengthens the reach of rare terms in long documents.
/// </summary>
/// <remarks>
/// <para>
/// The formula is BM25L as given in Lv (<i>When documents are very long, BM25 fails!</i>, SIGIR
/// 2011), and reproduced in the large-scale reproducibility study <i>Which BM25 Do You Mean?</i>
/// (ECIR 2022):
/// </para>
/// <code>
/// ctd(t,d) = tf(t,d) / (1 &#8722; b + b &#183; |d| / avgdl)          compressed term frequency
///
/// score(q,d) = &#8721;_t  idf(t) &#183; (k1 + 1) &#183; (ctd(t,d) + &#948;)
///                 / (k1 + ctd(t,d) + &#948;)
/// </code>
/// <para>
/// <b>The denominator is the compressed frequency, not the raw one.</b> That is the whole variant,
/// and getting it wrong is easy: writing <c>k1 &#183; (1 &#8722; b + b|d|/avgdl) + tf</c> there —
/// the plain BM25 denominator — silently produces something that is neither BM25L nor BM25. An
/// earlier version of this class did exactly that; <c>Bm25LUsesTheCompressedDenominator</c> is the
/// test that discriminates.
/// </para>
/// <para>
/// <c>&#948;</c> appears in both numerator and denominator, which is what keeps the term weight
/// bounded below and stops a single occurrence in a very long document from being rounded away. The
/// paper reports <c>&#948; = 0.5</c> as most effective.
/// </para>
/// <para>
/// As with <see cref="Bm25PlusScorer"/>, terms absent from the document are skipped, so the score
/// stays exactly <c>0</c> for a document sharing no query term. Note that the correct BM25L does not
/// require that gate to preserve the convention — its term weight is already <c>0</c> at
/// <c>tf = 0</c> — so the gate is here only to skip the work, not to change the result.
/// </para>
/// <para>
/// <b>No claim that this retrieves better.</b> BM25L is a single-field variant, so it competes only
/// with <see cref="Bm25Scorer"/>. Measure it on your corpus against a <i>tuned</i> BM25
/// (<see cref="Bm25ParameterTuner"/>) — a default-versus-default comparison mostly measures which
/// default fits the corpus, which is a lesson this repository learned the hard way. And tune this
/// scorer's own <c>&#948;</c> with <see cref="Bm25LParameterTuner"/> before concluding anything: a row
/// that fixes the bound at the paper's value is not a tuned row.
/// </para>
/// </remarks>
public sealed class Bm25LScorer : IScoreExplainer, ITermOverlapScorer, IQueryPlannableScorer
{
    private readonly double _k1;
    private readonly double _b;
    private readonly double _delta;
    private readonly QueryTermWeighting _queryTerms;

    /// <param name="k1">Term-frequency saturation; higher values let frequent terms contribute more.</param>
    /// <param name="b">Document-length normalization, in <c>[0, 1]</c>. <c>0</c> disables normalization.</param>
    /// <param name="delta">
    /// The lower bound added to the compressed term frequency, non-negative and finite. The
    /// paper's reported range is around <c>0.5</c> to <c>1.0</c>; that is a starting point to tune,
    /// not a recommended value.
    /// </param>
    /// <param name="queryTermWeighting">
    /// How a term repeated in the query is treated. Default
    /// <see cref="QueryTermWeighting.Distinct"/>; pass
    /// <see cref="QueryTermWeighting.QueryFrequency"/> to reproduce a published BM25 figure.
    /// See <see cref="QueryTermWeighting"/> for the measured effect.
    /// </param>
    public Bm25LScorer(double k1 = 1.5, double b = 0.75, double delta = 0.5,
        QueryTermWeighting queryTermWeighting = QueryTermWeighting.Distinct)
    {
        if (double.IsNaN(k1) || double.IsInfinity(k1) || k1 < 0)
            throw new ArgumentOutOfRangeException(nameof(k1), k1, "k1 must be non-negative and finite.");
        if (double.IsNaN(b) || double.IsInfinity(b) || b is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(b), b, "b must be within [0, 1] and finite.");
        if (double.IsNaN(delta) || double.IsInfinity(delta) || delta < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(delta), delta, "delta must be non-negative and finite.");
        }

        _k1 = k1;
        _b = b;
        _delta = delta;
        _queryTerms = queryTermWeighting;
    }

    /// <summary>
    /// The query terms as this scorer reads them: deduplicated, or kept whole so a repeated
    /// term is scored once per occurrence.
    /// </summary>
    private IReadOnlyList<string> Terms(IReadOnlyList<string> queryTerms)
    {
        if (_queryTerms == QueryTermWeighting.QueryFrequency)
            return queryTerms;

        // The engine already deduplicated, and DistinctTermList is the marker saying so. Deduplicating
        // again would hash every term to arrive at the list this was handed.
        return queryTerms is DistinctTermList ? queryTerms : TermDeduplicator.Distinct(queryTerms);
    }

    /// <inheritdoc />
    public string Name => "BM25L";

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

        double normalization = 1.0 - _b + (_b * documentLength / averageLength);
        var terms = Terms(queryTerms);

        double score = 0;

        for (int i = 0; i < terms.Count; i++)
        {
            int tf = index.TermFrequency(documentId, terms[i]);

            if (tf == 0)
                continue;

            int df = index.DocumentFrequency(terms[i]);
            double idf = Math.Log(1.0 + ((documentCount - df + 0.5) / (df + 0.5)));
            double compressed = tf / normalization;

            score += idf * (_k1 + 1.0) * (compressed + _delta) / (_k1 + compressed + _delta);
        }

        return score;
    }

    ISearchQueryPlan IQueryPlannableScorer.CreatePlan(IReadOnlyList<string> queryTerms, IReadOnlyTextIndex index)
    {
        ArgumentNullException.ThrowIfNull(queryTerms);
        ArgumentNullException.ThrowIfNull(index);

        return new Bm25LQueryPlan(Terms(queryTerms), index, _k1, _b, _delta);
    }

    /// <inheritdoc />
    public ScoreExplanation Explain(string documentId, IReadOnlyList<string> queryTerms, IReadOnlyTextIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(queryTerms);

        int documentCount = index.StatisticDocumentCount;
        int documentLength = index.DocumentLength(documentId);
        double averageLength = index.AverageDocumentLength;
        double lengthRatio = averageLength > 0 ? documentLength / averageLength : 0;
        double normalization = 1.0 - _b + (_b * lengthRatio);

        var contributions = new List<TermContribution>();
        double total = 0;

        if (documentCount > 0 && documentLength > 0 && averageLength > 0)
        {
            var terms = Terms(queryTerms);

            for (int i = 0; i < terms.Count; i++)
            {
                int tf = index.TermFrequency(documentId, terms[i]);

                if (tf == 0)
                    continue;

                int df = index.DocumentFrequency(terms[i]);
                double idf = Math.Log(1.0 + ((documentCount - df + 0.5) / (df + 0.5)));
                double compressed = tf / normalization;
                double termScore = idf * (_k1 + 1.0) * (compressed + _delta) / (_k1 + compressed + _delta);

                contributions.Add(new TermContribution(terms[i], tf, df, idf, termScore));
                total += termScore;
            }
        }

        var parameters = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["k1"] = _k1,
            ["b"] = _b,
            ["delta"] = _delta,
        };

        return new ScoreExplanation(
            documentId,
            Name,
            total,
            documentLength,
            averageLength,
            lengthRatio,
            normalization,
            contributions,
            parameters);
    }

    private sealed class Bm25LQueryPlan : IAccumulatingQueryPlan
    {
        private readonly IReadOnlyTextIndex _index;
        private readonly string[] _terms;
        private readonly double[] _idf;
        private readonly double _avgLength;
        private readonly double _k1;
        private readonly double _b;
        private readonly double _delta;

        public Bm25LQueryPlan(
            IReadOnlyList<string> queryTerms,
            IReadOnlyTextIndex index,
            double k1,
            double b,
            double delta)
        {
            _index = index;
            _k1 = k1;
            _b = b;
            _delta = delta;
            _avgLength = index.AverageDocumentLength;

            int documentCount = index.StatisticDocumentCount;
            _terms = new string[queryTerms.Count];
            _idf = new double[queryTerms.Count];

            for (int i = 0; i < queryTerms.Count; i++)
            {
                string term = queryTerms[i];
                _terms[i] = term;

                if (documentCount == 0)
                    continue;

                int df = index.DocumentFrequency(term);
                _idf[i] = Math.Log(1.0 + ((documentCount - df + 0.5) / (df + 0.5)));
            }
        }

        /// <inheritdoc />
        public double Score(string documentId)
        {
            int documentLength = _index.DocumentLength(documentId);

            if (documentLength == 0 || _avgLength <= 0)
                return 0;

            double normalization = 1.0 - _b + (_b * documentLength / _avgLength);
            double score = 0;

            for (int i = 0; i < _terms.Length; i++)
            {
                int tf = _index.TermFrequency(documentId, _terms[i]);

                if (tf == 0)
                    continue;

                double compressed = tf / normalization;
                score += _idf[i] * (_k1 + 1.0) * (compressed + _delta) / (_k1 + compressed + _delta);
            }

            return score;
        }

        /// <inheritdoc />
        public bool TryAccumulate(IAccumulatingIndex index, ScoreAccumulator accumulator)
        {
            if (_index.Count == 0 || _avgLength <= 0)
                return false;

            for (int i = 0; i < _terms.Length; i++)
            {
                index.Accumulate(
                    new Bm25LWeight(_terms[i], _idf[i], _k1, _b, _delta, _avgLength),
                    accumulator);
            }

            return true;
        }
    }

    private readonly struct Bm25LWeight(
        string term, double idf, double k1, double b, double delta, double averageLength) : IPostingWeight
    {
        public string Term => term;

        public double Weight(int termFrequency, int documentLength)
        {
            double normalization = 1.0 - b + (b * documentLength / averageLength);
            double compressed = termFrequency / normalization;

            return idf * (k1 + 1.0) * (compressed + delta) / (k1 + compressed + delta);
        }
    }
}
