using LexiSharp.Classification;
using Xunit;

namespace LexiSharp.Tests;

public class QuestionCostTests
{
    private sealed record Sample(string Id, string Category, string[] Features);

    private sealed class SampleFeatures : IFeatureSource<Sample>
    {
        public IFeatureSet FeaturesOf(Sample item) => new FeatureSet(item.Features);
    }

    private static readonly Sample[] Corpus =
    {
        new("a1", "A", new[] { "expensive", "cheap" }),
        new("a2", "A", new[] { "expensive", "cheap" }),
        new("a3", "A", new[] { "expensive", "cheap" }),
        new("a4", "A", new[] { "expensive", "cheap" }),
        new("a5", "A", new[] { "expensive", "cheap" }),
        new("a6", "A", new[] { "expensive" }),
        new("b1", "B", new[] { "cheap" }),
        new("b2", "B", Array.Empty<string>()),
        new("b3", "B", Array.Empty<string>()),
        new("b4", "B", Array.Empty<string>()),
    };

    private static DecisionTreeClassifier<Sample> Trained(Func<string, double>? cost = null)
    {
        var classifier = new DecisionTreeClassifier<Sample>(
            new SampleFeatures(),
            sample => sample.Category,
            options: new DecisionTreeOptions
            {
                MaxDepth = 1,
                MinDocumentsPerLeaf = 1,
                MinDocumentFrequency = 1,
                QuestionCost = cost,
            });
        classifier.Train(Corpus);
        return classifier;
    }

    [Fact]
    public void WithoutACost_TheMostDiscriminativeFeatureWins()
    {
        var classifier = Trained();

        var root = Assert.IsType<DecisionBranch>(classifier.Root);
        Assert.Equal(new FeaturePresenceQuestion("expensive"), root.Question);
        Assert.Equal(1.0, root.Metrics.Cost, precision: 12);
    }

    [Fact]
    public void WithACost_ACheapSlightlyWorseFeatureWins()
    {
        var classifier = Trained(feature => feature == "expensive" ? 10 : 1);

        var root = Assert.IsType<DecisionBranch>(classifier.Root);
        Assert.Equal(new FeaturePresenceQuestion("cheap"), root.Question);
        Assert.Equal(1.0, root.Metrics.Cost, precision: 12);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void WithAnInvalidCost_TrainThrows(double cost)
    {
        Assert.Throws<ArgumentException>(() => Trained(_ => cost));
    }
}
