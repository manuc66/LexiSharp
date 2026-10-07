using System.Linq;
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

    [Theory]
    [InlineData(11)]   // seed: a set built to contain tied thresholds
    [InlineData(7)]
    [InlineData(31)]
    public void YoudenThreshold_achievesTheBestJOfAnyThreshold(int seed)
    {
        // The threshold search was rewritten from a suffix recomputed per candidate to a running
        // suffix. This checks it still finds a maximizer, against an O(n^2) reference that
        // plainly does — so a regression to "some threshold" rather than the best one fails here
        // rather than silently raising the abstention rate.
        var random = new Random(seed);
        var pairs = new List<(double Score, bool IsCorrect)>();

        for (int i = 0; i < 400; i++)
        {
            // A tied run of scores, so more than one threshold can reach the maximum.
            double score = (i / 3) * 0.5;
            pairs.Add((score, random.NextDouble() < 0.55));
        }

        var calibrator = CalibratedScoreConfidence.Fit(pairs);

        // A positive is an example the calibrator predicts — i.e. one it does not abstain on.
        bool Predicted(double score) => !calibrator.ShouldAbstain(score);

        int positives = pairs.Count(p => p.IsCorrect);
        int negatives = pairs.Count - positives;
        Assert.True(positives > 0 && negatives > 0, "the fixture needs both classes");

        double achieved = J(pairs, Predicted, positives, negatives);

        double best = double.NegativeInfinity;
        foreach (double candidate in pairs.Select(p => p.Score).Distinct())
        {
            bool Predict(double s) => s >= candidate;
            best = Math.Max(best, J(pairs, Predict, positives, negatives));
        }

        Assert.Equal(best, achieved, 9);

        static double J(
            List<(double Score, bool IsCorrect)> pairs,
            Func<double, bool> predicted,
            int positives,
            int negatives)
        {
            int tp = 0, fp = 0;
            foreach (var (score, correct) in pairs)
            {
                if (!predicted(score)) continue;
                if (correct) tp++; else fp++;
            }

            return (double)tp / positives - (double)fp / negatives;
        }
    }

    [Fact]
    public void YoudenThreshold_walksTheLabelledSetOncePerPass()
    {
        // One pass in each direction is a property of the code, not of a machine, so it is
        // counted rather than timed. A stopwatch could not hold it: on a shared CI runner the
        // same 20 000-pair fit measured 68.5 ms against 3.7 ms on the same input locally, and
        // the ratio of two timings read that pause as a quadratic search — the failure that
        // removed this test's clock. The count is the claim, taken where the claim lives.
        static (double Score, double Label)[] Sorted(int count)
        {
            var pairs = new List<(double Score, double Label)>(count);
            var random = new Random(0);
            for (int i = 0; i < count; i++)
                pairs.Add(((double)i, random.NextDouble() < 0.5 ? 1d : 0d));

            return [.. pairs.OrderBy(p => p.Score)];
        }

        _ = CalibratedScoreConfidence.ComputeYoudenThreshold(Sorted(2_000), out int small);
        _ = CalibratedScoreConfidence.ComputeYoudenThreshold(Sorted(20_000), out int large);

        Assert.Equal(2 * 2_000, small);
        Assert.Equal(2 * 20_000, large);
        Assert.Equal(10 * small, large);
    }
}
