using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class Bm25ParameterTunerTests
{
    /// <summary>
    /// Corpus designed so the winning ranking for "alpha" flips with b: without length
    /// normalization the long, term-heavy document leads; with strong normalization the
    /// short document takes the top spot.
    /// </summary>
    private static ITextIndex CreateIndex()
    {
        var shortDoc = new SearchDocument("short", "alpha beta");
        var longDoc = new SearchDocument(
            "long",
            string.Join(" ", Enumerable.Repeat("alpha", 3).Concat(Enumerable.Repeat("filler", 57))));
        var otherDoc = new SearchDocument("other", "zulu");

        var index = new InMemoryTextIndex();
        index.Index(new[] { shortDoc, longDoc, otherDoc });
        return index;
    }

    private static Bm25ValidationQuery[] ValidationSet() =>
    [
        new("alpha", ["short"]),
        new("zulu", ["other"]),
    ];

    [Fact]
    public void Tune_FindsParametersThatPutTheRelevantDocumentFirst()
    {
        var tuner = new Bm25ParameterTuner(CreateIndex(), ValidationSet());

        var result = tuner.Tune(topK: 1);

        Assert.True(result.MetricScore > 0, "a perfect top-1 ranking is achievable on this corpus");
        Assert.Equal(0.5, result.Parameters.B, 12);
        Assert.Equal(1, result.MetricScore, 12);

        var engine = new RankedTextSearchEngine(CreateIndex(), new Bm25Scorer(result.Parameters));
        var top = Assert.Single(engine.Search("alpha", new SearchOptions(1)));
        Assert.Equal("short", top.Document.Id);
    }

    [Fact]
    public void Tune_ReturnsTheFullGridInAscendingOrder()
    {
        var tuner = new Bm25ParameterTuner(CreateIndex(), ValidationSet());
        double[] k1Values = [0.5, 1.0];
        double[] bValues = [0.0, 0.5, 1.0];

        var result = tuner.Tune(k1Values, bValues, topK: 1);

        Assert.Equal(6, result.Grid.Count);
        for (int i = 1; i < result.Grid.Count; i++)
        {
            Assert.True(
                result.Grid[i - 1].K1 < result.Grid[i].K1 ||
                (result.Grid[i - 1].K1 == result.Grid[i].K1 && result.Grid[i - 1].B < result.Grid[i].B),
                "the grid must be evaluated in ascending (k1, b) order");
        }
    }

    [Fact]
    public void Tune_UsesTheDefaultGridWhenNoGridIsSupplied()
    {
        var tuner = new Bm25ParameterTuner(CreateIndex(), ValidationSet());

        var result = tuner.Tune();

        Assert.Equal(25, result.Grid.Count); // 5 k1 values x 5 b values
        Assert.Equal(TuningMetric.F1, result.Metric);
        Assert.Equal(10, result.TopK);
        Assert.Contains(result.Grid, g => g.K1 == 1.5 && g.B == 0.75);
    }

    [Fact]
    public void Tune_BreaksTiesWithTheLowestGridPoint()
    {
        // Every document is relevant for every query, so every grid point achieves the same
        // F1: the deterministic winner must be the first ascending (k1, b) point.
        var validation = new[] { new Bm25ValidationQuery("zulu", ["short", "long", "other"]) };
        var tuner = new Bm25ParameterTuner(CreateIndex(), validation);

        var result = tuner.Tune(k1Values: [1.0, 2.0], bValues: [0.5, 1.0], topK: 10);

        Assert.Equal(1.0, result.Parameters.K1, 12);
        Assert.Equal(0.5, result.Parameters.B, 12);
        Assert.Equal(result.Grid.Max(g => g.MetricScore), result.MetricScore, 12);
    }

    [Fact]
    public void Tune_RecomputableMetric_MatchesTheReportedScore()
    {
        var index = CreateIndex();
        var validation = ValidationSet();
        var tuner = new Bm25ParameterTuner(index, validation);

        var result = tuner.Tune(topK: 3, metric: TuningMetric.Ndcg);

        var engine = new RankedTextSearchEngine(index, new Bm25Scorer(result.Parameters));
        double recomputed = validation.Average(query =>
            RetrievalMetrics.NdcgAtK(
                engine.Search(query.Query, new SearchOptions(3)).Select(r => r.DocumentId).ToArray(),
                query.RelevantDocumentIds,
                3));

        Assert.Equal(recomputed, result.MetricScore, 12);
    }

    [Fact]
    public void Constructor_RejectsEmptyOrMislabeledValidationSets()
    {
        var index = CreateIndex();

        Assert.Throws<ArgumentException>(() => new Bm25ParameterTuner(index, Array.Empty<Bm25ValidationQuery>()));
        Assert.Throws<ArgumentException>(() => new Bm25ParameterTuner(index, new[] { new Bm25ValidationQuery("alpha", Array.Empty<string>()) }));
        Assert.Throws<ArgumentNullException>(() => new Bm25ParameterTuner(index, null!));
        Assert.Throws<ArgumentNullException>(() => new Bm25ParameterTuner(null!, ValidationSet()));
    }

    [Fact]
    public void Tune_ValidatesGridsAndTopK()
    {
        var tuner = new Bm25ParameterTuner(CreateIndex(), ValidationSet());

        Assert.Throws<ArgumentOutOfRangeException>(() => tuner.Tune(topK: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tuner.Tune(k1Values: [-1.0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => tuner.Tune(k1Values: [double.NaN]));
        Assert.Throws<ArgumentOutOfRangeException>(() => tuner.Tune(k1Values: [double.PositiveInfinity]));
        Assert.Throws<ArgumentOutOfRangeException>(() => tuner.Tune(bValues: [1.5]));
        Assert.Throws<ArgumentOutOfRangeException>(() => tuner.Tune(bValues: [-0.1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => tuner.Tune(bValues: [double.NaN]));
        Assert.Throws<ArgumentOutOfRangeException>(() => tuner.Tune(bValues: [double.NegativeInfinity]));
        Assert.Throws<ArgumentException>(() => tuner.Tune(k1Values: []));
        Assert.Throws<ArgumentException>(() => tuner.Tune(bValues: Array.Empty<double>()));
    }

    [Fact]
    public void Tune_DoesNotMutateTheIndex()
    {
        var index = CreateIndex();
        int documentsBefore = index.Count;
        long tokensBefore = index.CorpusTokenCount;

        var tuner = new Bm25ParameterTuner(index, ValidationSet());
        tuner.Tune();

        Assert.Equal(documentsBefore, index.Count);
        Assert.Equal(tokensBefore, index.CorpusTokenCount);
    }
}