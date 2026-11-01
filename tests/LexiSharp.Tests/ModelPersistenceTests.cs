using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class ModelPersistenceTests
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

    private static RandomForestClassifier TrainForest()
    {
        var forest = new RandomForestClassifier(options: new RandomForestOptions
        {
            Trees = 5,
            MinDocumentsPerLeaf = 1,
            MinDocumentFrequency = 1,
            FeatureSubsetFraction = 1,
            MaxDepth = 6,
            Seed = 3,
        });
        forest.Train(Corpus);
        return forest;
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
    public void DecisionTree_RoundTripsPredictionsExplanationAndLedger()
    {
        var tree = TrainTree();
        tree.Reinforce("alpha", "B", 5);

        using var buffer = new MemoryStream();
        ClassificationModelSerializer.Save(tree, buffer);
        buffer.Position = 0;

        var loaded = ClassificationModelSerializer.LoadDecisionTree(buffer);

        foreach (var document in Corpus)
        {
            Assert.Equal(tree.PredictBest(document.Text), loaded.PredictBest(document.Text));
            Assert.Equal(
                tree.Predict(document.Text).Select(result => (result.Category, result.Probability)),
                loaded.Predict(document.Text).Select(result => (result.Category, result.Probability)));
        }

        Assert.Equal(tree.Describe(), loaded.Describe());
        Assert.Equal(tree.FeatureSpaceHash, loaded.FeatureSpaceHash);
        Assert.Equal("B", loaded.PredictBest("alpha"));
    }

    [Fact]
    public void RandomForest_RoundTripsPredictions()
    {
        var forest = TrainForest();

        using var buffer = new MemoryStream();
        ClassificationModelSerializer.Save(forest, buffer);
        buffer.Position = 0;

        var loaded = ClassificationModelSerializer.LoadRandomForest(buffer);

        Assert.Equal(forest.TreeCount, loaded.TreeCount);
        Assert.Equal(forest.Categories, loaded.Categories);

        foreach (var document in Corpus)
        {
            Assert.Equal(
                forest.Predict(document.Text).Select(result => (result.Category, result.Probability)),
                loaded.Predict(document.Text).Select(result => (result.Category, result.Probability)));
        }
    }

    [Fact]
    public void Linear_RoundTripsPredictionsFeaturesAndLedger()
    {
        var linear = TrainLinear();
        linear.Reinforce("alpha", "B", 100);

        using var buffer = new MemoryStream();
        ClassificationModelSerializer.Save(linear, buffer);
        buffer.Position = 0;

        var loaded = ClassificationModelSerializer.LoadLinear(buffer);

        Assert.Equal(linear.Categories, loaded.Categories);
        Assert.Equal(
            linear.TopFeatures("A", 5).Select(weight => (weight.Feature, weight.Weight)),
            loaded.TopFeatures("A", 5).Select(weight => (weight.Feature, weight.Weight)));

        foreach (var document in Corpus)
        {
            Assert.Equal(
                linear.Predict(document.Text).Select(result => (result.Category, result.Probability)),
                loaded.Predict(document.Text).Select(result => (result.Category, result.Probability)));
        }

        Assert.Equal("B", loaded.PredictBest("alpha"));
        Assert.Equal(linear.PredictBest("alpha"), loaded.PredictBest("alpha"));
    }

    [Fact]
    public void CalibratedModel_RoundTripsTheTemperatureAndTheInnerModel()
    {
        var linear = TrainLinear();
        var calibrator = new TemperatureCalibrator(0.75);
        var calibrated = new CalibratedClassifier(linear, calibrator);

        using var buffer = new MemoryStream();
        ClassificationModelSerializer.Save(calibrated, buffer);
        buffer.Position = 0;

        var loaded = ClassificationModelSerializer.LoadCalibrated(buffer);

        Assert.Equal(0.75, loaded.Temperature, precision: 12);
        Assert.Equal(
            calibrated.Predict("alpha red").Select(result => (result.Category, result.Probability)),
            loaded.Predict("alpha red").Select(result => (result.Category, result.Probability)));
    }

    [Fact]
    public void UntrainedTree_RoundTripsAsUntrained()
    {
        var tree = new DecisionTreeClassifier();

        using var buffer = new MemoryStream();
        ClassificationModelSerializer.Save(tree, buffer);
        buffer.Position = 0;

        var loaded = ClassificationModelSerializer.LoadDecisionTree(buffer);

        Assert.Null(loaded.Root);
        Assert.Empty(loaded.Predict("anything"));
    }

    [Fact]
    public void Load_WithAnUnknownVersion_Throws()
    {
        byte[] bytes = Save(TrainTree());
        bytes[4] = 2;

        Assert.Throws<InvalidDataException>(
            () => ClassificationModelSerializer.LoadDecisionTree(new MemoryStream(bytes)));
    }

    [Fact]
    public void Load_WithABadMagic_Throws()
    {
        byte[] bytes = Save(TrainTree());
        bytes[0] = 0;

        Assert.Throws<InvalidDataException>(
            () => ClassificationModelSerializer.LoadDecisionTree(new MemoryStream(bytes)));
    }

    [Fact]
    public void Load_WithTheWrongKind_Throws()
    {
        byte[] bytes = Save(TrainLinear());

        Assert.Throws<InvalidDataException>(
            () => ClassificationModelSerializer.LoadDecisionTree(new MemoryStream(bytes)));
    }

    [Fact]
    public void Load_WithATruncatedStream_Throws()
    {
        byte[] bytes = Save(TrainTree());

        Assert.ThrowsAny<IOException>(
            () => ClassificationModelSerializer.LoadDecisionTree(new MemoryStream(bytes[..12])));
    }

    [Fact]
    public void Load_OfACorruptedFeatureSpace_Throws()
    {
        byte[] bytes = Save(TrainTree());
        int hashOffset = 4 + sizeof(int) + 1 + sizeof(int);
        bytes[hashOffset] = (byte)(bytes[hashOffset] == (byte)'0' ? (byte)'1' : (byte)'0');

        Assert.Throws<InvalidDataException>(
            () => ClassificationModelSerializer.LoadDecisionTree(new MemoryStream(bytes)));
    }

    [Fact]
    public void DecisionTree_RoundTripsLearnedTokens()
    {
        var tree = TrainTree();
        long token = tree.Learn("alpha", "B");
        var learned = tree.Predict("alpha");

        using var buffer = new MemoryStream();
        ClassificationModelSerializer.Save(tree, buffer);
        buffer.Position = 0;

        var loaded = ClassificationModelSerializer.LoadDecisionTree(buffer);

        Assert.Equal(
            learned.Select(result => (result.Category, result.Probability)),
            loaded.Predict("alpha").Select(result => (result.Category, result.Probability)));

        Assert.True(loaded.Unlearn(token));
        Assert.True(tree.Unlearn(token));
        Assert.Equal(
            tree.Predict("alpha").Select(result => (result.Category, result.Probability)),
            loaded.Predict("alpha").Select(result => (result.Category, result.Probability)));
    }

    [Fact]
    public void Linear_RoundTripsLearnedTokens()
    {
        var linear = TrainLinear();
        long token = linear.Learn("alpha", "B");
        var learned = linear.Predict("alpha");

        using var buffer = new MemoryStream();
        ClassificationModelSerializer.Save(linear, buffer);
        buffer.Position = 0;

        var loaded = ClassificationModelSerializer.LoadLinear(buffer);

        Assert.Equal(
            learned.Select(result => (result.Category, result.Probability)),
            loaded.Predict("alpha").Select(result => (result.Category, result.Probability)));

        Assert.True(loaded.Unlearn(token));
        Assert.True(linear.Unlearn(token));
        Assert.Equal(
            linear.Predict("alpha").Select(result => (result.Category, result.Probability)),
            loaded.Predict("alpha").Select(result => (result.Category, result.Probability)));
    }

    private static byte[] Save(DecisionTreeClassifier tree)
    {
        using var buffer = new MemoryStream();
        ClassificationModelSerializer.Save(tree, buffer);
        return buffer.ToArray();
    }

    private static byte[] Save(LinearClassifier linear)
    {
        using var buffer = new MemoryStream();
        ClassificationModelSerializer.Save(linear, buffer);
        return buffer.ToArray();
    }
}
