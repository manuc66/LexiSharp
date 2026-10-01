using LexiSharp.Core;

namespace LexiSharp.Ranking;

/// <summary>
/// The score post-processing a system that writes its scores down applies to them, and nothing else
/// needs it.
/// </summary>
/// <remarks>
/// Kept apart from the scorers because it is not scoring. It runs once over the page being returned, in
/// rank order, and it reads the score of the hit above the one it is adjusting — so it has no meaning
/// per document and cannot live next to the arithmetic that produces one.
/// </remarks>
internal static class ScoreRoundingStep
{
    /// <summary>
    /// The grid scores are rounded onto: ten thousandths. Also the width of a group of scores counted as
    /// tied, which is the same number for the same reason.
    /// </summary>
    private const int Decimals = 4;

    /// <summary>How far down the grid is rounded, and how far apart two scores may be and still tie.</summary>
    private const float Unit = 1e-4f;

    /// <summary>The distance each successive member of a tied group is moved down by.</summary>
    private const float Step = 1e-6f;

    /// <summary>
    /// Rewrites <paramref name="results"/> in place, in rank order, and returns the same array.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three steps, in this order, and the last two read what the previous one wrote:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// Round the score onto the ten-thousandth grid, half away from zero. Not half to even, which is
    /// what .NET does by default: on a boundary the two disagree, and a boundary is exactly where a
    /// comparison against another implementation is won or lost.
    /// </description></item>
    /// <item><description>
    /// Narrow it to single precision. The system being reproduced stores its scores in one, so this is
    /// not a choice here but a transcription: a score kept at double precision differs from it by more
    /// than it will ever be compared at, and the difference is invisible in the printed digits.
    /// </description></item>
    /// <item><description>
    /// Walk down each run of scores no more than a ten-thousandth apart, by one millionth per position
    /// within the run, so a group the arithmetic scored identically comes out strictly ordered. A wider
    /// gap ends the run and the next position starts a new one.
    /// </description></item>
    /// </list>
    /// <para>
    /// The comparison reads the previous score <i>after</i> it has been adjusted, which is what makes a
    /// run of three come out evenly spaced rather than only the first two being measured against each
    /// other. All three steps are in single precision because that is the precision they happen in over
    /// there, and a double would put the adjusted scores on a different grid.
    /// </para>
    /// </remarks>
    public static SearchResult[] Apply(SearchResult[] results)
    {
        double scale = Math.Pow(10, Decimals);
        int ties = 0;
        float previous = 0;

        for (int i = 0; i < results.Length; i++)
        {
            var result = results[i];
            var score = (float)(Math.Floor(result.Score * scale + 0.5) / scale);

            if (i == 0)
            {
                ties = 0;
            }
            else if (previous - score <= Unit)
            {
                ties++;
                score -= Step * ties;
            }
            else
            {
                ties = 0;
            }

            previous = score;

            if ((double)score != result.Score)
                results[i] = result with { Score = score };
        }

        return results;
    }
}