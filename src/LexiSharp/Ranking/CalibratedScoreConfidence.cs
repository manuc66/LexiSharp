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
    private readonly double _abstainScore;
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
        _abstainScore = 0;
        _abstainThreshold = 0.5;
        _isTrained = false;
    }

    private CalibratedScoreConfidence(
        double[] thresholds,
        double[] probabilities,
        double abstainScore,
        double abstainThreshold)
    {
        _thresholds = thresholds;
        _probabilities = probabilities;
        _abstainScore = abstainScore;
        _abstainThreshold = abstainThreshold;
        _isTrained = true;
    }

    /// <summary>Whether the calibrator has been fitted on training data.</summary>
    public bool IsTrained => _isTrained;

    /// <summary>
    /// The raw score Youden's J maximizes at: below it <see cref="ShouldAbstain"/> declines.
    /// </summary>
    /// <remarks>
    /// <b>This</b> is the decision boundary, and <see cref="ShouldAbstain"/> compares a score
    /// against it. Youden is defined over the labelled pairs' own scores, so the threshold that
    /// maximizes it is a score — converting to the calibrated probability first and comparing
    /// there would move it, because <see cref="PredictProba"/> is not injective: wherever the fit
    /// is flat, every score on that plateau maps to one probability and the boundary slides to the
    /// plateau's edge. Measured against an O(n²) reference that plainly does not move: with a
    /// tied-score fixture, the probability-space threshold reached J = 0.1552 where 0.1591 was
    /// available. <see cref="AbstainThreshold"/> is the same boundary expressed as a probability,
    /// for reading rather than for comparing.
    /// </remarks>
    public double AbstainScore => _abstainScore;

    /// <summary>
    /// The calibrated probability of a result sitting exactly on the abstention boundary —
    /// <c>PredictProba(AbstainScore)</c>.
    /// </summary>
    /// <remarks>
    /// Interpretive. <see cref="ShouldAbstain"/> does not threshold on this, and a caller who
    /// writes <c>PredictProba(x) &lt; AbstainThreshold</c> by hand may get a different answer
    /// than <see cref="ShouldAbstain"/> on scores whose fit is flat. Reach for
    /// <see cref="ShouldAbstain"/>.
    /// </remarks>
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

        return LookupProbability(_thresholds, _probabilities, score);
    }

    /// <summary>
    /// Binary search over a fitted table, linearly interpolating between adjacent points.
    /// Static so a <see cref="Fit"/> can read the boundary it has just built without an instance
    /// that would need the boundary to exist first.
    /// </summary>
    private static double LookupProbability(double[] thresholds, double[] probabilities, double score)
    {
        // Binary search for the interval containing the score
        int index = Array.BinarySearch(thresholds, score);

        if (index >= 0)
            return probabilities[index];

        // BinarySearch returns the bitwise complement of the insertion point when not found
        index = ~index;

        if (index == 0)
            return probabilities[0];

        if (index >= thresholds.Length)
            return probabilities[probabilities.Length - 1];

        // Linear interpolation between adjacent thresholds
        double t0 = thresholds[index - 1];
        double t1 = thresholds[index];
        double p0 = probabilities[index - 1];
        double p1 = probabilities[index];

        if (t1 - t0 < double.Epsilon)
            return p1;

        double fraction = (score - t0) / (t1 - t0);
        return p0 + fraction * (p1 - p0);
    }

    /// <summary>
    /// Whether the system should abstain (return no result / fallback) for a result
    /// with the given raw score. Returns false if the calibrator is not trained.
    /// </summary>
    /// <remarks>
    /// A raw score compared against <see cref="AbstainScore"/>, not a probability against
    /// <see cref="AbstainThreshold"/>. See <see cref="AbstainScore"/> for why converting first
    /// would move the boundary — a test asserts this reaches the best J available on a tied-score
    /// fixture, which is the behaviour Youden's J is supposed to deliver.
    /// </remarks>
    public bool ShouldAbstain(double score)
    {
        if (!_isTrained)
            return false;

        return score < _abstainScore;
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
        double[] thresholdArray = thresholds.ToArray();
        double[] probabilityArray = probabilities.ToArray();

        // The Youden threshold is a score, and stays a score: See AbstainScore for why the
        // probability form cannot carry the decision. It is computed here only to publish it.
        return new CalibratedScoreConfidence(
            thresholdArray,
            probabilityArray,
            youdenScoreThreshold,
            LookupProbability(thresholdArray, probabilityArray, youdenScoreThreshold));
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

        return new CalibratedScoreConfidence(
            thresholds,
            probabilities,
            youdenScoreThreshold,
            LookupProbability(thresholds, probabilities, youdenScoreThreshold));
    }

    /// <summary>
    /// The score maximizing Youden's J (<c>TPR - FPR</c>) over the labelled pairs.
    /// </summary>
    /// <remarks>
    /// Walks downward with a running suffix sum rather than upward recomputing each one: the
    /// threshold at <c>i</c> scores every example from <c>i</c> on, so moving <c>i</c> down one
    /// moves exactly one example from "not predicted" to "predicted", and
    /// <c>tp</c> follows it. Recomputing the suffix per threshold made this
    /// <c>O(n²)</c> over the labelled set — measured at 0.7 ms for 1 000 pairs and 52.4 ms for
    /// 10 000, which is the 75× an <c>O(n²)</c> pass shows at 10× the input, and puts a
    /// 100 000-pair fit at seconds. The suffix pass is one <c>O(n)</c> walk in the other
    /// direction, so the whole method is linear.
    /// <para>
    /// It also evaluates only where the score <b>changes</b>. The predicate is
    /// <c>score &gt;= threshold</c>, and with tied scores that spans the entire run of equal
    /// values — including examples sitting at lower indices — while a suffix starting at
    /// <c>i</c> covers only <c>[i, n)</c>. Walking mid-run would score one threshold as counting
    /// part of a tied group positive and part negative, which no threshold can actually do, and
    /// would report a J that no classifier attains. Measured on a fixture built to contain ties:
    /// the mid-run version reached J = 0.1552 against 0.1591 achievable.
    /// </para>
    /// <para>
    /// The comparison is <c>&gt;=</c> while walking downward, deliberately. An upward walk with
    /// <c>&gt;</c> keeps the <b>smallest</b> threshold among those achieving the maximum; walking
    /// down with <c>&gt;=</c> keeps updating toward smaller <c>i</c> and lands on the same one. A
    /// strict <c>&gt;</c> here would silently pick the largest, changing which threshold a caller
    /// abstains at whenever two tie.
    /// </para>
    /// </remarks>
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
        int truePositives = 0;

        // Downward: entering iteration i, truePositives already holds the labels from i to n-1.
        for (int i = n - 1; i >= 0; i--)
        {
            truePositives += (int)sortedExamples[i].Label;

            // A threshold only exists where the score *changes*. The predicate being scored is
            // "score >= sortedExamples[i].Score", which with ties spans the whole run of equal
            // scores — indices below i included — while this suffix covers only [i, n-1].
            // Evaluating mid-run would count part of a tied group as predicted positive and part
            // as predicted negative for one threshold, which no threshold can actually do, and
            // would report a J no real classifier attains. The run's first index is where the
            // suffix and the predicate agree.
            if (i > 0 && sortedExamples[i].Score == sortedExamples[i - 1].Score)
                continue;

            int predictedPositives = n - i;
            int falsePositives = predictedPositives - truePositives;

            double truePositiveRate = (double)truePositives / totalPositives;
            double falsePositiveRate = (double)falsePositives / totalNegatives;
            double j = truePositiveRate - falsePositiveRate;

            if (j >= bestJ)
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
