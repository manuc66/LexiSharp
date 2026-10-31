using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class DecisionTreeClassifierTests
{
    private static readonly SearchDocument[] Corpus =
    {
        new("1", "internet connection problem", Category: "Support"),
        new("2", "internet access problem", Category: "Support"),
        new("3", "password reset problem", Category: "Support"),
        new("4", "invoice payment problem", Category: "Billing"),
        new("5", "invoice refund problem", Category: "Billing"),
        new("6", "card payment problem", Category: "Billing"),
        new("7", "delivery delay problem", Category: "Shipping"),
        new("8", "parcel delivery problem", Category: "Shipping"),
        new("9", "tracking parcel problem", Category: "Shipping"),
    };

    private static readonly SearchDocument[] Conjunction =
    {
        new("1", "red apple", Category: "A"),
        new("2", "red apple juice", Category: "A"),
        new("3", "red car", Category: "B"),
        new("4", "green apple", Category: "B"),
        new("5", "green car", Category: "B"),
    };

    private static DecisionTreeClassifier Trained(DecisionTreeOptions? options = null)
    {
        var classifier = new DecisionTreeClassifier(options: options);
        classifier.Train(Corpus);
        return classifier;
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

    [Fact]
    public void Predict_ClassifiesTheThreeTopics()
    {
        var classifier = Trained();

        Assert.Equal("Support", classifier.PredictBest("i cannot connect to the internet"));
        Assert.Equal("Billing", classifier.PredictBest("i would like my december invoice"));
        Assert.Equal("Shipping", classifier.PredictBest("i am waiting for my parcel delivery"));
    }

    [Fact]
    public void Predict_ReturnsProbabilitiesThatSumToOne()
    {
        var classifier = Trained();

        var results = classifier.Predict("invoice payment", limit: 3);

        Assert.Equal(3, results.Count);
        Assert.Equal(1.0, results.Sum(result => result.Probability), precision: 10);
        Assert.Equal("Billing", results[0].Category);
    }

    [Fact]
    public void Predict_WithANonPositiveLimit_ReturnsNothing()
    {
        var classifier = Trained();

        Assert.Empty(classifier.Predict("invoice", limit: 0));
        Assert.Empty(classifier.Predict("invoice", limit: -1));
    }

    [Fact]
    public void Predict_Untrained_ReturnsNothing()
    {
        var classifier = new DecisionTreeClassifier();

        Assert.Empty(classifier.Predict("invoice"));
        Assert.Null(classifier.PredictBest("invoice"));
    }

    [Fact]
    public void Train_IgnoresDocumentsWithoutACategory()
    {
        var classifier = new DecisionTreeClassifier(
            options: new DecisionTreeOptions { MinDocumentsPerLeaf = 1 });
        classifier.Train(new[]
        {
            new SearchDocument("1", "alpha", Category: "A"),
            new SearchDocument("2", "beta"),
            new SearchDocument("3", "gamma", Category: "B"),
        });

        Assert.Equal(new[] { "A", "B" }, classifier.Categories);
        Assert.Equal("A", classifier.PredictBest("alpha"));
        Assert.Equal("B", classifier.PredictBest("gamma"));
    }

    [Fact]
    public void Categories_AreSortedOrdinal()
    {
        var classifier = Trained();

        Assert.Equal(new[] { "Billing", "Shipping", "Support" }, classifier.Categories);
    }

    [Fact]
    public void Predict_ExcludingTheBestCategory_RenormalizesTheRest()
    {
        var classifier = Trained();

        var results = classifier.Predict(
            "i would like my december invoice",
            limit: 3,
            excludedCategories: new HashSet<string>(StringComparer.Ordinal) { "Billing" });

        Assert.DoesNotContain(results, result => result.Category == "Billing");
        Assert.Equal(1.0, results.Sum(result => result.Probability), precision: 10);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void Explain_ReturnsTheQuestionsAskedAndTheOutcome()
    {
        var classifier = Trained();

        var explanation = classifier.Explain("i would like my december invoice");

        Assert.NotNull(explanation);
        Assert.NotEmpty(explanation.Path);
        Assert.All(explanation.Path, step => Assert.NotNull(step.Question));
        Assert.Equal("Billing", explanation.Outcome.Category);
        Assert.Equal(explanation.Outcome, classifier.Explain("i would like my december invoice")!.Outcome);
    }

    [Fact]
    public void Explain_Untrained_ReturnsNull()
    {
        Assert.Null(new DecisionTreeClassifier().Explain("anything"));
    }

    [Fact]
    public void Predict_TextWithNoKnownTerm_StillLandsOnALeaf()
    {
        var classifier = Trained();

        var results = classifier.Predict("zzz qqq", limit: 3);

        Assert.NotEmpty(results);
        Assert.Equal(1.0, results.Sum(result => result.Probability), precision: 10);
    }

    [Fact]
    public void Root_IsTheMostDiscriminativeQuestion()
    {
        // "apple" and "red" carry the same information gain on this corpus; the ordinal
        // tie-break makes the choice deterministic instead of dependent on dictionary order.
        var classifier = new DecisionTreeClassifier(
            options: new DecisionTreeOptions
            {
                Criterion = SplitCriterion.InformationGain,
                MinDocumentsPerLeaf = 1,
            });
        classifier.Train(Conjunction);

        var root = Assert.IsType<DecisionBranch>(classifier.Root);
        Assert.Equal(new FeaturePresenceQuestion("apple"), root.Question);

        // "green" and "red" carry the same gain on the remaining three documents; the ordinal
        // tie-break picks "green", which is the same conjunction asked from the other side.
        var afterApple = Assert.IsType<DecisionBranch>(root.TrueBranch);
        Assert.Equal(new FeaturePresenceQuestion("green"), afterApple.Question);
    }

    [Fact]
    public void Tree_LearnsAConjunction()
    {
        var classifier = new DecisionTreeClassifier(
            options: new DecisionTreeOptions
            {
                Criterion = SplitCriterion.InformationGain,
                MinDocumentsPerLeaf = 1,
            });
        classifier.Train(Conjunction);

        Assert.Equal("A", classifier.PredictBest("red apple"));
        Assert.Equal("A", classifier.PredictBest("red apple juice"));
        Assert.Equal("B", classifier.PredictBest("green apple"));
        Assert.Equal("B", classifier.PredictBest("red car"));
    }

    [Fact]
    public void MinDocumentsPerLeaf_IsRespected()
    {
        var classifier = Trained(new DecisionTreeOptions { MinDocumentsPerLeaf = 2 });

        var leaves = Leaves(classifier.Root!).ToList();

        Assert.NotEmpty(leaves);
        Assert.All(leaves, leaf => Assert.True(leaf.DocumentCount >= 2));
    }

    [Fact]
    public void Train_IsDeterministic()
    {
        Assert.Equal(Trained().Describe(), Trained().Describe());
    }

    [Fact]
    public void Describe_RendersTheQuestionsAndLeaves()
    {
        var text = Trained().Describe();

        Assert.Contains("contains \"delivery\"", text, StringComparison.Ordinal);
        Assert.Contains("Shipping", text, StringComparison.Ordinal);
    }

    [Fact]
    public void GiniCriterion_TrainsAndClassifies()
    {
        var classifier = Trained(new DecisionTreeOptions { Criterion = SplitCriterion.Gini });

        Assert.Equal("Billing", classifier.PredictBest("invoice payment"));
        Assert.Equal("Shipping", classifier.PredictBest("parcel delivery"));
    }

    [Fact]
    public void Describe_Untrained_ReportsTheEmptyModel()
    {
        Assert.Equal("(untrained)", new DecisionTreeClassifier().Describe());
    }

    [Fact]
    public void WeightOf_DecaysTheClassDistribution()
    {
        // Two documents of A at 0.01 and one of B at 1.0: the labels are 2:1, the evidence is
        // 0.02:1. A weighted tree must read the second ratio, not the first.
        var items = new[]
        {
            new WeightedSample("a1", "A", 0.01),
            new WeightedSample("a2", "A", 0.01),
            new WeightedSample("b1", "B", 1.0),
        };
        var classifier = new DecisionTreeClassifier<WeightedSample>(
            new DelegateFeatureSource<WeightedSample>(_ => new[] { "sample" }),
            sample => sample.Category,
            sample => sample.Weight,
            new DecisionTreeOptions { MaxDepth = 0 });
        classifier.Train(items);

        var explanation = classifier.Explain(new WeightedSample("q", "A", 1.0));

        Assert.NotNull(explanation);
        Assert.Equal("B", explanation.Outcome.Category);
        Assert.True(explanation.Outcome.Probabilities["B"] > 0.6);
    }

    private sealed record WeightedSample(string Id, string Category, double Weight);
}
