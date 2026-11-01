using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class OnlineLearningTests
{
    private static readonly SearchDocument[] Corpus =
    {
        new("1", "alpha alpha red", Category: "A"),
        new("2", "alpha red apple", Category: "A"),
        new("3", "beta beta blue", Category: "B"),
        new("4", "beta blue berry", Category: "B"),
    };

    private static DecisionTreeClassifier TrainTree()
    {
        var tree = new DecisionTreeClassifier(options: new DecisionTreeOptions
        {
            MinDocumentsPerLeaf = 1,
            MinDocumentFrequency = 1,
            MaxDepth = 6,
        });
        tree.Train(Corpus);
        return tree;
    }

    private static LinearClassifier TrainLinear()
    {
        var linear = new LinearClassifier(options: new LinearClassifierOptions
        {
            Epochs = 40,
            LearningRate = 0.5,
            L2 = 0,
            MinDocumentFrequency = 1,
            Seed = 5,
        });
        linear.Train(Corpus);
        return linear;
    }

    [Fact]
    public void DecisionTree_LearnRaisesTheCategoryAndUnlearnRestoresTheLeaf()
    {
        var tree = TrainTree();
        string description = tree.Describe();
        var before = Probabilities(tree.Predict("alpha", limit: 10));

        long token = tree.Learn("alpha", "B");

        var learned = Probabilities(tree.Predict("alpha", limit: 10));
        Assert.True(learned["B"] > before["B"]);
        Assert.Equal(description, tree.Describe());

        Assert.True(tree.Unlearn(token));

        var restored = Probabilities(tree.Predict("alpha", limit: 10));

        foreach (var pair in before)
            Assert.Equal(pair.Value, restored[pair.Key], precision: 12);
    }

    [Fact]
    public void DecisionTree_UnlearnWithAnUnknownTokenReturnsFalse()
    {
        var tree = TrainTree();

        Assert.False(tree.Unlearn(42));
    }

    [Fact]
    public void DecisionTree_TrainClearsTheLearnedTokens()
    {
        var tree = TrainTree();
        long token = tree.Learn("alpha", "B");

        tree.Train(Corpus);

        Assert.False(tree.Unlearn(token));
    }

    [Fact]
    public void DecisionTree_LearnOnAnUntrainedModelThrows()
    {
        var tree = new DecisionTreeClassifier();

        Assert.Throws<InvalidOperationException>(() => tree.Learn("alpha", "A"));
    }

    [Fact]
    public void DecisionTree_LearnWithAnUnseenCategoryThrows()
    {
        var tree = TrainTree();

        Assert.Throws<ArgumentException>(() => tree.Learn("alpha", "missing"));
    }

    [Fact]
    public void Linear_LearnRaisesTheCategoryAndUnlearnRestoresTheLogits()
    {
        var linear = TrainLinear();
        var before = Probabilities(linear.Predict("alpha", limit: 10));

        long token = linear.Learn("alpha", "B");

        var learned = Probabilities(linear.Predict("alpha", limit: 10));
        Assert.True(learned["B"] > before["B"]);

        Assert.True(linear.Unlearn(token));

        var restored = Probabilities(linear.Predict("alpha", limit: 10));

        foreach (var pair in before)
            Assert.Equal(pair.Value, restored[pair.Key], precision: 12);
    }

    [Fact]
    public void Linear_UnlearnWithAnUnknownTokenReturnsFalse()
    {
        var linear = TrainLinear();

        Assert.False(linear.Unlearn(42));
    }

    [Fact]
    public void Linear_LearnOnAnUntrainedModelThrows()
    {
        var linear = new LinearClassifier();

        Assert.Throws<InvalidOperationException>(() => linear.Learn("alpha", "A"));
    }

    private static Dictionary<string, double> Probabilities(IReadOnlyList<ClassificationResult> results) =>
        results.ToDictionary(result => result.Category, result => result.Probability, StringComparer.Ordinal);
}
