using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class CalibrationTests
{
    private static IReadOnlyList<ClassificationResult> Distribution(params (string Category, double Probability)[] values) =>
        values.Select(value => new ClassificationResult(value.Category, value.Probability)).ToArray();

    private static double LogLoss(TemperatureCalibrator calibrator, IEnumerable<CalibrationSample> samples)
    {
        double total = 0;
        int count = 0;

        foreach (var sample in samples)
        {
            var calibrated = calibrator.Calibrate(sample.Distribution);
            double probability = calibrated
                .Where(result => result.Category == sample.Actual)
                .Select(result => result.Probability)
                .DefaultIfEmpty(1e-12)
                .Single();

            total -= Math.Log(Math.Max(probability, 1e-12));
            count++;
        }

        return total / count;
    }

    [Fact]
    public void TemperatureOfOne_IsTheIdentity()
    {
        var distribution = Distribution(("A", 0.9), ("B", 0.1));

        var calibrated = new TemperatureCalibrator(1.0).Calibrate(distribution);

        Assert.Equal(distribution.Count, calibrated.Count);
        Assert.Equal(0.9, calibrated[0].Probability, precision: 10);
        Assert.Equal(0.1, calibrated[1].Probability, precision: 10);
    }

    [Fact]
    public void TemperatureAboveOne_SoftensTheDistribution()
    {
        var distribution = Distribution(("A", 0.9), ("B", 0.1));

        var calibrated = new TemperatureCalibrator(2.0).Calibrate(distribution);

        Assert.True(calibrated[0].Probability < 0.9);
        Assert.Equal(1.0, calibrated.Sum(result => result.Probability), precision: 10);
        Assert.Equal("A", calibrated[0].Category);
    }

    [Fact]
    public void TemperatureBelowOne_SharpensTheDistribution()
    {
        var distribution = Distribution(("A", 0.9), ("B", 0.1));

        var calibrated = new TemperatureCalibrator(0.5).Calibrate(distribution);

        Assert.True(calibrated[0].Probability > 0.9);
        Assert.Equal(1.0, calibrated.Sum(result => result.Probability), precision: 10);
    }

    [Fact]
    public void Fit_ReducesLogLossOnAnOverconfidentSet()
    {
        var samples = new List<CalibrationSample>();

        for (int i = 0; i < 10; i++)
        {
            bool wrong = i % 2 == 0;
            samples.Add(new CalibrationSample(
                Distribution(("A", wrong ? 0.01 : 0.99), ("B", wrong ? 0.99 : 0.01)),
                wrong ? "A" : "B"));
        }

        var calibrator = TemperatureCalibrator.Fit(samples);

        Assert.True(calibrator.Temperature > 1);
        Assert.True(LogLoss(calibrator, samples) < LogLoss(new TemperatureCalibrator(1.0), samples));
    }

    [Fact]
    public void Fit_WithAFlatZeroLoss_KeepsTheIdentity()
    {
        var samples = new[]
        {
            new CalibrationSample(Distribution(("A", 1.0)), "A"),
        };

        var calibrator = TemperatureCalibrator.Fit(samples);

        Assert.Equal(1.0, calibrator.Temperature, precision: 12);
    }

    [Fact]
    public void Fit_WithNoSamples_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => TemperatureCalibrator.Fit(Array.Empty<CalibrationSample>()));
    }

    [Fact]
    public void CalibratedClassifier_AppliesTheCalibrator()
    {
        var inner = new FixedClassifier(
            new ClassificationResult("A", 0.9),
            new ClassificationResult("B", 0.1));
        var classifier = new CalibratedClassifier(inner, new TemperatureCalibrator(2.0));

        var results = classifier.Predict("x", limit: 2);

        Assert.True(results[0].Probability < 0.9);
        Assert.Equal("A", results[0].Category);
        Assert.Equal(1.0, results.Sum(result => result.Probability), precision: 10);
    }

    [Fact]
    public void CalibratedClassifier_SortsAnUnsortedInnerDistribution()
    {
        var inner = new FixedClassifier(
            new ClassificationResult("B", 0.1),
            new ClassificationResult("A", 0.9));
        var classifier = new CalibratedClassifier(inner, new TemperatureCalibrator(1.0));

        var results = classifier.Predict("x", limit: 1);

        Assert.Equal("A", results[0].Category);
    }

    [Fact]
    public void CalibratedClassifier_PreservesTheTopCategory()
    {
        var inner = new FixedClassifier(
            new ClassificationResult("A", 0.6),
            new ClassificationResult("B", 0.3),
            new ClassificationResult("C", 0.1));
        var classifier = new CalibratedClassifier(inner, new TemperatureCalibrator(3.0));

        Assert.Equal("A", classifier.PredictBest("x"));
    }

    [Fact]
    public void CalibratedClassifier_Train_Delegates()
    {
        var inner = new FixedClassifier(new ClassificationResult("A", 1.0));
        var classifier = new CalibratedClassifier(inner, new TemperatureCalibrator(1.0));

        classifier.Train(new[] { new SearchDocument("1", "x", Category: "A") });

        Assert.Equal(1, inner.Trains);
    }

    private sealed class FixedClassifier : ITextClassifier
    {
        private readonly IReadOnlyList<ClassificationResult> _results;

        public FixedClassifier(params ClassificationResult[] results) => _results = results;

        public int Trains { get; private set; }

        public void Train(IEnumerable<SearchDocument> documents) => Trains++;

        public IReadOnlyList<ClassificationResult> Predict(
            string text,
            int limit = 3,
            IReadOnlySet<string>? excludedCategories = null) =>
            _results
                .Where(result => excludedCategories?.Contains(result.Category) != true)
                .Take(limit)
                .ToList();

        public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null)
        {
            IReadOnlyList<ClassificationResult> results = Predict(text, 1, excludedCategories);

            return results.Count > 0 ? results[0].Category : null;
        }
    }
}
