using LexiSharp.Core;
using LexiSharp.Expansion;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Covers query-term reweighting: the scorer's weight application, the co-occurrence weighter, and
/// the decorator that ties them together. The property that keeps the decorator honest is the
/// parity check — a search through it must return exactly what an explicitly weighted scorer returns
/// for the same query, scores included.
/// </summary>
public class QueryReweightingTests
{
    private static InMemoryTextIndex Index(params (string Id, string Text)[] documents)
    {
        var index = new InMemoryTextIndex();
        index.Index(documents.Select(document => new SearchDocument(document.Id, document.Text)));
        return index;
    }

    private static InMemoryTextIndex GraphCorpus() =>
        Index(
            ("1", "hub alpha filler one two three four five"),
            ("2", "hub alpha filler one two three four five"),
            ("3", "hub beta filler one two three four five"),
            ("4", "hub beta filler one two three four five"),
            ("5", "zebra lonely filler one two three four five"),
            ("6", "zebra lonely filler one two three four five"),
            ("7", "filler one two three four five six seven"),
            ("8", "filler one two three four five six seven"),
            ("9", "filler one two three four five six seven"),
            ("10", "filler one two three four five six seven"));

    // ---- the scorer's weights ----------------------------------------------------------------

    [Fact]
    public void AZeroWeightRemovesATermsContribution()
    {
        var index = Index(("1", "alpha beta"), ("2", "alpha"), ("3", "beta"));
        var terms = new[] { "alpha", "beta" };

        var plain = new Bm25Scorer(1.5, 0.75);
        var weighted = plain.WithTermWeights(new Dictionary<string, double> { ["alpha"] = 0, ["beta"] = 1 });

        Assert.Equal(
            plain.Score("1", ["beta"], index),
            weighted.Score("1", terms, index),
            12);
    }

    [Fact]
    public void AWeightScalesTheTermContributionAndNothingElse()
    {
        var index = Index(("1", "alpha beta"), ("2", "alpha"), ("3", "beta"));
        var terms = new[] { "alpha", "beta" };

        var plain = new Bm25Scorer(1.5, 0.75);
        var weighted = plain.WithTermWeights(new Dictionary<string, double> { ["alpha"] = 2, ["beta"] = 1 });

        double expected = (2 * plain.Score("1", ["alpha"], index)) + plain.Score("1", ["beta"], index);

        Assert.Equal(expected, weighted.Score("1", terms, index), 12);
    }

    [Fact]
    public void WithTermWeightsDoesNotMutateThePrototype()
    {
        var index = Index(("1", "alpha beta"));
        var plain = new Bm25Scorer(1.5, 0.75);
        double before = plain.Score("1", ["alpha", "beta"], index);

        _ = plain.WithTermWeights(new Dictionary<string, double> { ["alpha"] = 0, ["beta"] = 0 });

        Assert.Equal(before, plain.Score("1", ["alpha", "beta"], index));
    }

    [Fact]
    public void TheQueryPlanAndTheScalarPathAgreeForAWeightedQuery()
    {
        var index = Index(
            ("1", "alpha beta gamma"),
            ("2", "alpha"),
            ("3", "beta"),
            ("4", "gamma delta"));

        var scorer = new Bm25Scorer(1.5, 0.75)
            .WithTermWeights(new Dictionary<string, double> { ["alpha"] = 0.5, ["beta"] = 1, ["gamma"] = 0.25 });

        var engine = new RankedTextSearchEngine(index, scorer, index.Tokenizer);
        var results = engine.Search("alpha beta gamma", new SearchOptions(Limit: 10));

        foreach (var result in results)
        {
            Assert.Equal(
                scorer.Score(result.DocumentId, ["alpha", "beta", "gamma"], index),
                result.Score,
                12);
        }
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void WithTermWeightsRejectsInvalidWeights(double weight)
    {
        var scorer = new Bm25Scorer();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            scorer.WithTermWeights(new Dictionary<string, double> { ["alpha"] = weight }));
    }

    // ---- the weighter ------------------------------------------------------------------------

    [Fact]
    public void StrengthZeroIsANoOp()
    {
        var graph = PmiTermExpander.LearnFrom(GraphCorpus().Documents);
        var weighter = new CoOccurrenceQueryTermWeighter(graph, strength: 0);

        var weights = weighter.Weigh(["hub", "alpha", "zebra"]);

        Assert.All(weights.Values, weight => Assert.Equal(1.0, weight));
    }

    [Fact]
    public void ASingleTermQueryIsUnweighted()
    {
        var graph = PmiTermExpander.LearnFrom(GraphCorpus().Documents);
        var weighter = new CoOccurrenceQueryTermWeighter(graph, strength: 1);

        var weights = weighter.Weigh(["hub"]);

        Assert.Equal(1.0, weights["hub"]);
    }

    [Fact]
    public void ATermAssociatedWithTheRestOfTheQueryIsDownWeighted()
    {
        var graph = PmiTermExpander.LearnFrom(GraphCorpus().Documents);
        var weighter = new CoOccurrenceQueryTermWeighter(graph, strength: 1);

        var weights = weighter.Weigh(["hub", "alpha", "zebra"]);

        // hub and alpha co-occur, so each names the other; zebra names nobody in this query.
        Assert.True(weights["hub"] < weights["zebra"], $"hub={weights["hub"]}, zebra={weights["zebra"]}");
        Assert.True(weights["alpha"] < weights["zebra"]);
        Assert.Equal(1.0, weights["zebra"]);
    }

    [Fact]
    public void TheMostDiscriminativeTermKeepsWeightOne()
    {
        var graph = PmiTermExpander.LearnFrom(GraphCorpus().Documents);
        var weighter = new CoOccurrenceQueryTermWeighter(graph, strength: 2);

        var weights = weighter.Weigh(["hub", "alpha", "zebra"]);

        Assert.Equal(1.0, weights.Values.Max());
    }

    [Fact]
    public void CentralityUpWeightsTheTermAssociatedWithTheRestOfTheQuery()
    {
        var graph = PmiTermExpander.LearnFrom(GraphCorpus().Documents);
        var weighter = new CoOccurrenceQueryTermWeighter(graph, strength: 1, mode: QueryTermWeightingMode.Centrality);

        var weights = weighter.Weigh(["hub", "alpha", "zebra"]);

        // The same statistic read the other way: hub and alpha are the query's core, zebra is the
        // isolated term, and after normalization the core keeps the full weight.
        Assert.Equal(1.0, weights["hub"]);
        Assert.True(weights["zebra"] < weights["hub"], $"zebra={weights["zebra"]}, hub={weights["hub"]}");
    }

    [Fact]
    public void BelowTheFloorTheWeighterIsANoOp()
    {
        var graph = PmiTermExpander.LearnFrom(GraphCorpus().Documents);
        var weighter = new CoOccurrenceQueryTermWeighter(
            graph, strength: 1, mode: QueryTermWeightingMode.Centrality, minimumQueryTerms: 4);

        Assert.All(weighter.Weigh(["hub", "alpha", "zebra"]).Values, weight => Assert.Equal(1.0, weight));
    }

    [Fact]
    public void RejectsANegativeFloor()
    {
        var graph = PmiTermExpander.LearnFrom(GraphCorpus().Documents);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CoOccurrenceQueryTermWeighter(graph, minimumQueryTerms: -1));
    }

    [Fact]
    public void TheGraphReturnsTheSameAssociationInBothDirections()
    {
        var graph = PmiTermExpander.LearnFrom(GraphCorpus().Documents);
        var candidates = new HashSet<string>(["hub", "alpha"], StringComparer.Ordinal);

        double forward = graph.AssociationsWith("hub", candidates)["alpha"];
        double backward = graph.AssociationsWith("alpha", candidates)["hub"];

        Assert.True(forward > 0);
        Assert.Equal(forward, backward, 12);
    }

    // ---- the decorator -----------------------------------------------------------------------

    [Fact]
    public void TheDecoratorReturnsWhatAnExplicitlyWeightedScorerReturns()
    {
        var index = GraphCorpus();
        var graph = PmiTermExpander.LearnFrom(index.Documents);
        var weighter = new CoOccurrenceQueryTermWeighter(graph, strength: 1);

        const string query = "hub alpha zebra";

        var decorated = new ReweightingTextSearchEngine(
            index, new Bm25Scorer(1.5, 0.75), weighter, index.Tokenizer);

        ITextScorer weighted = new Bm25Scorer(1.5, 0.75)
            .WithTermWeights(weighter.Weigh(index.Tokenizer.Tokenize(query)));

        var expected = new RankedTextSearchEngine(index, weighted, index.Tokenizer)
            .Search(query, new SearchOptions(Limit: 10));

        var actual = decorated.Search(query, new SearchOptions(Limit: 10));

        Assert.Equal(expected.Select(result => result.DocumentId), actual.Select(result => result.DocumentId));

        for (int i = 0; i < expected.Count; i++)
            Assert.Equal(expected[i].Score, actual[i].Score, 12);
    }

    [Fact]
    public void TheDecoratorForwardsWritesToTheIndex()
    {
        var index = Index(("1", "hub"));
        var graph = PmiTermExpander.LearnFrom(index.Documents);
        var engine = new ReweightingTextSearchEngine(
            index, new Bm25Scorer(1.5, 0.75), new CoOccurrenceQueryTermWeighter(graph), index.Tokenizer);

        engine.Add(new SearchDocument("2", "zebra"));
        Assert.Single(engine.Search("zebra", new SearchOptions(Limit: 10)));
        Assert.True(engine.Remove("2"));
        Assert.Empty(engine.Search("zebra", new SearchOptions(Limit: 10)));
    }
}
