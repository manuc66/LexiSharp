using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class AbstentionTests
{
    private static readonly SearchDocument[] Balanced =
    {
        new("a", "alpha", Category: "A"),
        new("b", "beta", Category: "B"),
    };

    private static DecisionTreeClassifier Trained(DecisionTreeOptions options)
    {
        var classifier = new DecisionTreeClassifier(options: options);
        classifier.Train(Balanced);
        return classifier;
    }

    [Fact]
    public void DecisionTree_AbstainsBelowMinLeafProbability()
    {
        var classifier = Trained(new DecisionTreeOptions
        {
            MaxDepth = 0,
            MinLeafProbability = 0.6,
        });

        Assert.Empty(classifier.Predict("alpha"));
        Assert.Null(classifier.PredictBest("alpha"));
    }

    [Fact]
    public void DecisionTree_AnswersAtTheProbabilityThreshold()
    {
        var classifier = Trained(new DecisionTreeOptions
        {
            MaxDepth = 0,
            MinLeafProbability = 0.5,
        });

        Assert.Equal("A", classifier.PredictBest("alpha"));
    }

    [Fact]
    public void DecisionTree_AbstainsBelowMinLeafSupport()
    {
        var classifier = Trained(new DecisionTreeOptions
        {
            MaxDepth = 0,
            MinLeafSupport = 3,
        });

        Assert.Null(classifier.PredictBest("alpha"));

        var answered = Trained(new DecisionTreeOptions
        {
            MaxDepth = 0,
            MinLeafSupport = 2,
        });

        Assert.Equal("A", answered.PredictBest("alpha"));
    }

    [Fact]
    public void DecisionTree_ExplainStillReturnsTheLeafWhenPredictionAbstains()
    {
        var classifier = Trained(new DecisionTreeOptions
        {
            MaxDepth = 0,
            MinLeafProbability = 1.0,
        });

        Assert.Null(classifier.PredictBest("alpha"));

        var explanation = classifier.Explain("alpha");

        Assert.NotNull(explanation);
        Assert.Equal(2, explanation.Outcome.DocumentCount);
    }

    [Fact]
    public void RandomForest_AbstainsBelowMinProbability()
    {
        var classifier = new RandomForestClassifier(options: new RandomForestOptions
        {
            Trees = 5,
            MaxDepth = 0,
            MinDocumentFrequency = 1,
            MinProbability = 0.9,
        });
        classifier.Train(Balanced);

        Assert.Null(classifier.PredictBest("alpha"));

        var answered = new RandomForestClassifier(options: new RandomForestOptions
        {
            Trees = 5,
            MaxDepth = 0,
            MinDocumentFrequency = 1,
            MinProbability = 0.4,
        });
        answered.Train(Balanced);

        Assert.Equal("A", answered.PredictBest("alpha"));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    public void DecisionTree_WithAnInvalidProbabilityThreshold_Throws(double threshold)
    {
        Assert.Throws<ArgumentException>(
            () => new DecisionTreeClassifier(options: new DecisionTreeOptions
            {
                MinLeafProbability = threshold,
            }));
    }

    [Fact]
    public void DecisionTree_WithANegativeSupportThreshold_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => new DecisionTreeClassifier(options: new DecisionTreeOptions
            {
                MinLeafSupport = -1,
            }));
    }

    [Fact]
    public void RandomForest_WithAnInvalidProbabilityThreshold_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => new RandomForestClassifier(options: new RandomForestOptions
            {
                MinProbability = 1.1,
            }));
    }
}
