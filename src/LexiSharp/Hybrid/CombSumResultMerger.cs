using LexiSharp.Core;

namespace LexiSharp.Hybrid;

/// <summary>
/// Blend strategy that <b>sums</b> the normalized per-engine scores of every document — the
/// classic <i>CombSUM</i> of score-based fusion.
/// </summary>
/// <remarks>
/// Each engine's scores are put on a comparable scale by an <see cref="IScoreNormalizer"/> — by
/// default divided by that engine's maximum positive score, the same normalization as
/// <see cref="WeightedScoreResultMerger"/> — so engines on unrelated scales (BM25 vs cosine)
/// contribute comparably. A document returned by several engines accumulates one normalized
/// contribution per engine; non-positive and non-finite scores are ignored. Unlike rank-based fusion
/// (<see cref="ReciprocalRankFusionMerger"/>), a low rank with a dominant score still counts
/// fully — use CombSUM when the scores, not just the order, are meaningful.
/// </remarks>
public sealed class CombSumResultMerger : IResultMerger
{
    private readonly IScoreNormalizer _normalizer;

    /// <summary>Creates a CombSUM merger with the default normalizer.</summary>
    public CombSumResultMerger()
        : this(new MaxScoreNormalizer())
    {
    }

    /// <summary>Creates a CombSUM merger whose per-engine scores go through <paramref name="normalizer"/>.</summary>
    /// <param name="normalizer">How each engine's scores become comparable.</param>
    /// <exception cref="ArgumentNullException"><paramref name="normalizer"/> is null.</exception>
    public CombSumResultMerger(IScoreNormalizer normalizer)
    {
        ArgumentNullException.ThrowIfNull(normalizer);
        _normalizer = normalizer;
    }

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
            ScoreNormalization normalization = _normalizer.Measure(results);

            foreach (var result in results)
            {
                if (!double.IsFinite(result.Score) || result.Score <= 0)
                    continue;

                double normalized = normalization.Apply(result.Score);

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
}
