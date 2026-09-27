using LexiSharp.Core;

namespace LexiSharp.Ranking;

/// <summary>
/// BM25L relevance: Okapi BM25 with a <b>compressed</b> term frequency in the numerator and a lower
/// bound on it, which lengthens the reach of rare terms in long documents.
/// </summary>
/// <remarks>
/// <para>
/// The formula is BM25L as given in Lv, Fan, Nie &amp; Ma (<i>Improving BM25 for web page
/// retrieval</i>, 2006), using their correction to the base TF-IDF formulation:
/// </para>
/// <code>
/// ctd(t,d) = tf(t,d) / (1 &#8722; b + b &#183; |d| / avgdl)          compressed term frequency
///
/// score(q,d) = &#8721;_t  idf(t) &#183; (k1 + 1) &#183; (ctd(t,d) + &#948;)
///                 / (k1 &#183; (1 &#8722; b + b &#183; |d| / avgdl) + tf(t,d))
/// </code>
/// <para>
/// Note the asymmetry, which is the whole point: the <b>numerator</b> carries the compressed
/// frequency plus the lower bound, while the <b>denominator</b> uses the raw <c>tf</c>. That
/// asymmetry is what makes a term in a long document count for more than Okapi BM25 would give it,
/// and why BM25L is not simply BM25 with a different saturation curve.
/// </para>
/// <para>
/// As with <see cref="Bm25PlusScorer"/>, terms absent from the document are skipped so the score
/// stays exactly <c>0</c> for a document sharing no query term. <c>&#948;</c> is not redundant here
/// either: it is what stops a term occurring once in a very long document from being rounded away
/// by the denominator.
/// </para>
/// <para>
/// <b>No claim that this retrieves better.</b> BM25L is a single-field variant, so it competes only
/// with <see cref="Bm25Scorer"/>. Measure it on your corpus against a <i>tuned</i> BM25
/// (<see cref="Bm25ParameterTuner"/>) — a default-versus-default comparison mostly measures which
/// default fits the corpus, which is a lesson this repository learned the hard way.
/// </para>
/// </remarks>
public sealed class Bm25LScorer : IScoreExplainer, ITermOverlapScorer, IQueryPlannableScorer
{
    private readonly double _k1;
    private readonly double _b;
    private readonly double _delta;

    /// <param name="k1">Term-frequency saturation; higher values let frequent terms contribute more.</param>
    /// <param name="b">Document-length normalization, in <c>[0, 1]</c>. <c>0</c> disables normalization.</param>
    /// <param name="delta">
    /// The lower bound added to the compressed term frequency, non-negative and finite. The
    /// paper's reported range is around <c>0.5</c> to <c>1.0</c>; that is a starting point to tune,
    /// not a recommended value.
    /// </param>
    public Bm25LScorer(double k1 = 1.5, double b = 0.75, double delta = 0.5)
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
    }

    /// <inheritdoc />
    public string Name => "BM25L";

    /// <inheritdoc />
    public double Score(string documentId, IReadOnlyList<string> queryTerms, ITextIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(queryTerms);

        int documentCount = index.Count;
        int documentLength = index.DocumentLength(documentId);
        double averageLength = index.AverageDocumentLength;

        if (documentCount == 0 || documentLength == 0 || averageLength <= 0)
            return 0;

        double normalization = 1.0 - _b + _b * documentLength / averageLength;
        var terms = queryTerms is DistinctTermList ? queryTerms : TermDeduplicator.Distinct(queryTerms);

        double score = 0;

        for (int i = 0; i < terms.Count; i++)
        {
            int tf = index.TermFrequency(documentId, terms[i]);

            if (tf == 0)
                continue;

            int df = index.DocumentFrequency(terms[i]);
            double idf = Math.Log(1.0 + (documentCount - df + 0.5) / (df + 0.5));
            double compressed = tf / normalization;

            score += idf * (_k1 + 1.0) * (compressed + _delta) / (_k1 * normalization + tf);
        }

        return score;
    }

    ISearchQueryPlan IQueryPlannableScorer.CreatePlan(IReadOnlyList<string> queryTerms, ITextIndex index)
    {
        ArgumentNullException.ThrowIfNull(queryTerms);
        ArgumentNullException.ThrowIfNull(index);

        return new Bm25LQueryPlan(queryTerms, index, _k1, _b, _delta);
    }

    /// <inheritdoc />
    public ScoreExplanation Explain(string documentId, IReadOnlyList<string> queryTerms, ITextIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(queryTerms);

        int documentCount = index.Count;
        int documentLength = index.DocumentLength(documentId);
        double averageLength = index.AverageDocumentLength;
        double lengthRatio = averageLength > 0 ? documentLength / averageLength : 0;
        double normalization = 1.0 - _b + _b * lengthRatio;

        var contributions = new List<TermContribution>();
        double total = 0;

        if (documentCount > 0 && documentLength > 0 && averageLength > 0)
        {
            var terms = queryTerms is DistinctTermList ? queryTerms : TermDeduplicator.Distinct(queryTerms);

            for (int i = 0; i < terms.Count; i++)
            {
                int tf = index.TermFrequency(documentId, terms[i]);

                if (tf == 0)
                    continue;

                int df = index.DocumentFrequency(terms[i]);
                double idf = Math.Log(1.0 + (documentCount - df + 0.5) / (df + 0.5));
                double compressed = tf / normalization;
                double termScore = idf * (_k1 + 1.0) * (compressed + _delta) / (_k1 * normalization + tf);

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

    private sealed class Bm25LQueryPlan : ISearchQueryPlan
    {
        private readonly ITextIndex _index;
        private readonly string[] _terms;
        private readonly double[] _idf;
        private readonly double _avgLength;
        private readonly double _k1;
        private readonly double _b;
        private readonly double _delta;

        public Bm25LQueryPlan(
            IReadOnlyList<string> queryTerms,
            ITextIndex index,
            double k1,
            double b,
            double delta)
        {
            _index = index;
            _k1 = k1;
            _b = b;
            _delta = delta;
            _avgLength = index.AverageDocumentLength;

            int documentCount = index.Count;
            _terms = new string[queryTerms.Count];
            _idf = new double[queryTerms.Count];

            for (int i = 0; i < queryTerms.Count; i++)
            {
                string term = queryTerms[i];
                _terms[i] = term;

                if (documentCount == 0)
                    continue;

                int df = index.DocumentFrequency(term);
                _idf[i] = Math.Log(1.0 + (documentCount - df + 0.5) / (df + 0.5));
            }
        }

        /// <inheritdoc />
        public double Score(string documentId)
        {
            int documentLength = _index.DocumentLength(documentId);

            if (documentLength == 0 || _avgLength <= 0)
                return 0;

            double normalization = 1.0 - _b + _b * documentLength / _avgLength;
            double score = 0;

            for (int i = 0; i < _terms.Length; i++)
            {
                int tf = _index.TermFrequency(documentId, _terms[i]);

                if (tf == 0)
                    continue;

                double compressed = tf / normalization;
                score += _idf[i] * (_k1 + 1.0) * (compressed + _delta) / (_k1 * normalization + tf);
            }

            return score;
        }
    }
}
