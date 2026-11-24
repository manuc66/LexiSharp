using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class AdaptiveClassifierTests
{
    private static NaiveBayesClassifier Trained(params (string Text, string Category)[] rows)
    {
        var model = new NaiveBayesClassifier();
        model.Train(rows.Select((row, i) => new SearchDocument(i.ToString(), row.Text, Category: row.Category)));
        return model;
    }

    [Fact]
    public void Predict_DelegatesToCurrentModel()
    {
        var adaptive = new AdaptiveClassifier(Trained(("alpha", "A")));
        Assert.Equal("A", adaptive.PredictBest("alpha"));
    }

    [Fact]
    public void Learn_ForwardsToIncrementalModel()
    {
        var adaptive = new AdaptiveClassifier(Trained(("alpha", "A")));
        Assert.True(adaptive.Learn(new SearchDocument("2", "beta", Category: "B")));
        Assert.Equal("B", adaptive.PredictBest("beta"));
    }

    [Fact]
    public void Unlearn_ForwardsToIncrementalModel()
    {
        var adaptive = new AdaptiveClassifier(Trained(("alpha", "A")));
        adaptive.Learn(new SearchDocument("2", "beta", Category: "B"));
        Assert.True(adaptive.Unlearn(new SearchDocument("2", "beta", Category: "B")));
        Assert.NotEqual("B", adaptive.PredictBest("zzz"));
    }

    [Fact]
    public void TrySwap_AcceptsBetterCandidate()
    {
        var current = Trained(("alpha", "A"));
        var better = Trained(("alpha", "A"), ("beta", "B"));
        var validation = new[]
        {
            new SearchDocument("v1", "alpha", Category: "A"),
            new SearchDocument("v2", "beta", Category: "B"),
        };

        var adaptive = new AdaptiveClassifier(current);
        Assert.True(adaptive.TrySwap(better, validation));
        Assert.Same(better, adaptive.Current);
    }

    [Fact]
    public void TrySwapSignificant_Tie_DoesNotSwap()
    {
        var current = Trained(("alpha", "A"), ("beta", "B"));
        var candidate = Trained(("alpha", "A"), ("beta", "B"));
        var validation = new[]
        {
            new SearchDocument("v1", "alpha", Category: "A"),
            new SearchDocument("v2", "beta", Category: "B"),
        };

        var adaptive = new AdaptiveClassifier(current);
        Assert.False(adaptive.TrySwapSignificant(candidate, validation));
        Assert.Same(current, adaptive.Current);
    }

    [Fact]
    public void TrySwapSignificant_ClearImprovement_Swaps()
    {
        var current = Trained(("alpha", "A"));
        var candidate = Trained(("alpha", "A"), ("beta", "B"));
        var validation = Enumerable.Range(0, 40)
            .Select(i => new SearchDocument("v" + i, i % 2 == 0 ? "alpha" : "beta", Category: i % 2 == 0 ? "A" : "B"))
            .ToArray();

        var adaptive = new AdaptiveClassifier(current);
        Assert.True(adaptive.TrySwapSignificant(candidate, validation, alpha: 0.05));
        Assert.Same(candidate, adaptive.Current);
    }

    [Fact]
    public void TrySwap_RejectsWorseCandidate()
    {
        var current = Trained(("alpha", "A"), ("beta", "B"));
        var worse = Trained(("alpha", "A"));
        var validation = new[]
        {
            new SearchDocument("v1", "alpha", Category: "A"),
            new SearchDocument("v2", "beta", Category: "B"),
        };

        var adaptive = new AdaptiveClassifier(current);
        Assert.False(adaptive.TrySwap(worse, validation));
        Assert.Same(current, adaptive.Current);
    }
}
