using System;
using System.Linq;
using LexiSharp.Benchmarking;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class RetrievalEvaluatorTests
{
    private static RankedTextSearchEngine Engine(params SearchDocument[] documents)
    {
        var engine = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());
        engine.Index(documents);
        return engine;
    }

    [Fact]
    public void HandChecked_PanelMetrics_AreCorrect()
    {
        // One document matches q1 perfectly at rank 1; q2's judged document does not exist.
        var evaluator = new RetrievalEvaluator(
        [
            new BenchmarkQuery("q1", "alpha", ["x"]),
            new BenchmarkQuery("q2", "ghost", ["missing"]),
        ],
        topK: 3);

        var result = evaluator.Evaluate(Engine(new SearchDocument("x", "alpha beta")));

        Assert.Equal(2, result.JudgedQueries);
        Assert.Equal(2, result.ByQuery.Count);

        // q1: [x] at rank 1 — every metric at its perfect or convention-defined value.
        Assert.Equal(1, result.ByQuery[0].Metrics.NdcgAtK, 6);
        Assert.Equal(1, result.ByQuery[0].Metrics.MrrAtK, 6);
        Assert.Equal(1, result.ByQuery[0].Metrics.MapAtK, 6);
        Assert.Equal(1, result.ByQuery[0].Metrics.RecallAtK, 6);
        Assert.Equal(1.0 / 3.0, result.ByQuery[0].Metrics.PrecisionAtK, 6);
        Assert.Equal(0.5, result.ByQuery[0].Metrics.F1AtK, 6);
        Assert.Equal(1, result.ByQuery[0].FirstRelevantRank);

        // q2: nothing retrieved — every metric zero.
        Assert.Equal(0, result.ByQuery[1].Metrics.NdcgAtK);
        Assert.Equal(0, result.ByQuery[1].Metrics.MrrAtK);
        Assert.Null(result.ByQuery[1].FirstRelevantRank);

        // Means over the two judged queries.
        Assert.Equal(0.5, result.Metrics.NdcgAtK, 6);
        Assert.Equal(0.5, result.Metrics.MrrAtK, 6);
        Assert.Equal(0.5, result.Metrics.MapAtK, 6);
        Assert.Equal(0.5, result.Metrics.RecallAtK, 6);
        Assert.Equal(1.0 / 6.0, result.Metrics.PrecisionAtK, 6);
        Assert.Equal(0.25, result.Metrics.F1AtK, 6);
        Assert.False(result.IsGraded);
    }

    [Fact]
    public void UnjudgedQueries_AreLoadedButExcludedFromTheAverages()
    {
        var evaluator = new RetrievalEvaluator(
        [
            new BenchmarkQuery("q1", "alpha", ["x"]),
            new BenchmarkQuery("no-gold", "gamma", []),
        ]);

        var result = evaluator.Evaluate(Engine(new SearchDocument("x", "alpha beta")));

        Assert.Equal(1, result.JudgedQueries);
        Assert.Single(result.ByQuery);
        Assert.Equal("q1", result.ByQuery[0].QueryId);
        Assert.Equal(1, result.Metrics.NdcgAtK);
    }

    [Fact]
    public void TopK_PinsTheRetrievalDepth()
    {
        var evaluator = new RetrievalEvaluator(
        [
            new BenchmarkQuery("q1", "alpha beta", ["a", "b"]),
        ],
        topK: 1);

        var result = evaluator.Evaluate(Engine(
            new SearchDocument("a", "alpha beta"),
            new SearchDocument("b", "alpha beta gamma delta")));

        // Only the top-1 page is retrieved and measured, even though both documents match.
        Assert.Single(result.ByQuery[0].RetrievedIds);
        Assert.Equal(1, result.ByQuery[0].Metrics.MrrAtK, 6); // a relevant doc sits at rank 1
    }

    [Fact]
    public void EvaluatingTheLiveEngine_DetectsDrift()
    {
        var engine = Engine(
            new SearchDocument("x", "alpha beta gamma"),
            new SearchDocument("other", "tulips bloom in spring"));

        var evaluator = new RetrievalEvaluator([new BenchmarkQuery("q1", "alpha", ["x"])]);

        var before = evaluator.Evaluate(engine);
        Assert.Equal(1, before.Metrics.NdcgAtK);
        Assert.Equal(1, before.Metrics.MrrAtK);

        // The corpus drifts: the judged document leaves the engine. The next evaluation sees it.
        engine.Remove("x");

        var after = evaluator.Evaluate(engine);
        Assert.Equal(0, after.Metrics.NdcgAtK);
        Assert.Equal(0, after.Metrics.MrrAtK);
        Assert.Null(after.ByQuery[0].FirstRelevantRank);
    }

    [Fact]
    public void GainConventions_ArePlumbedThrough()
    {
        var engine = Engine(
            new SearchDocument("a", "apple zebra one two three"),
            new SearchDocument("b", "banana"));

        var query = new BenchmarkQuery(
            "q1",
            "apple banana",
            new Dictionary<string, double> { ["a"] = 2, ["b"] = 1 });
        var panel = new[] { query };

        var exponential = new RetrievalEvaluator(panel, topK: 3);
        var exponentialResult = exponential.Evaluate(engine);
        Assert.Equal(
            RetrievalMetrics.NdcgAtK(
                exponentialResult.ByQuery[0].RetrievedIds, query.GradedRelevance, 3),
            exponentialResult.ByQuery[0].Metrics.NdcgAtK, 6);
        Assert.True(exponentialResult.IsGraded);

        var linear = new RetrievalEvaluator(panel, topK: 3, NdcgGain.Linear);
        var linearResult = linear.Evaluate(engine);
        Assert.Equal(
            RetrievalMetrics.NdcgAtK(
                linearResult.ByQuery[0].RetrievedIds, query.GradedRelevance, 3, NdcgGain.Linear),
            linearResult.ByQuery[0].Metrics.NdcgAtK, 6);

        // The two conventions read the same ranking differently when the ranking is imperfect;
        // with the graded query this panel's nDCG differs between them.
        Assert.NotEqual(exponentialResult.Metrics.NdcgAtK, linearResult.Metrics.NdcgAtK);
    }

    [Fact]
    public void CallerOptions_PassThrough_ExceptLimit()
    {
        var engine = Engine(
            new SearchDocument("x", "alpha beta", Fields: new Dictionary<string, string> { ["kind"] = "doc" }),
            new SearchDocument("y", "alpha beta", Fields: new Dictionary<string, string> { ["kind"] = "blocked" }));

        var evaluator = new RetrievalEvaluator([new BenchmarkQuery("q1", "alpha", ["x", "y"])], topK: 5);

        var unfiltered = evaluator.Evaluate(engine);
        Assert.Equal(2, unfiltered.ByQuery[0].RetrievedIds.Count);

        // A filter excluding the judged documents passes through and zeroes the metrics.
        var filtered = evaluator.Evaluate(
            engine,
            new SearchOptions(Filters: [new MetadataFilter("kind", MetadataFilterOperator.Equal, "blocked")]));
        Assert.Equal(new[] { "y" }, filtered.ByQuery[0].RetrievedIds);
        Assert.Equal(1, filtered.ByQuery[0].Metrics.MrrAtK, 6);   // y is relevant and at rank 1
        Assert.Equal(0.5, filtered.ByQuery[0].Metrics.RecallAtK, 6); // only one of the two judged docs made the page
    }

    [Fact]
    public void AnAllUnjudgedPanel_ReportsZeros_NotAMeasurement()
    {
        var evaluator = new RetrievalEvaluator([]);
        var result = evaluator.Evaluate(Engine(new SearchDocument("x", "alpha beta")));

        Assert.Equal(0, result.JudgedQueries);
        Assert.Empty(result.ByQuery);
        Assert.Equal(0, result.Metrics.NdcgAtK);
        Assert.Equal(0, result.Metrics.MrrAtK);
        Assert.Equal(0, result.TotalMilliseconds);
        Assert.False(result.IsGraded);
    }

    [Fact]
    public void ThePanelAndDepth_AreExposed()
    {
        var query = new BenchmarkQuery("q1", "alpha", ["x"]);
        var evaluator = new RetrievalEvaluator([query], topK: 7);

        Assert.Equal(7, evaluator.TopK);
        Assert.Same(query, Assert.Single(evaluator.Panel));
    }
}