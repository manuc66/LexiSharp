using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class CalibratedScoreConfidenceTests
{
    [Fact]
    public void Untrained_ReturnsNeutralProbability()
    {
        var calibrator = new CalibratedScoreConfidence();

        Assert.False(calibrator.IsTrained);
        Assert.Equal(0.5, calibrator.PredictProba(100));
        Assert.Equal(0.5, calibrator.PredictProba(0));
        Assert.False(calibrator.ShouldAbstain(100));
    }

    [Fact]
    public void Fit_IsotonicRegression_ProducesMonotonicMapping()
    {
        var examples = new[]
        {
            (10.0, false),
            (20.0, false),
            (30.0, true),
            (40.0, true),
            (50.0, true),
        };

        var calibrator = CalibratedScoreConfidence.Fit(examples, CalibrationMethod.IsotonicRegression);

        Assert.True(calibrator.IsTrained);

        // Higher scores should have higher or equal probability
        double p10 = calibrator.PredictProba(10);
        double p30 = calibrator.PredictProba(30);
        double p50 = calibrator.PredictProba(50);

        Assert.True(p10 <= p30, $"p10={p10} should be <= p30={p30}");
        Assert.True(p30 <= p50, $"p30={p30} should be <= p50={p50}");
    }

    [Fact]
    public void Fit_PlattScaling_ProducesMonotonicMapping()
    {
        var examples = new[]
        {
            (10.0, false),
            (20.0, false),
            (30.0, true),
            (40.0, true),
            (50.0, true),
        };

        var calibrator = CalibratedScoreConfidence.Fit(examples, CalibrationMethod.PlattScaling);

        Assert.True(calibrator.IsTrained);

        double p10 = calibrator.PredictProba(10);
        double p30 = calibrator.PredictProba(30);
        double p50 = calibrator.PredictProba(50);

        Assert.True(p10 <= p30, $"p10={p10} should be <= p30={p30}");
        Assert.True(p30 <= p50, $"p30={p30} should be <= p50={p50}");
    }

    [Fact]
    public void Fit_ThrowsOnSingleClass()
    {
        var examples = new[]
        {
            (10.0, true),
            (20.0, true),
            (30.0, true),
        };

        Assert.Throws<ArgumentException>(() =>
            CalibratedScoreConfidence.Fit(examples));
    }

    [Fact]
    public void Fit_ThrowsOnTooFewExamples()
    {
        var examples = new[] { (10.0, true) };

        Assert.Throws<ArgumentException>(() =>
            CalibratedScoreConfidence.Fit(examples));
    }

    [Fact]
    public void ShouldAbstain_UsesYoudenThreshold()
    {
        // Create examples where low scores are clearly wrong and high scores clearly right
        var examples = new[]
        {
            (1.0, false),
            (2.0, false),
            (3.0, false),
            (8.0, true),
            (9.0, true),
            (10.0, true),
        };

        var calibrator = CalibratedScoreConfidence.Fit(examples);

        // Very low scores should trigger abstention
        Assert.True(calibrator.ShouldAbstain(1.0));
        Assert.True(calibrator.ShouldAbstain(2.0));

        // Very high scores should not trigger abstention
        Assert.False(calibrator.ShouldAbstain(9.0));
        Assert.False(calibrator.ShouldAbstain(10.0));
    }

    [Fact]
    public void PredictProba_InterpolatesBetweenThresholds()
    {
        var examples = new[]
        {
            (10.0, false),
            (20.0, true),
        };

        var calibrator = CalibratedScoreConfidence.Fit(examples, CalibrationMethod.IsotonicRegression);

        // A score between the two training points should get an interpolated probability
        double p15 = calibrator.PredictProba(15);

        Assert.InRange(p15, 0.0, 1.0);
    }

    [Fact]
    public void PredictProba_ClampsOutOfRangeScores()
    {
        var examples = new[]
        {
            (10.0, false),
            (20.0, true),
        };

        var calibrator = CalibratedScoreConfidence.Fit(examples);

        // Scores outside the training range should clamp to the nearest probability
        double pLow = calibrator.PredictProba(-100);
        double pHigh = calibrator.PredictProba(1000);

        Assert.InRange(pLow, 0.0, 1.0);
        Assert.InRange(pHigh, 0.0, 1.0);
    }
}
