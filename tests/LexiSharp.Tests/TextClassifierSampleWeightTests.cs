using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class TextClassifierSampleWeightTests
{
    private static SearchDocument[] Corpus() =>
    [
        new("a1", "alpha beta", Category: "A"),
        new("a2", "alpha gamma", Category: "A"),
        new("b1", "beta delta", Category: "B"),
        new("b2", "beta epsilon", Category: "B"),
    ];

    private static double ExcludeB(SearchDocument document) =>
        document.Id.StartsWith('b') ? 0 : 1;

    private static void AssertBIsExcluded(ITextClassifier model, Func<IReadOnlyList<string>> categories)
    {
        model.Train(Corpus());

        Assert.Contains("A", categories());
        Assert.DoesNotContain("B", categories());
    }

    [Fact]
    public void Linear_ZeroWeightExcludesTheCategory()
    {
        var model = new LinearClassifier(tokenizer: null, options: null, weightOf: ExcludeB);
        AssertBIsExcluded(model, () => model.Categories);
    }

    [Fact]
    public void DecisionTree_ZeroWeightExcludesTheCategory()
    {
        var model = new DecisionTreeClassifier(tokenizer: null, options: null, weightOf: ExcludeB);
        AssertBIsExcluded(model, () => model.Categories);
    }

    [Fact]
    public void RandomForest_ZeroWeightExcludesTheCategory()
    {
        var model = new RandomForestClassifier(tokenizer: null, options: null, weightOf: ExcludeB);
        AssertBIsExcluded(model, () => model.Categories);
    }

    [Fact]
    public void Linear_AllZeroWeightsLeaveAnUntrainedModel()
    {
        var model = new LinearClassifier(tokenizer: null, options: null, weightOf: _ => 0);
        model.Train(Corpus());

        Assert.Empty(model.Categories);
    }

    [Fact]
    public void Linear_FractionalWeightTrains()
    {
        var model = new LinearClassifier(tokenizer: null, options: null, weightOf: _ => 0.5);
        model.Train(Corpus());

        Assert.Equal(["A", "B"], model.Categories);
    }

    [Fact]
    public void Linear_NegativeWeightIsRejected()
    {
        var model = new LinearClassifier(tokenizer: null, options: null, weightOf: _ => -1);

        Assert.Throws<ArgumentException>(() => model.Train(Corpus()));
    }

    [Fact]
    public void Linear_LearnUsesTheWeightAndRejectsZero()
    {
        var model = new LinearClassifier(
            tokenizer: null,
            options: null,
            weightOf: document => document.Id == "x" ? 0 : 1);
        model.Train(Corpus());

        Assert.Throws<ArgumentException>(() =>
            model.Learn(new SearchDocument("x", "gamma", Category: "A")));
    }

    [Fact]
    public void Linear_WeightsAreBakedIntoThePersistedModel()
    {
        var model = new LinearClassifier(tokenizer: null, options: null, weightOf: ExcludeB);
        model.Train(Corpus());
        string? before = model.PredictBest("alpha beta");

        using var stream = new MemoryStream();
        ClassificationModelSerializer.Save(model, stream);
        stream.Position = 0;
        LinearClassifier loaded = ClassificationModelSerializer.LoadLinear(stream);

        Assert.Equal(model.Categories, loaded.Categories);
        Assert.Equal(before, loaded.PredictBest("alpha beta"));
    }
}
