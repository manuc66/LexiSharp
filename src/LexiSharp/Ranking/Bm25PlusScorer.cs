using LexiSharp.Core;

namespace LexiSharp.Ranking;

/// <summary>
/// BM25+ relevance: Okapi BM25 with the hard term-presence cut replaced by a lower-bounded one, so
/// a document that matches <i>more distinct</i> query terms is rewarded rather than merely not
/// penalised.
/// </summary>
/// <remarks>
/// <para>
/// The formula is BM25+ as given in Lv &amp; Zhai (<i>Optimizing inverted index with term
/// dependence model for text retrieval</i>, 2011):
/// </para>
/// <code>
/// score(q,d) = &#8721;_t  idf(t) &#183; (k1 + 1) &#183; (tf(t,d) + &#948;)
///                 / (k1 &#183; (1 &#8722; b + b &#183; |d| / avgdl) + tf(t,d) + &#948;)
/// </code>
/// <para>
/// <b>One departure, and it is load-bearing.</b> Read literally, the formula assigns a positive
/// contribution to a term the document does not contain, which would make every document in the
/// corpus match every query and break the engine's « score 0 means no match » convention
/// (see <i>Scoring conventions</i> in the README). This implementation therefore only sums over
/// terms the
/// document actually contains, which is the standard practical reading of BM25+ and keeps the
/// scorer an <see cref="ITermOverlapScorer"/>. The lower bound is not redundant: it still raises
/// documents with a single occurrence of many terms relative to one term repeated many times,
/// which is the coordination effect the paper is after.
/// </para>
/// <para>
/// <c>tf + &#948;</c> in both numerator and denominator is what differs from
/// <see cref="Bm25Scorer"/>: with <c>&#948; = 0</c> the two coincide, and a document with no
/// matching term contributes nothing under either.
/// </para>
/// <para>
/// <b>No claim that this retrieves better.</b> BM25+ is a single-field variant, so it competes only
/// with <see cref="Bm25Scorer"/>, and the published BEIR comparison against BM25L was measured on a
/// different corpus. Measure it on yours against a <i>tuned</i> BM25, not a default one —
/// <see cref="Bm25ParameterTuner"/> — because a default-versus-default comparison mostly measures
/// which default fits the corpus.
/// </para>
/// </remarks>
public sealed class Bm25PlusScorer : IScoreExplainer, ITermOverlapScorer, IQueryPlannableScorer
{
    private readonly double _k1;
    private readonly double _b;
    private readonly double _delta;

    /// <param name="k1">Term-frequency saturation; higher values let frequent terms contribute more.</param>
    /// <param name="b">Document-length normalization, in <c>[0, 1]</c>. <c>0</c> disables normalization.</param>
    /// <param name="delta">
    /// The lower bound added to the term frequency, non-negative and finite. <c>0</c> degenerates to
    /// <see cref="Bm25Scorer"/>. The paper's reported range is around <c>0.5</c> to <c>1.0</c>; that
    /// is a starting point to tune, not a recommended value.
    /// </param>
    public Bm25PlusScorer(double k1 = 1.5, double b = 0.75, double delta = 1.0)
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
    public string Name => "BM25+";

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

            // The gate: a term the document does not contain contributes nothing, which is what keeps
            // the score 0 for a document sharing no query term.
            if (tf == 0)
                continue;

            int df = index.DocumentFrequency(terms[i]);
            double idf = Math.Log(1.0 + (documentCount - df + 0.5) / (df + 0.5));
            double lowered = tf + _delta;

            score += idf * (_k1 + 1.0) * lowered / (_k1 * normalization + lowered);
        }

        return score;
    }

    ISearchQueryPlan IQueryPlannableScorer.CreatePlan(IReadOnlyList<string> queryTerms, ITextIndex index)
    {
        ArgumentNullException.ThrowIfNull(queryTerms);
        ArgumentNullException.ThrowIfNull(index);

        return new Bm25PlusQueryPlan(queryTerms, index, _k1, _b, _delta);
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
                double lowered = tf + _delta;
                double termScore = idf * (_k1 + 1.0) * lowered / (_k1 * normalization + lowered);

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

    private sealed class Bm25PlusQueryPlan : ISearchQueryPlan
    {
        private readonly ITextIndex _index;
        private readonly string[] _terms;
        private readonly double[] _idf;
        private readonly double _avgLength;
        private readonly double _k1;
        private readonly double _b;
        private readonly double _delta;

        public Bm25PlusQueryPlan(
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

                double lowered = tf + _delta;
                score += _idf[i] * (_k1 + 1.0) * lowered / (_k1 * normalization + lowered);
            }

            return score;
        }
    }
}
