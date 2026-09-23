using LexiSharp.Core;

namespace LexiSharp.Ranking;

/// <summary>
/// How an ordered result set's raw scores are mapped into a calibrated per-result confidence
/// in [0,1].
/// </summary>
public enum ScoreConfidenceMethod
{
    /// <summary>
    /// Confidence from the relative gap to the next result: every result scores
    /// <c>1 - score_next / score_current</c> against its follower, the last one is <c>0</c>.
    /// A unanimous clear winner approaches <c>1</c>, a near-tie approaches <c>0</c>.
    /// Scale-invariant and cheap, but blind to the absolute quality of the match — a flat
    /// ranking of high scores still looks unsure.
    /// </summary>
    WinnerMargin,

    /// <summary>
    /// Confidence from each score's position in the result set's own distribution (z-score),
    /// squashed through the logistic function. An all-tied ranking collapses to the neutral
    /// <c>0.5</c> whatever its scale; a set with one clear standout lifts the leader and pushes
    /// the tail down.
    /// </summary>
    ZScore,
}

/// <summary>
/// Maps an ordered result set's raw scores into calibrated confidences in [0,1] — the
/// "is this really the answer?" gauge that raw lexical scores (BM25, TF-IDF, ...) do not
/// carry on their own. Feed it the engine's <see cref="SearchResult"/> list (or its scores)
/// after each search, then compare the top result's confidence to an application-level
/// threshold such as a <c>minConfidence</c> gate.
/// </summary>
public static class ScoreConfidence
{
    /// <summary>
    /// Computes one confidence per result, in ranking order, from the served results' scores.
    /// </summary>
    public static IReadOnlyList<double> Compute(
        IReadOnlyList<SearchResult> results,
        ScoreConfidenceMethod method = ScoreConfidenceMethod.WinnerMargin)
    {
        ArgumentNullException.ThrowIfNull(results);

        var scores = new double[results.Count];

        for (int i = 0; i < results.Count; i++)
            scores[i] = results[i].Score;

        return Compute(scores, method);
    }

    /// <summary>
    /// Computes one confidence per score, in the given order, which must be the ranking order.
    /// </summary>
    /// <param name="scores">The raw scores, best first.</param>
    /// <param name="method">The normalization strategy.</param>
    public static IReadOnlyList<double> Compute(
        IReadOnlyList<double> scores,
        ScoreConfidenceMethod method = ScoreConfidenceMethod.WinnerMargin)
    {
        ArgumentNullException.ThrowIfNull(scores);

        var confidence = new double[scores.Count];

        if (scores.Count == 1)
        {
            confidence[0] = scores[0] > 0 ? 1 : 0;
            return confidence;
        }

        switch (method)
        {
            case ScoreConfidenceMethod.ZScore:
                ComputeZScore(scores, confidence);
                break;
            default:
                ComputeWinnerMargin(scores, confidence);
                break;
        }

        return confidence;
    }

    /// <summary>
    /// Confidence of the best result — the one to compare against a
    /// <c>minConfidence</c> gate. Returns <c>0</c> for an empty result set.
    /// </summary>
    public static double TopConfidence(
        IReadOnlyList<double> scores,
        ScoreConfidenceMethod method = ScoreConfidenceMethod.WinnerMargin)
    {
        var confidence = Compute(scores, method);
        return confidence.Count == 0 ? 0 : confidence[0];
    }

    /// <summary>
    /// Confidence of the best result — the one to compare against a
    /// <c>minConfidence</c> gate. Returns <c>0</c> for an empty result set.
    /// </summary>
    public static double TopConfidence(
        IReadOnlyList<SearchResult> results,
        ScoreConfidenceMethod method = ScoreConfidenceMethod.WinnerMargin)
    {
        var confidence = Compute(results, method);
        return confidence.Count == 0 ? 0 : confidence[0];
    }

    /// <summary>
    /// A score of exactly 0 means "not a match" by engine convention: such results — and any
    /// non-finite score a caller might pass — must not look confident.
    /// </summary>
    private static bool IsGenuineScore(double score) => score > 0 && !double.IsNaN(score) && !double.IsInfinity(score);

    private static void ComputeWinnerMargin(IReadOnlyList<double> scores, double[] confidence)
    {
        for (int i = 0; i < scores.Count - 1; i++)
        {
            double current = scores[i];

            if (!IsGenuineScore(current))
                continue;

            double next = scores[i + 1];

            if (!IsGenuineScore(next))
            {
                confidence[i] = 1;
                continue;
            }

            double value = 1 - next / current;

            if (value < 0)
                continue;

            confidence[i] = value > 1 ? 1 : value;
        }

        // The last result has no follower to beat: by convention it is the least confident.
    }

    private static void ComputeZScore(IReadOnlyList<double> scores, double[] confidence)
    {
        if (scores.Count == 0)
            return;

        double mean = 0;

        for (int i = 0; i < scores.Count; i++)
            mean += scores[i];

        mean /= scores.Count;

        double variance = 0;

        for (int i = 0; i < scores.Count; i++)
        {
            double deviation = scores[i] - mean;
            variance += deviation * deviation;
        }

        variance /= scores.Count;
        double standardDeviation = Math.Sqrt(variance);

        // A perfectly flat ranking carries no usable spread: every result is as plausible as
        // the next, so each gets the neutral 0.5 — never zero, never one.
        if (!(standardDeviation > 0) || double.IsNaN(standardDeviation))
        {
            Array.Fill(confidence, 0.5);
            return;
        }

        for (int i = 0; i < scores.Count; i++)
        {
            double zScore = (scores[i] - mean) / standardDeviation;
            confidence[i] = 1 / (1 + Math.Exp(-zScore));
        }
    }
}