using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Ranking;

/// <summary>
/// Finds the Okapi BM25 parameters (<c>k1</c>, <c>b</c>) that best satisfy a set of labeled
/// validation queries, by brute-force grid search.
/// </summary>
/// <remarks>
/// For every grid point the tuner runs a <see cref="RankedTextSearchEngine"/> backed by the
/// <b>same</b> <see cref="ITextIndex"/> with a fresh <see cref="Bm25Scorer"/>, measures the
/// chosen <see cref="TuningMetric"/> on each validation query and keeps the point with the
/// highest mean. The index is never mutated and no scorer state is shared, so tuning is
/// side-effect free. Ties are broken in favor of the first point in ascending <c>(k1, b)</c>
/// order, which keeps the result deterministic.
/// </remarks>
public sealed class Bm25ParameterTuner
{
    private static readonly double[] DefaultK1Values = [0.5, 1.0, 1.2, 1.5, 2.0];
    private static readonly double[] DefaultBValues = [0.0, 0.25, 0.5, 0.75, 1.0];

    private readonly ITextIndex _index;
    private readonly ITokenizer _tokenizer;
    private readonly IReadOnlyList<Bm25ValidationQuery> _validationQueries;

    /// <param name="index">The indexed corpus to tune against (read-only for the tuner).</param>
    /// <param name="validationQueries">
    /// Labeled queries with the ids of their relevant documents. Every query must list at
    /// least one relevant document, otherwise recall is undefined.
    /// </param>
    /// <param name="tokenizer">
    /// Tokenizer for queries; should be the same one the index was built with.
    /// </param>
    public Bm25ParameterTuner(
        ITextIndex index,
        IEnumerable<Bm25ValidationQuery> validationQueries,
        ITokenizer? tokenizer = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(validationQueries);

        _index = index;
        _tokenizer = tokenizer ?? Tokenizer.Default;
        _validationQueries = validationQueries.ToList();

        if (_validationQueries.Count == 0)
            throw new ArgumentException("The validation set must contain at least one query.", nameof(validationQueries));

        foreach (var validationQuery in _validationQueries)
        {
            ArgumentNullException.ThrowIfNull(validationQuery);

            if (validationQuery.RelevantDocumentIds.Count == 0)
                throw new ArgumentException(
                    $"Validation query '{validationQuery.Query}' must list at least one relevant document.",
                    nameof(validationQueries));
        }
    }

    /// <summary>
    /// Runs the grid search and returns the best parameter pair together with the full grid.
    /// </summary>
    /// <param name="k1Values">Candidate saturation values; defaults to <c>0.5, 1.0, 1.2, 1.5, 2.0</c>.</param>
    /// <param name="bValues">Candidate length-normalization values; defaults to <c>0.0, 0.25, 0.5, 0.75, 1.0</c>.</param>
    /// <param name="topK">Retrieval depth used to judge each candidate ranking.</param>
    /// <param name="metric">Quality metric maximized over the validation set.</param>
    /// <returns>A <see cref="Bm25TuningResult"/> whose <see cref="Bm25TuningResult.Parameters"/>
    /// can be fed straight into a <see cref="Bm25Scorer"/> constructor.</returns>
    public Bm25TuningResult Tune(
        IEnumerable<double>? k1Values = null,
        IEnumerable<double>? bValues = null,
        int topK = 10,
        TuningMetric metric = TuningMetric.F1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);

        double[] k1Grid = (k1Values ?? DefaultK1Values).ToArray();
        double[] bGrid = (bValues ?? DefaultBValues).ToArray();

        if (k1Grid.Length == 0 || bGrid.Length == 0)
            throw new ArgumentException("Both the k1 and b grids must contain at least one value.");

        foreach (double k1 in k1Grid)
        {
            if (double.IsNaN(k1) || double.IsInfinity(k1) || k1 < 0)
                throw new ArgumentOutOfRangeException(nameof(k1Values), k1, "k1 values must be non-negative and finite.");
        }

        foreach (double b in bGrid)
        {
            if (double.IsNaN(b) || double.IsInfinity(b) || b is < 0 or > 1)
                throw new ArgumentOutOfRangeException(nameof(bValues), b, "b values must be within [0, 1] and finite.");
        }

        var grid = new List<Bm25GridPoint>(k1Grid.Length * bGrid.Length);
        Bm25GridPoint best = new(double.NaN, double.NaN, double.NegativeInfinity);

        foreach (double k1 in k1Grid)
        {
            foreach (double b in bGrid)
            {
                var point = new Bm25GridPoint(k1, b, Evaluate(new Bm25Parameters(k1, b), topK, metric));
                grid.Add(point);

                if (point.MetricScore > best.MetricScore)
                    best = point;
            }
        }

        return new Bm25TuningResult(
            new Bm25Parameters(best.K1, best.B),
            best.MetricScore,
            metric,
            topK,
            grid);
    }

    private double Evaluate(Bm25Parameters parameters, int topK, TuningMetric metric)
    {
        var engine = new RankedTextSearchEngine(_index, new Bm25Scorer(parameters), _tokenizer);

        double total = 0;

        foreach (var validationQuery in _validationQueries)
        {
            var retrievedIds = engine
                .Search(validationQuery.Query, new SearchOptions(topK))
                .Select(result => result.DocumentId)
                .ToArray();

            total += metric switch
            {
                TuningMetric.Precision => RetrievalMetrics.PrecisionAtK(retrievedIds, validationQuery.RelevantDocumentIds, topK),
                TuningMetric.Recall => RetrievalMetrics.RecallAtK(retrievedIds, validationQuery.RelevantDocumentIds, topK),
                TuningMetric.F1 => RetrievalMetrics.F1AtK(retrievedIds, validationQuery.RelevantDocumentIds, topK),
                TuningMetric.Ndcg => RetrievalMetrics.NdcgAtK(retrievedIds, validationQuery.RelevantDocumentIds, topK),
                _ => throw new ArgumentOutOfRangeException(nameof(metric), metric, "Unknown tuning metric."),
            };
        }

        return total / _validationQueries.Count;
    }
}