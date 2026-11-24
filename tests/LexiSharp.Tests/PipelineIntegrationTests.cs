using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class PipelineIntegrationTests
{
    [Fact]
    public void TrainCalibrateConformalExplainAndCounterfactuals_EndToEnd()
    {
        var training = new[]
        {
            new SearchDocument("1", "alpha beta", Category: "A"),
            new SearchDocument("2", "alpha gamma", Category: "A"),
            new SearchDocument("3", "beta delta", Category: "B"),
            new SearchDocument("4", "delta epsilon", Category: "B"),
        };
        var calibration = new[]
        {
            new SearchDocument("c1", "alpha", Category: "A"),
            new SearchDocument("c2", "beta", Category: "A"),
            new SearchDocument("c3", "delta", Category: "B"),
            new SearchDocument("c4", "epsilon", Category: "B"),
        };

        var nb = new NaiveBayesClassifier();
        nb.Train(training);

        var calibrated = CalibratedClassifier.Fit(nb, calibration);
        Assert.True(calibrated.Temperature > 0);

        var conformal = new ConformalClassifier<CalibratedClassifier>(calibrated, calibration, alpha: 0.10, minCalibrationSize: 2);
        Assert.True(conformal.IsCalibrated);

        var set = conformal.PredictSet("alpha");
        Assert.NotEmpty(set.Categories);

        var explanation = conformal.Explain("alpha");
        Assert.NotNull(explanation);
        Assert.NotEmpty(explanation!.Terms);

        IExplainableTextClassifier explainable = conformal;
        var counterfactuals = explainable.Counterfactuals("alpha beta", top: 3);
        Assert.NotNull(counterfactuals);
    }

    [Fact]
    public void TemporalPriorAndEntityExtractor_EndToEnd()
    {
        var history = new[]
        {
            new ObservedAssignment(DayOfWeek.Monday, 9, "A"),
            new ObservedAssignment(DayOfWeek.Monday, 9, "A"),
            new ObservedAssignment(DayOfWeek.Monday, 9, "A"),
        };
        ITemporalPriorSource prior = new TemporalPrior(history, blockHours: 3, minSupport: 1);
        Assert.NotEmpty(prior.Prior(new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero)));

        var extractor = new EntityExtractor(new[] { GenericEntityPatterns.Url });
        var result = extractor.Extract("see https://example.com");
        Assert.Single(result.Entities);
    }
}
