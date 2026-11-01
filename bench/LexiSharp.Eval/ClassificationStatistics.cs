namespace LexiSharp.Eval;

/// <summary>A percentile confidence interval.</summary>
internal sealed record ConfidenceInterval(double Lower, double Upper);

/// <summary>Bootstrap intervals for accuracy and macro-F1.</summary>
internal sealed record BootstrapIntervals(
    ConfidenceInterval Accuracy,
    ConfidenceInterval MacroF1);

/// <summary>
/// Deterministic percentile bootstrap over the test documents. It resamples the paired
/// (document, prediction) pairs, so the interval reflects the test set's size and the model's
/// errors rather than a normal approximation.
/// </summary>
internal static class ClassificationStatistics
{
    public static BootstrapIntervals Bootstrap(
        IReadOnlyList<ClassificationDocument> test,
        IReadOnlyList<string?> predictions,
        int resamples = 1000,
        int seed = 7)
    {
        ArgumentNullException.ThrowIfNull(test);
        ArgumentNullException.ThrowIfNull(predictions);

        if (test.Count != predictions.Count)
            throw new ArgumentException("One prediction is required per test document.", nameof(predictions));

        if (resamples < 1)
            throw new ArgumentOutOfRangeException(nameof(resamples), "At least one resample is required.");

        if (test.Count == 0)
            return new BootstrapIntervals(new(0, 0), new(0, 0));

        string[] categories = test
            .Select(document => document.Category)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(category => category, StringComparer.Ordinal)
            .ToArray();
        var categoryIndex = new Dictionary<string, int>(categories.Length, StringComparer.Ordinal);

        for (int i = 0; i < categories.Length; i++)
            categoryIndex[categories[i]] = i;

        var (actual, predicted) = Encode(test, predictions, categoryIndex);
        var (accuracyValues, macroValues) = Resample(
            actual,
            predicted,
            categories.Length,
            resamples,
            seed);

        Array.Sort(accuracyValues);
        Array.Sort(macroValues);

        return new BootstrapIntervals(
            Percentile(accuracyValues),
            Percentile(macroValues));
    }

    private static (int[] Actual, int[] Predicted) Encode(
        IReadOnlyList<ClassificationDocument> test,
        IReadOnlyList<string?> predictions,
        IReadOnlyDictionary<string, int> categoryIndex)
    {
        var actual = new int[test.Count];
        var predicted = new int[test.Count];

        for (int i = 0; i < test.Count; i++)
        {
            actual[i] = categoryIndex[test[i].Category];
            predicted[i] = predictions[i] is { } value && categoryIndex.TryGetValue(value, out int index)
                ? index
                : -1;
        }

        return (actual, predicted);
    }

    private static (double[] Accuracy, double[] Macro) Resample(
        int[] actual,
        int[] predicted,
        int categoryCount,
        int resamples,
        int seed)
    {
        var accuracy = new double[resamples];
        var macro = new double[resamples];
        var confusion = new int[categoryCount * categoryCount];
        var random = new Random(seed); // NOSONAR:S2245 (fixed seed: the interval is reproducible)

        for (int r = 0; r < resamples; r++)
        {
            Array.Clear(confusion, 0, confusion.Length);
            int correct = 0;

            for (int i = 0; i < actual.Length; i++)
            {
                int sample = random.Next(actual.Length);
                int a = actual[sample];
                int p = predicted[sample];

                if (p >= 0)
                    confusion[(a * categoryCount) + p]++;

                if (a == p)
                    correct++;
            }

            accuracy[r] = (double)correct / actual.Length;
            macro[r] = MacroF1(confusion, categoryCount);
        }

        return (accuracy, macro);
    }

    private static double MacroF1(int[] confusion, int categoryCount)
    {
        double sum = 0;
        int present = 0;

        for (int c = 0; c < categoryCount; c++)
        {
            int tp = confusion[(c * categoryCount) + c];
            int fp = 0;
            int fn = 0;

            for (int other = 0; other < categoryCount; other++)
            {
                if (other != c)
                {
                    fp += confusion[(other * categoryCount) + c];
                    fn += confusion[(c * categoryCount) + other];
                }
            }

            // A class absent from the resample carries no evidence: averaging it in as a zero
            // F1 would drag the interval down for the classes that were sampled.
            if (tp + fp + fn == 0)
                continue;

            present++;

            double precision = tp + fp == 0 ? 0 : (double)tp / (tp + fp);
            double recall = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
            sum += precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
        }

        return present == 0 ? 0 : sum / present;
    }

    private static ConfidenceInterval Percentile(double[] sorted)
    {
        int lower = (int)(0.025 * (sorted.Length - 1));
        int upper = (int)(0.975 * (sorted.Length - 1));

        return new ConfidenceInterval(sorted[lower], sorted[upper]);
    }
}
