using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class NumericQuestionTests
{
    private sealed record Sample(string Id, string Category, double? Hour);

    private sealed class HourFeatures : IFeatureSource<Sample>
    {
        public IFeatureSet FeaturesOf(Sample item)
        {
            if (item.Hour is not { } hour)
                return FeatureSet.Empty;

            return new FeatureSet(
                new[] { "hour" },
                new Dictionary<string, double>(StringComparer.Ordinal) { ["hour"] = hour });
        }
    }

    private static DecisionTreeClassifier<Sample> Trained()
    {
        var classifier = new DecisionTreeClassifier<Sample>(
            new HourFeatures(),
            sample => sample.Category,
            options: new DecisionTreeOptions
            {
                MaxDepth = 2,
                MinDocumentsPerLeaf = 1,
                MinDocumentFrequency = 1,
            });
        classifier.Train(new[]
        {
            new Sample("a1", "A", 14),
            new Sample("a2", "A", 15),
            new Sample("a3", "A", 16),
            new Sample("b1", "B", 8),
            new Sample("b2", "B", 9),
            new Sample("b3", "B", 10),
        });
        return classifier;
    }

    [Fact]
    public void FeatureSet_KeepsANumericFeaturePresent()
    {
        var features = new FeatureSet(
            Array.Empty<string>(),
            new Dictionary<string, double>(StringComparer.Ordinal) { ["hour"] = 14 });

        Assert.True(features.Contains("hour"));
        Assert.Equal(14, features.NumericValue("hour"));
        Assert.Contains("hour", features.Features);
    }

    [Fact]
    public void FeatureSet_ReturnsNullForABinaryFeature()
    {
        var features = new FeatureSet(new[] { "term" });

        Assert.Null(features.NumericValue("term"));
        Assert.Null(features.NumericValue("missing"));
    }

    [Fact]
    public void NumericThresholdQuestion_MatchesAtOrAboveTheThreshold()
    {
        var question = new NumericThresholdQuestion("hour", 14);
        var features = new FeatureSet(
            new[] { "hour" },
            new Dictionary<string, double>(StringComparer.Ordinal) { ["hour"] = 14 });

        Assert.True(question.Matches(features));
        Assert.True(question.Matches(new FeatureSet(
            new[] { "hour" },
            new Dictionary<string, double>(StringComparer.Ordinal) { ["hour"] = 17 })));
        Assert.False(question.Matches(new FeatureSet(
            new[] { "hour" },
            new Dictionary<string, double>(StringComparer.Ordinal) { ["hour"] = 13 })));
    }

    [Fact]
    public void NumericThresholdQuestion_WithAMissingValue_DoesNotMatch()
    {
        var question = new NumericThresholdQuestion("hour", 14);

        Assert.False(question.Matches(FeatureSet.Empty));
        Assert.False(question.Matches(new FeatureSet(new[] { "term" })));
    }

    [Fact]
    public void NumericThresholdQuestion_DescribesWithInvariantCulture()
    {
        Assert.Equal("hour >= 14", new NumericThresholdQuestion("hour", 14).Describe());
        Assert.Equal("score >= 0.5", new NumericThresholdQuestion("score", 0.5).Describe());
    }

    [Fact]
    public void DecisionTree_LearnsANumericThreshold()
    {
        var classifier = Trained();

        var root = Assert.IsType<DecisionBranch>(classifier.Root);
        var question = Assert.IsType<NumericThresholdQuestion>(root.Question);

        Assert.Equal("hour", question.Feature);
        Assert.InRange(question.Threshold, 10, 14);
    }

    [Fact]
    public void DecisionTree_GeneralizesToUnseenValuesOnBothSidesOfTheThreshold()
    {
        var classifier = Trained();

        Assert.Equal("A", classifier.PredictBest(new Sample("q1", "", 17)));
        Assert.Equal("A", classifier.PredictBest(new Sample("q2", "", 14)));
        Assert.Equal("B", classifier.PredictBest(new Sample("q3", "", 13)));
        Assert.Equal("B", classifier.PredictBest(new Sample("q4", "", 7)));
    }

    [Fact]
    public void DecisionTree_WithAMissingNumericValue_TakesTheFalseBranch()
    {
        var classifier = Trained();

        Assert.Equal("B", classifier.PredictBest(new Sample("q", "", null)));

        var explanation = classifier.Explain(new Sample("q", "", null));
        Assert.NotNull(explanation);
        Assert.False(explanation.Path[0].Answer);
    }

    [Fact]
    public void CompositeFeatureSource_KeepsNumericValues()
    {
        var source = new CompositeFeatureSource<Sample>(
            new DelegateFeatureSource<Sample>(_ => new[] { "term" }),
            new HourFeatures());

        var features = source.FeaturesOf(new Sample("1", "A", 14));

        Assert.True(features.Contains("term"));
        Assert.True(features.Contains("hour"));
        Assert.Equal(14, features.NumericValue("hour"));
    }
}
