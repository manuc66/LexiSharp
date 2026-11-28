using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class PriorWeightTunerTests
{
    private static readonly PriorValidationSample[] Samples =
    [
        new("first text", "A"),
        new("second text", "A"),
        new("third text", "A"),
    ];

    [Fact]
    public void FitGivesAWeightToAnInformativePrior()
    {
        var classifier = new TextClassifier(_ =>
            [new ClassificationResult("A", 0.45), new ClassificationResult("B", 0.55)]);
        var prior = new FixedPrior(new TextEvidence("A", 2.0));

        PriorWeightTuning tuning = PriorWeightTuner.Fit(classifier, [prior], Samples);

        Assert.Single(tuning.Weights);
        Assert.True(tuning.Weights[0] > 0);
        Assert.True(tuning.LogLoss < -Math.Log(0.45));
    }

    [Fact]
    public void FitDropsASourceWithNoEvidence()
    {
        var classifier = new TextClassifier(_ =>
            [new ClassificationResult("A", 0.5), new ClassificationResult("B", 0.5)]);

        PriorWeightTuning tuning = PriorWeightTuner.Fit(
            classifier, [new FixedPrior()], Samples);

        Assert.Equal(0, tuning.Weights[0]);
        Assert.Equal(-Math.Log(0.5), tuning.LogLoss, 10);
    }

    [Fact]
    public void FitRecordsTheTemperatureItApplied()
    {
        var classifier = new TextClassifier(_ =>
            [new ClassificationResult("A", 0.2), new ClassificationResult("B", 0.8)]);

        PriorWeightTuning tuning = PriorWeightTuner.Fit(
            classifier,
            [new FixedPrior()],
            Samples,
            new TemperatureCalibrator(3.0));

        Assert.Equal(3.0, tuning.Temperature);
        Assert.Equal(0, tuning.Weights[0]);
        Assert.True(tuning.LogLoss < -Math.Log(0.2));
    }

    [Fact]
    public void FitIsDeterministic()
    {
        var classifier = new TextClassifier(_ =>
            [new ClassificationResult("A", 0.4), new ClassificationResult("B", 0.6)]);
        ITextPriorSource[] sources =
        [
            new FixedPrior(new TextEvidence("A", 1.0)),
            new FixedPrior(new TextEvidence("B", 1.0)),
        ];

        PriorWeightTuning first = PriorWeightTuner.Fit(classifier, sources, Samples);
        PriorWeightTuning second = PriorWeightTuner.Fit(classifier, sources, Samples);

        Assert.Equal(first.Weights, second.Weights);
        Assert.Equal(first.LogLoss, second.LogLoss);
    }

    [Fact]
    public void FitRefusesInvalidInputs()
    {
        var classifier = new TextClassifier(_ =>
            [new ClassificationResult("A", 0.5), new ClassificationResult("B", 0.5)]);
        ITextPriorSource[] sources = [new FixedPrior()];

        Assert.Throws<ArgumentOutOfRangeException>(() => PriorWeightTuner.Fit(
            classifier, sources, Samples, minWeight: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PriorWeightTuner.Fit(
            classifier, sources, Samples, maxWeight: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PriorWeightTuner.Fit(
            classifier, sources, Samples, gridSteps: 1));
        Assert.Throws<ArgumentException>(() => PriorWeightTuner.Fit(
            classifier, sources, []));
        Assert.Throws<ArgumentException>(() => PriorWeightTuner.Fit(
            classifier,
            [new FixedPrior(), null!],
            Samples));
    }

    private sealed class TextClassifier(
        Func<string, IReadOnlyList<ClassificationResult>> predict) : ITextClassifier
    {
        public void Train(IEnumerable<SearchDocument> documents) { }

        public IReadOnlyList<ClassificationResult> Predict(
            string text, int limit = 3, IReadOnlySet<string>? excludedCategories = null) =>
            predict(text).Take(limit).ToArray();

        public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null) =>
            predict(text).FirstOrDefault()?.Category;
    }

    private sealed class FixedPrior(params TextEvidence[] evidence) : ITextPriorSource
    {
        public IReadOnlyList<TextEvidence> Prior(string text) => evidence;
    }
}
