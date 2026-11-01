using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class LinearFeedbackTests
{
    private static readonly SearchDocument[] Corpus =
    {
        new("1", "alpha one", Category: "A"),
        new("2", "alpha two", Category: "A"),
        new("3", "beta one", Category: "B"),
        new("4", "beta two", Category: "B"),
    };

    private static LinearClassifier Trained()
    {
        var classifier = new LinearClassifier(options: new LinearClassifierOptions
        {
            Epochs = 50,
            LearningRate = 0.5,
            L2 = 0,
            MinDocumentFrequency = 1,
            Seed = 7,
        });
        classifier.Train(Corpus);
        return classifier;
    }

    [Fact]
    public void Reinforce_MovesThePrediction()
    {
        var classifier = Trained();

        Assert.Equal("A", classifier.PredictBest("alpha one"));

        classifier.Reinforce("alpha one", "B", 10);

        Assert.Equal("B", classifier.PredictBest("alpha one"));
    }

    [Fact]
    public void Unreinforce_UndoesTheCorrectionExactly()
    {
        var classifier = Trained();
        var before = classifier.Predict("alpha one", limit: 2);

        classifier.Reinforce("alpha one", "B", 10);
        classifier.Unreinforce("alpha one", "B", 10);
        var after = classifier.Predict("alpha one", limit: 2);

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
        classifier.Reinforce("alpha one", "B", 10);

        classifier.ForgetReinforcement();

        Assert.Equal("A", classifier.PredictBest("alpha one"));
    }

    [Fact]
    public void Train_ClearsTheLedger()
    {
        var classifier = Trained();
        classifier.Reinforce("alpha one", "B", 10);

        classifier.Train(Corpus);

        Assert.Equal("A", classifier.PredictBest("alpha one"));
    }

    [Fact]
    public void Reinforce_WithAnUnknownCategory_Throws()
    {
        var classifier = Trained();

        Assert.Throws<ArgumentException>(() => classifier.Reinforce("alpha one", "Z"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void Reinforce_WithAnInvalidWeight_Throws(double weight)
    {
        var classifier = Trained();

        Assert.Throws<ArgumentException>(() => classifier.Reinforce("alpha one", "B", weight));
    }

    [Fact]
    public void Reinforce_OnAnUntrainedModel_Throws()
    {
        var classifier = new LinearClassifier();

        Assert.Throws<InvalidOperationException>(() => classifier.Reinforce("alpha", "A"));
    }
}
