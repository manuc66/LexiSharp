using LexiSharp.Adaptive;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class WeightedConformalTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    private static ClassificationResult[] Offered(double actualProbability) =>
        [
            new("A", actualProbability),
            new("B", 1 - actualProbability),
        ];

    [Fact]
    public void UniformWeights_MatchTheUnweightedQuantileFormula()
    {
        var calibrator = new WeightedConformalCalibrator(
            alpha: 0.2,
            RecencyPolicy.None,
            minSamples: 5);

        foreach (double probability in new[] { 0.9, 0.8, 0.7, 0.6, 0.5 })
        {
            calibrator.Observe(Now, Offered(probability), "A");
        }

        PredictionSet set = calibrator.PredictSet(Offered(0.9), Now);

        Assert.True(calibrator.IsCalibrated);
        Assert.Equal(0.5, calibrator.Threshold, precision: 10);
        Assert.Equal(["A"], set.Categories);
    }

    [Fact]
    public void NotEnoughSamples_FallBackToTheProbabilityThreshold()
    {
        var calibrator = new WeightedConformalCalibrator(
            alpha: 0.1,
            RecencyPolicy.None,
            minSamples: 20);

        calibrator.Observe(Now, Offered(0.5), "A");

        PredictionSet set = calibrator.PredictSet(Offered(0.95), Now);

        Assert.False(calibrator.IsCalibrated);
        Assert.Equal(0.9, calibrator.Threshold, precision: 10);
        Assert.Equal(["A"], set.Categories);
    }

    [Fact]
    public void ExpiredEntries_DoNotCarryWeight()
    {
        var calibrator = new WeightedConformalCalibrator(
            alpha: 0.1,
            new RecencyPolicy(TimeSpan.FromDays(10), TimeSpan.FromDays(30)),
            minSamples: 10);

        for (int i = 0; i < 10; i++)
        {
            calibrator.Observe(Now.AddDays(-100), Offered(0.5), "A");
            calibrator.Observe(Now, Offered(0.9), "A");
        }

        PredictionSet set = calibrator.PredictSet(Offered(0.9), Now);

        Assert.Equal(0.9, calibrator.Threshold, precision: 10);
        Assert.Equal(["A"], set.Categories);
    }

    [Fact]
    public void Purge_DropsExpiredEntries()
    {
        var calibrator = new WeightedConformalCalibrator(
            alpha: 0.1,
            new RecencyPolicy(TimeSpan.FromDays(10), TimeSpan.FromDays(30)),
            minSamples: 1);

        calibrator.Observe(Now.AddDays(-31), Offered(0.5), "A");
        calibrator.Observe(Now, Offered(0.9), "A");

        Assert.Equal(2, calibrator.Count);

        calibrator.Purge(Now);

        Assert.Equal(1, calibrator.Count);
    }

    [Fact]
    public void TopK_CapsThePredictionSet()
    {
        var calibrator = new WeightedConformalCalibrator(
            alpha: 0.2,
            RecencyPolicy.None,
            minSamples: 1);

        calibrator.Observe(Now, [new("A", 0.1), new("B", 0.9)], "A");

        PredictionSet set = calibrator.PredictSet(
            [new("A", 0.6), new("B", 0.4)],
            Now,
            topK: 1);

        Assert.Single(set.Categories);
    }

    [Fact]
    public void EmptyDistribution_ReturnsAnEmptySet()
    {
        var calibrator = new WeightedConformalCalibrator(0.1, RecencyPolicy.None);

        Assert.True(calibrator.PredictSet([], Now).IsEmpty);
    }

    [Fact]
    public void Reset_ClearsTheCalibration()
    {
        var calibrator = new WeightedConformalCalibrator(0.2, RecencyPolicy.None, minSamples: 1);
        calibrator.Observe(Now, Offered(0.9), "A");

        Assert.True(calibrator.IsCalibrated);

        calibrator.Reset();

        Assert.False(calibrator.IsCalibrated);
        Assert.Equal(0, calibrator.Count);
        Assert.True(double.IsNaN(calibrator.Threshold));
    }

    [Fact]
    public void Snapshot_RoundTripsTheEntries()
    {
        var calibrator = new WeightedConformalCalibrator(0.2, RecencyPolicy.None, minSamples: 2);
        calibrator.Observe(Now, Offered(0.9), "A");
        calibrator.Observe(Now, Offered(0.5), "A");

        WeightedConformalSnapshot snapshot = calibrator.Save(Now);
        WeightedConformalCalibrator loaded = WeightedConformalCalibrator.Load(snapshot);

        Assert.Equal(calibrator.Count, loaded.Count);
        Assert.Equal(
            calibrator.PredictSet(Offered(0.9), Now).Categories,
            loaded.PredictSet(Offered(0.9), Now).Categories);
    }
}
