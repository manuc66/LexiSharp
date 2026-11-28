using LexiSharp.Core;

namespace LexiSharp.Classification;

/// <summary>One validation observation for tuning prior weights: the text and the category that was right.</summary>
/// <param name="Text">The text handed to both the classifier and every prior source.</param>
/// <param name="Actual">The category that was actually assigned.</param>
public readonly record struct PriorValidationSample(string Text, string Actual);

/// <summary>The weights a <see cref="PriorWeightTuner"/> chose, aligned with the source list.</summary>
/// <param name="Weights">One finite, non-negative weight per source, in the same order.</param>
/// <param name="LogLoss">Mean log-loss on the validation samples with those weights applied.</param>
/// <param name="Temperature">The temperature applied before the priors; <c>1</c> is identity.</param>
public sealed record PriorWeightTuning(
    IReadOnlyList<double> Weights,
    double LogLoss,
    double Temperature);

/// <summary>
/// Fits the weights of log-prior sources on a validation split by minimizing log-loss, optionally
/// after a temperature has been applied to the base classifier.
/// </summary>
/// <remarks>
/// <para>
/// Calibration comes first: a temperature changes confidence but not the ranking, while a prior
/// shifts categories and can change it. Fit the temperature on one validation slice, then fit the
/// weights on another, or pass an already fitted <see cref="TemperatureCalibrator"/> here. Using the
/// same observations for both fits is a form of double dipping and will usually overstate the gain.
/// </para>
/// <para>
/// The search is coordinate descent over a fixed grid that includes zero, so a source that does not
/// help is dropped rather than kept at an arbitrary small weight. It is deterministic: sources are
/// visited in order and ties keep the smaller weight.
/// </para>
/// </remarks>
public static class PriorWeightTuner
{
    private const double ProbabilityFloor = 1e-12;

    /// <summary>Fits one weight per source.</summary>
    /// <param name="classifier">An already trained classifier.</param>
    /// <param name="sources">The prior sources, in the order the returned weights follow.</param>
    /// <param name="samples">Validation texts and their actual categories.</param>
    /// <param name="calibrator">
    /// Optional temperature applied before the priors. <see langword="null"/> means identity.
    /// </param>
    /// <param name="minWeight">Lower bound of the grid; must be zero or positive.</param>
    /// <param name="maxWeight">Upper bound of the grid; must exceed <paramref name="minWeight"/>.</param>
    /// <param name="gridSteps">Number of grid points, including both bounds; at least two.</param>
    /// <param name="passes">Coordinate-descent passes; at least one.</param>
    public static PriorWeightTuning Fit(
        ITextClassifier classifier,
        IReadOnlyList<ITextPriorSource> sources,
        IEnumerable<PriorValidationSample> samples,
        TemperatureCalibrator? calibrator = null,
        double minWeight = 0,
        double maxWeight = 4,
        int gridSteps = 17,
        int passes = 2)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(samples);

        if (!double.IsFinite(minWeight) || minWeight < 0)
            throw new ArgumentOutOfRangeException(nameof(minWeight), minWeight, "The lower bound must be finite and non-negative.");
        if (!double.IsFinite(maxWeight) || maxWeight <= minWeight)
            throw new ArgumentOutOfRangeException(nameof(maxWeight), maxWeight, "The upper bound must be finite and above the lower bound.");
        if (gridSteps < 2)
            throw new ArgumentOutOfRangeException(nameof(gridSteps), gridSteps, "At least two grid points are required.");
        if (passes < 1)
            throw new ArgumentOutOfRangeException(nameof(passes), passes, "At least one pass is required.");

        var observations = samples.ToArray();
        if (observations.Length == 0)
            throw new ArgumentException("At least one validation sample is required.", nameof(samples));

        double temperature = calibrator?.Temperature ?? 1.0;
        var prepared = new List<PreparedSample>(observations.Length);
        foreach (PriorValidationSample sample in observations)
        {
            IReadOnlyList<ClassificationResult> distribution = classifier.Predict(sample.Text, limit: int.MaxValue);
            if (distribution.Count == 0 || distribution.All(result => result.Category != sample.Actual))
                continue;

            var evidence = new List<IReadOnlyList<TextEvidence>>(sources.Count);
            foreach (ITextPriorSource source in sources)
            {
                if (source is null)
                    throw new ArgumentException("A prior source cannot be null.", nameof(sources));
                evidence.Add(source.Prior(sample.Text));
            }
            prepared.Add(new PreparedSample(distribution, sample.Actual, evidence));
        }

        if (prepared.Count == 0)
            throw new ArgumentException("No validation sample contained its actual category in the classifier's distribution.", nameof(samples));

        var weights = new double[sources.Count];
        double best = MeanLogLoss(prepared, weights, temperature);

        double[] grid = Enumerable.Range(0, gridSteps)
            .Select(i => minWeight + (maxWeight - minWeight) * i / (double)(gridSteps - 1))
            .ToArray();

        for (int pass = 0; pass < passes; pass++)
        {
            for (int source = 0; source < weights.Length; source++)
            {
                double current = weights[source];
                double passBest = current;
                double passLoss = best;

                foreach (double candidate in grid)
                {
                    if (candidate == current)
                        continue;
                    weights[source] = candidate;
                    double loss = MeanLogLoss(prepared, weights, temperature);
                    if (loss >= passLoss)
                        continue;
                    passLoss = loss;
                    passBest = candidate;
                }

                weights[source] = passBest;
                best = passLoss;
            }
        }

        return new PriorWeightTuning(Array.AsReadOnly(weights), best, temperature);
    }

    private static double MeanLogLoss(
        IReadOnlyList<PreparedSample> samples,
        double[] weights,
        double temperature)
    {
        double total = 0;
        foreach (PreparedSample sample in samples)
        {
            var scores = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (ClassificationResult result in sample.Distribution)
            {
                double probability = Math.Max(result.Probability, ProbabilityFloor);
                scores[result.Category] = temperature == 1.0
                    ? Math.Log(probability)
                    : Math.Log(probability) / temperature;
            }

            for (int source = 0; source < weights.Length; source++)
            {
                if (weights[source] == 0)
                    continue;
                foreach (TextEvidence evidence in sample.Evidence[source])
                {
                    if (scores.ContainsKey(evidence.Category))
                        scores[evidence.Category] += weights[source] * evidence.Score;
                }
            }

            double max = scores.Values.Max();
            double denominator = scores.Values.Sum(value => Math.Exp(value - max));
            double actual = scores[sample.Actual] - max;
            double probabilityOfActual = Math.Exp(actual) / denominator;
            total -= Math.Log(Math.Max(probabilityOfActual, ProbabilityFloor));
        }
        return total / samples.Count;
    }

    private sealed record PreparedSample(
        IReadOnlyList<ClassificationResult> Distribution,
        string Actual,
        IReadOnlyList<IReadOnlyList<TextEvidence>> Evidence);
}
