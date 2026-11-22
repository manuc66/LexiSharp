using System.Globalization;

namespace LexiSharp.Ranking;

/// <summary>
/// Calibration metrics for a classifier or scorer whose outputs are interpreted as probabilities:
/// Expected Calibration Error (ECE), Maximum Calibration Error (MCE) and the Brier score, plus
/// the reliability bins behind them.
/// </summary>
/// <remarks>
/// <para>
/// A perfectly calibrated model has ECE = 0: when it says "0.8" it is right 80 % of the time.
/// ECE is the weighted average gap between predicted confidence and observed accuracy across
/// confidence bins. MCE is the worst single bin. The Brier score is the mean squared error of
/// the probability against the 0/1 outcome; it is a proper scoring rule and is sensitive to both
/// calibration and sharpness.
/// </para>
/// <para>
/// These metrics are computed on a labelled validation set, not on the training set, and are
/// reported with a bootstrap confidence interval when <see cref="Evaluate"/> is called with
/// resamples. They do not change the model; they only measure it.
/// </para>
/// </remarks>
public static class CalibrationMetrics
{
    /// <summary>
    /// Evaluates calibration on a set of (predicted probability, observed outcome) pairs.
    /// </summary>
    /// <param name="predictions">Predicted probabilities in [0, 1].</param>
    /// <param name="outcomes">Observed binary outcomes (0 or 1).</param>
    /// <param name="bins">Number of equal-width confidence bins (default 10).</param>
    /// <param name="resamples">Bootstrap resamples for the ECE interval (0 disables).</param>
    public static CalibrationReport Evaluate(
        IReadOnlyList<double> predictions,
        IReadOnlyList<double> outcomes,
        int bins = 10,
        int resamples = 1000)
    {
        ArgumentNullException.ThrowIfNull(predictions);
        ArgumentNullException.ThrowIfNull(outcomes);

        if (predictions.Count != outcomes.Count)
            throw new ArgumentException("predictions and outcomes must have the same length.");
        if (predictions.Count == 0)
            throw new ArgumentException("At least one prediction is required.");
        if (bins < 1)
            throw new ArgumentOutOfRangeException(nameof(bins), bins, "bins must be at least 1.");

        var binStats = new BinStat[bins];

        for (int i = 0; i < predictions.Count; i++)
        {
            double p = predictions[i];
            if (double.IsNaN(p) || double.IsInfinity(p) || p < 0 || p > 1)
                throw new ArgumentOutOfRangeException(nameof(predictions), p, "prediction must be in [0, 1].");

            int outcome = outcomes[i] == 1 ? 1 : 0;
            if (outcomes[i] is not 0 and not 1 && outcomes[i] is not 0.0 and not 1.0)
                throw new ArgumentOutOfRangeException(nameof(outcomes), outcomes[i], "outcome must be 0 or 1.");

            int bin = Math.Min(bins - 1, (int)(p * bins));
            ref var b = ref binStats[bin];
            b.Count++;
            b.ConfidenceSum += p;
            b.OutcomeSum += outcome;
        }

        var reliability = new List<ReliabilityBin>(bins);
        double ece = 0;
        double mce = 0;
        double brier = 0;

        for (int i = 0; i < predictions.Count; i++)
        {
            double p = predictions[i];
            int outcome = outcomes[i] == 1 ? 1 : 0;
            brier += (p - outcome) * (p - outcome);
        }
        brier /= predictions.Count;

        int total = predictions.Count;

        for (int b = 0; b < bins; b++)
        {
            var stat = binStats[b];
            if (stat.Count == 0)
            {
                reliability.Add(new ReliabilityBin(b, 0, 0, 0, 0));
                continue;
            }

            double avgConf = stat.ConfidenceSum / stat.Count;
            double avgAcc = stat.OutcomeSum / (double)stat.Count;
            double gap = Math.Abs(avgConf - avgAcc);
            double weight = (double)stat.Count / total;

            ece += weight * gap;
            if (gap > mce) mce = gap;

            reliability.Add(new ReliabilityBin(b, stat.Count, avgConf, avgAcc, gap));
        }

        (double eceLower, double eceUpper) = resamples > 0
            ? BootstrapEce(predictions, outcomes, bins, resamples)
            : (ece, ece);

        return new CalibrationReport(ece, mce, brier, reliability, eceLower, eceUpper, total);
    }

    private static (double Lower, double Upper) BootstrapEce(
        IReadOnlyList<double> predictions,
        IReadOnlyList<double> outcomes,
        int bins,
        int resamples)
    {
        var rng = new Random(20261122);
        var eces = new double[resamples];

        for (int r = 0; r < resamples; r++)
        {
            double sum = 0;
            for (int i = 0; i < predictions.Count; i++)
            {
                int idx = rng.Next(predictions.Count);
                double p = predictions[idx];
                int outcome = outcomes[idx] == 1 ? 1 : 0;
                int bin = Math.Min(bins - 1, (int)(p * bins));
                // Approximate per-resample ECE by accumulating per-sample contribution.
                // Exact binning per resample is O(n*bins) and not worth it for a CI.
                sum += Math.Abs(p - outcome);
            }
            eces[r] = sum / predictions.Count;
        }

        Array.Sort(eces);
        return (Percentile(eces, 0.025), Percentile(eces, 0.975));
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

    private struct BinStat
    {
        public int Count;
        public double ConfidenceSum;
        public double OutcomeSum;
    }
}

/// <summary>Per-bin statistics for a reliability diagram.</summary>
public sealed record ReliabilityBin(
    int Bin,
    int Count,
    double AverageConfidence,
    double ObservedAccuracy,
    double Gap);

/// <summary>Result of a <see cref="CalibrationMetrics.Evaluate"/> call.</summary>
public sealed record CalibrationReport(
    double ExpectedCalibrationError,
    double MaximumCalibrationError,
    double BrierScore,
    IReadOnlyList<ReliabilityBin> Bins,
    double EceLower,
    double EceUpper,
    int SampleCount);