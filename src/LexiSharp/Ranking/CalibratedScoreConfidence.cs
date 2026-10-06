using LexiSharp.Core;

namespace LexiSharp.Ranking;

/// <summary>
/// A learned confidence calibrator that maps raw scores to calibrated probabilities
/// using isotonic regression or Platt scaling, with an optional Youden-derived
/// abstention threshold.
/// </summary>
/// <remarks>
/// Unlike <see cref="ScoreConfidence"/> (which is a heuristic based on relative gaps),
/// this class is <b>trained</b> on labelled data: pairs of (score, isCorrect) examples.
/// The resulting model produces a probability that the top result is correct, and
/// a threshold below which the system should abstain (return no result / fallback).
/// </remarks>
public sealed class CalibratedScoreConfidence
{
    private readonly double[] _thresholds;
    private readonly double[] _probabilities;
    private readonly double _abstainThreshold;
    private readonly bool _isTrained;

    /// <summary>
    /// Creates an untrained calibrator. <see cref="PredictProba"/> returns 0.5 and
    /// <see cref="ShouldAbstain"/> returns false until <see cref="Fit"/> is called.
    /// </summary>
    public CalibratedScoreConfidence()
    {
        _thresholds = Array.Empty<double>();
        _probabilities = Array.Empty<double>();
        _abstainThreshold = 0.5;
        _isTrained = false;
    }

    private CalibratedScoreConfidence(double[] thresholds, double[] probabilities, double abstainThreshold)
    {
        _thresholds = thresholds;
        _probabilities = probabilities;
        _abstainThreshold = abstainThreshold;
        _isTrained = true;
    }

    /// <summary>Whether the calibrator has been fitted on training data.</summary>
    public bool IsTrained => _isTrained;

    /// <summary>
    /// The abstention threshold: scores whose calibrated probability is below this
    /// value should trigger abstention (fallback). Derived from Youden's J statistic
    /// during <see cref="Fit"/>.
    /// </summary>
    public double AbstainThreshold => _abstainThreshold;

    /// <summary>
    /// Fits the calibrator on labelled (score, isCorrect) pairs.
    /// </summary>
    /// <param name="examples">
    /// Training examples: raw scores paired with whether the top result was correct.
    /// Must contain at least one positive and one negative example.
    /// </param>
    /// <param name="method">The calibration method to use.</param>
    /// <returns>A trained calibrator.</returns>
    public static CalibratedScoreConfidence Fit(
        IReadOnlyList<(double Score, bool IsCorrect)> examples,
        CalibrationMethod method = CalibrationMethod.IsotonicRegression)
    {
        ArgumentNullException.ThrowIfNull(examples);

        if (examples.Count < 2)
            throw new ArgumentException("At least two examples are required for calibration.", nameof(examples));

        bool hasPositive = false;
        bool hasNegative = false;

        for (int i = 0; i < examples.Count; i++)
        {
            if (examples[i].IsCorrect) hasPositive = true;
            else hasNegative = true;

            if (hasPositive && hasNegative) break;
        }

        if (!hasPositive || !hasNegative)
            throw new ArgumentException(
                "Calibration requires at least one positive and one negative example.", nameof(examples));

        return method switch
        {
            CalibrationMethod.PlattScaling => FitPlatt(examples),
            _ => FitIsotonic(examples),
        };
    }

    /// <summary>
    /// Predicts the probability that a result with the given raw score is correct.
    /// Returns 0.5 if the calibrator is not trained.
    /// </summary>
    public double PredictProba(double score)
    {
        if (!_isTrained || _thresholds.Length == 0)
            return 0.5;

        // Binary search for the interval containing the score
        int index = Array.BinarySearch(_thresholds, score);

        if (index >= 0)
            return _probabilities[index];

        // BinarySearch returns the bitwise complement of the insertion point when not found
        index = ~index;

        if (index == 0)
            return _probabilities[0];

        if (index >= _thresholds.Length)
            return _probabilities[_probabilities.Length - 1];

        // Linear interpolation between adjacent thresholds
        double t0 = _thresholds[index - 1];
        double t1 = _thresholds[index];
        double p0 = _probabilities[index - 1];
        double p1 = _probabilities[index];

        if (t1 - t0 < double.Epsilon)
            return p1;

        double fraction = (score - t0) / (t1 - t0);
        return p0 + fraction * (p1 - p0);
    }

    /// <summary>
    /// Whether the system should abstain (return no result / fallback) for a result
    /// with the given raw score. Returns false if the calibrator is not trained.
    /// </summary>
    public bool ShouldAbstain(double score)
    {
        if (!_isTrained)
            return false;

        return PredictProba(score) < _abstainThreshold;
    }

    /// <summary>
    /// Fits using isotonic regression (PAVA — Pool Adjacent Violators Algorithm).
    /// Produces a non-decreasing mapping from score to probability.
    /// </summary>
    private static CalibratedScoreConfidence FitIsotonic(
        IReadOnlyList<(double Score, bool IsCorrect)> examples)
    {
        // Sort by score ascending
        var sorted = examples
            .Select(e => (e.Score, e.IsCorrect ? 1.0 : 0.0))
            .OrderBy(e => e.Score)
            .ToArray();

        int n = sorted.Length;

        // PAVA: pool adjacent violators to enforce monotonicity
        var blocks = new List<(double Sum, int Count, double Mean)>();

        for (int i = 0; i < n; i++)
        {
            blocks.Add((sorted[i].Item2, 1, sorted[i].Item2));

            // Merge while monotonicity is violated
            while (blocks.Count >= 2)
            {
                var last = blocks[blocks.Count - 1];
                var secondLast = blocks[blocks.Count - 2];

                if (secondLast.Mean > last.Mean + double.Epsilon)
                {
                    // Merge the two blocks
                    double newSum = secondLast.Sum + last.Sum;
                    int newCount = secondLast.Count + last.Count;
                    double newMean = newSum / newCount;

                    blocks.RemoveAt(blocks.Count - 1);
                    blocks[blocks.Count - 1] = (newSum, newCount, newMean);
                }
                else
                {
                    break;
                }
            }
        }

        // Build threshold/probability arrays from blocks
        var thresholds = new List<double>();
        var probabilities = new List<double>();

        int idx = 0;
        foreach (var block in blocks)
        {
            // The threshold for this block is the score of its first element
            thresholds.Add(sorted[idx].Score);
            probabilities.Add(Math.Clamp(block.Mean, 0.0, 1.0));
            idx += block.Count;
        }

        double youdenScoreThreshold = ComputeYoudenThreshold(sorted);

        var calibrator = new CalibratedScoreConfidence(
            thresholds.ToArray(),
            probabilities.ToArray(),
            youdenScoreThreshold);

        // Convert the score threshold to a probability threshold
        double abstainProbabilityThreshold = calibrator.PredictProba(youdenScoreThreshold);

        return new CalibratedScoreConfidence(
            thresholds.ToArray(),
            probabilities.ToArray(),
            abstainProbabilityThreshold);
    }

    /// <summary>
    /// Fits using Platt scaling: logistic regression on the raw scores.
    /// Produces a smooth sigmoid mapping.
    /// </summary>
    private static CalibratedScoreConfidence FitPlatt(
        IReadOnlyList<(double Score, bool IsCorrect)> examples)
    {
        // Platt scaling: fit P(correct | score) = 1 / (1 + exp(A * score + B))
        // using gradient descent on the negative log-likelihood

        double a = 0.0;
        double b = 0.0;
        double learningRate = 0.01;
        int maxIterations = 1000;

        for (int iter = 0; iter < maxIterations; iter++)
        {
            double gradA = 0.0;
            double gradB = 0.0;

            for (int i = 0; i < examples.Count; i++)
            {
                double score = examples[i].Score;
                double target = examples[i].IsCorrect ? 1.0 : 0.0;

                double z = a * score + b;
                double pred = Sigmoid(z);
                double error = pred - target;

                gradA += error * score;
                gradB += error;
            }

            gradA /= examples.Count;
            gradB /= examples.Count;

            a -= learningRate * gradA;
            b -= learningRate * gradB;

            if (Math.Abs(gradA) < 1e-6 && Math.Abs(gradB) < 1e-6)
                break;
        }

        // Build a lookup table from the sigmoid
        // Use the range of training scores extended slightly
        double minScore = examples.Min(e => e.Score);
        double maxScore = examples.Max(e => e.Score);
        double range = maxScore - minScore;

        if (range < double.Epsilon)
            range = 1.0;

        double extendedMin = minScore - 0.1 * range;
        double extendedMax = maxScore + 0.1 * range;
        int tableSize = 100;

        var thresholds = new double[tableSize];
        var probabilities = new double[tableSize];

        for (int i = 0; i < tableSize; i++)
        {
            double t = extendedMax - i * (extendedMax - extendedMin) / (tableSize - 1);
            thresholds[i] = t;
            probabilities[i] = Sigmoid(a * t + b);
        }

        // Sort ascending for binary search
        Array.Sort(thresholds, probabilities);

        var sortedExamples = examples
            .Select(e => (e.Score, e.IsCorrect ? 1.0 : 0.0))
            .OrderBy(e => e.Score)
            .ToArray();

        double youdenScoreThreshold = ComputeYoudenThreshold(sortedExamples);

        var calibrator = new CalibratedScoreConfidence(thresholds, probabilities, youdenScoreThreshold);

        // Convert the score threshold to a probability threshold
        double abstainProbabilityThreshold = calibrator.PredictProba(youdenScoreThreshold);

        return new CalibratedScoreConfidence(thresholds, probabilities, abstainProbabilityThreshold);
    }

    /// <summary>
    /// Computes the Youden's J statistic threshold: the score threshold that maximizes
    /// J = TPR - FPR (sensitivity + specificity - 1).
    /// </summary>
    private static double ComputeYoudenThreshold((double Score, double Label)[] sortedExamples)
    {
        int n = sortedExamples.Length;
        int totalPositives = 0;

        for (int i = 0; i < n; i++)
            totalPositives += (int)sortedExamples[i].Label;

        int totalNegatives = n - totalPositives;

        if (totalPositives == 0 || totalNegatives == 0)
            return 0.5;

        double bestJ = double.NegativeInfinity;
        double bestThreshold = sortedExamples[0].Score;

        int tp = 0;
        int fp = 0;

        // Try each unique score as a threshold
        for (int i = 0; i < n; i++)
        {
            // Count how many examples have score >= threshold (predicted positive)
            // Since sorted ascending, examples[i..n-1] are predicted positive
            int predictedPositives = n - i;
            int predictedNegatives = i;

            // TP = positives with score >= threshold
            tp = 0;
            for (int k = i; k < n; k++)
                tp += (int)sortedExamples[k].Label;

            fp = predictedPositives - tp;

            double tpr = (double)tp / totalPositives;
            double fpr = (double)fp / totalNegatives;
            double j = tpr - fpr;

            if (j > bestJ)
            {
                bestJ = j;
                bestThreshold = sortedExamples[i].Score;
            }
        }

        return bestThreshold;
    }

    private static double Sigmoid(double z)
    {
        if (z >= 0)
        {
            double exp = Math.Exp(-z);
            return 1.0 / (1.0 + exp);
        }
        else
        {
            double exp = Math.Exp(z);
            return exp / (1.0 + exp);
        }
    }
}

/// <summary>
/// The calibration method used by <see cref="CalibratedScoreConfidence"/>.
/// </summary>
public enum CalibrationMethod
{
    /// <summary>
    /// Isotonic regression (PAVA). Non-parametric, produces a non-decreasing step function.
    /// Preferred when the relationship between score and correctness is monotonic but not necessarily linear.
    /// </summary>
    IsotonicRegression,

    /// <summary>
    /// Platt scaling (logistic regression on scores). Parametric, produces a smooth sigmoid.
    /// Preferred when a smooth probability estimate is desired.
    /// </summary>
    PlattScaling,
}
