namespace LexiSharp.Benchmarking;

/// <summary>How a query's nDCG moved between two configurations.</summary>
public enum BenchmarkDeltaVerdict
{
    /// <summary>The candidate scored below the baseline.</summary>
    Degraded = -1,

    /// <summary>The two scored the same, within the comparison's tolerance.</summary>
    Unchanged = 0,

    /// <summary>The candidate scored above the baseline.</summary>
    Improved = 1,
}

/// <summary>
/// One query's movement between two benchmark runs, with the evidence for it.
/// </summary>
/// <param name="QueryId">The query's id, the join key between the two runs.</param>
/// <param name="QueryText">The raw query.</param>
/// <param name="BaselineNdcg">The baseline run's nDCG@k for this query.</param>
/// <param name="CandidateNdcg">The candidate run's nDCG@k for this query.</param>
/// <param name="Delta">Candidate minus baseline.</param>
/// <param name="Verdict">Which way it moved, after the tolerance.</param>
/// <param name="BaselineFirstRelevantRank">Where the first judged document landed, or null.</param>
/// <param name="CandidateFirstRelevantRank">Same, for the candidate run.</param>
public sealed record BenchmarkQueryDelta(
    string QueryId,
    string QueryText,
    double BaselineNdcg,
    double CandidateNdcg,
    double Delta,
    BenchmarkDeltaVerdict Verdict,
    int? BaselineFirstRelevantRank,
    int? CandidateFirstRelevantRank)
{
    /// <summary>
    /// A one-line reason, phrased from what actually happened rather than restating the numbers.
    /// The three causes need different fixes, so collapsing them into a delta would waste the
    /// diagnostic.
    /// </summary>
    public string Reading => (BaselineFirstRelevantRank, CandidateFirstRelevantRank) switch
    {
        (null, null) => "neither run retrieved a judged document",
        (null, not null) => $"rescued: first judged document now at rank {CandidateFirstRelevantRank}",
        (not null, null) => $"lost: the judged document at rank {BaselineFirstRelevantRank} is gone",

        _ when CandidateFirstRelevantRank < BaselineFirstRelevantRank =>
            $"promoted: rank {BaselineFirstRelevantRank} -> {CandidateFirstRelevantRank}",
        _ when CandidateFirstRelevantRank > BaselineFirstRelevantRank =>
            $"demoted: rank {BaselineFirstRelevantRank} -> {CandidateFirstRelevantRank}",

        _ => $"same rank {BaselineFirstRelevantRank}, score moved on the rest of the page",
    };
}

/// <summary>
/// A baseline-versus-candidate comparison, per query and in aggregate. This is the artifact that
/// answers "what did this change actually do", which a pair of means cannot: two configurations
/// can have near-identical averages while one rescued four queries and lost four others.
/// </summary>
/// <param name="BaselineName">The reference configuration's name.</param>
/// <param name="CandidateName">The evaluated configuration's name.</param>
/// <param name="Queries">Every compared query, in query-set order.</param>
/// <param name="Improved">Queries the candidate scored better on, best gain first.</param>
/// <param name="Degraded">Queries the candidate scored worse on, worst regression first.</param>
/// <param name="Unchanged">Queries that did not move beyond the tolerance.</param>
/// <param name="MeanDelta">
/// Mean of the raw per-query nDCG differences: plain arithmetic, <b>not</b> filtered by the
/// comparison's tolerance, which classifies queries rather than adjusting the average.
/// </param>
public sealed record BenchmarkComparison(
    string BaselineName,
    string CandidateName,
    IReadOnlyList<BenchmarkQueryDelta> Queries,
    IReadOnlyList<BenchmarkQueryDelta> Improved,
    IReadOnlyList<BenchmarkQueryDelta> Degraded,
    IReadOnlyList<BenchmarkQueryDelta> Unchanged,
    double MeanDelta)
{
    /// <summary>Queries the candidate scored strictly better on.</summary>
    public int ImprovedCount => Improved.Count;

    /// <summary>Queries the candidate scored strictly worse on.</summary>
    public int DegradedCount => Degraded.Count;

    /// <summary>Queries that did not move beyond the tolerance.</summary>
    public int UnchangedCount => Unchanged.Count;

    /// <summary>
    /// <c>Improved - Degraded</c>. A net of zero is not "no change": it is changes that
    /// cancelled out, which is exactly the case a mean-only report hides.
    /// </summary>
    public int Net => Improved.Count - Degraded.Count;
}

/// <summary>Builds a <see cref="BenchmarkComparison"/> from two runs.</summary>
public static class BenchmarkComparer
{
    /// <summary>
    /// Compares two runs query by query. Both must have been produced by
    /// <see cref="CorpusBenchmark"/> over the same query set in the same order; a run without
    /// per-query data cannot be compared, and saying so is better than reporting a comparison
    /// against nothing.
    /// </summary>
    /// <param name="baseline">The reference run.</param>
    /// <param name="candidate">The run being evaluated.</param>
    /// <param name="epsilon">
    /// Score difference below which a query counts as unchanged, to keep floating-point noise and
    /// meaningless decimals from being reported as a movement. Default <c>1e-9</c>.
    /// </param>
    public static BenchmarkComparison Compare(
        BenchmarkConfigResult baseline,
        BenchmarkConfigResult candidate,
        double epsilon = 1e-9)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentOutOfRangeException.ThrowIfNegative(epsilon);

        if (baseline.PerQuery.Count == 0 || candidate.PerQuery.Count == 0)
        {
            throw new ArgumentException(
                "Both runs need per-query results. They come from CorpusBenchmark.Run; a hand-built BenchmarkConfigResult has none.",
                baseline.PerQuery.Count == 0 ? nameof(baseline) : nameof(candidate));
        }

        if (baseline.PerQuery.Count != candidate.PerQuery.Count)
        {
            throw new ArgumentException(
                $"The runs cover different query counts ({baseline.PerQuery.Count} vs {candidate.PerQuery.Count}), so they are not comparable.",
                nameof(candidate));
        }

        var deltas = new List<BenchmarkQueryDelta>(baseline.PerQuery.Count);
        var improved = new List<BenchmarkQueryDelta>();
        var degraded = new List<BenchmarkQueryDelta>();
        var unchanged = new List<BenchmarkQueryDelta>();

        for (int i = 0; i < baseline.PerQuery.Count; i++)
        {
            BenchmarkQueryResult before = baseline.PerQuery[i];
            BenchmarkQueryResult after = candidate.PerQuery[i];

            if (!string.Equals(before.QueryId, after.QueryId, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"The runs disagree on query order at position {i}: '{before.QueryId}' vs '{after.QueryId}'. They were not run over the same query set.",
                    nameof(candidate));
            }

            double delta = after.Metrics.NdcgAtK - before.Metrics.NdcgAtK;
            BenchmarkDeltaVerdict verdict = Classify(delta, epsilon);

            var entry = new BenchmarkQueryDelta(
                before.QueryId,
                before.QueryText,
                before.Metrics.NdcgAtK,
                after.Metrics.NdcgAtK,
                delta,
                verdict,
                before.FirstRelevantRank,
                after.FirstRelevantRank);

            deltas.Add(entry);

            switch (verdict)
            {
                case BenchmarkDeltaVerdict.Improved:
                    improved.Add(entry);
                    break;
                case BenchmarkDeltaVerdict.Degraded:
                    degraded.Add(entry);
                    break;
                default:
                    unchanged.Add(entry);
                    break;
            }
        }

        // Worst regression first, then best improvement: the queries that need looking at lead.
        improved.Sort(static (a, b) => b.Delta.CompareTo(a.Delta));
        degraded.Sort(static (a, b) => a.Delta.CompareTo(b.Delta));

        return new BenchmarkComparison(
            baseline.Name,
            candidate.Name,
            deltas,
            improved,
            degraded,
            unchanged,
            deltas.Count == 0 ? 0 : deltas.Sum(entry => entry.Delta) / deltas.Count);
    }

    /// <summary>
    /// Whether a per-query nDCG@K delta counts as movement, and which way.
    /// </summary>
    /// <remarks>
    /// A delta within <paramref name="epsilon"/> of zero is unchanged rather than a tiny improvement
    /// or a tiny regression: on a metric that runs to a handful of decimal places, reporting a move
    /// of 1e-9 as one would fill the improved and degraded lists with noise and bury the queries
    /// that actually moved.
    /// </remarks>
    private static BenchmarkDeltaVerdict Classify(double delta, double epsilon)
    {
        if (Math.Abs(delta) <= epsilon)
            return BenchmarkDeltaVerdict.Unchanged;

        return delta > 0 ? BenchmarkDeltaVerdict.Improved : BenchmarkDeltaVerdict.Degraded;
    }
}
