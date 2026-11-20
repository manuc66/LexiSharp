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

        return max == double.NegativeInfinity ? ScoreNormalization.None : new ScoreNormalization(0, max);
    }
}
