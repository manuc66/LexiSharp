using LexiSharp.Classification;
using Xunit;

namespace LexiSharp.Tests;

public class UnknownIndicatorTests
{
    [Fact]
    public void UnknownIndicatorSource_TurnsUnknownIntoAnIndicator()
    {
        var source = new UnknownIndicatorSource<Item>(
            new UnknownSource(),
            "unknown:");

        var features = source.FeaturesOf(new Item());

        Assert.True(features.Contains("unknown:hour"));
        Assert.False(features.Contains("hour"));
        Assert.Equal(FeatureState.Present, features.State("unknown:hour"));
        Assert.Empty(features.UnknownFeatures);
    }

    [Fact]
    public void UnknownIndicatorSource_PreservesPresentFeaturesAndNumericValues()
    {
        var source = new UnknownIndicatorSource<Item>(new NumericSource(), "unknown:");

        var features = source.FeaturesOf(new Item());

        Assert.True(features.Contains("term"));
        Assert.Equal(14, features.NumericValue("hour"));
        Assert.False(features.Contains("unknown:hour"));
    }

    [Fact]
    public void UnknownIndicatorSource_WithNoUnknownFeatures_ReturnsTheInnerSet()
    {
        var source = new UnknownIndicatorSource<Item>(new TermSource(), "unknown:");

        var features = source.FeaturesOf(new Item());

        Assert.True(features.Contains("term"));
        Assert.Empty(features.UnknownFeatures);
    }

    [Fact]
    public void DecisionTree_LearnsTheUnknownIndicator()
    {
        var classifier = new DecisionTreeClassifier<Sample>(
            new UnknownIndicatorSource<Sample>(new HourFeatures(), "unknown:"),
            sample => sample.Category,
            options: new DecisionTreeOptions
            {
                MaxDepth = 3,
                MinDocumentsPerLeaf = 1,
                MinDocumentFrequency = 1,
            });
        classifier.Train(new[]
        {
            new Sample("a1", "A", 14),
            new Sample("a2", "A", 15),
            new Sample("b1", "B", 8),
            new Sample("b2", "B", 9),
            new Sample("u1", "U", null),
            new Sample("u2", "U", null),
        });

        Assert.Equal("U", classifier.PredictBest(new Sample("q1", "", null)));
        Assert.Equal("A", classifier.PredictBest(new Sample("q2", "", 15)));
        Assert.Equal("B", classifier.PredictBest(new Sample("q3", "", 8)));
    }

    private sealed record Item;

    private sealed record Sample(string Id, string Category, double? Hour);

    private sealed class UnknownSource : IFeatureSource<Item>
    {
        public IFeatureSet FeaturesOf(Item item) =>
            new FeatureSet(Array.Empty<string>(), numericValues: null, unknownFeatures: new[] { "hour" });
    }

    private sealed class NumericSource : IFeatureSource<Item>
    {
        public IFeatureSet FeaturesOf(Item item) =>
            new FeatureSet(
                new[] { "term" },
                new Dictionary<string, double>(StringComparer.Ordinal) { ["hour"] = 14 });
    }

    private sealed class TermSource : IFeatureSource<Item>
    {
        public IFeatureSet FeaturesOf(Item item) => new FeatureSet(new[] { "term" });
    }

    private sealed class HourFeatures : IFeatureSource<Sample>
    {
        public IFeatureSet FeaturesOf(Sample item) =>
            item.Hour is { } hour
                ? new FeatureSet(
                    new[] { "hour" },
                    new Dictionary<string, double>(StringComparer.Ordinal) { ["hour"] = hour })
                : new FeatureSet(Array.Empty<string>(), numericValues: null, unknownFeatures: new[] { "hour" });
    }
}
