using LexiSharp.Benchmarking;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Covers the per-query breakdown and the baseline-versus-candidate comparison. The reason this
/// exists is concrete: on the reference corpus, two configurations whose means differ by 0.015
/// turned out to be one query that improved by 0.29 and one that lost its only judged document
/// entirely. A mean-only report would have shown neither.
/// </summary>
public class BenchmarkComparisonTests
{
    private static readonly SearchDocument[] Corpus =
    [
        new("alpha", "refresh token renewal rotates the credential"),
        new("beta", "the deployment pipeline builds a container image"),
        new("gamma", "session cookie lifetime and logout behaviour"),
    ];

    private static BenchmarkConfigResult Run(
        IReadOnlyList<BenchmarkQuery> queries,
        IReadOnlyList<BenchmarkConfig> configs) =>
        CorpusBenchmark.Run(Corpus, queries, configs, new BenchmarkOptions { TopK = 3 })[0];

    private static BenchmarkConfigResult HandBuilt(string name, params (string Id, double Ndcg, int? Rank)[] queries) =>
        new(
            name,
            new BenchmarkMetrics(
                queries.Average(q => q.Ndcg), 0, 0, 0, 0, 0),
            0,
            0,
            queries.Length)
        {
            PerQuery = queries
                .Select(q => new BenchmarkQueryResult(q.Id, $"text of {q.Id}", new BenchmarkMetrics(q.Ndcg, 0, 0, 0, 0, 0), [], q.Rank))
                .ToArray(),
        };

    [Fact]
    public void ARunReportsOneResultPerJudgedQuery()
    {
        var queries = new[]
        {
            new BenchmarkQuery("q1", "refresh token", new[] { "alpha" }),
            new BenchmarkQuery("q2", "container image", new[] { "beta" }),
        };

        var result = Run(queries, [BenchmarkConfig.Bm25()]);

        Assert.Equal(2, result.PerQuery.Count);
        Assert.Equal("q1", result.PerQuery[0].QueryId);
        Assert.Equal("refresh token", result.PerQuery[0].QueryText);
    }

    [Fact]
    public void QueriesWithoutJudgmentsAreExcludedFromTheBreakdown()
    {
        // The aggregate already skips them; the breakdown must agree, or a query would appear in
        // the detail with metrics that never reached the average.
        var queries = new[]
        {
            new BenchmarkQuery("q1", "refresh token", new[] { "alpha" }),
            new BenchmarkQuery("q-unjudged", "nothing", Array.Empty<string>()),
        };

        var result = Run(queries, [BenchmarkConfig.Bm25()]);

        Assert.Equal(1, result.PerQuery.Count);
        Assert.Equal(result.JudgedQueries, result.PerQuery.Count);
    }

    [Fact]
    public void FirstRelevantRankTellsLateFromLost()
    {
        var queries = new[]
        {
            new BenchmarkQuery("q1", "refresh token", new[] { "alpha" }),
            new BenchmarkQuery("q2", "cookie lifetime logout", new[] { "gamma" }),
        };

        var result = Run(queries, [BenchmarkConfig.Bm25()]);

        var byId = result.PerQuery.ToDictionary(entry => entry.QueryId, entry => entry);

        Assert.True(byId["q1"].RetrievedRelevant);
        Assert.True(byId["q2"].RetrievedRelevant);
    }

    [Fact]
    public void AQueryWithNoJudgedDocumentInThePageReportsNoRank()
    {
        // No corpus term appears in the query, so nothing judged can be retrieved and the rank
        // must be null rather than a fabricated 0 or 4.
        var result = Run([new BenchmarkQuery("q1", "kubernetes ingress zzzz", new[] { "beta" })],
            [BenchmarkConfig.Bm25()]);

        Assert.Null(result.PerQuery[0].FirstRelevantRank);
        Assert.False(result.PerQuery[0].RetrievedRelevant);
    }

    [Fact]
    public void ComparisonClassifiesEveryQueryAndSumsTheNet()
    {
        var baseline = HandBuilt("baseline",
            ("q-better", 0.2, 4),
            ("q-worse", 0.9, 1),
            ("q-same", 0.5, 2));

        var candidate = HandBuilt("candidate",
            ("q-better", 0.8, 1),
            ("q-worse", 0.4, null),
            ("q-same", 0.5, 2));

        var comparison = BenchmarkComparer.Compare(baseline, candidate);

        Assert.Equal(1, comparison.ImprovedCount);
        Assert.Equal(1, comparison.DegradedCount);
        Assert.Equal(1, comparison.UnchangedCount);
        Assert.Equal(0, comparison.Net);
    }

    [Fact]
    public void ANetOfZeroStillHidesRealMovements()
    {
        // The whole reason this exists: both means are identical, and the queries are not.
        var baseline = HandBuilt("baseline", ("q1", 0.0, null), ("q2", 1.0, 1));
        var candidate = HandBuilt("candidate", ("q1", 1.0, 1), ("q2", 0.0, null));

        var comparison = BenchmarkComparer.Compare(baseline, candidate);

        Assert.Equal(0.5, baseline.Metrics.NdcgAtK, 10);
        Assert.Equal(0.5, candidate.Metrics.NdcgAtK, 10);
        Assert.Equal(0.0, comparison.MeanDelta, 10);
        Assert.Equal(0, comparison.Net);
        Assert.Equal(1, comparison.ImprovedCount);
        Assert.Equal(1, comparison.DegradedCount);
    }

    [Fact]
    public void TheReadingDistinguishesRescuedFromLostFromDemoted()
    {
        var baseline = HandBuilt("baseline",
            ("rescued", 0.0, null),
            ("lost", 0.9, 3),
            ("demoted", 0.9, 1),
            ("promoted", 0.1, 7));

        var candidate = HandBuilt("candidate",
            ("rescued", 0.8, 2),
            ("lost", 0.0, null),
            ("demoted", 0.4, 3),
            ("promoted", 0.7, 1));

        var readings = BenchmarkComparer.Compare(baseline, candidate)
            .Queries
            .ToDictionary(entry => entry.QueryId, entry => entry.Reading);

        Assert.Contains("rescued", readings["rescued"], StringComparison.Ordinal);
        Assert.Contains("rank 2", readings["rescued"], StringComparison.Ordinal);

        Assert.Contains("lost", readings["lost"], StringComparison.Ordinal);
        Assert.Contains("rank 3", readings["lost"], StringComparison.Ordinal);

        Assert.Contains("demoted", readings["demoted"], StringComparison.Ordinal);
        Assert.Contains("1 -> 3", readings["demoted"], StringComparison.Ordinal);

        Assert.Contains("promoted", readings["promoted"], StringComparison.Ordinal);
        Assert.Contains("7 -> 1", readings["promoted"], StringComparison.Ordinal);
    }

    [Fact]
    public void WorstRegressionsAreReportedFirst()
    {
        // The queries that need looking at should lead the report, not sort alphabetically.
        var baseline = HandBuilt("baseline", ("a", 1.0, 1), ("b", 1.0, 1), ("c", 1.0, 1));
        var candidate = HandBuilt("candidate", ("a", 0.9, 1), ("b", 0.5, 3), ("c", 0.99, 1));

        var comparison = BenchmarkComparer.Compare(baseline, candidate);

        Assert.Equal(["b", "a", "c"], comparison.Degraded.Select(entry => entry.QueryId));
    }

    [Fact]
    public void ComparisonRefusesRunsOverDifferentQuerySets()
    {
        var baseline = HandBuilt("baseline", ("q1", 0.5, 1), ("q2", 0.5, 1));
        var candidate = HandBuilt("candidate", ("q1", 0.5, 1));

        var error = Assert.Throws<ArgumentException>(() => BenchmarkComparer.Compare(baseline, candidate));
        Assert.Contains("query counts", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ComparisonRefusesRunsThatDisagreeOnQueryOrder()
    {
        var baseline = HandBuilt("baseline", ("q1", 0.5, 1), ("q2", 0.5, 1));
        var candidate = HandBuilt("candidate", ("q2", 0.5, 1), ("q1", 0.5, 1));

        var error = Assert.Throws<ArgumentException>(() => BenchmarkComparer.Compare(baseline, candidate));
        Assert.Contains("query order", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ComparisonRefusesARunWithoutPerQueryResults()
    {
        // Better to say "I cannot compare" than to report a comparison against nothing.
        var baseline = HandBuilt("baseline", ("q1", 0.5, 1));
        var withoutDetail = new BenchmarkConfigResult("candidate", new BenchmarkMetrics(0.5, 0, 0, 0, 0, 0), 0, 0, 1);

        Assert.Throws<ArgumentException>(() => BenchmarkComparer.Compare(baseline, withoutDetail));
    }

    [Fact]
    public void DifferencesBelowTheToleranceCountAsUnchanged()
    {
        var baseline = HandBuilt("baseline", ("q1", 0.5, 1));
        // 1e-8 is above the default 1e-9 (a real move) and below the 1e-6 tolerance below (noise).
        var candidate = HandBuilt("candidate", ("q1", 0.5 + 1e-8, 1));

        var strict = BenchmarkComparer.Compare(baseline, candidate);
        var lenient = BenchmarkComparer.Compare(baseline, candidate, epsilon: 1e-6);

        Assert.Equal(BenchmarkDeltaVerdict.Improved, strict.Queries[0].Verdict);
        Assert.Equal(BenchmarkDeltaVerdict.Unchanged, lenient.Queries[0].Verdict);
        Assert.Equal(1, lenient.UnchangedCount);
    }

    [Fact]
    public void ADefaultToleranceSwallowsSubNanoscopicDrift()
    {
        // Documented default: 1e-9. A 1e-12 wobble is not a movement and must not be reported as
        // one, or every diff would be full of noise. MeanDelta stays the raw arithmetic mean -
        // the tolerance filters the per-query verdicts, not the arithmetic.
        var baseline = HandBuilt("baseline", ("q1", 0.5, 1));
        var candidate = HandBuilt("candidate", ("q1", 0.5 + 1e-12, 1));

        var comparison = BenchmarkComparer.Compare(baseline, candidate);

        Assert.Equal(BenchmarkDeltaVerdict.Unchanged, comparison.Queries[0].Verdict);
        Assert.Equal(1, comparison.UnchangedCount);
        Assert.Equal(0, comparison.ImprovedCount + comparison.DegradedCount);
        Assert.True(Math.Abs(comparison.MeanDelta) < 1e-9, $"mean delta {comparison.MeanDelta} should be negligible");
    }
}
