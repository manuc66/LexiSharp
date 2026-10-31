using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class ClassWeightTests
{
    private static readonly SearchDocument[] Imbalanced = BuildImbalanced();

    private static SearchDocument[] BuildImbalanced()
    {
        var documents = new List<SearchDocument>();

        for (int i = 0; i < 9; i++)
            documents.Add(new SearchDocument($"a{i}", "same", Category: "A"));

        documents.Add(new SearchDocument("b", "same", Category: "B"));

        return documents.ToArray();
    }

    private static DecisionTreeClassifier Trained(IReadOnlyDictionary<string, double>? weights)
    {
        var classifier = new DecisionTreeClassifier(options: new DecisionTreeOptions
        {
            MaxDepth = 0,
            MinDocumentsPerLeaf = 1,
            ClassWeights = weights,
        });
        classifier.Train(Imbalanced);
        return classifier;
    }

    [Fact]
    public void WithoutClassWeights_TheMajorityClassDominatesTheLeaf()
    {
        var classifier = Trained(weights: null);

        var results = classifier.Predict("same", limit: 2);
        double minority = results.Single(result => result.Category == "B").Probability;

        Assert.True(minority < 0.2);
    }

    [Fact]
    public void WithClassWeights_TheMinorityClassGainsMass()
    {
        var classifier = Trained(new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["A"] = 1,
            ["B"] = 9,
        });

        var results = classifier.Predict("same", limit: 2);
        double minority = results.Single(result => result.Category == "B").Probability;

        Assert.Equal(0.5, minority, precision: 10);
    }

    [Fact]
    public void WithAZeroClassWeight_TheClassIsRemoved()
    {
        var classifier = Trained(new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["A"] = 1,
            ["B"] = 0,
        });

        Assert.Equal(new[] { "A" }, classifier.Categories);
        Assert.Equal("A", classifier.PredictBest("same"));
    }

    [Fact]
    public void WithANegativeClassWeight_TrainThrows()
    {
        Assert.Throws<ArgumentException>(() => Trained(new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["B"] = -1,
        }));
    }
}
