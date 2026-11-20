using LexiSharp.Core;

namespace LexiSharp.Hybrid;

/// <summary>
/// Stretches each engine's scores across <c>[0, 1]</c>: subtract that engine's smallest positive
/// score, divide by the distance to its largest.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it buys.</b> Dividing by the maximum fixes the top of the scale and leaves the bottom where
/// the engine put it: an engine whose answers score <c>12.4</c> and whose second-best scores
/// <c>3.1</c> contributes <c>0.25</c> for that second-best, while an engine whose scores are
/// <c>12.4</c> and <c>12.3</c> contributes <c>0.99</c>. Neither is wrong, but the two are answers to
/// different questions, and a blend that averages them is comparing "how far below its own ceiling"
/// with "how far below its own ceiling". Min-max asks the comparable question instead: where in this
/// engine's own spread does the document sit.
/// </para>
/// <para>
/// <b>What it costs.</b> It is monotone within an engine but not across engines: a list whose scores
/// are tightly packed is stretched as hard as one with a wide range, so a small absolute difference
/// becomes a large normalized one. That is the trade — the spread is the signal — and it is why the
/// default stays <see cref="MaxScoreNormalizer"/> rather than this: the measurement decides, not the
/// argument.
/// </para>
/// <para>
/// <b>Degenerate lists.</b> A list where every score is equal has no spread to stretch; its documents
/// normalize to <c>1</c>, as they do under <see cref="MaxScoreNormalizer"/>, rather than all collapsing
/// to <c>0</c>. Zeros and negative scores are outside the measured range: score <c>0</c> is this
/// library's "not a match", so including it in the minimum would stretch every list down to a value
/// that means absence.
/// </para>
/// <para>
/// <b>The bottom of every list contributes nothing.</b> By construction the smallest positive score
/// normalizes to <c>0</c>, so a document that is last in every engine that returned it is dropped by
/// the merge's "a score of exactly 0 is not a match" rule. That is consistent — it is the worst
/// candidate of each list — but it is lossy in a way max normalization is not, where the worst
/// document still contributes <c>min/max</c> of a unit. On a two-engine hybrid it means a document
/// that only one engine returned is kept only if it was not that engine's last result.
/// </para>
/// </remarks>
public sealed class MinMaxScoreNormalizer : IScoreNormalizer
{
    /// <inheritdoc />
    public string Name => "minmax";

    /// <inheritdoc />
    public ScoreNormalization Measure(IReadOnlyList<SearchResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;

        for (int i = 0; i < results.Count; i++)
        {
            double score = results[i].Score;

            if (!double.IsFinite(score) || score <= 0)
                continue;

            if (score < min) min = score;
            if (score > max) max = score;
        }

        if (max == double.NegativeInfinity)
            return ScoreNormalization.None;

        // One distinct positive score: no spread. Dividing by zero would drop every document from the
        // blend, so the list degenerates to the max normalizer's answer — the top document carries 1.
        return max == min
            ? new ScoreNormalization(0, max)
            : new ScoreNormalization(min, max - min);
    }
}
