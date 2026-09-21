using LexiSharp.Core;

namespace LexiSharp.Hybrid;

/// <summary>
/// Blends the native per-engine scores instead of re-ranking. Every engine's scores are
/// normalized to <c>[0, 1]</c> (divided by that engine's maximum positive score), then a
/// weighted average is computed across the engines that returned the document.
/// </summary>
/// <remarks>
/// Use this when the source rankings are trusted as-is (e.g. PostgreSQL <c>ts_rank</c>) and you
/// only need a sensible global ordering, or when the engines sit on deliberately different
/// semantics (lexical vs vector similarity). Weights default to <c>1</c> for every engine.
/// A per-engine score that is NaN or infinite never poisons the blend: it is dropped from that
/// engine's normalization and from every document it would have contributed to.
/// </remarks>
public sealed class WeightedScoreResultMerger : IResultMerger
{
    private readonly double[] _weights;

    /// <param name="weights">
    /// One non-negative weight per engine, in the same order as the engines passed to the
    /// <see cref="HybridTextSearchEngine"/>. Defaults to <c>1</c> for every engine.
    /// </param>
    public WeightedScoreResultMerger(params double[] weights)
    {
        if (weights.Any(w => double.IsNaN(w) || double.IsInfinity(w) || w < 0))
            throw new ArgumentOutOfRangeException(nameof(weights), "Weights must be non-negative and finite.");

        _weights = weights.Length > 0 ? weights : new[] { 1.0 };
    }

    /// <inheritdoc />
    public string Name => "WeightedScore";

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

        double[] weights = _weights.Length == 1 && engineCount > 1
            ? Enumerable.Repeat(_weights[0], engineCount).ToArray()
            : _weights;

        if (weights.Length != engineCount)
            throw new InvalidOperationException(
                $"WeightedScoreResultMerger expects {engineCount} weights but got {weights.Length}.");

        // Per-engine normalization factors (max positive score) so scales become comparable.
        var normalization = new double[engineCount];

        for (int i = 0; i < engineCount; i++)
        {
            double max = 0;

            foreach (var result in perEngineResults[i])
            {
                if (double.IsNaN(result.Score) || double.IsInfinity(result.Score))
                    continue;

                max = Math.Max(max, result.Score);
            }

            normalization[i] = max;
        }

        var merged = new Dictionary<string, (SearchDocument Document, double Score)>(StringComparer.Ordinal);

        for (int i = 0; i < engineCount; i++)
        {
            double divisor = normalization[i] > 0 ? normalization[i] : 0;

            foreach (var result in perEngineResults[i])
            {
                if (double.IsNaN(result.Score) || double.IsInfinity(result.Score))
                    continue;

                double normalized = divisor > 0 ? result.Score / divisor : 0;

                if (merged.TryGetValue(result.DocumentId, out var existing))
                {
                    merged[result.DocumentId] = (
                        existing.Document,
                        existing.Score + weights[i] * normalized);
                }
                else
                {
                    merged[result.DocumentId] = (result.Document, weights[i] * normalized);
                }
            }
        }

        return merged
            .Where(x => x.Value.Score > 0)
            .Select(x => new SearchResult(x.Key, x.Value.Score, x.Value.Document))
            .OrderByDescending(x => x.Score)
            .ToList();
    }
}