using LexiSharp.Adaptive;
using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class AdaptiveFeedbackRankerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    private static NaiveBayesClassifier Model()
    {
        var model = new NaiveBayesClassifier();
        model.Train(
        [
            new SearchDocument("a1", "alpha", Category: "A"),
            new SearchDocument("a2", "alpha alpha", Category: "A"),
            new SearchDocument("b1", "beta", Category: "B"),
            new SearchDocument("b2", "beta beta", Category: "B"),
        ]);

        return model;
    }

    private static FeedbackMemory<string> Memory(FeedbackMemoryOptions? options = null) =>
        new(
            RecencyPolicy.None,
            options ?? new FeedbackMemoryOptions(),
            ContextCodecs.Raw<string>());

    private static FeedbackEvent<string> Event(
        FeedbackOutcome outcome,
        string? offered,
        string? accepted,
        string context = "ctx") =>
        new("alpha", offered, accepted, context, Now, outcome);

    [Fact]
    public void EmptyMemory_ReturnsTheModelDistributionExactly()
    {
        NaiveBayesClassifier model = Model();
        var ranker = new AdaptiveFeedbackRanker<string>(model, Memory());

        AdaptiveRanking ranking = ranker.Predict("alpha", "ctx", Now);

        IReadOnlyList<ClassificationResult> text = model.Predict("alpha", limit: int.MaxValue);

        Assert.Equal(text.Select(result => result.Category), ranking.Results.Select(result => result.Category));

        for (int i = 0; i < text.Count; i++)
        {
            Assert.Equal(text[i].Probability, ranking.Results[i].FinalProbability, precision: 12);
        }
    }

    [Fact]
    public void Acceptance_RaisesTheCategoryMultiplier()
    {
        var memory = Memory();
        var ranker = new AdaptiveFeedbackRanker<string>(Model(), memory);

        ranker.Observe(Event(FeedbackOutcome.Accepted, "A", "A"));

        AdaptiveScore a = ranker.Predict("alpha", "ctx", Now).AllScores.Single(score => score.Category == "A");

        Assert.True(a.ContextMultiplier > 1);
        Assert.Equal(1.0, a.AcceptanceShare, precision: 10);
        Assert.Equal("A", ranker.Predict("alpha", "ctx", Now).Results[0].Category);
    }

    [Fact]
    public void Refusal_VetoesTheCategory()
    {
        var memory = Memory(new FeedbackMemoryOptions { VetoAfter = 2 });
        var ranker = new AdaptiveFeedbackRanker<string>(Model(), memory);

        ranker.Observe(Event(FeedbackOutcome.Rejected, "B", null));
        ranker.Observe(Event(FeedbackOutcome.Rejected, "B", null));

        AdaptiveRanking ranking = ranker.Predict("alpha", "ctx", Now);

        Assert.DoesNotContain(ranking.AllScores, score => score.Category == "B");
        Assert.False(ranking.VetoFallback);
    }

    [Fact]
    public void AllVetoed_FallsBackWithTheFlag()
    {
        var memory = Memory(new FeedbackMemoryOptions { VetoAfter = 1 });
        var ranker = new AdaptiveFeedbackRanker<string>(Model(), memory);

        ranker.Observe(Event(FeedbackOutcome.Rejected, "A", null));
        ranker.Observe(Event(FeedbackOutcome.Rejected, "B", null));

        AdaptiveRanking ranking = ranker.Predict("alpha", "ctx", Now);

        Assert.True(ranking.VetoFallback);
        Assert.NotEmpty(ranking.Results);
        Assert.All(ranking.AllScores, score => Assert.True(score.Vetoed));
    }

    [Fact]
    public void Correction_PenalizesTheOfferedCategory()
    {
        var memory = Memory();
        var ranker = new AdaptiveFeedbackRanker<string>(Model(), memory);

        ranker.Observe(Event(FeedbackOutcome.Corrected, "A", "B"));

        AdaptiveRanking ranking = ranker.Predict("alpha", "ctx", Now);
        AdaptiveScore a = ranking.AllScores.Single(score => score.Category == "A");
        AdaptiveScore b = ranking.AllScores.Single(score => score.Category == "B");

        Assert.Equal(1.0, a.RefusalMass, precision: 10);
        Assert.True(b.AcceptanceShare > 0);
        Assert.True(a.ContextMultiplier < 1);
    }

    [Fact]
    public void MinScore_AbstainsWithoutACalibrator()
    {
        var ranker = new AdaptiveFeedbackRanker<string>(
            Model(),
            Memory(),
            new AdaptiveOptions { MinScore = 0.99 });

        Assert.True(ranker.Predict("alpha", "ctx", Now).Abstained);
    }

    [Fact]
    public void ObserveWithoutOfferedScores_CountsTheGap()
    {
        var ranker = new AdaptiveFeedbackRanker<string>(Model(), Memory());

        ranker.Observe(Event(FeedbackOutcome.Accepted, "A", "A"));

        Assert.Equal(1, ranker.MissingOfferedScores);
    }

    [Fact]
    public void Calibrator_ProducesAPredictionSet()
    {
        NaiveBayesClassifier model = Model();
        var calibrator = new WeightedConformalCalibrator(0.2, RecencyPolicy.None, minSamples: 1);
        var ranker = new AdaptiveFeedbackRanker<string>(model, Memory(), calibrator: calibrator);

        ranker.Observe(new FeedbackEvent<string>(
            "alpha",
            "A",
            "A",
            "ctx",
            Now,
            FeedbackOutcome.Accepted,
            Offered: model.Predict("alpha", limit: int.MaxValue)));

        AdaptiveRanking ranking = ranker.Predict("alpha", "ctx", Now);

        Assert.NotNull(ranking.PredictionSet);
        Assert.False(ranking.Abstained);
        Assert.True(ranking.AllScores[0].InPredictionSet);
    }

    [Fact]
    public void Explain_ReturnsThePriorAndEveryScore()
    {
        var ranker = new AdaptiveFeedbackRanker<string>(Model(), Memory());
        ranker.Observe(Event(FeedbackOutcome.Accepted, "A", "A"));

        AdaptiveExplanation explanation = ranker.Explain("alpha", "ctx", Now);

        Assert.NotEmpty(explanation.Prior.Categories);
        Assert.Equal(ranker.Predict("alpha", "ctx", Now).AllScores.Count, explanation.Scores.Count);
    }

    [Fact]
    public void InvalidFeedback_IsRejected()
    {
        var ranker = new AdaptiveFeedbackRanker<string>(Model(), Memory());

        Assert.Throws<ArgumentException>(() =>
            ranker.Observe(Event(FeedbackOutcome.Accepted, "A", "B")));
    }

    [Fact]
    public void Limit_SlicesResultsButKeepsEveryScore()
    {
        var ranker = new AdaptiveFeedbackRanker<string>(Model(), Memory());

        AdaptiveRanking ranking = ranker.Predict("alpha", "ctx", Now, limit: 1);

        Assert.Single(ranking.Results);
        Assert.True(ranking.AllScores.Count > 1);
    }
}
