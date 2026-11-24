namespace LexiSharp.Core;

/// <summary>Non-parametric significance tests for paired samples.</summary>
public static class SignificanceTests
{
    /// <summary>Two-sided Wilcoxon signed-rank test p-value (normal approximation).</summary>
    public static double WilcoxonSignedRank(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count != b.Count)
        {
            throw new ArgumentException("Samples must have the same length.");
        }

        var diffs = new List<(double Value, int Sign)>();
        for (int i = 0; i < a.Count; i++)
        {
            double d = a[i] - b[i];
            if (d != 0)
            {
                diffs.Add((Math.Abs(d), d > 0 ? 1 : -1));
            }
        }

        if (diffs.Count == 0)
        {
            return 1.0;
        }

        diffs.Sort((x, y) => x.Value.CompareTo(y.Value));

        // Assign ranks (average ties).
        double[] ranks = new double[diffs.Count];
        int idx = 0;
        while (idx < diffs.Count)
        {
            int j = idx;
            while (j + 1 < diffs.Count && diffs[j + 1].Value == diffs[idx].Value)
            {
                j++;
            }

            double rank = (idx + 1 + j + 1) / 2.0;
            for (int kk = idx; kk <= j; kk++)
            {
                ranks[kk] = rank;
            }

            idx = j + 1;
        }

        double wPlus = 0;
        for (int k = 0; k < diffs.Count; k++)
        {
            if (diffs[k].Sign > 0)
            {
                wPlus += ranks[k];
            }
        }

        int n = diffs.Count;
        double mean = n * (n + 1) / 4.0;
        double variance = n * (n + 1) * (2 * n + 1) / 24.0;
        if (variance <= 0)
        {
            return 1.0;
        }

        double z = (wPlus - mean) / Math.Sqrt(variance);
        return 2.0 * (1.0 - NormalCdf(Math.Abs(z)));
    }

    /// <summary>Two-sided paired bootstrap p-value for the mean difference.</summary>
    public static double PairedBootstrap(IReadOnlyList<double> a, IReadOnlyList<double> b, int resamples = 1000)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count != b.Count)
        {
            throw new ArgumentException("Samples must have the same length.");
        }

        if (resamples < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(resamples));
        }

        int n = a.Count;
        if (n == 0)
        {
            return 1.0;
        }

        double[] diffs = new double[n];
        for (int i = 0; i < n; i++)
        {
            diffs[i] = a[i] - b[i];
        }

        double observed = diffs.Average();

        // Center the differences under H0: mean difference = 0, then resample the centered
        // differences. The p-value is the share of resampled means at least as extreme as the
        // observed one. Comparing raw bootstrap means to the observed mean (the previous
        // implementation) makes a perfectly consistent improvement look non-significant.
        double[] centered = new double[n];
        for (int i = 0; i < n; i++)
        {
            centered[i] = diffs[i] - observed;
        }

        var rng = new Random(12345);
        int extreme = 0;
        for (int r = 0; r < resamples; r++)
        {
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                sum += centered[rng.Next(n)];
            }

            double mean = sum / n;
            if (Math.Abs(mean) >= Math.Abs(observed))
            {
                extreme++;
            }
        }

        return (extreme + 1.0) / (resamples + 1.0);
    }

    private static double NormalCdf(double x)
    {
        // Abramowitz and Stegun approximation.
        const double a1 = 0.254829592;
        const double a2 = -0.284496736;
        const double a3 = 1.421413741;
        const double a4 = -1.453152027;
        const double a5 = 1.061405429;
        const double p = 0.3275911;

        int sign = x < 0 ? -1 : 1;
        x = Math.Abs(x) / Math.Sqrt(2.0);
        double t = 1.0 / (1.0 + p * x);
        double y = 1.0 - (((((a5 * t + a4) * t) + a3) * t + a2) * t + a1) * t * Math.Exp(-x * x);
        return 0.5 * (1.0 + sign * y);
    }
}
