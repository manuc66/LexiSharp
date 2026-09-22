using LexiSharp.Core;

namespace LexiSharp.Ranking;

/// <summary>
/// Classic TF-IDF relevance. A document matches when it contains at least one query term;
/// rare terms are weighted more than common ones.
/// </summary>
/// <remarks>
/// <c>score(t, d) = tf(t, d) * idf(t)</c> with
/// <c>idf(t) = log((N + 1) / (df(t) + 1)) + 1</c>.
/// This smoothed variant guarantees a positive idf, so a score of 0 truly means « no match ».
/// </remarks>
public sealed class TfIdfScorer : ITermOverlapScorer, IQueryPlannableScorer, IScoreExplainer
{
    /// <inheritdoc />
    public string Name => "TF-IDF";

    /// <inheritdoc />
    public double Score(string documentId, IReadOnlyList<string> queryTerms, ITextIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(queryTerms);

        int documentCount = index.Count;

        if (documentCount == 0)
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

            double idf = Math.Log((documentCount + 1.0) / (df + 1.0)) + 1.0;
            score += tf * idf;
        }

        return score;
    }

    ISearchQueryPlan IQueryPlannableScorer.CreatePlan(IReadOnlyList<string> queryTerms, ITextIndex index)
    {
        ArgumentNullException.ThrowIfNull(queryTerms);
        ArgumentNullException.ThrowIfNull(index);

        return new TfIdfQueryPlan(queryTerms, index);
    }

    /// <inheritdoc />
    /// <remarks>
    /// TF-IDF applies no document-length normalization, so
    /// <see cref="ScoreExplanation.LengthNormalization"/> is always <c>1</c>. Terms absent from
    /// the document are omitted, and <see cref="ScoreExplanation.TotalScore"/> always equals
    /// <see cref="Score"/> for the same inputs.
    /// </remarks>
    public ScoreExplanation Explain(string documentId, IReadOnlyList<string> queryTerms, ITextIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(queryTerms);

        int documentCount = index.Count;
        int documentLength = index.DocumentLength(documentId);
        double averageLength = index.AverageDocumentLength;
        double lengthRatio = averageLength > 0 ? documentLength / averageLength : 0;

        var contributions = new List<TermContribution>();
        double total = 0;

        if (documentCount > 0)
        {
            var terms = queryTerms is DistinctTermList ? queryTerms : TermDeduplicator.Distinct(queryTerms);

            for (int i = 0; i < terms.Count; i++)
            {
                string term = terms[i];
                int tf = index.TermFrequency(documentId, term);

                if (tf == 0)
                    continue;

                int df = index.DocumentFrequency(term);
                double idf = Math.Log((documentCount + 1.0) / (df + 1.0)) + 1.0;
                double termScore = tf * idf;

                contributions.Add(new TermContribution(term, tf, df, idf, termScore));
                total += termScore;
            }
        }

        return new ScoreExplanation(
            documentId,
            Name,
            total,
            documentLength,
            averageLength,
            lengthRatio,
            LengthNormalization: 1.0,
            contributions,
            new Dictionary<string, double>());
    }

    private sealed class TfIdfQueryPlan : ISearchQueryPlan
    {
        private readonly ITextIndex _index;
        private readonly string[] _terms;
        private readonly double[] _idf;

        public TfIdfQueryPlan(IReadOnlyList<string> queryTerms, ITextIndex index)
        {
            _index = index;
            _terms = new string[queryTerms.Count];
            _idf = new double[queryTerms.Count];

            int documentCount = index.Count;

            for (int i = 0; i < queryTerms.Count; i++)
            {
                string term = queryTerms[i];
                _terms[i] = term;

                if (documentCount == 0)
                    continue;

                int df = index.DocumentFrequency(term);
                _idf[i] = Math.Log((documentCount + 1.0) / (df + 1.0)) + 1.0;
            }
        }

        /// <inheritdoc />
        public double Score(string documentId)
        {
            double score = 0;

            for (int i = 0; i < _terms.Length; i++)
            {
                int tf = _index.TermFrequency(documentId, _terms[i]);

                if (tf == 0)
                    continue;

                score += tf * _idf[i];
            }

            return score;
        }
    }
}