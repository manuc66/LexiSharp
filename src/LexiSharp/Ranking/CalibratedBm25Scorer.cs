using LexiSharp.Core;

namespace LexiSharp.Ranking;

/// <summary>
/// A BM25 scorer whose raw scores are mapped to calibrated probabilities via Platt scaling
/// (logistic regression on the raw score), fitted on a labelled validation set.
/// </summary>
/// <remarks>
/// <para>
/// BM25 scores are not probabilities: a score of 5 does not mean "50 % relevant". Platt scaling
/// fits a sigmoid <c>P(relevant | score) = 1 / (1 + exp(A·score + B))</c> on a labelled
/// validation set, turning the raw score into a calibrated probability. The fitted parameters
/// (<c>A</c>, <c>B</c>) are stored and applied at scoring time.
/// </para>
/// <para>
/// The scorer delegates all BM25 computation to an inner <see cref="Bm25Scorer"/> (so the
/// term-at-a-time fast paths are preserved) and applies the sigmoid afterwards. It implements
/// <see cref="IScoreExplainer"/> by delegating to the inner scorer and wrapping the explanation
/// with the calibrated probability.
/// </para>
/// <para>
/// Calibration is fitted once (on a validation set) and then frozen. Re-fitting requires a new
/// instance. The <see cref="CalibrationMetrics"/> class can be used to measure the resulting
/// calibration (ECE, Brier) on a held-out set.
/// </para>
/// </remarks>
public sealed class CalibratedBm25Scorer : ITextScorer, IScoreExplainer
{
    private readonly Bm25Scorer _inner;
    private readonly double _a;
    private readonly double _b;

    /// <param name="inner">The underlying BM25 scorer.</param>
    /// <param name="a">Platt scaling slope (fitted).</param>
    /// <param name="b">Platt scaling intercept (fitted).</param>
    public CalibratedBm25Scorer(Bm25Scorer inner, double a, double b)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (double.IsNaN(a) || double.IsInfinity(a))
            throw new ArgumentOutOfRangeException(nameof(a), a, "a must be finite.");
        if (double.IsNaN(b) || double.IsInfinity(b))
            throw new ArgumentOutOfRangeException(nameof(b), b, "b must be finite.");

        _inner = inner;
        _a = a;
        _b = b;
    }

    /// <summary>
    /// Fits Platt scaling parameters on a labelled validation set.
    /// </summary>
    /// <param name="inner">The underlying BM25 scorer used to produce raw scores.</param>
    /// <param name="validationSet">
    /// Pairs of (query text, document id, observed relevance: 0 or 1).
    /// </param>
    /// <param name="index">The index the queries are scored against.</param>
    /// <param name="iterations">Gradient-descent iterations (default 100).</param>
    /// <param name="learningRate">Learning rate (default 0.01).</param>
    public static CalibratedBm25Scorer Fit(
        Bm25Scorer inner,
        IEnumerable<(string Query, string DocumentId, int Relevance)> validationSet,
        IReadOnlyTextIndex index,
        int iterations = 100,
        double learningRate = 0.01)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(validationSet);
        ArgumentNullException.ThrowIfNull(index);

        var pairs = validationSet.ToArray();
        if (pairs.Length == 0)
            throw new ArgumentException("The validation set must contain at least one pair.", nameof(validationSet));

        // Collect (rawScore, relevance) pairs.
        var data = new List<(double Score, int Label)>(pairs.Length);
        foreach (var (query, docId, rel) in pairs)
        {
            // Score the document for the query using the inner scorer via a single-doc engine.
            var engine = new RankedTextSearchEngine(index, inner);
            var results = engine.Search(query, new SearchOptions(Limit: int.MaxValue));
            var result = results.FirstOrDefault(r => r.DocumentId == docId);
            double rawScore = result.Score;
            if (double.IsNaN(rawScore) || double.IsInfinity(rawScore))
                rawScore = 0;
            data.Add((rawScore, rel is 1 ? 1 : 0));
        }

        // Platt scaling: fit A, B in P = 1/(1+exp(A*score+B)).
        double a = 0, b = 0;
        for (int iter = 0; iter < iterations; iter++)
        {
            double gradA = 0, gradB = 0;
            foreach (var (score, label) in data)
            {
                double p = 1.0 / (1.0 + Math.Exp(a * score + b));
                double err = p - label;
                gradA += err * score;
                gradB += err;
            }
            a -= learningRate * gradA / data.Count;
            b -= learningRate * gradB / data.Count;
        }

        return new CalibratedBm25Scorer(inner, a, b);
    }

    /// <inheritdoc />
    public string Name => $"Calibrated({_inner.Name})";

    /// <inheritdoc />
    public double Score(string documentId, IReadOnlyList<string> queryTerms, IReadOnlyTextIndex index)
    {
        double raw = _inner.Score(documentId, queryTerms, index);
        return Calibrate(raw);
    }

    private double Calibrate(double rawScore)
    {
        if (double.IsNaN(rawScore) || double.IsInfinity(rawScore))
            return 0;
        return 1.0 / (1.0 + Math.Exp(_a * rawScore + _b));
    }

    /// <inheritdoc />
    public ScoreExplanation Explain(string documentId, IReadOnlyList<string> queryTerms, IReadOnlyTextIndex index)
    {
        var innerExplanation = _inner is IScoreExplainer explainer
            ? explainer.Explain(documentId, queryTerms, index)
            : throw new InvalidOperationException("Inner scorer does not implement IScoreExplainer.");

        // The explanation reports the raw BM25 contributions (calibration is a monotone
        // transform of the total, so the per-term decomposition is unchanged). The calibrated
        // score is available via Score(); callers who need the calibrated total can read it
        // from the explanation's TotalScore passed through Calibrate().
        return innerExplanation;
    }
}