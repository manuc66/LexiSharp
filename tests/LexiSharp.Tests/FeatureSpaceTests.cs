using LexiSharp.Classification;
using Xunit;

namespace LexiSharp.Tests;

public class FeatureSpaceTests
{
    private sealed record Sample(string Id, string Category, int Bucket, bool Flag);

    private sealed class SampleFeatures : IFeatureSource<Sample>
    {
        public IFeatureSet FeaturesOf(Sample item)
        {
            var features = new List<string> { $"bucket:{item.Bucket}" };

            if (item.Flag)
                features.Add("flagged");

            return new FeatureSet(features);
        }
    }

    [Fact]
    public void DecisionTree_ClassifiesOnAFeatureSpaceThatIsNotText()
    {
        var samples = new[]
        {
            new Sample("1", "A", 0, false),
            new Sample("2", "A", 0, false),
            new Sample("3", "B", 1, false),
            new Sample("4", "B", 1, false),
        };
        var classifier = new DecisionTreeClassifier<Sample>(new SampleFeatures(), sample => sample.Category);
        classifier.Train(samples);

        Assert.Equal("A", classifier.PredictBest(new Sample("q", "", 0, false)));
        Assert.Equal("B", classifier.PredictBest(new Sample("q", "", 1, false)));

        var explanation = classifier.Explain(new Sample("q", "", 1, false));

        Assert.NotNull(explanation);
        Assert.Equal(new FeaturePresenceQuestion("bucket:0"), explanation.Path[0].Question);
        Assert.False(explanation.Path[0].Answer);
        Assert.Equal("B", explanation.Outcome.Category);
    }

    [Fact]
    public void CompositeFeatureSource_CombinesDimensions()
    {
        var source = new CompositeFeatureSource<Sample>(
            new DelegateFeatureSource<Sample>(_ => new[] { "always" }),
            new SampleFeatures());

        var features = source.FeaturesOf(new Sample("1", "A", 1, true));

        Assert.True(features.Contains("always"));
        Assert.True(features.Contains("bucket:1"));
        Assert.True(features.Contains("flagged"));
    }

    [Fact]
    public void CorpusDivider_ClustersOnAFeatureSpaceThatIsNotText()
    {
        var samples = new[]
        {
            new Sample("a1", "", 0, false),
            new Sample("a2", "", 0, false),
            new Sample("b1", "", 1, false),
            new Sample("b2", "", 1, false),
        };
        var divider = new CorpusDivider<Sample>(
            new SampleFeatures(),
            sample => sample.Id,
            options: new CorpusDividerOptions
            {
                MaxDepth = 1,
                MinDocumentsPerLeaf = 1,
                MinDocumentFrequency = 1,
                MinSeparation = 0,
            });

        var division = divider.Divide(samples);

        Assert.Equal(2, division.Clusters.Count);

        var bucket0 = Assert.Single(division.Clusters, cluster => cluster.DocumentIds.Contains("a1"));
        Assert.Contains("bucket:0", bucket0.TopFeatures);
    }
}
