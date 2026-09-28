using LexiSharp.Core;
using LexiSharp.Linguistics;

// CA1859 ("use a concrete type instead of the interface") is suppressed on the lines
// below. The interface is the published return type: a List<T> or an array in its place
// would hand callers a mutable collection through a contract that says they cannot have
// one, and what it saves is a single interface dispatch per call, which no measurement in
// docs/benchmarks.md attributes time to.

namespace LexiSharp.Ranking;

/// <summary>One evaluated <c>(k1, b, delta)</c> combination and the quality it achieved.</summary>
/// <param name="K1">The evaluated term-frequency saturation.</param>
/// <param name="B">The evaluated document-length normalization.</param>
/// <param name="Delta">The evaluated lower bound on the term weight.</param>
/// <param name="MetricScore">Mean value of the tuned <see cref="TuningMetric"/> over the validation set.</param>
internal sealed record Bm25VariantGridPoint(double K1, double B, double Delta, double MetricScore);

/// <summary>What a variant grid search found, before it is shaped into a variant's public result.</summary>
internal sealed record Bm25VariantSearchResult(
    IReadOnlyList<Bm25VariantGridPoint> Grid,
    double K1,
    double B,
    double Delta,
    double MetricScore,
    double UnflooredMetricScore,
    int EvaluatedConfigurations);

/// <summary>
/// The search that <see cref="Bm25PlusParameterTuner"/> and <see cref="Bm25LParameterTuner"/> share:
/// an exhaustive product grid over <c>(k1, b, delta)</c>, scored with a scorer the caller supplies.
/// </summary>
/// <remarks>
/// <para>
/// Shared because the two variants differ only in which scorer the parameters go to, and a third
/// copy of the grid loop would be three places to fix the same bug. The two public result types stay
/// separate so a BM25+ <c>(k1, b, delta)</c> cannot be handed to a <see cref="Bm25LScorer"/> by
/// accident — the parameters are three bare doubles with nothing in their type to catch that.
/// </para>
/// <para>
/// <b>Exhaustive, not coordinate descent.</b> The product is searched in full, so a configuration
/// that is only good jointly is found. This is the deliberate contrast with
/// <see cref="Bm25FParameterTuner"/>, whose weight stage is a product over fields and therefore
/// staged: the two agree on cost only while the grids stay small, which is what
/// <c>maxConfigurations</c> is for.
/// </para>
/// </remarks>
internal static class Bm25VariantTunerCore
{
    /// <summary>
    /// Default ceiling on evaluated configurations. Beyond this the search is refused with the
    /// count, because a grid nobody waits for is a grid nobody trusts — and the more configurations
    /// were scored on the same queries, the more of the winner's margin is fitting noise.
    /// </summary>
    internal const int DefaultMaxConfigurations = 512;

    /// <summary>Default candidate lower bounds: no floor, then the range both papers report in.</summary>
    internal static readonly double[] DefaultDeltaValues = [0.0, 0.25, 0.5, 0.75, 1.0];

    /// <summary>
    /// Copies and checks the validation set. Every query needs at least one relevant document,
    /// otherwise recall — and F1 with it — is undefined rather than zero.
    /// </summary>
    internal static IReadOnlyList<Bm25ValidationQuery> ValidateValidationSet(
        IEnumerable<Bm25ValidationQuery> validationQueries,
        string paramName)
    {
        ArgumentNullException.ThrowIfNull(validationQueries);

        var queries = validationQueries.ToList();

        if (queries.Count == 0)
            throw new ArgumentException("The validation set must contain at least one query.", paramName);

        foreach (var validationQuery in queries)
        {
            ArgumentNullException.ThrowIfNull(validationQuery);

            if (validationQuery.RelevantDocumentIds.Count == 0)
            {
                throw new ArgumentException(
                    $"Validation query '{validationQuery.Query}' must list at least one relevant document.",
                    paramName);
            }
        }

        return queries;
    }

    /// <summary>Materializes the three grids and checks every value against its valid range.</summary>
    internal static (double[] K1, double[] B, double[] Delta) ResolveGrids(
        IEnumerable<double>? k1Values,
        IEnumerable<double>? bValues,
        IEnumerable<double>? deltaValues)
    {
        double[] k1Grid = (k1Values ?? [0.5, 1.0, 1.2, 1.5, 2.0]).ToArray();
        double[] bGrid = (bValues ?? [0.0, 0.25, 0.5, 0.75, 1.0]).ToArray();
        double[] deltaGrid = (deltaValues ?? DefaultDeltaValues).ToArray();

        if (k1Grid.Length == 0 || bGrid.Length == 0)
            throw new ArgumentException("Both the k1 and b grids must contain at least one value.");

        if (deltaGrid.Length == 0)
            throw new ArgumentException("The delta grid must contain at least one value.");

        if (k1Grid.Any(k1 => !double.IsFinite(k1) || k1 < 0))
            throw new ArgumentOutOfRangeException(nameof(k1Values), "k1 values must be non-negative and finite.");

        if (bGrid.Any(b => !double.IsFinite(b) || b is < 0 or > 1))
            throw new ArgumentOutOfRangeException(nameof(bValues), "b values must be within [0, 1] and finite.");

        if (deltaGrid.Any(delta => !double.IsFinite(delta) || delta < 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(deltaValues), "delta values must be non-negative and finite.");
        }

        return (k1Grid, bGrid, deltaGrid);
    }

    /// <summary>
    /// Scores every <c>(k1, b, delta)</c> combination and returns the grid, the winner and the best
    /// any combination reached with <b>no</b> lower bound — the baseline the bound has to beat.
    /// </summary>
    internal static Bm25VariantSearchResult Run(
        ITextIndex index,
        ITokenizer tokenizer,
        IReadOnlyList<Bm25ValidationQuery> validationQueries,
        double[] k1Grid,
        double[] bGrid,
        double[] deltaGrid,
        int topK,
        TuningMetric metric,
        int maxConfigurations,
        Func<double, double, double, ITextScorer> scorerFactory)
    {
        long total = (long)k1Grid.Length * bGrid.Length * deltaGrid.Length;

        if (total > maxConfigurations)
        {
            throw new ArgumentException(
                $"This search would evaluate {total} configurations, above the cap of {maxConfigurations}. " +
                "Narrow a grid — delta is the cheapest axis to drop — or raise " +
                $"{nameof(maxConfigurations)} deliberately, because a grid this size fits its noise.",
                nameof(maxConfigurations));
        }

        var grid = new List<Bm25VariantGridPoint>((int)total);
        var best = new Bm25VariantGridPoint(double.NaN, double.NaN, double.NaN, double.NegativeInfinity);
        double bestUnfloored = double.NegativeInfinity;

        // Ascending k1, then b, then delta: a first-encountered strict maximum is the winner, which
        // makes the result deterministic and biases a tie towards the least aggressive parameters.
        foreach (double k1 in k1Grid)
        {
            foreach (double b in bGrid)
            {
                foreach (double delta in deltaGrid)
                {
                    // A fresh engine per point, over the same index. The index is read-only here and
                    // no scorer state is shared, so tuning mutates nothing.
                    var engine = new RankedTextSearchEngine(
                        index, scorerFactory(k1, b, delta), tokenizer);

                    var point = new Bm25VariantGridPoint(
                        k1, b, delta, Evaluate(engine, validationQueries, topK, metric));

                    grid.Add(point);

                    if (point.MetricScore > best.MetricScore)
                        best = point;

                    if (point.Delta == 0 && point.MetricScore > bestUnfloored)
                        bestUnfloored = point.MetricScore;
                }
            }
        }

        // NaN rather than -Infinity when the delta grid held no 0: there is then nothing to compare
        // against, and a baseline of "nothing beat nothing" would be an answer nobody asked for.
        // Every comparison against NaN is false, so DeltaHelped reports false rather than guessing.
        return new Bm25VariantSearchResult(
            grid,
            best.K1,
            best.B,
            best.Delta,
            best.MetricScore,
            double.IsNegativeInfinity(bestUnfloored) ? double.NaN : bestUnfloored,
            grid.Count);
    }

    private static double Evaluate(
        ITextSearchEngine engine, // NOSONAR:CA1859
        IReadOnlyList<Bm25ValidationQuery> validationQueries,
        int topK,
        TuningMetric metric)
    {
        double total = 0;

        foreach (var validationQuery in validationQueries)
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

        return total / validationQueries.Count;
    }
}
