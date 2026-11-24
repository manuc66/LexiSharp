using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class ConformalClassifierTests
{
    private static readonly SearchDocument[] Training =
    {
        new("1", "alpha beta", Category: "A"),
        new("2", "alpha gamma", Category: "A"),
        new("3", "beta gamma", Category: "B"),
        new("4", "beta delta", Category: "B"),
    };

    private static readonly SearchDocument[] Calibration =
    {
        new("c1", "alpha", Category: "A"),
        new("c2", "alpha beta", Category: "A"),
        new("c3", "beta", Category: "B"),
        new("c4", "beta gamma", Category: "B"),
        new("c5", "gamma", Category: "A"),
        new("c6", "delta", Category: "B"),
    };

    [Fact]
    public void Uncalibrated_UsesProbabilityThreshold()
    {
        var inner = new NaiveBayesClassifier();
        inner.Train(Training);
        var conformal = new ConformalClassifier<NaiveBayesClassifier>(inner, alpha: 0.10);

        Assert.False(conformal.IsCalibrated);
        var set = conformal.PredictSet("alpha");
        Assert.NotNull(set);
    }

    [Fact]
    public void Calibrated_ProducesNonEmptySetForKnownText()
    {
        var inner = new NaiveBayesClassifier();
        inner.Train(Training);
        var conformal = new ConformalClassifier<NaiveBayesClassifier>(inner, Calibration, alpha: 0.10, minCalibrationSize: 3);

        Assert.True(conformal.IsCalibrated);
        var set = conformal.PredictSet("alpha");
        Assert.NotEmpty(set.Categories);
    }

    [Fact]
    public void SmallerAlpha_ProducesLargerOrEqualSet()
    {
        var inner = new NaiveBayesClassifier();
        inner.Train(Training);
        var loose = new ConformalClassifier<NaiveBayesClassifier>(inner, Calibration, alpha: 0.50, minCalibrationSize: 3);
        var tight = new ConformalClassifier<NaiveBayesClassifier>(inner, Calibration, alpha: 0.05, minCalibrationSize: 3);

        int looseCount = loose.PredictSet("alpha").Count;
        int tightCount = tight.PredictSet("alpha").Count;
        Assert.True(tightCount >= looseCount);
    }

    [Fact]
    public void TooFewCalibrationExamples_FallsBack()
    {
        var inner = new NaiveBayesClassifier();
        inner.Train(Training);
        var conformal = new ConformalClassifier<NaiveBayesClassifier>(inner, new[] { Calibration[0] }, alpha: 0.10, minCalibrationSize: 20);

        Assert.False(conformal.IsCalibrated);
        // No exception.
        _ = conformal.PredictSet("alpha");
    }

    [Fact]
    public void Calibration_RoundTripsThroughStream()
    {
        var inner = new NaiveBayesClassifier();
        inner.Train(Training);
        var conformal = new ConformalClassifier<NaiveBayesClassifier>(inner, Calibration, alpha: 0.10, minCalibrationSize: 3);
        Assert.True(conformal.IsCalibrated);

        using var stream = new MemoryStream();
        conformal.SaveCalibration(stream);
        stream.Position = 0;

        var restored = new ConformalClassifier<NaiveBayesClassifier>(inner, alpha: 0.10, minCalibrationSize: 3);
        Assert.False(restored.IsCalibrated);
        restored.LoadCalibration(stream);

        Assert.True(restored.IsCalibrated);
        Assert.Equal(conformal.Quantile, restored.Quantile, 12);
    }

    [Fact]
    public void Predict_DelegatesToInner()
    {
        var inner = new NaiveBayesClassifier();
        inner.Train(Training);
        var conformal = new ConformalClassifier<NaiveBayesClassifier>(inner);

        var direct = inner.Predict("alpha", limit: 3);
        var wrapped = conformal.Predict("alpha", limit: 3);
        Assert.Equal(direct.Count, wrapped.Count);
    }
}
