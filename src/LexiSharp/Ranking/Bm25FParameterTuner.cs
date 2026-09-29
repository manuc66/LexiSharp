using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Ranking;

/// <summary>One evaluated BM25F configuration and the quality it achieved.</summary>
/// <param name="K1">The evaluated term-frequency saturation.</param>
/// <param name="B">The evaluated document-length normalization.</param>
/// <param name="FieldWeights">
/// The field weights this point was evaluated with, or <c>null</c> for the first stage, where
/// every field was left neutral.
/// </param>
/// <param name="MetricScore">Mean value of the tuned <see cref="TuningMetric"/> over the validation set.</param>
public sealed record Bm25FGridPoint(
    double K1,
    double B,
    IReadOnlyDictionary<string, double>? FieldWeights,
    double MetricScore);

/// <summary>
/// The outcome of a <see cref="Bm25FParameterTuner.Tune"/> run: the best configuration found, the
/// quality it achieved, and the full evaluation grid for inspection.
/// </summary>
/// <param name="Parameters">
/// The best <see cref="Bm25FParameters"/> found on the grid. Its
/// <see cref="Bm25FParameters.FieldWeights"/> is never null from a tuner run — it is empty when the
/// winner turned out to be unweighted, so indexing it is safe without a null check.
/// </param>
/// <param name="MetricScore">Mean metric value achieved by <paramref name="Parameters"/> over the validation set.</param>
/// <param name="Metric">The metric that was optimized.</param>
/// <param name="TopK">The retrieval depth used while evaluating candidates.</param>
/// <param name="Grid">Every evaluated configuration, first stage then second, in evaluation order.</param>
/// <param name="UnweightedMetricScore">
/// The best score any <c>(k1, b)</c> reached with <b>no</b> field weighting — the baseline the
/// weighting has to beat. Read against <see cref="MetricScore"/> this is the answer to "did
/// weighting help?": a <see cref="MetricScore"/> at or below this means it did not.
/// </param>
/// <param name="EvaluatedConfigurations">How many configurations were run, for cost accounting.</param>
public sealed record Bm25FTuningResult(
    Bm25FParameters Parameters,
    double MetricScore,
    TuningMetric Metric,
    int TopK,
    IReadOnlyList<Bm25FGridPoint> Grid,
    double UnweightedMetricScore,
    int EvaluatedConfigurations)
{
    /// <summary>
    /// Whether the tuned weighting actually beat leaving every field neutral. False means the tuner
    /// found no weighting better than the unweighted baseline on this validation set — which is a
    /// real answer, and the one to act on, not a failure of the search.
    /// </summary>
    public bool WeightingHelped => MetricScore > UnweightedMetricScore;
}

/// <summary>
/// Finds the BM25F parameters (<c>k1</c>, <c>b</c>, and a weight per named field) that best satisfy
/// a set of labeled validation queries, by brute-force grid search in two stages.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two stages, not a product.</b> Stage 1 searches <c>(k1, b)</c> with every field left neutral.
/// Stage 2 then holds that winning pair and searches the field weights. A joint product search would
/// cost <c>|k1| × |b| × Π|weights|</c> evaluations; the staged search costs
/// <c>(|k1| × |b|) + Π|weights|</c>. That is coordinate descent, not an exhaustive search: the
/// winning <c>(k1, b)</c> is chosen assuming neutral weights and the winning weights assuming that
/// <c>(k1, b)</c>, so a configuration that is only good jointly can be missed. The cap on
/// configuration count is checked and reported rather than silently truncating the grid.
/// </para>
/// <para>
/// Every grid point runs a <see cref="RankedTextSearchEngine"/> over the <b>same</b>
/// <see cref="ITextIndex"/> with a fresh <see cref="Bm25FScorer"/>. The index is never mutated and no
/// scorer state is shared, so tuning is side-effect free. Ties keep the first point in evaluation
/// order, which makes the result deterministic.
/// </para>
/// <para>
/// <b>The result is an oracle, not a fair baseline.</b> The best of N configurations fitted on the
/// same N queries it is scored on is partly fitting noise, and the more configurations were tried the
/// more of the reported gain is that. Score the winning parameters on a held-out set, or the number
/// is an upper bound.
/// </para>
/// <para>
/// Requires an index that tracks per-field statistics. With no <c>weightedFields</c> to search the
/// run reduces to a plain <c>(k1, b)</c> search over a single-field ranking.
/// </para>
/// </remarks>
public sealed class Bm25FParameterTuner
{
    private static readonly double[] DefaultK1Values = [0.5, 1.0, 1.2, 1.5, 2.0];
    private static readonly double[] DefaultBValues = [0.0, 0.25, 0.5, 0.75, 1.0];
    private static readonly double[] DefaultWeightValues = [1.0, 1.5, 2.0, 3.0];

    /// <summary>
    /// Default ceiling on evaluated configurations. Beyond this the search is refused with the count,
    /// because a grid nobody waits for is a grid nobody trusts.
    /// </summary>
    private const int DefaultMaxConfigurations = 512;

    private readonly ITextIndex _index;
    private readonly ITokenizer _tokenizer;
    private readonly IReadOnlyList<Bm25ValidationQuery> _validationQueries;

    /// <param name="index">
    /// The indexed corpus to tune against (read-only for the tuner). Must track per-field statistics.
    /// </param>
    /// <param name="validationQueries">
    /// Labeled queries with the ids of their relevant documents. Every query must list at least one
    /// relevant document, otherwise recall is undefined.
    /// </param>
    /// <param name="tokenizer">
    /// Tokenizer for queries; should be the same one the index was built with.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The validation set is empty, or a query lists no relevant document.
    /// </exception>
    /// <exception cref="NotSupportedException">The index tracks no per-field statistics.</exception>
    public Bm25FParameterTuner(
        ITextIndex index,
        IEnumerable<Bm25ValidationQuery> validationQueries,
        ITokenizer? tokenizer = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(validationQueries);

        if (!index.HasFieldStatistics)
        {
            throw new NotSupportedException(
                $"BM25F tuning needs per-field statistics, and {index.GetType().Name} has none. " +
                "Index SearchDocument.TextFields, or use Bm25ParameterTuner for a single-field corpus.");
        }

        _index = index;
        _tokenizer = tokenizer ?? Tokenizer.Default;
        _validationQueries = validationQueries.ToList();

        if (_validationQueries.Count == 0)
        {
            throw new ArgumentException(
                "The validation set must contain at least one query.", nameof(validationQueries));
        }

        foreach (var validationQuery in _validationQueries)
        {
            ArgumentNullException.ThrowIfNull(validationQuery);

            if (validationQuery.RelevantDocumentIds.Count == 0)
            {
                throw new ArgumentException(
                    $"Validation query '{validationQuery.Query}' must list at least one relevant document.",
                    nameof(validationQueries));
            }
        }
    }

    /// <summary>
    /// Runs the two-stage grid search and returns the best configuration together with the full grid.
    /// </summary>
    /// <param name="k1Values">Candidate saturation values; defaults to <c>0.5, 1.0, 1.2, 1.5, 2.0</c>.</param>
    /// <param name="bValues">Candidate length-normalization values; defaults to <c>0.0, 0.25, 0.5, 0.75, 1.0</c>.</param>
    /// <param name="weightedFields">
    /// Fields whose weight stage 2 will search. Defaults to none, which reduces the run to a plain
    /// <c>(k1, b)</c> search. Name the fields you actually want to weight — every extra field
    /// multiplies the second stage's grid.
    /// </param>
    /// <param name="weightValues">
    /// Candidate weights, searched as a cartesian product across <paramref name="weightedFields"/>;
    /// defaults to <c>1.0, 1.5, 2.0, 3.0</c>. Including <c>1.0</c> matters: it is the neutral weight,
    /// so the search can conclude that no weighting beats none.
    /// </param>
    /// <param name="topK">Retrieval depth used to judge each candidate ranking.</param>
    /// <param name="metric">Quality metric maximized over the validation set.</param>
    /// <param name="maxConfigurations">
    /// Ceiling on evaluated configurations; defaults to <see cref="DefaultMaxConfigurations"/>. The
    /// run is refused above it rather than trimmed.
    /// </param>
    /// <returns>
    /// A <see cref="Bm25FTuningResult"/> whose <see cref="Bm25FTuningResult.Parameters"/> can be fed
    /// straight into a <see cref="Bm25FScorer"/> constructor, and whose
    /// <see cref="Bm25FTuningResult.WeightingHelped"/> says whether the weighting earned its place.
    /// </returns>
    /// <exception cref="ArgumentException">A grid is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A grid value is outside its valid range.</exception>
    public Bm25FTuningResult Tune(
        IEnumerable<double>? k1Values = null,
        IEnumerable<double>? bValues = null,
        IEnumerable<string>? weightedFields = null,
        IEnumerable<double>? weightValues = null,
        int topK = 10,
        TuningMetric metric = TuningMetric.F1,
        int maxConfigurations = DefaultMaxConfigurations)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConfigurations);

        double[] k1Grid = (k1Values ?? DefaultK1Values).ToArray();
        double[] bGrid = (bValues ?? DefaultBValues).ToArray();
        double[] weightGrid = (weightValues ?? DefaultWeightValues).ToArray();
        string[] fields = (weightedFields ?? []).ToArray();

        if (k1Grid.Length == 0 || bGrid.Length == 0)
            throw new ArgumentException("Both the k1 and b grids must contain at least one value.");

        if (fields.Length > 0 && weightGrid.Length == 0)
            throw new ArgumentException("The weight grid must contain at least one value when fields are named.");

        if (k1Grid.Any(k1 => !double.IsFinite(k1) || k1 < 0))
            throw new ArgumentOutOfRangeException(nameof(k1Values), "k1 values must be non-negative and finite.");

        if (bGrid.Any(b => !double.IsFinite(b) || b is < 0 or > 1))
            throw new ArgumentOutOfRangeException(nameof(bValues), "b values must be within [0, 1] and finite.");

        if (weightGrid.Any(weight => !double.IsFinite(weight) || weight < 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(weightValues), "Field weights must be non-negative and finite.");
        }

        foreach (string field in fields)
            TextFields.Validate(field, nameof(weightedFields));

        var unknown = fields.Where(field => !_index.Fields.Contains(field)).ToArray();

        if (unknown.Length > 0)
        {
            throw new ArgumentException(
                $"The index has no field(s) named {string.Join(", ", unknown)}. It tracks: " +
                $"{string.Join(", ", _index.Fields.Select(Describe))}.",
                nameof(weightedFields));
        }

        long weightCombinations = WeightCombinations(weightGrid.Length, fields.Length);
        long total = ((long)k1Grid.Length * bGrid.Length) + weightCombinations;

        if (total > maxConfigurations)
        {
            throw new ArgumentException(
                $"This search would evaluate {total} configurations, above the cap of " +
                $"{maxConfigurations}. Narrow a grid, weight fewer fields, or raise " +
                $"{nameof(maxConfigurations)} deliberately — a grid this size fits its noise.",
                nameof(maxConfigurations));
        }

        var grid = new List<Bm25FGridPoint>((int)Math.Min(total, int.MaxValue));
        var best = new Bm25FGridPoint(double.NaN, double.NaN, null, double.NegativeInfinity);

        // Stage 1: (k1, b), every field neutral.
        foreach (double k1 in k1Grid)
        {
            foreach (double b in bGrid)
            {
                var point = new Bm25FGridPoint(k1, b, null, Evaluate(k1, b, null, topK, metric));
                grid.Add(point);

                if (point.MetricScore > best.MetricScore)
                    best = point;
            }
        }

        double unweightedScore = best.MetricScore;

        // Stage 2: hold stage 1's winner, search the weight product.
        foreach (var weights in WeightProducts(weightGrid, fields))
        {
            var point = new Bm25FGridPoint(
                best.K1, best.B, weights, Evaluate(best.K1, best.B, weights, topK, metric));

            grid.Add(point);

            if (point.MetricScore > best.MetricScore)
                best = point;
        }

        // Normalized to an empty map rather than null when the winner is unweighted: a caller
        // reaching for Parameters.FieldWeights["title"] must not have to null-check a field the
        // search never set. Use WeightingHelped to ask whether weighting was used at all.
        var winningWeights = best.FieldWeights is { Count: > 0 }
            ? new Dictionary<string, double>(best.FieldWeights, StringComparer.Ordinal)
            : new Dictionary<string, double>(StringComparer.Ordinal);

        return new Bm25FTuningResult(
            new Bm25FParameters(best.K1, best.B, winningWeights),
            best.MetricScore,
            metric,
            topK,
            grid,
            unweightedScore,
            grid.Count);
    }

    private static string Describe(string field) => field.Length == 0 ? "<default>" : field;

    private static long WeightCombinations(int weightCount, int fieldCount)
    {
        long combinations = 1;

        for (int i = 0; i < fieldCount; i++)
        {
            combinations *= weightCount;

            // Guard the multiplication itself: a large product must be refused, not overflowed into
            // a small number that passes the cap.
            if (combinations > long.MaxValue / 2)
                return long.MaxValue;
        }

        return combinations;
    }

    /// <summary>
    /// The cartesian product of <paramref name="weightGrid"/> across <paramref name="fields"/>, with
    /// the fields visited in the order given, so the sequence is deterministic.
    /// </summary>
    private static IEnumerable<IReadOnlyDictionary<string, double>> WeightProducts(
        double[] weightGrid,
        string[] fields)
    {
        if (fields.Length == 0)
        {
            yield return new Dictionary<string, double>(StringComparer.Ordinal);
            yield break;
        }

        int total = 1;

        for (int i = 0; i < fields.Length; i++)
            total *= weightGrid.Length;

        for (int combination = 0; combination < total; combination++)
        {
            var weights = new Dictionary<string, double>(fields.Length, StringComparer.Ordinal);
            int remaining = combination;

            for (int f = 0; f < fields.Length; f++)
            {
                weights[fields[f]] = weightGrid[remaining % weightGrid.Length];
                remaining /= weightGrid.Length;
            }

            yield return weights;
        }
    }

    private double Evaluate(
        double k1,
        double b,
        IReadOnlyDictionary<string, double>? fieldWeights,
        int topK,
        TuningMetric metric)
    {
        var scorer = fieldWeights is { Count: > 0 }
            ? new Bm25FScorer(k1, b, fieldWeights)
            : new Bm25FScorer(k1, b);

        var engine = new RankedTextSearchEngine(_index, scorer, _tokenizer);
        double total = 0;

        foreach (var validationQuery in _validationQueries)
        {
            var retrievedIds = engine
                .Search(validationQuery.Query, new SearchOptions(topK, ExcludedDocumentIds: validationQuery.ExcludedDocumentIds))
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
