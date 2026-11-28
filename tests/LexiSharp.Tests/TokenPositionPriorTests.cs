using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class TokenPositionPriorTests
{
    private static readonly ObservedText[] Observations =
    [
        new("alpha first document", "A"),
        new("alpha another document", "A"),
        new("beta first document", "B"),
        new("beta another document", "B"),
    ];

    [Fact]
    public void EmptyPriorsProposeNothing()
    {
        Assert.Empty(LeadingTokenPrior.Empty.Prior("alpha first document"));
        Assert.Empty(TrailingTokenPrior.Empty.Prior("alpha first document"));
    }

    [Fact]
    public void LeadingPriorFavoursTheCategoryOfTheFirstToken()
    {
        var prior = new LeadingTokenPrior(Observations);

        var evidence = prior.Prior("alpha some unrelated body");
        var score = evidence.ToDictionary(value => value.Category, value => value.Score);

        Assert.True(score["A"] > score["B"]);
        Assert.Equal(["A", "B"], prior.Categories);
        Assert.Equal(3, prior.TokenCount);
        Assert.Equal(TokenPosition.Leading, prior.Position);
    }

    [Fact]
    public void TrailingPriorReadsTheEndAndIgnoresTheBeginning()
    {
        var prior = new TrailingTokenPrior([
            new ObservedText("body about nothing alpha", "A"),
            new ObservedText("body about nothing beta", "B"),
        ]);

        var alpha = prior.Prior("beta in the middle alpha").ToDictionary(value => value.Category, value => value.Score);
        var beta = prior.Prior("alpha in the middle beta").ToDictionary(value => value.Category, value => value.Score);

        Assert.True(alpha["A"] > alpha["B"]);
        Assert.True(beta["B"] > beta["A"]);
        Assert.Equal(TokenPosition.Trailing, prior.Position);
    }

    [Fact]
    public void AnUnseenTokenYieldsUniformSmoothedEvidence()
    {
        var prior = new LeadingTokenPrior(Observations);

        var evidence = prior.Prior("zzz unknown body");

        Assert.All(evidence, value => Assert.Equal(Math.Log(0.5), value.Score, 10));
    }

    [Fact]
    public void OneShotObservationsAreConsumedOnce()
    {
        var oneShot = new OneShotEnumerable(Observations);

        var prior = new LeadingTokenPrior(oneShot);

        Assert.Equal(1, oneShot.Enumerations);
        Assert.True(prior.Prior("alpha body")[0].Score > prior.Prior("alpha body")[1].Score);
    }

    [Fact]
    public void LeadingAndTrailingPriorsComposeAdditivelyInABlend()
    {
        var leading = new LeadingTokenPrior(Observations);
        var trailing = new TrailingTokenPrior([
            new ObservedText("first body alpha", "A"),
            new ObservedText("first body omega", "B"),
        ]);
        var classifier = new FixedClassifier(
            new ClassificationResult("A", 0.5),
            new ClassificationResult("B", 0.5));

        var oneLane = new BlendTextClassifier(
            [new TextClassifierLane(classifier, 1)],
            priors: [new PriorLane(leading, 2.0)]);
        var twoLanes = new BlendTextClassifier(
            [new TextClassifierLane(classifier, 1)],
            priors: [new PriorLane(leading, 2.0), new PriorLane(trailing, 2.0)]);

        var text = "alpha middle alpha";
        var one = oneLane.Predict(text, limit: 2).ToDictionary(value => value.Category, value => value.Probability);
        var two = twoLanes.Predict(text, limit: 2).ToDictionary(value => value.Category, value => value.Probability);

        Assert.True(one["A"] > one["B"]);
        Assert.True(two["A"] > one["A"]);
    }

    [Fact]
    public void InvalidOptionsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LeadingTokenPrior(Observations, tokenCount: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LeadingTokenPrior(Observations, alpha: 0));
        Assert.Throws<ArgumentNullException>(() => LeadingTokenPrior.Empty.Prior(null!));
    }

    private sealed class OneShotEnumerable(ObservedText[] items) : IEnumerable<ObservedText>
    {
        public int Enumerations { get; private set; }

        public IEnumerator<ObservedText> GetEnumerator()
        {
            Enumerations++;
            Assert.Equal(1, Enumerations);
            return ((IEnumerable<ObservedText>)items).GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class FixedClassifier(params ClassificationResult[] results) : ITextClassifier
    {
        public void Train(IEnumerable<SearchDocument> documents) { }

        public IReadOnlyList<ClassificationResult> Predict(
            string text, int limit = 3, IReadOnlySet<string>? excludedCategories = null) =>
            results.Take(limit).ToArray();

        public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null) =>
            results.Length > 0 ? results[0].Category : null;
    }
}
