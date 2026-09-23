using LexiSharp.Core;

namespace LexiSharp.Hybrid;

/// <summary>
/// Blend strategy that multiplies each document's <i>CombSUM</i> score by the number of engines
/// that retrieved it — the classic <b>CombMNZ</b>.
/// </summary>
/// <remarks>
/// The intuition: a document found by several independent engines is more likely relevant than
/// one found by a single engine with the same summed score, so agreement is rewarded. It reuses
/// <see cref="CombSumResultMerger"/> for the normalized sum (same per-engine normalization and
/// non-positive/non-finite handling) and only scales the result by the hit count, then re-orders.
/// </remarks>
public sealed class CombMNZResultMerger : IResultMerger
{
    private readonly CombSumResultMerger _sum = new();

    /// <inheritdoc />
    public string Name => "CombMNZ";

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Merge(
        IReadOnlyList<IReadOnlyList<SearchResult>> perEngineResults,
        string query)
    {
        ArgumentNullException.ThrowIfNull(perEngineResults);

        var summed = _sum.Merge(perEngineResults, query);

        if (summed.Count == 0)
            return summed;

        var hitCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var engineResults in perEngineResults)
        {
            foreach (var result in engineResults)
            {
                if (!double.IsFinite(result.Score) || result.Score <= 0)
                    continue;

                hitCounts[result.DocumentId] = hitCounts.GetValueOrDefault(result.DocumentId) + 1;
            }
        }

        return summed
            .Select(result => result with { Score = result.Score * hitCounts[result.DocumentId] })
            .OrderByDescending(x => x.Score)
            .ToList();
    }
}
