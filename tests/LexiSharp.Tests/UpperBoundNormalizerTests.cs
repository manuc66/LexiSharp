using LexiSharp.Core;
using LexiSharp.Hybrid;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The normalizer that divides by a query's theoretical bound rather than by the best score the page
/// happened to return, and the seam that carries the bound from an engine to a merger.
/// </summary>
public class UpperBoundNormalizerTests
{
    private static SearchResult Scored(string id, double score) =>
        new(id, score, new SearchDocument(id, id));

    private static IReadOnlyList<SearchResult> List(params (string Id, double Score)[] scores) =>
        scores.Select(pair => Scored(pair.Id, pair.Score)).ToList();

    [Fact]
    public void TheBoundIsTheDivisorAndTheOffsetIsZero()
    {
        var normalization = new UpperBoundScoreNormalizer().Measure(
            List(("a", 4), ("b", 2)),
            new ScoreNormalizationContext(10, "BM25"));

        Assert.Equal(0, normalization.Offset);
        Assert.Equal(10, normalization.Scale);
        Assert.Equal(0.4, normalization.Apply(4), 12);
        Assert.Equal(0.2, normalization.Apply(2), 12);
    }

    [Fact]
    public void AnEmptyListHasNoScaleEvenWhenTheQueryHasABound()
    {
        // The merge explanation reports divisor 0 for an engine that returned nothing; a known
        // query bound must not turn that into a scale the lane never used.
        var normalization = new UpperBoundScoreNormalizer().Measure(
            List(),
            new ScoreNormalizationContext(10, "BM25"));

        Assert.Equal(ScoreNormalization.None, normalization);
    }

    [Fact]
    public void AnObservedScoreAboveTheBoundRaisesTheDivisorInsteadOfReturningAboveOne()
    {
        // The bound is the scorer's, and a rounding step must not be able to make the normalized
        // value exceed one. The observed maximum is a guard, not a second source of truth.
        var normalization = new UpperBoundScoreNormalizer().Measure(
            List(("a", 12), ("b", 3)),
            new ScoreNormalizationContext(10, "BM25"));

        Assert.Equal(12, normalization.Scale);
        Assert.Equal(1.0, normalization.Apply(12));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AContextWithoutAUsableBound_FallsBackToThePageMaximum(double? bound)
    {
        var normalization = new UpperBoundScoreNormalizer().Measure(
            List(("a", 4), ("b", 2)),
            new ScoreNormalizationContext(bound, "BM25"));

        Assert.Equal(4, normalization.Scale);
        Assert.Equal(1.0, normalization.Apply(4));
    }

    [Fact]
    public void AListOnlyCall_HasNoQueryToBoundAndUsesThePageMaximum()
    {
        var normalization = new UpperBoundScoreNormalizer().Measure(List(("a", 4), ("b", 2)));

        Assert.Equal(4, normalization.Scale);
    }

    [Fact]
    public void AMergerWithAListMeasuringNormalizerDoesNotAskForContexts()
    {
        // The hybrid reads this before asking an engine for a bound, so an existing weighted
        // pipeline does not pay a query tokenization per engine for a context it will not use.
        Assert.False(new WeightedScoreResultMerger().UsesContexts);
        Assert.False(new WeightedScoreResultMerger(new MinMaxScoreNormalizer()).UsesContexts);
        Assert.True(new WeightedScoreResultMerger(new UpperBoundScoreNormalizer()).UsesContexts);
    }

    [Fact]
    public void TheContextualMerge_DividesEachEngineByItsOwnBound()
    {
        var merger = new WeightedScoreResultMerger(new UpperBoundScoreNormalizer(), 1.0, 1.0);

        var merged = merger.Merge(
            [
                List(("x", 5), ("y", 2)),
                List(("x", 1), ("z", 0.5)),
            ],
            "q",
            [
                new ScoreNormalizationContext(10, "BM25"),
                new ScoreNormalizationContext(2, "Cosine"),
            ]);

        // x is half of the first bound and half of the second, so one unit in total.
        Assert.Equal(1.0, merged.Single(r => r.DocumentId == "x").Score, 12);
        Assert.Equal(0.2, merged.Single(r => r.DocumentId == "y").Score, 12);
        Assert.Equal(0.25, merged.Single(r => r.DocumentId == "z").Score, 12);
    }

    [Fact]
    public void AContextualMergeWithNoContexts_IsTheHistoricalPageMaximum()
    {
        var merger = new WeightedScoreResultMerger(new UpperBoundScoreNormalizer());

        var merged = merger.Merge(
            [
                List(("x", 10), ("y", 5)),
                List(("x", 2), ("z", 1)),
            ],
            "q");

        Assert.Equal(2.0, merged.Single(r => r.DocumentId == "x").Score, 12);
        Assert.Equal(0.5, merged.Single(r => r.DocumentId == "y").Score, 12);
    }

    [Fact]
    public void CombSumAndCombMnZAlsoUseTheContext()
    {
        var contexts = new[] { new ScoreNormalizationContext(10, "BM25") };

        var sum = new CombSumResultMerger(new UpperBoundScoreNormalizer())
            .Merge([List(("x", 5))], "q", contexts);

        Assert.Equal(0.5, sum.Single().Score, 12);

        var mnz = new CombMNZResultMerger(new UpperBoundScoreNormalizer())
            .Merge([List(("x", 5)), List(("x", 1))], "q", [contexts[0], new ScoreNormalizationContext(2, "Cosine")]);

        // 0.5 + 0.5, multiplied by the two engines that returned x.
        Assert.Equal(2.0, mnz.Single().Score, 12);
    }

    [Fact]
    public void TheExplanationReportsTheBoundTheMergeUsed()
    {
        var merger = new WeightedScoreResultMerger(new UpperBoundScoreNormalizer());

        var explanation = merger.Explain(
            [List(("x", 5))],
            [new ScoreNormalizationContext(10, "BM25")]);

        Assert.Equal(10, explanation.Normalization.Single());
        Assert.Empty(explanation.Offsets);
    }

    [Fact]
    public void AWeightedMergeWithMaxNormalizerDoesNotAskForAQueryBound()
    {
        // A max-normalized weighted hybrid is the shipped default; asking an engine for a bound
        // would add a query tokenization per engine for a number the merge never reads.
        var engine = new FixedEngine(
            "fixed",
            [Scored("x", 1)],
            new QueryScoreBound("Fixed", 10, Array.Empty<TermScoreBound>()));

        var hybrid = new HybridTextSearchEngine([engine], new WeightedScoreResultMerger());

        hybrid.Search("q");

        Assert.Equal(0, engine.BoundCalls);
    }

    [Fact]
    public void TheHybridAsksABoundCapableEngineAndUsesIt()
    {
        var bounded = new FixedEngine(
            "bounded",
            [Scored("x", 5), Scored("y", 2)],
            new QueryScoreBound("Fixed", 10, Array.Empty<TermScoreBound>()));

        var unbounded = new FixedEngine(
            "unbounded",
            [Scored("x", 1), Scored("z", 0.5)],
            bound: null);

        var hybrid = new HybridTextSearchEngine(
            [bounded, unbounded],
            new WeightedScoreResultMerger(new UpperBoundScoreNormalizer(), 1.0, 1.0));

        var results = hybrid.Search("q");

        // The bounded lane divides by 10, the unbounded one by its page maximum of 1: x is 0.5 + 1.
        Assert.Equal(1.5, results.Single(r => r.DocumentId == "x").Score, 12);
        Assert.Equal(0.2, results.Single(r => r.DocumentId == "y").Score, 12);
        Assert.Equal(0.5, results.Single(r => r.DocumentId == "z").Score, 12);
    }

    /// <summary>An engine with fixed scores and an optional bound, for the hybrid wiring.</summary>
    private sealed class FixedEngine(
        string name,
        IReadOnlyList<SearchResult> results,
        QueryScoreBound? bound) : IScoreBoundSearchEngine
    {
        public string Name => name;

        /// <summary>How many times the hybrid asked this engine for a query bound.</summary>
        public int BoundCalls { get; private set; }

        public void Index(IEnumerable<SearchDocument> documents) { }
        public void Add(SearchDocument document) { }
        public bool Remove(string documentId) => false;
        public void Clear() { }

        public IReadOnlyList<SearchResult> Search(
            string query,
            SearchOptions? options = null,
            CancellationToken cancellationToken = default) => results;

        public bool TryGetQueryBound(string query, SearchOptions? options, out QueryScoreBound value)
        {
            BoundCalls++;
            value = bound.GetValueOrDefault();

            return bound.HasValue;
        }
    }
}
