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

        double[] weights = ResolveWeights(engineCount);
        double[] normalization = ComputeNormalization(perEngineResults, engineCount);

        var merged = new Dictionary<string, (SearchDocument Document, double Score)>(StringComparer.Ordinal);

        for (int i = 0; i < engineCount; i++)
            AccumulateEngine(merged, perEngineResults[i], weights[i], normalization[i]);

        return Rank(merged);
    }

    private double[] ResolveWeights(int engineCount)
    {
        double[] weights = _weights.Length == 1 && engineCount > 1
            ? Enumerable.Repeat(_weights[0], engineCount).ToArray()
            : _weights;

        if (weights.Length != engineCount)
            throw new InvalidOperationException(
                $"WeightedScoreResultMerger expects {engineCount} weights but got {weights.Length}.");

        return weights;
    }

    // Per-engine normalization factors (max positive score) so scales become comparable.
    private static double[] ComputeNormalization(
        IReadOnlyList<IReadOnlyList<SearchResult>> perEngineResults,
        int engineCount)
    {
        var normalization = new double[engineCount];

        for (int i = 0; i < engineCount; i++)
        {
            normalization[i] = perEngineResults[i]
                .Where(r => double.IsFinite(r.Score))
                .Select(r => r.Score)
                .DefaultIfEmpty(0)
                .Max();
        }

        return normalization;
    }

    private static void AccumulateEngine(
        Dictionary<string, (SearchDocument Document, double Score)> merged,
        IReadOnlyList<SearchResult> results,
        double weight,
        double divisor)
    {
        foreach (var result in results)
        {
            // A per-engine score that is NaN or infinite never poisons the blend.
            if (!double.IsFinite(result.Score))
                continue;

            double normalized = divisor > 0 ? result.Score / divisor : 0;
            double contribution = weight * normalized;

            if (merged.TryGetValue(result.DocumentId, out var existing))
                merged[result.DocumentId] = (existing.Document, existing.Score + contribution);
            else
                merged[result.DocumentId] = (result.Document, contribution);
        }
    }

    private static IReadOnlyList<SearchResult> Rank(
        Dictionary<string, (SearchDocument Document, double Score)> merged) =>
        merged
            .Where(x => x.Value.Score > 0)
            .Select(x => new SearchResult(x.Key, x.Value.Score, x.Value.Document))
            .OrderByDescending(x => x.Score)
            .ToList();
}