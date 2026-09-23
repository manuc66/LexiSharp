using LexiSharp.Core;

namespace LexiSharp.Hybrid;

/// <summary>
/// Blend strategy that <b>sums</b> the normalized per-engine scores of every document — the
/// classic <i>CombSUM</i> of score-based fusion.
/// </summary>
/// <remarks>
/// Each engine's scores are normalized to <c>[0, 1]</c> by dividing them by that engine's
/// maximum positive score (the same normalization as <see cref="WeightedScoreResultMerger"/>,
/// weights aside), so engines on unrelated scales (BM25 vs cosine) contribute comparably.
/// A document returned by several engines accumulates one normalized contribution per engine;
/// non-positive and non-finite scores are ignored. Unlike rank-based fusion
/// (<see cref="ReciprocalRankFusionMerger"/>), a low rank with a dominant score still counts
/// fully — use CombSUM when the scores, not just the order, are meaningful.
/// </remarks>
public sealed class CombSumResultMerger : IResultMerger
{
    /// <inheritdoc />
    public string Name => "CombSum";

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Merge(
        IReadOnlyList<IReadOnlyList<SearchResult>> perEngineResults,
        string query)
    {
        ArgumentNullException.ThrowIfNull(perEngineResults);
        _ = query;

        int engineCount = perEngineResults.Count;

        if (engineCount == 0)
            return Array.Empty<SearchResult>();

        var merged = new Dictionary<string, (SearchDocument Document, double Score)>(StringComparer.Ordinal);

        for (int i = 0; i < engineCount; i++)
        {
            var results = perEngineResults[i];
            double divisor = MaxPositiveScore(results);

            foreach (var result in results)
            {
                if (!double.IsFinite(result.Score) || result.Score <= 0)
                    continue;

                // divisor > 0 is guaranteed: a positive score implies a positive maximum.
                double normalized = result.Score / divisor;

                if (merged.TryGetValue(result.DocumentId, out var existing))
                    merged[result.DocumentId] = (existing.Document, existing.Score + normalized);
                else
                    merged[result.DocumentId] = (result.Document, normalized);
            }
        }

        return merged
            .Where(x => x.Value.Score > 0)
            .Select(x => new SearchResult(x.Key, x.Value.Score, x.Value.Document))
            .OrderByDescending(x => x.Score)
            .ToList();
    }

    private static double MaxPositiveScore(IReadOnlyList<SearchResult> results)
    {
        double max = 0;

        for (int i = 0; i < results.Count; i++)
        {
            double score = results[i].Score;

            if (double.IsFinite(score) && score > max)
                max = score;
        }

        return max;
    }
}
