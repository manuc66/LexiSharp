using System.Globalization;

namespace LexiSharp.Eval;

/// <summary>
/// One configuration's paired-bootstrap summary against a baseline.
/// </summary>
/// <param name="Mean">Mean per-query metric over the queries the run scored.</param>
/// <param name="Lower">Lower bound of the 95 % percentile bootstrap interval for <paramref name="Mean"/>.</param>
/// <param name="Upper">Upper bound of that interval.</param>
/// <param name="Delta">Mean per-query difference from the baseline.</param>
/// <param name="DeltaLower">Lower bound of the 95 % interval for <paramref name="Delta"/>.</param>
/// <param name="DeltaUpper">Upper bound of that interval.</param>
/// <param name="PValue">
/// Two-sided paired sign-flip randomization test of the mean difference. It is not a rank test.
/// </param>
internal sealed record BootstrapRow(
    string Name,
    int Queries,
    double Mean,
    double Lower,
    double Upper,
    double Delta,
    double DeltaLower,
    double DeltaUpper,
    double PValue);

internal static class RetrievalBootstrap
{
    public const int DefaultResamples = 10_000;
    private const int Seed = 20261102;

    public static void Print(
        IReadOnlyList<ConfigResult> results,
        string baselineName,
        int resamples = DefaultResamples)
    {
        Console.WriteLine();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Paired bootstrap over queries ({resamples:N0} resamples, 95 % percentile CI; delta vs {baselineName})."));

        PrintMetric(results, baselineName, resamples, "nDCG@10", outcome => outcome.NdcgAt10);
        PrintMetric(results, baselineName, resamples, "R@10", outcome => outcome.RecallAt10);

        Console.WriteLine(
            "p is a two-sided paired sign-flip randomization test on the per-query mean difference; "
            + "it tests the mean, not the whole ranking, and it is not corrected for multiple comparisons.");
    }

    private static void PrintMetric(
        IReadOnlyList<ConfigResult> results,
        string baselineName,
        int resamples,
        string metricName,
        Func<QueryOutcome, double> metric)
    {
        IReadOnlyList<BootstrapRow> rows = Compare(results, baselineName, resamples, metric);

        Console.WriteLine();
        Console.WriteLine(metricName);
        Console.WriteLine(
            "Config".PadRight(46) + " " + "n".PadLeft(5) + " " + "mean".PadLeft(7) + " "
            + "95 % CI".PadLeft(18) + " " + "delta".PadLeft(8) + " " + "95 % CI delta".PadLeft(18)
            + " " + "p".PadLeft(8));
        Console.WriteLine(new string('-', 46 + 1 + 5 + 1 + 7 + 1 + 18 + 1 + 8 + 1 + 18 + 1 + 8));

        foreach (BootstrapRow row in rows)
        {
            string ci = FormattableString.Invariant($"[{row.Lower:0.0000}, {row.Upper:0.0000}]");
            string deltaCi = FormattableString.Invariant(
                $"[{row.DeltaLower:+0.0000;-0.0000;0.0000}, {row.DeltaUpper:+0.0000;-0.0000;0.0000}]");

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{row.Name,-46} {row.Queries,5} {row.Mean,7:0.0000} {ci,18} {row.Delta,8:+0.0000;-0.0000;0.0000} {deltaCi,18} {row.PValue,8:0.0000}"));
        }
    }

    internal static IReadOnlyList<BootstrapRow> Compare(
        IReadOnlyList<ConfigResult> results,
        string baselineName,
        int resamples = DefaultResamples,
        Func<QueryOutcome, double>? metric = null)
    {
        ArgumentNullException.ThrowIfNull(results);

        if (resamples <= 0)
            throw new ArgumentOutOfRangeException(nameof(resamples), resamples, "At least one resample is required.");

        metric ??= outcome => outcome.NdcgAt10;

        ConfigResult baseline = results.FirstOrDefault(result => result.Name == baselineName)
            ?? throw new ArgumentException(
                $"No configuration named '{baselineName}'. Available: "
                + string.Join(", ", results.Select(result => result.Name)) + ".",
                nameof(baselineName));

        IReadOnlyList<QueryOutcome> baselineOutcomes = baseline.Outcomes
            ?? throw new ArgumentException(
                $"Configuration '{baselineName}' has no per-query outcomes; run with --bootstrap to capture them.",
                nameof(baselineName));

        if (baselineOutcomes.Count == 0)
            throw new ArgumentException("The baseline configuration scored no queries.", nameof(results));

        var baselineById = baselineOutcomes.ToDictionary(outcome => outcome.QueryId, StringComparer.Ordinal);

        if (baselineById.Count != baselineOutcomes.Count)
            throw new ArgumentException($"Configuration '{baselineName}' has duplicate query ids.", nameof(results));

        double[] baselineValues = baselineOutcomes.Select(metric).ToArray();
        var random = new Random(Seed);
        var rows = new List<BootstrapRow>(results.Count);

        foreach (ConfigResult result in results)
        {
            if (result.Outcomes is null)
            {
                Console.WriteLine($"Note: '{result.Name}' has no per-query outcomes and is omitted from the bootstrap.");
                continue;
            }

            var byId = result.Outcomes.ToDictionary(outcome => outcome.QueryId, StringComparer.Ordinal);

            if (byId.Count != baselineById.Count || !baselineById.Keys.All(byId.ContainsKey))
                throw new ArgumentException(
                    $"Configuration '{result.Name}' scored a different query set than '{baselineName}'.",
                    nameof(results));

            double[] values = baselineOutcomes.Select(outcome => metric(byId[outcome.QueryId])).ToArray();
            double[] deltas = new double[values.Length];

            for (int i = 0; i < values.Length; i++)
                deltas[i] = values[i] - baselineValues[i];

            (double lower, double upper) = BootstrapInterval(values, resamples, random);
            (double deltaLower, double deltaUpper) = BootstrapInterval(deltas, resamples, random);

            rows.Add(new BootstrapRow(
                result.Name,
                values.Length,
                Mean(values),
                lower,
                upper,
                Mean(deltas),
                deltaLower,
                deltaUpper,
                result.Name == baselineName ? 1.0 : SignFlipPValue(deltas, resamples, random)));
        }

        return rows;
    }

    private static double Mean(double[] values)
    {
        double sum = 0;
        for (int i = 0; i < values.Length; i++)
            sum += values[i];

        return sum / values.Length;
    }

    private static (double Lower, double Upper) BootstrapInterval(
        double[] values,
        int resamples,
        Random random)
    {
        var means = new double[resamples];

        for (int b = 0; b < resamples; b++)
        {
            double sum = 0;
            for (int i = 0; i < values.Length; i++)
                sum += values[random.Next(values.Length)];

            means[b] = sum / values.Length;
        }

        Array.Sort(means);
        return (Percentile(means, 0.025), Percentile(means, 0.975));
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        double position = percentile * (sorted.Length - 1);
        int lower = (int)Math.Floor(position);
        int upper = (int)Math.Ceiling(position);

        return lower == upper
            ? sorted[lower]
            : sorted[lower] + ((sorted[upper] - sorted[lower]) * (position - lower));
    }

    private static double SignFlipPValue(double[] deltas, int resamples, Random random)
    {
        double observed = Math.Abs(Mean(deltas));

        if (observed == 0)
            return 1.0;

        int atLeast = 0;

        for (int b = 0; b < resamples; b++)
        {
            double sum = 0;

            for (int i = 0; i < deltas.Length; i++)
                sum += random.Next(2) == 0 ? -deltas[i] : deltas[i];

            if (Math.Abs(sum / deltas.Length) >= observed - 1e-15)
                atLeast++;
        }

        return (atLeast + 1.0) / (resamples + 1.0);
    }
}
