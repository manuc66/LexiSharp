using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class NaiveBayesClassifierTests
{
    private static readonly SearchDocument[] Training =
    {
        new("1", "internet connection problem", Category: "Support"),
        new("2", "cannot access the website", Category: "Support"),
        new("3", "forgotten password", Category: "Support"),
        new("4", "monthly consumption invoice", Category: "Billing"),
        new("5", "online invoice statement", Category: "Billing"),
        new("6", "card payment problem", Category: "Billing"),
        new("7", "delivery delay too long", Category: "Shipping"),
        new("8", "tracking my order", Category: "Shipping"),
    };

    private static NaiveBayesClassifier Trained()
    {
        var classifier = new NaiveBayesClassifier();
        classifier.Train(Training);
        return classifier;
    }

    [Fact]
    public void Predict_AssignsDocumentToItsMatchingCategory()
    {
        var classifier = Trained();

        var support = classifier.Predict("i cannot connect to the internet", limit: 3);
        Assert.Equal("Support", support[0].Category);

        var billing = classifier.Predict("i would like my december invoice");
        Assert.Equal("Billing", billing[0].Category);

        var delivery = classifier.Predict("i am waiting for my parcel delivery");
        Assert.Equal("Shipping", delivery[0].Category);
    }

    [Fact]
    public void Predict_ReturnsProbabilitiesThatSumToOne()
    {
        var classifier = Trained();

        var results = classifier.Predict("password", limit: 3);

        Assert.Equal(3, results.Count);
        Assert.Equal(1.0, results.Sum(r => r.Probability), precision: 10);
        Assert.Equal("Support", results[0].Category);
    }

    [Fact]
    public void Predict_OfUnknownText_FallsBackToClassPriors()
    {
        const string pattern = "aaa bbb ccc"; // 3 tokens per class, each class unique vocabulary
        var classifier = new NaiveBayesClassifier();
        classifier.Train(new[]
        {
            new SearchDocument("1", pattern, Category: "A"),
            new SearchDocument("2", "ddd eee fff", Category: "B"),
            new SearchDocument("3", "ggg hhh iii", Category: "C"),
        });

        var results = classifier.Predict("zzz www", limit: 3);

        // Equal class sizes and equal document counts => uniform prior, no term evidence.
        Assert.All(results, r => Assert.Equal(1.0 / 3, r.Probability, precision: 10));
    }

    [Fact]
    public void PredictBest_ReturnsSingleCategory()
    {
        var classifier = Trained();

        Assert.Equal("Support", classifier.PredictBest("internet connection"));
        Assert.Equal("Shipping", classifier.PredictBest("order delivery"));
    }

    [Fact]
    public void Predict_ReturnsEmpty_BeforeTraining()
    {
        var classifier = new NaiveBayesClassifier();

        Assert.Empty(classifier.Predict("anything"));
        Assert.Null(classifier.PredictBest("anything"));
    }

    [Fact]
    public void Train_IgnoresDocumentsWithoutCategory()
    {
        var classifier = new NaiveBayesClassifier();
        classifier.Train(new[]
        {
            new SearchDocument("1", "uncategorised text"),
            new SearchDocument("2", "categorised text", Category: "A"),
        });

        var results = classifier.Predict("categorised");

        Assert.Single(results);
        Assert.Equal("A", results[0].Category);
    }

    [Fact]
    public void Train_ReplacesPreviousModel()
    {
        var classifier = new NaiveBayesClassifier();
        classifier.Train(new[] { new SearchDocument("1", "alpha", Category: "X") });
        classifier.Train(new[] { new SearchDocument("2", "beta", Category: "Y") });

        Assert.Equal("Y", classifier.PredictBest("beta"));
        Assert.DoesNotContain(classifier.Predict("alpha"), r => r.Category == "X");
    }

    [Fact]
    public void Predict_RespectsLimit()
    {
        var classifier = new NaiveBayesClassifier();
        classifier.Train(Training);

        Assert.Empty(classifier.Predict("invoice", 0));
        Assert.Single(classifier.Predict("invoice", 1));
    }

    [Fact]
    public void Predict_EqualProbabilities_OrderByCategory()
    {
        const string pattern = "aaa bbb ccc";
        var classifier = new NaiveBayesClassifier();
        classifier.Train(new[]
        {
            new SearchDocument("1", pattern, Category: "b"),
            new SearchDocument("2", "ddd eee fff", Category: "a"),
            new SearchDocument("3", "ggg hhh iii", Category: "c"),
        });

        var results = classifier.Predict("zzz www", limit: 3);

        // Nothing matches and priors are uniform: deterministic order by category name.
        Assert.Equal(new[] { "a", "b", "c" }, results.Select(r => r.Category).ToArray());
        Assert.All(results, r => Assert.Equal(1.0 / 3, r.Probability, precision: 10));
    }

    [Fact]
    public void Predict_TrailingEmptyClasses_StayFinite()
    {
        var classifier = new NaiveBayesClassifier();
        classifier.Train(new[]
        {
            new SearchDocument("1", "", Category: "A"),
            new SearchDocument("2", "", Category: "B"),
            new SearchDocument("3", "real tokens", Category: "C"),
        });

        var results = classifier.Predict("anything", limit: 3);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.False(double.IsNaN(r.Probability)));
        Assert.Equal(1.0, results.Sum(r => r.Probability), precision: 10);
    }
}