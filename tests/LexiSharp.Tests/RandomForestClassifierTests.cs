using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class RandomForestClassifierTests
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

    private static RandomForestClassifier Trained(RandomForestOptions? options = null)
    {
        var classifier = new RandomForestClassifier(options: options);
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
    public void Train_BuildsTheRequestedNumberOfTrees()
    {
        var classifier = Trained(new RandomForestOptions { Trees = 7 });

        Assert.Equal(7, classifier.TreeCount);
        Assert.Equal(new[] { "A", "B", "C" }, classifier.Categories);
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
        var classifier = new RandomForestClassifier();

        Assert.Empty(classifier.Predict("alpha"));
        Assert.Null(classifier.PredictBest("alpha"));
        Assert.Equal(0, classifier.TreeCount);
    }

    [Fact]
    public void Forest_WithNoFeatures_LearnsThePrior()
    {
        var classifier = new RandomForestClassifier(options: new RandomForestOptions
        {
            Trees = 5,
            MinDocumentFrequency = 1,
        });
        classifier.Train(new[]
        {
            new SearchDocument("a", string.Empty, Category: "A"),
            new SearchDocument("b", string.Empty, Category: "B"),
        });

        Assert.Equal(5, classifier.TreeCount);
        Assert.NotNull(classifier.PredictBest("anything"));
    }

    [Fact]
    public void Train_IgnoresDocumentsWithoutACategory()
    {
        var classifier = new RandomForestClassifier(
            options: new RandomForestOptions { Trees = 5, MinDocumentFrequency = 1 });
        classifier.Train(new[]
        {
            new SearchDocument("1", "alpha", Category: "A"),
            new SearchDocument("2", "beta"),
            new SearchDocument("3", "gamma", Category: "B"),
        });

        Assert.Equal(new[] { "A", "B" }, classifier.Categories);
    }

    [Fact]
    public void Forest_UsesAFeatureSpaceThatIsNotText()
    {
        var samples = new[]
        {
            new Sample("1", "A", 0),
            new Sample("2", "A", 0),
            new Sample("3", "B", 1),
            new Sample("4", "B", 1),
        };
        var classifier = new RandomForestClassifier<Sample>(
            new DelegateFeatureSource<Sample>(sample => new[] { $"bucket:{sample.Bucket}" }),
            sample => sample.Category,
            options: new RandomForestOptions
            {
                Trees = 5,
                MaxDepth = 2,
                MinDocumentsPerLeaf = 1,
                MinDocumentFrequency = 1,
                FeatureSubsetFraction = 1.0,
            });
        classifier.Train(samples);

        Assert.Equal("A", classifier.PredictBest(new Sample("q", "", 0)));
        Assert.Equal("B", classifier.PredictBest(new Sample("q", "", 1)));
    }

    private sealed record Sample(string Id, string Category, int Bucket);
}
