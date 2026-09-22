using LexiSharp.Core;

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
/// </remarks>
public sealed class Bm25Scorer : IScoreExplainer, ITermOverlapScorer, IQueryPlannableScorer
{
    private readonly double _k1;
    private readonly double _b;

    /// <param name="k1">Term-frequency saturation: higher values let frequent terms contribute more.</param>
    /// <param name="b">Document-length normalization, in <c>[0, 1]</c>. <c>0</c> disables normalization.</param>
    public Bm25Scorer(double k1 = 1.5, double b = 0.75)
    {
        if (double.IsNaN(k1) || double.IsInfinity(k1) || k1 < 0)
            throw new ArgumentOutOfRangeException(nameof(k1), k1, "k1 must be non-negative and finite.");
        if (double.IsNaN(b) || double.IsInfinity(b) || b is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(b), b, "b must be within [0, 1] and finite.");

        _k1 = k1;
        _b = b;
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
    }

    /// <inheritdoc />
    public string Name => "BM25";

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

        double score = 0;

        // Indexed loop over the IReadOnlyList<string> interface: a foreach would box the
        // enumerator once per scored document.
        var terms = queryTerms is DistinctTermList ? queryTerms : TermDeduplicator.Distinct(queryTerms);

        for (int i = 0; i < terms.Count; i++)
        {
            string term = terms[i];
            int tf = index.TermFrequency(documentId, term);

            if (tf == 0)
                continue;

            int df = index.DocumentFrequency(term);

            double idf = Math.Log(1.0 + (documentCount - df + 0.5) / (df + 0.5));

            double normalization = 1.0 - _b + _b * documentLength / averageLength;
            score += idf * tf * (_k1 + 1.0) / (tf + _k1 * normalization);
        }

        return score;
    }

    ISearchQueryPlan IQueryPlannableScorer.CreatePlan(IReadOnlyList<string> queryTerms, ITextIndex index)
    {
        ArgumentNullException.ThrowIfNull(queryTerms);
        ArgumentNullException.ThrowIfNull(index);

        return new Bm25QueryPlan(queryTerms, index, _k1, _b);
    }

    private sealed class Bm25QueryPlan : ISearchQueryPlan
    {
        private readonly ITextIndex _index;
        private readonly string[] _terms;
        private readonly double[] _idf;
        private readonly double _avgLength;
        private readonly double _k1;
        private readonly double _b;

        public Bm25QueryPlan(IReadOnlyList<string> queryTerms, ITextIndex index, double k1, double b)
        {
            _index = index;
            _terms = new string[queryTerms.Count];
            _idf = new double[queryTerms.Count];
            _k1 = k1;
            _b = b;
            _avgLength = index.AverageDocumentLength;

            int documentCount = index.Count;

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

                score += _idf[i] * tf * (_k1 + 1.0) / (tf + _k1 * normalization);
            }

            return score;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Terms absent from the document contribute nothing and are omitted from
    /// <see cref="ScoreExplanation.Terms"/>. The reported
    /// <see cref="ScoreExplanation.TotalScore"/> always equals <see cref="Score"/> for the
    /// same inputs.
    /// </remarks>
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
                string term = terms[i];
                int tf = index.TermFrequency(documentId, term);

                if (tf == 0)
                    continue;

                int df = index.DocumentFrequency(term);
                double idf = Math.Log(1.0 + (documentCount - df + 0.5) / (df + 0.5));
                double termScore = idf * tf * (_k1 + 1.0) / (tf + _k1 * normalization);

                contributions.Add(new TermContribution(term, tf, df, idf, termScore));
                total += termScore;
            }
        }

        var parameters = new Dictionary<string, double>
        {
            ["k1"] = _k1,
            ["b"] = _b,
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
}