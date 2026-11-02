namespace LexiSharp.Classification;

/// <summary>A percentile confidence interval.</summary>
public readonly record struct ClassificationInterval(double Lower, double Upper);

/// <summary>The paired accuracy and macro-F1 intervals of one resample set.</summary>
internal readonly record struct EvaluationIntervals(
    ClassificationInterval Accuracy,
    ClassificationInterval MacroF1);

/// <summary>Block bootstrap used by <see cref="OnlineClassificationEvaluator"/>.</summary>
/// <remarks>
/// A stream is ordered in time; two rows from the same block are not independent. Resampling whole
/// blocks keeps that correlation, unlike a document bootstrap. The seed is fixed, so the interval is
/// reproducible.
/// </remarks>
internal static class OnlineEvaluationStatistics
{
    public static EvaluationIntervals BootstrapBlocks(
        IReadOnlyList<int> blockOf,
        IReadOnlyList<string> actual,
        IReadOnlyList<string?> predicted,
        int resamples = 1000,
        int seed = 7)
    {
        ArgumentNullException.ThrowIfNull(blockOf);
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(predicted);

        if (blockOf.Count != actual.Count || actual.Count != predicted.Count)
            throw new ArgumentException("One block and one prediction are required per row.", nameof(predicted));

        if (resamples < 1)
            throw new ArgumentOutOfRangeException(nameof(resamples), "At least one resample is required.");

        if (actual.Count == 0)
            return new EvaluationIntervals(new(0, 0), new(0, 0));

        (int[] encodedActual, int[] encodedPredicted, int categoryCount) = Encode(actual, predicted);
        int[][] members = BuildBlockMembers(blockOf);
        (double[] accuracy, double[] macro) = ResampleBlocks(
            members, encodedActual, encodedPredicted, categoryCount, resamples, seed);

        Array.Sort(accuracy);
        Array.Sort(macro);

        return new EvaluationIntervals(Percentile(accuracy), Percentile(macro));
    }

    public static ClassificationInterval BootstrapAccuracyBlocks(
        IReadOnlyList<int> blockOf,
        IReadOnlyList<bool> correct,
        int resamples = 1000,
        int seed = 7)
    {
        ArgumentNullException.ThrowIfNull(blockOf);
        ArgumentNullException.ThrowIfNull(correct);

        if (blockOf.Count != correct.Count)
            throw new ArgumentException("One block is required per row.", nameof(correct));

        if (resamples < 1)
            throw new ArgumentOutOfRangeException(nameof(resamples), "At least one resample is required.");

        if (correct.Count == 0)
            return new ClassificationInterval(0, 0);

        int[][] members = BuildBlockMembers(blockOf);
        double[] values = ResampleAccuracy(members, correct, resamples, seed);
        Array.Sort(values);
        return Percentile(values);
    }

    private static (int[] Actual, int[] Predicted, int CategoryCount) Encode(
        IReadOnlyList<string> actual,
        IReadOnlyList<string?> predicted)
    {
        string[] categories = actual
            .Distinct(StringComparer.Ordinal)
            .OrderBy(category => category, StringComparer.Ordinal)
            .ToArray();
        var categoryIndex = new Dictionary<string, int>(categories.Length, StringComparer.Ordinal);

        for (int i = 0; i < categories.Length; i++)
            categoryIndex[categories[i]] = i;

        var encodedActual = new int[actual.Count];
        var encodedPredicted = new int[actual.Count];

        for (int i = 0; i < actual.Count; i++)
        {
            encodedActual[i] = categoryIndex[actual[i]];
            encodedPredicted[i] = predicted[i] is { } value && categoryIndex.TryGetValue(value, out int index)
                ? index
                : -1;
        }

        return (encodedActual, encodedPredicted, categories.Length);
    }

    private static int[][] BuildBlockMembers(IReadOnlyList<int> blockOf)
    {
        int[] blockIds = blockOf.Distinct().OrderBy(id => id).ToArray();
        var members = new int[blockIds.Length][];
        var byBlock = new Dictionary<int, List<int>>();

        for (int i = 0; i < blockOf.Count; i++)
        {
            if (!byBlock.TryGetValue(blockOf[i], out List<int>? list))
            {
                list = new List<int>();
                byBlock[blockOf[i]] = list;
            }

            list.Add(i);
        }

        for (int b = 0; b < blockIds.Length; b++)
            members[b] = byBlock[blockIds[b]].ToArray();

        return members;
    }

    private static (double[] Accuracy, double[] Macro) ResampleBlocks(
        int[][] members,
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
            int sampled = 0;

            for (int draw = 0; draw < members.Length; draw++)
            {
                foreach (int row in members[random.Next(members.Length)])
                {
                    int a = actual[row];
                    int p = predicted[row];

                    if (p >= 0)
                        confusion[(a * categoryCount) + p]++;

                    if (a == p)
                        correct++;

                    sampled++;
                }
            }

            accuracy[r] = sampled == 0 ? 0 : (double)correct / sampled;
            macro[r] = MacroF1(confusion, categoryCount);
        }

        return (accuracy, macro);
    }

    private static double[] ResampleAccuracy(
        int[][] members,
        IReadOnlyList<bool> correct,
        int resamples,
        int seed)
    {
        var values = new double[resamples];
        var random = new Random(seed); // NOSONAR:S2245 (fixed seed: the interval is reproducible)

        for (int r = 0; r < resamples; r++)
        {
            int hits = 0;
            int sampled = 0;

            for (int draw = 0; draw < members.Length; draw++)
            {
                foreach (int row in members[random.Next(members.Length)])
                {
                    if (correct[row])
                        hits++;

                    sampled++;
                }
            }

            values[r] = sampled == 0 ? 0 : (double)hits / sampled;
        }

        return values;
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

            if (tp + fp + fn == 0)
                continue;

            present++;

            double precision = tp + fp == 0 ? 0 : (double)tp / (tp + fp);
            double recall = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
            sum += precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
        }

        return present == 0 ? 0 : sum / present;
    }

    private static ClassificationInterval Percentile(double[] sorted)
    {
        int lower = (int)(0.025 * (sorted.Length - 1));
        int upper = (int)(0.975 * (sorted.Length - 1));

        return new ClassificationInterval(sorted[lower], sorted[upper]);
    }
}
