using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class CalibrationMetricsTests
{
    [Fact]
    public void Evaluate_PerfectlyCalibrated_HasZeroEce()
    {
        // 10 predictions at 0.8, 8 of which are positive → perfectly calibrated.
        var predictions = Enumerable.Repeat(0.8, 10).ToList();
        var outcomes = new List<double> { 1, 1, 1, 1, 1, 1, 1, 1, 0, 0 };

        var report = CalibrationMetrics.Evaluate(predictions, outcomes, bins: 10, resamples: 0);

        Assert.Equal(0, report.ExpectedCalibrationError, 10);
        Assert.True(report.BrierScore >= 0);
        Assert.Equal(10, report.SampleCount);
    }

    [Fact]
    public void Evaluate_Overconfident_HasPositiveEce()
    {
        // Predicts 0.9 but only 50% are positive → overconfident.
        var predictions = Enumerable.Repeat(0.9, 10).ToList();
        var outcomes = new List<double> { 1, 1, 1, 1, 1, 0, 0, 0, 0, 0 };

        var report = CalibrationMetrics.Evaluate(predictions, outcomes, bins: 10, resamples: 0);

        Assert.True(report.ExpectedCalibrationError > 0.3);
        Assert.True(report.MaximumCalibrationError > 0);
    }

    [Fact]
    public void Evaluate_ThrowsOnMismatchedLengths()
    {
        Assert.Throws<ArgumentException>(() =>
            CalibrationMetrics.Evaluate(new[] { 0.5 }, new[] { 1.0, 0.0 }));
    }

    [Fact]
    public void Evaluate_ThrowsOnEmptyInput()
    {
        Assert.Throws<ArgumentException>(() =>
            CalibrationMetrics.Evaluate(Array.Empty<double>(), Array.Empty<double>()));
    }

    [Fact]
    public void Evaluate_BrierScorePerfectIsZero()
    {
        var predictions = new[] { 1.0, 0.0 };
        var outcomes = new[] { 1.0, 0.0 };

        var report = CalibrationMetrics.Evaluate(predictions, outcomes, bins: 2, resamples: 0);

        Assert.Equal(0, report.BrierScore, 10);
    }
}