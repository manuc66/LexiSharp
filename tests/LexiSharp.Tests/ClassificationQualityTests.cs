using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class ClassificationQualityTests
{
    [Fact]
    public void FeatureSet_DeduplicatesAndRejectsEmptyNames()
    {
        var set = new FeatureSet(new[] { "a", "a", "b" });

        Assert.Equal(2, set.Features.Count);
        Assert.True(set.Contains("a"));
        Assert.True(set.Contains("b"));
        var exception = Assert.Throws<ArgumentException>(() => new FeatureSet(new[] { "a", string.Empty }));
        Assert.Equal("features", exception.ParamName);
    }

    [Fact]
    public void CompositeFeatureSource_WithNoSources_Throws()
    {
        Assert.Throws<ArgumentException>(() => new CompositeFeatureSource<string>());
    }

    [Fact]
    public void DelegateFeatureSource_ReturningNull_Throws()
    {
        var source = new DelegateFeatureSource<string>(_ => null!);

        Assert.Throws<ArgumentNullException>(() => source.FeaturesOf("x"));
    }

    [Fact]
    public void DecisionTree_WithASingleClass_ReturnsThatClass()
    {
        var classifier = new DecisionTreeClassifier();
        classifier.Train(new[]
        {
            new SearchDocument("1", "alpha beta", Category: "Only"),
            new SearchDocument("2", "gamma delta", Category: "Only"),
        });

        var results = classifier.Predict("anything at all", limit: 3);

        var result = Assert.Single(results);
        Assert.Equal("Only", result.Category);
        Assert.Equal(1.0, result.Probability, precision: 10);
    }

    [Fact]
    public void DecisionTree_PathNeverExceedsMaxDepth()
    {
        var classifier = new DecisionTreeClassifier(
            options: new DecisionTreeOptions { MaxDepth = 2, MinDocumentsPerLeaf = 1 });
        classifier.Train(new[]
        {
            new SearchDocument("1", "a b c", Category: "A"),
            new SearchDocument("2", "a b d", Category: "B"),
            new SearchDocument("3", "a e f", Category: "C"),
            new SearchDocument("4", "g h i", Category: "D"),
        });

        var explanation = classifier.Explain("a b c d e f g h i");

        Assert.NotNull(explanation);
        Assert.True(explanation.Path.Count <= 2);
    }

    [Fact]
    public void DecisionTree_EveryLeafProbabilitySumsToOne()
    {
        var classifier = new DecisionTreeClassifier(
            options: new DecisionTreeOptions { MinDocumentsPerLeaf = 1 });
        classifier.Train(new[]
        {
            new SearchDocument("1", "a b", Category: "A"),
            new SearchDocument("2", "a c", Category: "B"),
            new SearchDocument("3", "d e", Category: "C"),
        });

        foreach (var leaf in Leaves(classifier.Root!))
            Assert.Equal(1.0, leaf.Probabilities.Values.Sum(), precision: 10);
    }

    [Fact]
    public void DecisionTree_ExcludingEveryCategory_ReturnsNothing()
    {
        var classifier = new DecisionTreeClassifier();
        classifier.Train(new[]
        {
            new SearchDocument("1", "alpha", Category: "A"),
            new SearchDocument("2", "beta", Category: "B"),
        });

        var results = classifier.Predict(
            "alpha",
            excludedCategories: new HashSet<string>(StringComparer.Ordinal) { "A", "B" });

        Assert.Empty(results);
        Assert.Null(classifier.PredictBest("alpha", new HashSet<string>(StringComparer.Ordinal) { "A", "B" }));
    }

    [Fact]
    public void DecisionTree_WithANegativeWeight_Throws()
    {
        var classifier = new DecisionTreeClassifier<Weighted>(
            new DelegateFeatureSource<Weighted>(item => new[] { item.Category }),
            item => item.Category,
            item => item.Weight);

        Assert.Throws<ArgumentException>(() => classifier.Train(new[] { new Weighted("A", -1) }));
    }

    [Fact]
    public void DecisionTree_WithNaNWeight_Throws()
    {
        var classifier = new DecisionTreeClassifier<Weighted>(
            new DelegateFeatureSource<Weighted>(_ => new[] { "x" }),
            item => item.Category,
            item => item.Weight);

        Assert.Throws<ArgumentException>(() => classifier.Train(new[] { new Weighted("A", double.NaN) }));
    }

    [Fact]
    public void DecisionTree_WithZeroWeightItems_SkipsThem()
    {
        var classifier = new DecisionTreeClassifier<Weighted>(
            new DelegateFeatureSource<Weighted>(item => new[] { item.Category }),
            item => item.Category,
            item => item.Weight);

        classifier.Train(new[]
        {
            new Weighted("A", 0),
            new Weighted("B", 1),
        });

        Assert.Equal(new[] { "B" }, classifier.Categories);
        Assert.Equal("B", classifier.PredictBest(new Weighted("A", 1)));
    }

    [Fact]
    public void DecisionTree_WithAFeaturePresentInEveryItem_DoesNotSplitOnIt()
    {
        var classifier = new DecisionTreeClassifier(
            options: new DecisionTreeOptions { MinDocumentsPerLeaf = 1, MaxDepth = 4 });
        classifier.Train(new[]
        {
            new SearchDocument("1", "common alpha", Category: "A"),
            new SearchDocument("2", "common beta", Category: "B"),
            new SearchDocument("3", "common gamma", Category: "A"),
            new SearchDocument("4", "common delta", Category: "B"),
        });

        var root = Assert.IsType<DecisionBranch>(classifier.Root);
        Assert.NotEqual(new FeaturePresenceQuestion("common"), root.Question);
    }

    [Fact]
    public void CorpusDivider_WithDuplicateIds_Throws()
    {
        var divider = new CorpusDivider(options: new CorpusDividerOptions
        {
            MaxDepth = 1,
            MinDocumentsPerLeaf = 1,
            MinDocumentFrequency = 1,
            MinSeparation = 0,
        });

        Assert.Throws<ArgumentException>(() => divider.Divide(new[]
        {
            new SearchDocument("same", "alpha"),
            new SearchDocument("same", "beta"),
        }));
    }

    [Fact]
    public void CorpusDivider_WithANegativeWeight_Throws()
    {
        var divider = new CorpusDivider<Weighted>(
            new DelegateFeatureSource<Weighted>(item => new[] { item.Category }),
            item => item.Category,
            item => item.Weight);

        Assert.Throws<ArgumentException>(() => divider.Divide(new[] { new Weighted("A", -1) }));
    }

    [Fact]
    public void CorpusDivider_WithZeroWeightItems_SkipsThem()
    {
        var divider = new CorpusDivider<Weighted>(
            new DelegateFeatureSource<Weighted>(item => new[] { item.Category }),
            item => item.Category,
            item => item.Weight);

        var division = divider.Divide(new[]
        {
            new Weighted("A", 0),
            new Weighted("B", 1),
        });

        var cluster = Assert.Single(division.Clusters);
        Assert.Equal(new[] { "B" }, cluster.DocumentIds);
        Assert.Equal(1, division.DocumentCount);
    }

    [Fact]
    public void CorpusDivider_WithAFeaturePresentInEveryItem_DoesNotSplitOnIt()
    {
        var divider = new CorpusDivider(options: new CorpusDividerOptions
        {
            MaxDepth = 4,
            MinDocumentsPerLeaf = 1,
            MinDocumentFrequency = 1,
            MinSeparation = 0,
        });

        var division = divider.Divide(new[]
        {
            new SearchDocument("1", "common alpha"),
            new SearchDocument("2", "common beta"),
            new SearchDocument("3", "common gamma"),
            new SearchDocument("4", "common delta"),
        });

        var root = Assert.IsType<DivisionBranch>(division.Root);
        Assert.NotEqual(new FeaturePresenceQuestion("common"), root.Question);
    }

    [Fact]
    public void DecisionTree_HoldsOutAThreeClassCorpus()
    {
        string[] topics = { "alpha", "beta", "gamma" };
        var training = new List<SearchDocument>();
        var heldOut = new List<(string Text, string Category)>();

        for (int t = 0; t < topics.Length; t++)
        {
            for (int i = 0; i < 8; i++)
                training.Add(new SearchDocument($"{t}-{i}", $"{topics[t]} shared word{i % 3}", Category: topics[t]));

            heldOut.Add(($"{topics[t]} shared word0", topics[t]));
            heldOut.Add(($"{topics[t]} shared word1", topics[t]));
        }

        var classifier = new DecisionTreeClassifier();
        classifier.Train(training);

        Assert.All(heldOut, sample => Assert.Equal(sample.Category, classifier.PredictBest(sample.Text)));
    }

    [Fact]
    public void DecisionTree_SplitMetricsCarryTheSampleWeight()
    {
        var classifier = new DecisionTreeClassifier<Weighted>(
            new DelegateFeatureSource<Weighted>(item => new[] { item.Category }),
            item => item.Category,
            item => item.Weight,
            new DecisionTreeOptions { MinDocumentsPerLeaf = 1 });
        classifier.Train(new[]
        {
            new Weighted("A", 10),
            new Weighted("B", 2),
            new Weighted("A", 1),
            new Weighted("B", 1),
        });

        var root = Assert.IsType<DecisionBranch>(classifier.Root);

        Assert.Equal(14.0, root.Metrics.WeightTrue + root.Metrics.WeightFalse, precision: 10);
    }

    [Fact]
    public void CorpusDivider_ClustersCarryTheSampleWeight()
    {
        var divider = new CorpusDivider<Weighted>(
            new DelegateFeatureSource<Weighted>(item => new[] { item.Category }),
            item => item.Category,
            item => item.Weight,
            new CorpusDividerOptions
            {
                MaxDepth = 1,
                MinDocumentsPerLeaf = 1,
                MinDocumentFrequency = 1,
                MinSeparation = 0,
            });

        var division = divider.Divide(new[]
        {
            new Weighted("A", 10),
            new Weighted("B", 0.1),
        });

        Assert.Equal(10.1, division.Clusters.Sum(cluster => cluster.Weight), precision: 10);
    }

    [Fact]
    public void CorpusDivider_WithNoPositiveSeparation_DoesNotSplit()
    {
        var divider = new CorpusDivider(options: new CorpusDividerOptions
        {
            MaxDepth = 4,
            MinDocumentsPerLeaf = 1,
            MinDocumentFrequency = 1,
            MinSeparation = 0,
        });

        var division = divider.Divide(new[]
        {
            new SearchDocument("1", "same"),
            new SearchDocument("2", "same"),
            new SearchDocument("3", "same"),
        });

        Assert.Single(division.Clusters);
    }

    private static IEnumerable<DecisionLeaf> Leaves(DecisionTreeNode node)
    {
        if (node is DecisionLeaf leaf)
        {
            yield return leaf;
            yield break;
        }

        var branch = (DecisionBranch)node;

        foreach (var child in Leaves(branch.TrueBranch))
            yield return child;

        foreach (var child in Leaves(branch.FalseBranch))
            yield return child;
    }

    private sealed record Weighted(string Category, double Weight);
}
