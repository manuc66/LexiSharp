using LexiSharp.Core;
using LexiSharp.Demo;
using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

public sealed class DemoClassificationServiceTests
{
    private static DemoClassificationService Create() =>
        new(DemoCorpusCatalog.Load(DemoCorpusCatalog.BuiltIn, null, "/nonexistent"),
            WordSegmentation.UnicodeWordBoundaries);

    [Fact]
    public void Classify_ReturnsPredictionsForEveryModel()
    {
        var service = Create();
        var result = service.Classify("refresh token");

        Assert.NotEmpty(result.NaiveBayes);
        Assert.NotEmpty(result.DecisionTree);
        Assert.NotEmpty(result.CalibratedTree);
        Assert.NotEmpty(result.Linear);
        Assert.NotEmpty(result.RandomForest);
        Assert.NotEmpty(result.Centroid);
        Assert.NotEmpty(result.NearestNeighbor);
        Assert.NotEmpty(result.Blend);
        Assert.NotEmpty(result.Synchronized);
    }

    [Fact]
    public void Classify_TreePathIsNonEmptyForKnownVocabulary()
    {
        var service = Create();
        var result = service.Classify("refresh token");

        Assert.NotEmpty(result.TreePath);
        Assert.False(string.IsNullOrWhiteSpace(result.TreeLeaf));
    }

    [Fact]
    public void SelectedTermsByClass_CoversAllCategories()
    {
        var service = Create();
        var info = service.Info();

        Assert.Equal(4, info.SelectedTermsByClass.Count);
        Assert.Contains("auth", info.SelectedTermsByClass.Keys);
        Assert.Contains("search", info.SelectedTermsByClass.Keys);
        Assert.All(info.SelectedTermsByClass.Values, terms => Assert.NotEmpty(terms));
    }

    [Fact]
    public void Divide_CoversEveryDocumentExactlyOnce()
    {
        var service = Create();
        var clusters = service.Divide();

        Assert.NotEmpty(clusters);
        var allIds = clusters.SelectMany(c => c.DocumentIds).ToList();
        Assert.Equal(26, allIds.Count);
        Assert.Equal(allIds.Count, allIds.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Evaluate_ReturnsSixModelsAndCounts()
    {
        var service = Create();
        var corpus = DemoCorpusCatalog.Load(DemoCorpusCatalog.BuiltIn, null, "/nonexistent");
        var result = service.Evaluate(corpus.Documents);

        Assert.Equal(6, result.Metrics.Count);
        Assert.True(result.TrainCount > 0);
        Assert.True(result.TestCount > 0);
        Assert.True(result.TrainCount + result.TestCount == corpus.Documents.Count);
        Assert.True(result.PersistenceRoundTrip);
    }

    [Fact]
    public void Reinforce_ThenForget_DoesNotThrow()
    {
        var service = Create();
        service.Reinforce("refresh token", "auth");
        var before = service.Classify("refresh token");
        service.ForgetReinforcement();
        var after = service.Classify("refresh token");

        Assert.NotEmpty(before.NaiveBayes);
        Assert.NotEmpty(after.NaiveBayes);
    }

    [Fact]
    public void DivideTree_ReturnsNonEmptyText()
    {
        var service = Create();
        Assert.False(string.IsNullOrWhiteSpace(service.DivideTree()));
    }

    [Fact]
    public void OnlineEvaluate_ReturnsMetrics()
    {
        var service = Create();
        var result = service.OnlineEvaluate();

        Assert.True(result.Documents > 0);
        Assert.InRange(result.AccuracyAt1, 0.0, 1.0);
        Assert.InRange(result.MacroF1, 0.0, 1.0);
    }

    [Fact]
    public void ClassifyWithContext_ReturnsPredictions()
    {
        var service = Create();
        var result = service.ClassifyWithContext("refresh token", "auth", 2.0);

        Assert.NotEmpty(result);
    }

    [Fact]
    public void TestPersistence_RoundTrips()
    {
        var service = Create();
        var (bytes, roundTrip) = service.TestPersistence();

        Assert.True(bytes > 0);
        Assert.True(roundTrip);
    }
}