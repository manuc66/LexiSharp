using System.Globalization;
using LexiSharp.Core;

namespace LexiSharp.Ranking;

/// <summary>
/// Auto-tunes the BM25 parameters (<c>k1</c>, <c>b</c>) of a corpus by grid search on a
/// validation set of (query, graded-relevance) pairs, and reports the best configuration with
/// its mean nDCG@k and a paired-bootstrap confidence interval.
/// </summary>
/// <remarks>
/// <para>
/// The tuning is corpus-specific: BM25's defaults (<c>k1 = 1.2</c>, <c>b = 0.75</c>) are rarely
/// optimal for a given corpus, but the optimum is stable within a corpus. This helper finds it
/// without any external dependency — only the index, the queries and the relevance judgements.
/// </para>
/// <para>
/// The grid is small and exhaustive (6 × 5 = 30 points) because BM25's surface is smooth and
/// unimodal in (k1, b); a finer grid rarely moves nDCG by more than the bootstrap noise. The
/// result is a <see cref="TuningResult"/> carrying the best parameters, the mean nDCG@k, the
/// bootstrap interval, and the full grid so a caller can inspect the landscape.
/// </para>
/// <para>
/// This is the "zero configuration" entry point: a caller who has a labelled validation set
/// (even a small one) can replace the defaults with a corpus-specific profile and know whether
/// the gain is real (the interval does not contain zero).
/// </para>
/// </remarks>
public static class Bm25Tuner
{
    /// <summary>Default k1 values explored by the grid.</summary>
    public static readonly double[] DefaultK1Grid = [0.5, 0.8, 1.0, 1.2, 1.5, 2.0];

    /// <summary>Default b values explored by the grid.</summary>
    public static readonly double[] DefaultBGrid = [0.0, 0.25, 0.5, 0.75, 1.0];

    /// <summary>
    /// Tunes BM25 on the given validation set.
    /// </summary>
    /// <param name="index">The index the queries are scored against (read-only view is enough).</param>
    /// <param name="validationSet">Pairs of (query text, graded relevance: document id → gain).</param>
    /// <param name="k">Cut-off for nDCG (default 10).</param>
    /// <param name="k1Grid">k1 values to explore (default <see cref="DefaultK1Grid"/>).</param>
    /// <param name="bGrid">b values to explore (default <see cref="DefaultBGrid"/>).</param>
    /// <param name="resamples">Bootstrap resamples for the confidence interval (default 1000).</param>
    public static TuningResult Tune(
        IReadOnlyTextIndex index,
        IEnumerable<(string Query, IReadOnlyDictionary<string, double> Relevance)> validationSet,
        int k = 10,
        double[]? k1Grid = null,
        double[]? bGrid = null,
        int resamples = 1000)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(validationSet);

        k1Grid ??= DefaultK1Grid;
        bGrid ??= DefaultBGrid;

        var queries = validationSet.ToArray();
        if (queries.Length == 0)
            throw new ArgumentException("The validation set must contain at least one query.", nameof(validationSet));

        var points = new List<TuningPoint>(k1Grid.Length * bGrid.Length);

        foreach (double k1 in k1Grid)
        {
            foreach (double b in bGrid)
            {
                var scorer = new Bm25Scorer(k1, b);
                var engine = new RankedTextSearchEngine(index, scorer);

                double[] ndcgs = new double[queries.Length];
                for (int i = 0; i < queries.Length; i++)
                {
                    var results = engine.Search(queries[i].Query, new SearchOptions(Limit: k));
                    var retrieved = results.Select(r => r.DocumentId).ToList();
                    ndcgs[i] = RetrievalMetrics.NdcgAtK(retrieved, queries[i].Relevance, k);
                }

                double mean = ndcgs.Average();
                (double lower, double upper) = BootstrapInterval(ndcgs, resamples);

                points.Add(new TuningPoint(k1, b, mean, lower, upper, ndcgs));
            }
        }

        TuningPoint best = points.OrderByDescending(p => p.MeanNdcg).First();

        // Baseline = defaults (k1=1.2, b=0.75) if present in the grid, else the first point.
        TuningPoint baseline = points.FirstOrDefault(p => p.K1 == 1.2 && p.B == 0.75) ?? points[0];

        double[] deltas = new double[best.PerQueryNdcg.Length];
        for (int i = 0; i < deltas.Length; i++)
            deltas[i] = best.PerQueryNdcg[i] - baseline.PerQueryNdcg[i];

        (double deltaLower, double deltaUpper) = BootstrapInterval(deltas, resamples);

        return new TuningResult(
            best.K1,
            best.B,
            best.MeanNdcg,
            best.Lower,
            best.Upper,
            best.MeanNdcg - baseline.MeanNdcg,
            deltaLower,
            deltaUpper,
            best.MeanNdcg - baseline.MeanNdcg > 0 && deltaLower > 0,
            points);
    }

    private static (double Lower, double Upper) BootstrapInterval(double[] values, int resamples)
    {
        var rng = new Random(20261122);
        var means = new double[resamples];
        for (int b = 0; b < resamples; b++)
        {
            double sum = 0;
            for (int i = 0; i < values.Length; i++)
                sum += values[rng.Next(values.Length)];
            means[b] = sum / values.Length;
        }
        Array.Sort(means);
        return (Percentile(means, 0.025), Percentile(means, 0.975));
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        double position = percentile * (sorted.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);
        return lower == upper
            ? sorted[lower]
            : sorted[lower] + ((sorted[upper] - sorted[lower]) * (position - lower));
    }
}

/// <summary>A single point in the (k1, b) tuning grid.</summary>
public sealed record TuningPoint(
    double K1,
    double B,
    double MeanNdcg,
    double Lower,
    double Upper,
    double[] PerQueryNdcg);

/// <summary>The result of a <see cref="Bm25Tuner.Tune"/> call.</summary>
public sealed record TuningResult(
    double BestK1,
    double BestB,
    double BestMeanNdcg,
    double BestLower,
    double BestUpper,
    double DeltaVsDefault,
    double DeltaLower,
    double DeltaUpper,
    bool IsSignificant,
    IReadOnlyList<TuningPoint> Grid);