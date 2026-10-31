using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class DecisionTreeFeedbackTests
{
    private static readonly SearchDocument[] Balanced =
    {
        new("a", "alpha", Category: "A"),
        new("b", "beta", Category: "B"),
    };

    private static DecisionTreeClassifier Trained()
    {
        var classifier = new DecisionTreeClassifier(options: new DecisionTreeOptions
        {
            MaxDepth = 0,
            MinDocumentsPerLeaf = 1,
        });
        classifier.Train(Balanced);
        return classifier;
    }

    [Fact]
    public void Reinforce_MovesTheLeafToTheCorrectedCategory()
    {
        var classifier = Trained();

        Assert.Equal("A", classifier.PredictBest("alpha"));

        classifier.Reinforce("alpha", "B", 10);

        Assert.Equal("B", classifier.PredictBest("alpha"));
        Assert.True(
            classifier.Predict("alpha", limit: 2).Single(result => result.Category == "B").Probability > 0.8);
    }

    [Fact]
    public void Unreinforce_UndoesTheCorrectionExactly()
    {
        var classifier = Trained();
        var before = classifier.Predict("alpha", limit: 2);

        classifier.Reinforce("alpha", "B", 10);
        classifier.Unreinforce("alpha", "B", 10);
        var after = classifier.Predict("alpha", limit: 2);

        Assert.Equal(before.Count, after.Count);

        for (int i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].Category, after[i].Category);
            Assert.Equal(before[i].Probability, after[i].Probability, precision: 12);
        }
    }

    [Fact]
    public void ForgetReinforcement_ResetsTheLedger()
    {
        var classifier = Trained();
        classifier.Reinforce("alpha", "B", 10);

        classifier.ForgetReinforcement();

        Assert.Equal("A", classifier.PredictBest("alpha"));
    }

    [Fact]
    public void Explain_ShowsTheAdjustedLeaf()
    {
        var classifier = Trained();
        classifier.Reinforce("alpha", "B", 10);

        var explanation = classifier.Explain("alpha");

        Assert.NotNull(explanation);
        Assert.Equal("B", explanation.Outcome.Category);
    }

    [Fact]
    public void Train_ClearsTheLedger()
    {
        var classifier = Trained();
        classifier.Reinforce("alpha", "B", 10);

        classifier.Train(Balanced);

        Assert.Equal("A", classifier.PredictBest("alpha"));
    }

    [Fact]
    public void Reinforce_WithAnUnknownCategory_Throws()
    {
        var classifier = Trained();

        Assert.Throws<ArgumentException>(() => classifier.Reinforce("alpha", "Z"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void Reinforce_WithAnInvalidWeight_Throws(double weight)
    {
        var classifier = Trained();

        Assert.Throws<ArgumentException>(() => classifier.Reinforce("alpha", "B", weight));
    }

    [Fact]
    public void Reinforce_OnAnUntrainedModel_Throws()
    {
        var classifier = new DecisionTreeClassifier();

        Assert.Throws<InvalidOperationException>(() => classifier.Reinforce("alpha", "A"));
    }
}
