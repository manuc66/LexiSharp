using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class FeatureKindTests
{
    [Fact]
    public void FeatureSet_DistinguishesBinaryCountAndNumeric()
    {
        var set = new FeatureSet(
            new[] { "binary" },
            numericValues: new Dictionary<string, double>(StringComparer.Ordinal) { ["hour"] = 14 },
            unknownFeatures: null,
            countValues: new Dictionary<string, double>(StringComparer.Ordinal) { ["term"] = 3 });

        Assert.Equal(FeatureKind.Binary, set.Kind("binary"));
        Assert.Equal(FeatureKind.Numeric, set.Kind("hour"));
        Assert.Equal(FeatureKind.Count, set.Kind("term"));
        Assert.Equal(14, set.NumericValue("hour"));
        Assert.Equal(3, set.NumericValue("term"));
    }

    [Fact]
    public void TextFeatureSource_WithTermFrequency_EmitsCounts()
    {
        var source = new TextFeatureSource(emitTermFrequency: true);

        var features = source.FeaturesOf(new SearchDocument("1", "apple apple banana"));

        Assert.Equal(FeatureKind.Count, features.Kind("apple"));
        Assert.Equal(2, features.NumericValue("apple"));
        Assert.Equal(1, features.NumericValue("banana"));
    }

    [Fact]
    public void CompositeFeatureSource_PreservesCountsAndNumerics()
    {
        var source = new CompositeFeatureSource<Item>(
            new DelegateFeatureSource<Item>(_ => new[] { "binary" }),
            new CountSource(),
            new NumericSource());

        var features = source.FeaturesOf(new Item());

        Assert.Equal(FeatureKind.Binary, features.Kind("binary"));
        Assert.Equal(FeatureKind.Count, features.Kind("term"));
        Assert.Equal(FeatureKind.Numeric, features.Kind("hour"));
        Assert.Equal(2, features.NumericValue("term"));
        Assert.Equal(14, features.NumericValue("hour"));
    }

    [Fact]
    public void LinearClassifier_WithTfIdf_ClassifiesTheCorpus()
    {
        var classifier = new LinearClassifier(
            new TextFeatureSource(emitTermFrequency: true),
            new LinearClassifierOptions
            {
                Epochs = 50,
                LearningRate = 0.5,
                L2 = 0,
                MinDocumentFrequency = 1,
                Seed = 7,
                IdfWeighting = true,
            });
        classifier.Train(new[]
        {
            new SearchDocument("1", "alpha alpha one", Category: "A"),
            new SearchDocument("2", "alpha alpha two", Category: "A"),
            new SearchDocument("3", "beta beta one", Category: "B"),
            new SearchDocument("4", "beta beta two", Category: "B"),
        });

        Assert.Equal("A", classifier.PredictBest("alpha alpha"));
        Assert.Equal("B", classifier.PredictBest("beta beta"));
    }

    private sealed record Item;

    private sealed class CountSource : IFeatureSource<Item>
    {
        public IFeatureSet FeaturesOf(Item item) =>
            new FeatureSet(
                new[] { "term" },
                numericValues: null,
                unknownFeatures: null,
                countValues: new Dictionary<string, double>(StringComparer.Ordinal) { ["term"] = 2 });
    }

    private sealed class NumericSource : IFeatureSource<Item>
    {
        public IFeatureSet FeaturesOf(Item item) =>
            new FeatureSet(
                new[] { "hour" },
                numericValues: new Dictionary<string, double>(StringComparer.Ordinal) { ["hour"] = 14 });
    }
}
