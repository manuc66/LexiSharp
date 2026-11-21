using LexiSharp.Core;

namespace LexiSharp.Hybrid;

/// <summary>
/// Divides each engine's scores by that engine's largest finite score — the mapping both score
/// mergers applied before <see cref="IScoreNormalizer"/> existed, and the default.
/// </summary>
/// <remarks>
/// <para>
/// It is the cheapest normalizer and the one with the weakest property: the best document of every
/// engine becomes <c>1</c>, so an engine contributes a full unit whatever its scores mean. That is
/// exactly right when the scores are comparable within an engine and the engines are trusted
/// equally. What it costs is that the unit is set by a single document: every other score is read as
/// a fraction of the best one, so an engine whose top two scores are <c>10</c> and <c>1</c> contributes
/// <c>0.1</c> for a document another engine would call nearly its best. It is not alone in that —
/// <see cref="MinMaxScoreNormalizer"/> is anchored at both ends instead of one — and the measurement
/// on the hybrid rows is what decides between them, not the argument.
/// </para>
/// <para>
/// It measures over the finite scores, negative ones included, because that is the divisor
/// <see cref="WeightedScoreResultMerger"/> computed before this type existed: keeping it here rather
/// than "fixing" it silently is what makes the default a no-op rather than a ranking change.
/// </para>
/// <para>
/// <b>One case does change, and it is out of contract.</b> A list whose every score is negative has
/// no positive divisor; before the seam, the merge divided by its negative maximum and turned those
/// scores into positive contributions. The mapping here refuses a non-positive scale, so such a list
/// contributes nothing. Engines in this library do not return negative scores — <c>0</c> is the
/// "not a match" convention and higher is better — so the change is a defect being removed from a
/// path no built-in pipeline reaches, not a ranking change.
/// </para>
/// </remarks>
public sealed class MaxScoreNormalizer : IScoreNormalizer
{
    /// <inheritdoc />
    public string Name => "max";

    /// <inheritdoc />
    public ScoreNormalization Measure(IReadOnlyList<SearchResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        double max = double.NegativeInfinity;

        for (int i = 0; i < results.Count; i++)
        {
            double score = results[i].Score;

            if (double.IsFinite(score) && score > max)
                max = score;
        }

        // Exact on purpose: a sentinel for "no finite score was seen", not a comparison between
        // two computed values. A range would have to pick an epsilon for a value that is an
        // identity, and the one that matters is the infinity itself.
        return max == double.NegativeInfinity ? ScoreNormalization.None : new ScoreNormalization(0, max); // NOSONAR:S1244
    }
}
