using LexiSharp.Classification;
using Xunit;

namespace LexiSharp.Tests;

public class FeatureStateAndMaskingTests
{
    [Fact]
    public void FeatureSet_DistinguishesPresentAbsentAndUnknown()
    {
        var set = new FeatureSet(
            new[] { "present" },
            numericValues: null,
            unknownFeatures: new[] { "unknown" });

        Assert.Equal(FeatureState.Present, set.State("present"));
        Assert.Equal(FeatureState.Unknown, set.State("unknown"));
        Assert.Equal(FeatureState.Absent, set.State("absent"));
    }

    [Fact]
    public void FeatureSet_UnknownFeatureIsNotPresent()
    {
        var set = new FeatureSet(
            Array.Empty<string>(),
            numericValues: null,
            unknownFeatures: new[] { "unknown" });

        Assert.False(set.Contains("unknown"));
        Assert.DoesNotContain("unknown", set.Features);
        Assert.Contains("unknown", set.UnknownFeatures);
    }

    [Fact]
    public void FeatureSet_WithAFeatureBothPresentAndUnknown_Throws()
    {
        Assert.Throws<ArgumentException>(() => new FeatureSet(
            new[] { "x" },
            numericValues: null,
            unknownFeatures: new[] { "x" }));
    }

    [Fact]
    public void FeatureSet_UnknownNumericFeature_HasNoValue()
    {
        var set = new FeatureSet(
            Array.Empty<string>(),
            numericValues: null,
            unknownFeatures: new[] { "hour" });

        Assert.Null(set.NumericValue("hour"));
        Assert.Equal(FeatureState.Unknown, set.State("hour"));
    }

    [Fact]
    public void CompositeFeatureSource_CarriesUnknownFeatures()
    {
        var source = new CompositeFeatureSource<Item>(
            new DelegateFeatureSource<Item>(_ => new[] { "text" }),
            new UnknownSource());

        var features = source.FeaturesOf(new Item());

        Assert.Equal(FeatureState.Present, features.State("text"));
        Assert.Equal(FeatureState.Unknown, features.State("hour"));
    }

    [Fact]
    public void MaskedFeatureSource_WhenMasked_SuppressesTheInnerFeatures()
    {
        var source = new MaskedFeatureSource<Item>(
            new DelegateFeatureSource<Item>(_ => new[] { "text" }),
            _ => true,
            "generic");

        var features = source.FeaturesOf(new Item());

        Assert.True(features.Contains("generic"));
        Assert.False(features.Contains("text"));
    }

    [Fact]
    public void MaskedFeatureSource_WhenNotMasked_ReturnsTheInnerFeatures()
    {
        var source = new MaskedFeatureSource<Item>(
            new DelegateFeatureSource<Item>(_ => new[] { "text" }),
            _ => false,
            "generic");

        var features = source.FeaturesOf(new Item());

        Assert.True(features.Contains("text"));
        Assert.False(features.Contains("generic"));
    }

    [Fact]
    public void DecisionTree_WithAMaskedItem_LearnsTheMaskBranch()
    {
        var classifier = new DecisionTreeClassifier<Sample>(
            new MaskedFeatureSource<Sample>(
                new DelegateFeatureSource<Sample>(sample => new[] { sample.Text }),
                sample => sample.Generic,
                "generic"),
            sample => sample.Category,
            options: new DecisionTreeOptions
            {
                MaxDepth = 3,
                MinDocumentsPerLeaf = 1,
                MinDocumentFrequency = 1,
            });
        classifier.Train(new[]
        {
            new Sample("a1", "A", "alpha", false),
            new Sample("a2", "A", "alpha", false),
            new Sample("b1", "B", "beta", false),
            new Sample("b2", "B", "beta", false),
            new Sample("g1", "Generic", "alpha", true),
            new Sample("g2", "Generic", "beta", true),
        });

        Assert.Equal("Generic", classifier.PredictBest(new Sample("q1", "", "alpha", true)));
        Assert.Equal("A", classifier.PredictBest(new Sample("q2", "", "alpha", false)));
        Assert.Equal("B", classifier.PredictBest(new Sample("q3", "", "beta", false)));
    }

    private sealed record Item;

    private sealed record Sample(string Id, string Category, string Text, bool Generic);

    private sealed class UnknownSource : IFeatureSource<Item>
    {
        public IFeatureSet FeaturesOf(Item item) =>
            new FeatureSet(Array.Empty<string>(), numericValues: null, unknownFeatures: new[] { "hour" });
    }
}
