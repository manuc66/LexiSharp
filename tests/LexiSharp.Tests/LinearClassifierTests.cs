using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class LinearClassifierTests
{
    private static readonly SearchDocument[] Corpus =
    {
        new("1", "alpha one", Category: "A"),
        new("2", "alpha two", Category: "A"),
        new("3", "beta one", Category: "B"),
        new("4", "beta two", Category: "B"),
        new("5", "gamma one", Category: "C"),
        new("6", "gamma two", Category: "C"),
    };

    private static LinearClassifier Trained(LinearClassifierOptions? options = null)
    {
        var classifier = new LinearClassifier(options: options ?? new LinearClassifierOptions
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
    public void Predict_ClassifiesTheThreeClasses()
    {
        var classifier = Trained();

        Assert.Equal("A", classifier.PredictBest("alpha one"));
        Assert.Equal("B", classifier.PredictBest("beta two"));
        Assert.Equal("C", classifier.PredictBest("gamma one"));
    }

    [Fact]
    public void Predict_ReturnsProbabilitiesThatSumToOne()
    {
        var classifier = Trained();

        var results = classifier.Predict("alpha one", limit: 3);

        Assert.Equal(3, results.Count);
        Assert.Equal(1.0, results.Sum(result => result.Probability), precision: 10);
        Assert.Equal("A", results[0].Category);
    }

    [Fact]
    public void Predict_ExcludingTheBestCategory_RenormalizesTheRest()
    {
        var classifier = Trained();

        var results = classifier.Predict(
            "alpha one",
            limit: 3,
            excludedCategories: new HashSet<string>(StringComparer.Ordinal) { "A" });

        Assert.DoesNotContain(results, result => result.Category == "A");
        Assert.Equal(1.0, results.Sum(result => result.Probability), precision: 10);
    }

    [Fact]
    public void Train_IsDeterministicWithTheSameSeed()
    {
        var first = Trained();
        var second = Trained();

        var left = first.Predict("alpha one", limit: 3);
        var right = second.Predict("alpha one", limit: 3);

        Assert.Equal(left.Count, right.Count);

        for (int i = 0; i < left.Count; i++)
        {
            Assert.Equal(left[i].Category, right[i].Category);
            Assert.Equal(left[i].Probability, right[i].Probability, precision: 12);
        }
    }

    [Fact]
    public void Predict_Untrained_ReturnsNothing()
    {
        var classifier = new LinearClassifier();

        Assert.Empty(classifier.Predict("alpha"));
        Assert.Null(classifier.PredictBest("alpha"));
    }

    [Fact]
    public void MinProbability_AbstainsOnAnAmbiguousInput()
    {
        var abstaining = new LinearClassifier(options: new LinearClassifierOptions
        {
            Epochs = 10,
            MinDocumentFrequency = 1,
            MinProbability = 0.9,
        });
        abstaining.Train(new[]
        {
            new SearchDocument("a", string.Empty, Category: "A"),
            new SearchDocument("b", string.Empty, Category: "B"),
        });

        Assert.Null(abstaining.PredictBest("anything"));

        var answering = new LinearClassifier(options: new LinearClassifierOptions
        {
            Epochs = 10,
            MinDocumentFrequency = 1,
            MinProbability = 0.4,
        });
        answering.Train(new[]
        {
            new SearchDocument("a", string.Empty, Category: "A"),
            new SearchDocument("b", string.Empty, Category: "B"),
        });

        Assert.NotNull(answering.PredictBest("anything"));
    }

    [Fact]
    public void TopFeatures_ContainsTheClassTerm()
    {
        var classifier = Trained();

        var features = classifier.TopFeatures("A", count: 3);

        Assert.Contains(features, feature => feature.Feature == "alpha");
    }
}
