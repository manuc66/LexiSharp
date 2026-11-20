using LexiSharp.Core;

namespace LexiSharp.Hybrid;

/// <summary>
/// Divides each engine's scores by their sum, so each engine contributes one unit of mass in total
/// however many documents it returned.
/// </summary>
/// <remarks>
/// <para>
/// <b>The property the others do not have.</b> Max and min-max normalize a document against its own
/// engine's ceiling, so a document's contribution does not depend on how many candidates the engine
/// returned. Sum normalization does the opposite: it makes each engine a distribution over its
/// candidates, so an engine that returned 500 documents gives each of them less than an engine that
/// returned 5. That is the right reading when the lists are short and the scores are comparable
/// within the list — a reranker's top few — and the wrong one for a deep candidate pool, where the
/// mass is diluted by documents nobody will ever read.
/// </para>
/// <para>
/// It is the classic L1 normalization of score-based fusion, and it inherits the classic weakness:
/// the total depends on the pool depth the caller asked for, so two configurations that return
/// different pool sizes are not comparable under it. The pool is the search's <c>Limit</c>, so that
/// is a knob a caller can hold fixed; it is stated here because it is the first thing that changes
/// when someone tunes a result count.
/// </para>
/// </remarks>
public sealed class SumScoreNormalizer : IScoreNormalizer
{
    /// <inheritdoc />
    public string Name => "sum";

    /// <inheritdoc />
    public ScoreNormalization Measure(IReadOnlyList<SearchResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        double total = 0;

        for (int i = 0; i < results.Count; i++)
        {
            double score = results[i].Score;

            if (double.IsFinite(score) && score > 0)
                total += score;
        }

        return total > 0 ? new ScoreNormalization(0, total) : ScoreNormalization.None;
    }
}
