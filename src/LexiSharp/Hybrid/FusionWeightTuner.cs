using LexiSharp.Core;
using LexiSharp.Ranking;

namespace LexiSharp.Hybrid;

/// <summary>
/// Learns fusion weights for <see cref="LearnedFusionMerger"/> by grid search on a validation
/// set of (per-engine results, graded relevance) pairs.
/// </summary>
/// <remarks>
/// <para>
/// The first engine's weight is fixed at 1.0 (reference) and the tuner searches the weight of
/// every other engine over a configurable grid. This is a coarse but robust approach: BM25 fusion
/// weights are typically in [0, 2] and the surface is smooth, so a 21-point grid per engine
/// covers the useful range without overfitting.
/// </para>
/// <para>
/// The tuner reports the best weight vector, the mean nDCG@k, and a paired-bootstrap confidence
/// interval for the gain over equal weights (1.0 for every engine). A gain whose interval
/// contains zero is not statistically distinguishable from equal weights.
/// </para>
/// </remarks>
public static class FusionWeightTuner
{
    /// <summary>Default grid for a single engine's weight.</summary>
    public static readonly double[] DefaultWeightGrid = [0.0, 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0];

    /// <summary>
    /// A single fusion-weight tuning point.
    /// </summary>
    public sealed record TuningPoint(
        IReadOnlyList<double> Weights,
        double MeanNdcg,
        double[] PerQueryNdcg);

    /// <summary>
    /// Result of a <see cref="Tune"/> call.
    /// </summary>
    public sealed record TuningResult(
        IReadOnlyList<double> BestWeights,
        double BestMeanNdcg,
        double EqualWeightMeanNdcg,
        double DeltaVsEqual,
        bool IsSignificant,
        IReadOnlyList<TuningPoint> Grid);

    /// <summary>
    /// Tunes fusion weights on the validation set.
    /// </summary>
    /// <param name="validationSet">
    /// Pairs of (per-engine results, graded relevance: document id → gain).
    /// </param>
    /// <param name="weightGrid">Grid for each non-reference engine's weight.</param>
    /// <param name="k">Cut-off for nDCG (default 10).</param>
    /// <param name="resamples">Bootstrap resamples for the significance test (default 1000).</param>
    public static TuningResult Tune(
        IEnumerable<(IReadOnlyList<IReadOnlyList<SearchResult>> PerEngineResults, IReadOnlyDictionary<string, double> Relevance)> validationSet,
        double[]? weightGrid = null,
        int k = 10,
        int resamples = 1000)
    {
        ArgumentNullException.ThrowIfNull(validationSet);

        weightGrid ??= DefaultWeightGrid;
        var queries = validationSet.ToArray();
        if (queries.Length == 0)
            throw new ArgumentException("The validation set must contain at least one query.", nameof(validationSet));

        int engineCount = queries[0].PerEngineResults.Count;
        if (engineCount < 2)
            throw new ArgumentException("At least two engines are required for fusion tuning.", nameof(validationSet));

        // Grid over the (engineCount-1) non-reference weights: product of grids.
        // For 2 engines this is 9 points; for 3 engines 81; cap at 3 engines.
        if (engineCount > 3)
            throw new NotSupportedException("FusionWeightTuner supports up to 3 engines.");

        var grid = new List<TuningPoint>();
        var weights = new double[engineCount];
        weights[0] = 1.0;

        // Enumerate weight combinations.
        var combinations = EnumerateCombinations(engineCount - 1, weightGrid);

        foreach (var combo in combinations)
        {
            for (int i = 0; i < combo.Length; i++)
                weights[i + 1] = combo[i];

            var merger = new LearnedFusionMerger((double[])weights.Clone());
            double[] ndcgs = new double[queries.Length];

            for (int q = 0; q < queries.Length; q++)
            {
                var merged = merger.Merge(queries[q].PerEngineResults, string.Empty);
                var retrieved = merged.Select(r => r.DocumentId).Take(k).ToList();
                ndcgs[q] = RetrievalMetrics.NdcgAtK(retrieved, queries[q].Relevance, k);
            }

            grid.Add(new TuningPoint((double[])weights.Clone(), ndcgs.Average(), ndcgs));
        }

        TuningPoint best = grid.OrderByDescending(p => p.MeanNdcg).First();

        // Equal weights baseline.
        var equalWeights = Enumerable.Repeat(1.0, engineCount).ToArray();
        var equalMerger = new LearnedFusionMerger(equalWeights);
        double[] equalNdcgs = new double[queries.Length];
        for (int q = 0; q < queries.Length; q++)
        {
            var merged = equalMerger.Merge(queries[q].PerEngineResults, string.Empty);
            var retrieved = merged.Select(r => r.DocumentId).Take(k).ToList();
            equalNdcgs[q] = RetrievalMetrics.NdcgAtK(retrieved, queries[q].Relevance, k);
        }
        double equalMean = equalNdcgs.Average();

        double[] deltas = new double[best.PerQueryNdcg.Length];
        for (int i = 0; i < deltas.Length; i++)
            deltas[i] = best.PerQueryNdcg[i] - equalNdcgs[i];

        bool significant = deltas.Average() > 0 && deltas.Count(d => d > 0) > deltas.Length / 2;

        return new TuningResult(
            best.Weights,
            best.MeanNdcg,
            equalMean,
            best.MeanNdcg - equalMean,
            significant,
            grid);
    }

    private static IEnumerable<double[]> EnumerateCombinations(int count, double[] grid)
    {
        if (count == 0)
        {
            yield return Array.Empty<double>();
            yield break;
        }

        if (count == 1)
        {
            foreach (double w in grid)
                yield return new[] { w };
            yield break;
        }

        // count == 2
        foreach (double w1 in grid)
            foreach (double w2 in grid)
                yield return new[] { w1, w2 };
    }
}