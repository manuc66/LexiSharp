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
    /// Tunes BM25 on the given validation set with the original grid-only search.
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
        int resamples = 1000) =>
        TuneWithRefinement(index, validationSet, k, k1Grid, bGrid, resamples, refine: false);

    /// <summary>
    /// Tunes BM25 on the given validation set, optionally refining the grid winner by coordinate
    /// descent.
    /// </summary>
    /// <param name="index">The index the queries are scored against (read-only view is enough).</param>
    /// <param name="validationSet">Pairs of (query text, graded relevance: document id → gain).</param>
    /// <param name="k">Cut-off for nDCG (default 10).</param>
    /// <param name="k1Grid">k1 values to explore (default <see cref="DefaultK1Grid"/>).</param>
    /// <param name="bGrid">b values to explore (default <see cref="DefaultBGrid"/>).</param>
    /// <param name="resamples">Bootstrap resamples for the confidence interval (default 1000).</param>
    /// <param name="refine">Whether to refine the grid winner by coordinate descent (default false).</param>
    public static TuningResult TuneWithRefinement(
        IReadOnlyTextIndex index,
        IEnumerable<(string Query, IReadOnlyDictionary<string, double> Relevance)> validationSet,
        int k = 10,
        double[]? k1Grid = null,
        double[]? bGrid = null,
        int resamples = 1000,
        bool refine = false)
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
                points.Add(Evaluate(index, queries, k, resamples, k1, b));
            }
        }

        TuningPoint best = points.OrderByDescending(p => p.MeanNdcg).First();

        // Coordinate-descent refinement around the grid winner: optimize k1 (holding b fixed)
        // then b (holding k1 fixed), repeated a few times. This is a local, deterministic
        // refinement that only ever keeps a point if it strictly improves the mean nDCG, so it
        // cannot regress below the grid winner.
        if (refine)
        {
            double k1 = best.K1;
            double b = best.B;

            for (int iteration = 0; iteration < 5; iteration++)
            {
                double newK1 = GoldenSection(k1v =>
                    EvaluateMean(index, queries, k, k1v, b),
                    Math.Max(0.1, k1 - 0.5), k1 + 0.5, maximize: true);
                double newB = GoldenSection(bv =>
                    EvaluateMean(index, queries, k, newK1, bv),
                    Math.Max(0.0, b - 0.25), Math.Min(1.0, b + 0.25), maximize: true);

                TuningPoint refined = Evaluate(index, queries, k, resamples, newK1, newB);
                if (refined.MeanNdcg > best.MeanNdcg + 1e-12)
                {
                    best = refined;
                    points.Add(refined);
                    k1 = newK1;
                    b = newB;
                }
                else
                {
                    break;
                }
            }
        }

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

    private static TuningPoint Evaluate(
        IReadOnlyTextIndex index,
        (string Query, IReadOnlyDictionary<string, double> Relevance)[] queries,
        int k,
        int resamples,
        double k1,
        double b)
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
        return new TuningPoint(k1, b, mean, lower, upper, ndcgs);
    }

    private static double EvaluateMean(
        IReadOnlyTextIndex index,
        (string Query, IReadOnlyDictionary<string, double> Relevance)[] queries,
        int k,
        double k1,
        double b)
    {
        var scorer = new Bm25Scorer(k1, b);
        var engine = new RankedTextSearchEngine(index, scorer);

        double total = 0;
        for (int i = 0; i < queries.Length; i++)
        {
            var results = engine.Search(queries[i].Query, new SearchOptions(Limit: k));
            var retrieved = results.Select(r => r.DocumentId).ToList();
            total += RetrievalMetrics.NdcgAtK(retrieved, queries[i].Relevance, k);
        }

        return total / queries.Length;
    }

    private static double GoldenSection(Func<double, double> f, double lo, double hi, bool maximize)
    {
        if (hi - lo < 1e-9)
        {
            return (lo + hi) / 2.0;
        }

        const double phi = 1.618033988749895;
        double x1 = hi - (hi - lo) / phi;
        double x2 = lo + (hi - lo) / phi;
        double f1 = f(x1);
        double f2 = f(x2);

        for (int i = 0; i < 60; i++)
        {
            if (maximize ? f1 < f2 : f1 > f2)
            {
                hi = x2;
                x2 = x1;
                f2 = f1;
                x1 = hi - (hi - lo) / phi;
                f1 = f(x1);
            }
            else
            {
                lo = x1;
                x1 = x2;
                f1 = f2;
                x2 = lo + (hi - lo) / phi;
                f2 = f(x2);
            }

            if (hi - lo < 1e-9)
            {
                break;
            }
        }

        return (lo + hi) / 2.0;
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