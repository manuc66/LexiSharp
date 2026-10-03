using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Covers the composite scorer: the sum it computes, the parts it refuses, and the two behaviours
/// its remarks promise — that a zero weight removes a component without calling it, and that a
/// document no query term reaches can still surface because a prior scored it.
/// </summary>
/// <remarks>
/// The arithmetic is checked against stub components whose scores are constants, because a stub
/// makes the expected total readable by hand. The one test that uses the real scorers is a wiring
/// test: it asserts the composite equals what those two scorers return, weighted — which catches a
/// dropped part or a misread weight and nothing about the parts themselves.
/// </remarks>
public class WeightedCompositeScorerTests
{
    [Fact]
    public void Score_IsTheWeightedSumOfItsComponents()
    {
        var index = new InMemoryTextIndex();
        index.Add(new SearchDocument("doc", "alpha"));
        string[] query = ["alpha"];

        var composite = new WeightedCompositeScorer(
            (new ConstantScorer(3), 2.0),
            (new ConstantScorer(2), -0.5));

        Assert.Equal(5.0, composite.Score("doc", query, index));
    }

    [Fact]
    public void Score_SumsInTheOrderTheComponentsWereGiven()
    {
        var index = new InMemoryTextIndex();
        index.Add(new SearchDocument("doc", "alpha"));
        string[] query = ["alpha"];

        // Three terms, because IEEE addition is commutative — two of them cannot disagree on order,
        // only associativity can. The totals differ in the last bits, which is why this type
        // documents the order as the sum's rather than as an implementation detail.
        double leftToRight = new WeightedCompositeScorer(
            (new ConstantScorer(0.1), 1.0),
            (new ConstantScorer(0.2), 1.0),
            (new ConstantScorer(0.3), 1.0)).Score("doc", query, index);

        double rightToLeft = new WeightedCompositeScorer(
            (new ConstantScorer(0.3), 1.0),
            (new ConstantScorer(0.2), 1.0),
            (new ConstantScorer(0.1), 1.0)).Score("doc", query, index);

        Assert.Equal(0.1 + 0.2 + 0.3, leftToRight);
        Assert.NotEqual(leftToRight, rightToLeft);
    }

    [Fact]
    public void Score_WithOneComponentAtWeightOne_IsThatComponent()
    {
        var index = new InMemoryTextIndex();
        index.Add(new SearchDocument("doc", "alpha"));
        string[] query = ["alpha"];
        var component = new ConstantScorer(2.5);

        var composite = new WeightedCompositeScorer((component, 1.0));

        Assert.Equal(component.Score("doc", query, index), composite.Score("doc", query, index));
    }

    [Fact]
    public void Score_DoesNotConsultAComponentAtWeightZero()
    {
        var index = new InMemoryTextIndex();
        index.Add(new SearchDocument("doc", "alpha"));
        string[] query = ["alpha"];
        var untouched = new CountingScorer(7);

        var composite = new WeightedCompositeScorer(
            (new ConstantScorer(3), 1.0),
            (untouched, 0));

        Assert.Equal(3.0, composite.Score("doc", query, index));
        Assert.Equal(0, untouched.Calls);
    }

    [Fact]
    public void Score_IgnoresANonFiniteAnswerFromAComponentAtWeightZero()
    {
        var index = new InMemoryTextIndex();
        index.Add(new SearchDocument("doc", "alpha"));
        string[] query = ["alpha"];

        // 0 * NaN is NaN, so a weight of zero would not protect the total if the component were
        // still consulted. Dropping it from the sum is what makes "a zero weight removes the
        // component" true rather than nearly true.
        var composite = new WeightedCompositeScorer(
            (new ConstantScorer(double.NaN), 0),
            (new ConstantScorer(3), 1.0));

        Assert.Equal(3.0, composite.Score("doc", query, index));
    }

    [Fact]
    public void Score_PropagatesANonFiniteAnswerFromAParticipatingComponent()
    {
        var index = new InMemoryTextIndex();
        index.Add(new SearchDocument("doc", "alpha"));
        string[] query = ["alpha"];

        var nan = new WeightedCompositeScorer(
            (new ConstantScorer(double.NaN), 1.0),
            (new ConstantScorer(3), 1.0));

        var infinite = new WeightedCompositeScorer(
            (new ConstantScorer(double.PositiveInfinity), 1.0),
            (new ConstantScorer(3), 1.0));

        // Both reach the total, and an engine rejects both. Not NaN in both cases: an infinite part
        // summed with a finite one is infinite, which is why "drop the part" and "propagate" are
        // distinguishable behaviours rather than one of them.
        Assert.True(double.IsNaN(nan.Score("doc", query, index)));
        Assert.True(double.IsInfinity(infinite.Score("doc", query, index)));
    }

    [Fact]
    public void Components_ListTheKeptPartsInSummationOrder()
    {
        var scorer = new WeightedCompositeScorer(
            (new ConstantScorer(1), 0.5),
            (new ConstantScorer(2), 0),
            (new ConstantScorer(3), -1.5));

        var components = scorer.Components;

        Assert.Equal(2, components.Count);
        Assert.Equal(0.5, components[0].Weight);
        Assert.Equal(-1.5, components[1].Weight);
        Assert.Equal("Const1", components[0].Component.Name);
        Assert.Equal("Const3", components[1].Component.Name);
    }

    [Fact]
    public void Constructor_RefusesAnEmptyNullOrNonFiniteComponent()
    {
        Assert.Throws<ArgumentNullException>(() => new WeightedCompositeScorer(null!));
        Assert.Throws<ArgumentException>(() => new WeightedCompositeScorer());
        (ITextScorer Component, double Weight) nullPart = (null!, 1.0);
        Assert.Throws<ArgumentException>(() => new WeightedCompositeScorer(nullPart));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WeightedCompositeScorer((new ConstantScorer(1), double.NaN)));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WeightedCompositeScorer((new ConstantScorer(1), double.PositiveInfinity)));
    }

    [Fact]
    public void Constructor_AcceptsANegativeWeight_WhichIsWhatAPriorIs()
    {
        var scorer = new WeightedCompositeScorer((new ConstantScorer(1), -0.25));

        Assert.Equal(-0.25, Assert.Single(scorer.Components).Weight);
    }

    [Fact]
    public void Score_WithoutItsArguments_Throws()
    {
        var index = new InMemoryTextIndex();
        var scorer = new WeightedCompositeScorer((new ConstantScorer(1), 1.0));

        Assert.Throws<ArgumentNullException>(() => scorer.Score("doc", ["alpha"], null!));
        Assert.Throws<ArgumentNullException>(() => scorer.Score("doc", null!, index));
    }

    [Fact]
    public void Name_NamesEveryPartAndItsWeight()
    {
        var scorer = new WeightedCompositeScorer(
            (new Bm25Scorer(), 1.0),
            (new DocumentLengthRatioScorer(), -0.2));

        Assert.Equal("Composite(BM25*1 + LengthRatio*-0.2)", scorer.Name);
    }

    [Fact]
    public void Score_WithTheRealParts_IsTheScorerSumWeightedAsDeclared()
    {
        var index = new InMemoryTextIndex();
        index.Add(new SearchDocument("short", "alpha beta"));
        index.Add(new SearchDocument("long", "alpha beta gamma delta"));
        string[] query = ["alpha"];

        var bm25 = new Bm25Scorer();
        var length = new DocumentLengthRatioScorer();
        var composite = new WeightedCompositeScorer((bm25, 1.0), (length, -0.2));

        Assert.Equal(
            bm25.Score("long", query, index) - 0.2 * length.Score("long", query, index),
            composite.Score("long", query, index));
    }

    [Fact]
    public void Search_WithAPriorComponent_ReturnsADocumentNoQueryTermReaches()
    {
        // The behaviour the type's remarks promise, pinned so it stays a checked property: a length
        // is a property of the document whatever the query asked for, so a document sharing no
        // query term is scored, and a total that is not zero reaches the page.
        var index = new InMemoryTextIndex();
        index.Add(new SearchDocument("match", "alpha beta"));
        index.Add(new SearchDocument("unrelated", "gamma delta"));
        var engine = new RankedTextSearchEngine(
            index, new WeightedCompositeScorer((new Bm25Scorer(), 1.0), (new DocumentLengthRatioScorer(), -0.2)));

        var results = engine.Search("alpha", new SearchOptions(Limit: 10));

        Assert.Contains(results, x => x.DocumentId == "unrelated");
    }

    [Fact]
    public void Search_WithACompositeOfMatchingPartsOnly_ReturnsNothingForAnUnmatchedDocument()
    {
        // The same corpus and the same query with the prior removed: a component set in which every
        // part promises 0 without shared terms still scores 0 there, which is why the engine can
        // keep the "score 0 means no match" convention with such a composite.
        var index = new InMemoryTextIndex();
        index.Add(new SearchDocument("match", "alpha beta"));
        index.Add(new SearchDocument("unrelated", "gamma delta"));
        var engine = new RankedTextSearchEngine(
            index, new WeightedCompositeScorer((new Bm25Scorer(), 1.0), (new TfIdfScorer(), 0.5)));

        var results = engine.Search("alpha", new SearchOptions(Limit: 10));

        Assert.DoesNotContain(results, x => x.DocumentId == "unrelated");
    }

    /// <summary>A scorer that answers the same number for every document.</summary>
    private sealed class ConstantScorer(double score) : ITextScorer
    {
        public string Name => $"Const{score:0.###}";

        public double Score(string documentId, IReadOnlyList<string> queryTerms, IReadOnlyTextIndex index) => score;
    }

    /// <summary>A scorer that counts its calls, to prove a dropped component is not consulted.</summary>
    private sealed class CountingScorer(double score) : ITextScorer
    {
        public int Calls { get; private set; }

        public string Name => "Counting";

        public double Score(string documentId, IReadOnlyList<string> queryTerms, IReadOnlyTextIndex index)
        {
            Calls++;
            return score;
        }
    }
}