using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class NaiveBayesClassSpecificTests
{
    private static readonly SearchDocument[] DistinctTraining =
    {
        new("a1", "alpha beta gamma", Category: "A"),
        new("a2", "alpha beta gamma", Category: "A"),
        new("b1", "delta epsilon zeta", Category: "B"),
        new("b2", "delta epsilon zeta", Category: "B"),
    };

    [Fact]
    public void ClassSpecific_DefaultsToOff_SelectedTermsEmpty()
    {
        var classifier = new NaiveBayesClassifier();
        classifier.Train(DistinctTraining);

        Assert.Empty(classifier.SelectedTerms("A"));
        Assert.Empty(classifier.SelectedTermsByClass);
    }

    [Fact]
    public void ClassSpecific_Training_SelectsOnlyDiscriminativeTerms()
    {
        var options = new NaiveBayesOptions { ClassSpecific = true };
        var classifier = new NaiveBayesClassifier(options: options);
        classifier.Train(DistinctTraining);

        var aTerms = classifier.SelectedTerms("A");
        var bTerms = classifier.SelectedTerms("B");

        Assert.Contains("alpha", aTerms);
        Assert.Contains("beta", aTerms);
        Assert.Contains("gamma", aTerms);
        Assert.DoesNotContain("delta", aTerms);
        Assert.DoesNotContain("epsilon", aTerms);
        Assert.DoesNotContain("zeta", aTerms);

        Assert.Contains("delta", bTerms);
        Assert.Contains("epsilon", bTerms);
        Assert.Contains("zeta", bTerms);
        Assert.DoesNotContain("alpha", bTerms);

        Assert.True(aTerms.Count < 6);
        Assert.True(bTerms.Count < 6);
    }

    [Fact]
    public void ClassSpecific_PredictsCorrectly()
    {
        var options = new NaiveBayesOptions { ClassSpecific = true };
        var classifier = new NaiveBayesClassifier(options: options);
        classifier.Train(DistinctTraining);

        Assert.Equal("A", classifier.PredictBest("alpha beta"));
        Assert.Equal("B", classifier.PredictBest("delta epsilon"));
    }

    [Fact]
    public void ClassSpecific_ExposesSelectedTermsByClass()
    {
        var options = new NaiveBayesOptions { ClassSpecific = true };
        var classifier = new NaiveBayesClassifier(options: options);
        classifier.Train(DistinctTraining);

        Assert.Equal(2, classifier.SelectedTermsByClass.Count);
        Assert.True(classifier.SelectedTermsByClass["A"].SetEquals(new[] { "alpha", "beta", "gamma" }));
        Assert.True(classifier.SelectedTermsByClass["B"].SetEquals(new[] { "delta", "epsilon", "zeta" }));
    }

    [Fact]
    public void ClassSpecific_LearnAndUnlearn_UpdateSelection()
    {
        var options = new NaiveBayesOptions { ClassSpecific = true };
        var classifier = new NaiveBayesClassifier(options: options);
        classifier.Train(new[]
        {
            new SearchDocument("a1", "alpha beta", Category: "A"),
            new SearchDocument("b1", "delta epsilon", Category: "B"),
        });

        Assert.DoesNotContain("gamma", classifier.SelectedTerms("A"));

        classifier.Learn(new SearchDocument("a2", "gamma unique", Category: "A"));

        Assert.Contains("gamma", classifier.SelectedTerms("A"));
        Assert.Contains("unique", classifier.SelectedTerms("A"));

        classifier.Unlearn(new SearchDocument("a2", "gamma unique", Category: "A"));

        Assert.DoesNotContain("gamma", classifier.SelectedTerms("A"));
    }

    [Fact]
    public void ClassSpecific_ThresholdZero_KeepsAllTerms()
    {
        var options = new NaiveBayesOptions { ClassSpecific = true, ClassSpecificThreshold = 0.0 };
        var classifier = new NaiveBayesClassifier(options: options);
        classifier.Train(DistinctTraining);

        Assert.Equal(3, classifier.SelectedTerms("A").Count);
        Assert.Equal(3, classifier.SelectedTerms("B").Count);
    }
}