using LexiSharp.Core;
using LexiSharp.Hybrid;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class BayesianFusionMergerTests
{
    private static SearchResult R(string id, double score) =>
        new(id, score, new SearchDocument(id, id));

    [Fact]
    public void Constructor_RequiresTrainedCalibrators()
    {
        var untrained = new CalibratedScoreConfidence();
        Assert.Throws<ArgumentException>(() => new BayesianFusionMerger(untrained));
    }

    [Fact]
    public void Constructor_RequiresAtLeastOneCalibrator()
    {
        Assert.Throws<ArgumentException>(() => new BayesianFusionMerger());
    }

    [Fact]
    public void Constructor_RejectsNullCalibrator()
    {
        var trained = CalibratedScoreConfidence.Fit(
            new[] { (1.0, true), (2.0, false) }, CalibrationMethod.IsotonicRegression);
        Assert.Throws<ArgumentNullException>(() => new BayesianFusionMerger(trained, null!));
    }

    [Fact]
    public void Merge_CombinesProbabilitiesByNaiveBayes()
    {
        // Fit calibrators on simple labeled data
        var cal1 = CalibratedScoreConfidence.Fit(
            new[] { (10.0, true), (1.0, false) }, CalibrationMethod.IsotonicRegression);
        var cal2 = CalibratedScoreConfidence.Fit(
            new[] { (5.0, true), (0.5, false) }, CalibrationMethod.IsotonicRegression);

        var engineA = new[] { R("a", 10.0), R("b", 1.0) };
        var engineB = new[] { R("a", 5.0), R("c", 0.5) };

        var merged = new BayesianFusionMerger(cal1, cal2)
            .Merge(new[] { engineA, engineB }, "query").ToList();

        // "a" appears in both engines with high scores → should be first and only
        Assert.Single(merged);
        Assert.Equal("a", merged[0].DocumentId);
    }

    [Fact]
    public void Merge_EmptyInput_ReturnsEmpty()
    {
        var cal = CalibratedScoreConfidence.Fit(
            new[] { (1.0, true), (2.0, false) }, CalibrationMethod.IsotonicRegression);
        var merged = new BayesianFusionMerger(cal)
            .Merge(Array.Empty<IReadOnlyList<SearchResult>>(), "query").ToList();
        Assert.Empty(merged);
    }

    [Fact]
    public void Merge_MismatchedCalibratorCount_Throws()
    {
        var cal = CalibratedScoreConfidence.Fit(
            new[] { (1.0, true), (2.0, false) }, CalibrationMethod.IsotonicRegression);
        var engineA = new[] { R("a", 10.0) };
        Assert.Throws<InvalidOperationException>(() =>
            new BayesianFusionMerger(cal)
                .Merge(new[] { engineA, engineA }, "query").ToList());
    }

    [Fact]
    public void Merge_NonFiniteScore_ContributesNothing()
    {
        var cal = CalibratedScoreConfidence.Fit(
            new[] { (1.0, true), (2.0, false) }, CalibrationMethod.IsotonicRegression);
        var engineA = new[] { R("a", double.NaN) };
        var merged = new BayesianFusionMerger(cal)
            .Merge(new[] { engineA }, "query").ToList();
        Assert.Empty(merged);
    }

    [Fact]
    public void Name_IsBayesianFusion()
    {
        var cal = CalibratedScoreConfidence.Fit(
            new[] { (1.0, true), (2.0, false) }, CalibrationMethod.IsotonicRegression);
        Assert.Equal("BayesianFusion", new BayesianFusionMerger(cal).Name);
    }
}