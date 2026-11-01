using LexiSharp.Core;

namespace LexiSharp.Classification;

/// <summary>
/// One validation observation for calibration: the distribution a model produced and the category
/// that was actually right.
/// </summary>
/// <param name="Distribution">The model's distribution for the item.</param>
/// <param name="Actual">The true category.</param>
public sealed record CalibrationSample(
    IReadOnlyList<ClassificationResult> Distribution,
    string Actual);

/// <summary>
/// A one-parameter post-hoc calibrator: it divides the log-probabilities by a temperature fitted
/// on a validation set to minimize log-loss.
/// </summary>
/// <remarks>
/// <para>
/// Temperature scaling cannot change the ranking — the argmax is the same at every positive
/// temperature — so it is safe to wrap any classifier. What it changes is the confidence: a
/// temperature above <c>1</c> softens an overconfident distribution, below <c>1</c> sharpens one.
/// The temperature is fitted by a grid search over <c>[0.05, 10]</c> with 400 steps, minimizing the
/// mean log-loss of the actual categories; the fit is deterministic.
/// </para>
/// <para>
/// It is not a guarantee. One parameter cannot repair a model that is systematically wrong, and a
/// temperature fitted on one corpus need not transfer to another. Measure log-loss on a held-out
/// set after applying it.
/// </para>
/// </remarks>
public sealed class TemperatureCalibrator
{
    private const double MinimumTemperature = 0.05;
    private const double MaximumTemperature = 10.0;
    private const int GridSteps = 400;
    private const double ProbabilityFloor = 1e-12;

    /// <summary>Creates a calibrator with a fixed temperature.</summary>
    /// <param name="temperature">The positive temperature; <c>1</c> is the identity.</param>
    public TemperatureCalibrator(double temperature)
    {
        if (!double.IsFinite(temperature) || temperature <= 0)
            throw new ArgumentOutOfRangeException(nameof(temperature), "The temperature must be finite and positive.");

        Temperature = temperature;
    }

    /// <summary>The fitted temperature; <c>1</c> leaves the distribution unchanged.</summary>
    public double Temperature { get; }

    /// <summary>Fits the temperature that minimizes the mean log-loss over the samples.</summary>
    /// <param name="samples">Validation distributions and their true categories.</param>
    /// <returns>The fitted calibrator.</returns>
    public static TemperatureCalibrator Fit(IEnumerable<CalibrationSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var valid = samples
            .Where(sample => sample.Distribution.Count > 0
                && sample.Distribution.Any(result => result.Category == sample.Actual))
            .ToList();

        if (valid.Count == 0)
        {
            throw new ArgumentException(
                "At least one sample must contain its actual category.",
                nameof(samples));
        }

        // Start at the identity, not at the first grid point: a flat log-loss (a perfectly
        // predicted single-class sample, say) has no temperature to choose and must leave the
        // distribution alone rather than pick the most extreme sharpening on the grid.
        double bestTemperature = 1.0;
        double bestLoss = MeanLogLoss(valid, 1.0);

        for (int i = 0; i <= GridSteps; i++)
        {
            double temperature = MinimumTemperature
                + (MaximumTemperature - MinimumTemperature) * i / GridSteps;
            double loss = MeanLogLoss(valid, temperature);

            if (loss < bestLoss - 1e-12)
            {
                bestLoss = loss;
                bestTemperature = temperature;
            }
        }

        return new TemperatureCalibrator(bestTemperature);
    }

    /// <summary>Applies the temperature to a distribution and returns it in decreasing probability.</summary>
    /// <param name="distribution">The model's distribution.</param>
    /// <returns>The calibrated distribution; the same order and categories.</returns>
    public IReadOnlyList<ClassificationResult> Calibrate(IReadOnlyList<ClassificationResult> distribution)
    {
        ArgumentNullException.ThrowIfNull(distribution);

        if (distribution.Count == 0)
            return distribution;

        if (Math.Abs(Temperature - 1.0) <= 1e-12)
        {
            var identity = new List<ClassificationResult>(distribution);

            identity.Sort(static (left, right) =>
            {
                int byProbability = right.Probability.CompareTo(left.Probability);

                return byProbability != 0 ? byProbability : string.CompareOrdinal(left.Category, right.Category);
            });

            return identity;
        }

        var logits = new double[distribution.Count];
        double max = double.NegativeInfinity;

        for (int i = 0; i < distribution.Count; i++)
        {
            logits[i] = Math.Log(Math.Max(distribution[i].Probability, ProbabilityFloor)) / Temperature;
            max = Math.Max(max, logits[i]);
        }

        double sum = 0;
        var probabilities = new double[distribution.Count];

        for (int i = 0; i < distribution.Count; i++)
        {
            double value = Math.Exp(logits[i] - max);
            probabilities[i] = value;
            sum += value;
        }

        var calibrated = new List<ClassificationResult>(distribution.Count);

        for (int i = 0; i < distribution.Count; i++)
        {
            calibrated.Add(new ClassificationResult(
                distribution[i].Category,
                probabilities[i] / sum));
        }

        calibrated.Sort(static (left, right) =>
        {
            int byProbability = right.Probability.CompareTo(left.Probability);

            return byProbability != 0 ? byProbability : string.CompareOrdinal(left.Category, right.Category);
        });

        return calibrated;
    }

    private static double MeanLogLoss(IReadOnlyList<CalibrationSample> samples, double temperature)
    {
        double total = 0;

        foreach (var sample in samples)
        {
            double sum = 0;
            double actual = 0;

            foreach (var result in sample.Distribution)
            {
                double value = Math.Pow(
                    Math.Max(result.Probability, ProbabilityFloor),
                    1.0 / temperature);
                sum += value;

                if (result.Category == sample.Actual)
                    actual = value;
            }

            total -= Math.Log(actual / sum);
        }

        return total / samples.Count;
    }
}

/// <summary>
/// Wraps an <see cref="ITextClassifier"/> with a fitted temperature, so <c>Predict</c> returns the
/// calibrated distribution and <c>PredictBest</c> the same top category.
/// </summary>
/// <remarks>
/// <see cref="Train"/> is forwarded unchanged: the calibrator is fitted after training, on data
/// the model did not see, and retraining the inner model does not refit it. Fit it again with
/// <see cref="Fit(ITextClassifier, IEnumerable{SearchDocument})"/> after a retrain.
/// </remarks>
public sealed class CalibratedClassifier : ITextClassifier
{
    private readonly ITextClassifier _inner;
    private readonly TemperatureCalibrator _calibrator;

    /// <summary>Wraps a trained classifier with a fitted calibrator.</summary>
    /// <param name="inner">The classifier whose distributions are calibrated.</param>
    /// <param name="calibrator">The fitted temperature.</param>
    public CalibratedClassifier(ITextClassifier inner, TemperatureCalibrator calibrator)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(calibrator);

        _inner = inner;
        _calibrator = calibrator;
    }

    /// <summary>Fits a temperature on validation documents and wraps the classifier with it.</summary>
    /// <param name="classifier">A trained classifier.</param>
    /// <param name="validation">Labelled documents the classifier did not train on.</param>
    /// <returns>The calibrated wrapper.</returns>
    public static CalibratedClassifier Fit(
        ITextClassifier classifier,
        IEnumerable<SearchDocument> validation)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(validation);

        var samples = new List<CalibrationSample>();

        foreach (var document in validation)
        {
            if (string.IsNullOrEmpty(document.Category))
                continue;

            samples.Add(new CalibrationSample(
                classifier.Predict(document.Text, limit: int.MaxValue),
                document.Category));
        }

        return new CalibratedClassifier(classifier, TemperatureCalibrator.Fit(samples));
    }

    /// <inheritdoc />
    public void Train(IEnumerable<SearchDocument> documents) => _inner.Train(documents);

    /// <inheritdoc />
    public IReadOnlyList<ClassificationResult> Predict(
        string text,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (limit <= 0)
            return Array.Empty<ClassificationResult>();

        var calibrated = _calibrator.Calibrate(
            _inner.Predict(text, limit: int.MaxValue, excludedCategories));

        if (calibrated.Count <= limit)
            return calibrated;

        var page = new List<ClassificationResult>(limit);

        for (int i = 0; i < limit; i++)
            page.Add(calibrated[i]);

        return page;
    }

    /// <inheritdoc />
    public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null) =>
        Predict(text, limit: 1, excludedCategories).FirstOrDefault()?.Category;
}
