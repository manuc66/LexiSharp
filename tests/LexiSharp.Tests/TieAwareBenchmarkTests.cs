using System.Collections.Generic;
using LexiSharp.Benchmarking;
using LexiSharp.Core;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The tie-aware numbers a benchmark reports. These decide whether the table can be trusted: a
/// mean that silently drops the query whose tie reached past the cut, or a null that reads as a
/// zero, would make the report look more certain than the run was.
/// </summary>
public class TieAwareBenchmarkTests
{
    [Fact]
    public void ATieInsideThePageIsReportedWithItsInterval()
    {
        // "c" is shorter, so it scores above the other two, which are identical: the page holds a
        // tie, and its boundary does not. One of the tied documents is the answer and one is not,
        // so the tie is worth something.
        var documents = new[]
        {
            new SearchDocument("c", "alpha"),
            new SearchDocument("a", "alpha beta"),
            new SearchDocument("b", "alpha beta"),
        };
        var query = new BenchmarkQuery("q", "alpha", new Dictionary<string, double> { ["a"] = 3, ["b"] = 0 });

        var result = Assert.Single(CorpusBenchmark.Run(
            documents, [query], [BenchmarkConfig.Bm25()], new BenchmarkOptions { TopK = 3 }));

        var perQuery = Assert.Single(result.PerQuery);

        Assert.NotNull(perQuery.Discrimination);
        Assert.Equal(DiscriminationVerdict.TiedWithinPage, perQuery.Discrimination!.Verdict);
        Assert.NotNull(perQuery.TieAwareNdcg);
        Assert.True(perQuery.TieAwareNdcg!.Value.Width > 0);

        Assert.NotNull(result.TieAware);
        Assert.Equal(1, result.TieAware!.Measured);
        Assert.Equal(1, result.TieAware.Affected);
        Assert.Equal(1, result.TieAware.TiedWithinPage);
    }

    [Fact]
    public void ATieReachingPastTheCutIsReportedAsNotComputableNotAsZero()
    {
        // Three documents at one score and a page of two: the class is incomplete, so the exact
        // expectation cannot be recovered. Null is the honest answer; zero would be a measurement.
        // The page is two documents and the tie is two documents: the second tied document sits
        // past the cut, so the class is incomplete and the exact expectation is unrecoverable.
        var documents = new[]
        {
            new SearchDocument("c", "alpha"),
            new SearchDocument("a", "alpha beta"),
            new SearchDocument("b", "alpha beta"),
        };
        var query = new BenchmarkQuery("q", "alpha", new Dictionary<string, double> { ["a"] = 3 });

        var result = Assert.Single(CorpusBenchmark.Run(
            documents, [query], [BenchmarkConfig.Bm25()], new BenchmarkOptions { TopK = 2 }));

        var perQuery = Assert.Single(result.PerQuery);

        Assert.NotNull(perQuery.Discrimination);
        Assert.Equal(DiscriminationVerdict.TiedAtCut, perQuery.Discrimination!.Verdict);
        Assert.Null(perQuery.TieAwareNdcg);

        Assert.NotNull(result.TieAware);
        Assert.Equal(0, result.TieAware!.Measured);
        Assert.Equal(1, result.TieAware.TiedAtCut);
        Assert.Equal(0, result.TieAware.MeanExpectedNdcg);
    }
}
