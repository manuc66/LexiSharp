using LexiSharp.Core;

namespace LexiSharp.Hybrid;

/// <summary>
/// Merges ranked lists with Reciprocal Rank Fusion: each document accumulates the reciprocal
/// of its rank (plus a constant <c>k</c>) across every engine that returned it.
/// </summary>
/// <remarks>
/// <c>RRF(d) = Σ_i w_i / (k + rank_i(d))</c>, where <c>rank_i(d)</c> is the 1-based position of
/// <c>d</c> in engine <c>i</c>'s result list and <c>w_i</c> is the engine weight (default <c>1</c>).
/// <para>
/// Because only ranks are used, RRF needs <b>no score normalization</b>: it is the safest bridge
/// between engines whose scores are not comparable — PostgreSQL <c>ts_rank_cd</c>, in-memory
/// BM25/TF-IDF, and later vector similarity (cosine) or any ANN store. Documents echoed by
/// several engines are naturally boosted, which is the behaviour you want when the same
/// document lives in both a hot in-memory index and a cold persistent one.
/// </para>
/// <para>
/// <c>k</c> (default <c>60</c>) is the standard constant from Cormack et al.; lower values give
/// more weight to the top of each list. When a document appears twice inside the same engine's
/// list, only its best rank is used.
/// </para>
/// </remarks>
public sealed class ReciprocalRankFusionMerger : IResultMerger
{
    private readonly double _k;
    private readonly double[] _weights;

    /// <param name="k">Rank offset constant, typically 60.</param>
    /// <param name="weights">
    /// One non-negative weight per engine, in the same order as the engines passed to the
    /// <see cref="HybridTextSearchEngine"/>. Defaults to <c>1</c> for every engine.
    /// </param>
    public ReciprocalRankFusionMerger(
        double k = 60,
        params double[] weights)
    {
        if (double.IsNaN(k) || double.IsInfinity(k) || k <= 0)
            throw new ArgumentOutOfRangeException(nameof(k), k, "k must be positive and finite.");

        if (weights.Any(w => double.IsNaN(w) || double.IsInfinity(w) || w < 0))
            throw new ArgumentOutOfRangeException(nameof(weights), "Weights must be non-negative and finite.");

        _k = k;
        _weights = weights.Length > 0 ? weights : new[] { 1.0 };
    }

    /// <inheritdoc />
    public string Name => "ReciprocalRankFusion";

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
                $"ReciprocalRankFusionMerger expects {engineCount} weights but got {weights.Length}.");

        // Accumulate 1/(k + rank) per document, using each document's best rank per engine.
        var fusion = new Dictionary<string, (SearchDocument Document, double Score)>(StringComparer.Ordinal);

        for (int i = 0; i < engineCount; i++)
        {
            if (weights[i] == 0)
                continue;

            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (int rankIndex = 0; rankIndex < perEngineResults[i].Count; rankIndex++)
            {
                var result = perEngineResults[i][rankIndex];

                if (!seen.Add(result.DocumentId))
                    continue;

                double contribution = weights[i] / (_k + rankIndex + 1);

                if (fusion.TryGetValue(result.DocumentId, out var existing))
                {
                    fusion[result.DocumentId] = (existing.Document, existing.Score + contribution);
                }
                else
                {
                    fusion[result.DocumentId] = (result.Document, contribution);
                }
            }
        }

        return fusion
            .Where(x => x.Value.Score > 0)
            .Select(x => new SearchResult(x.Key, x.Value.Score, x.Value.Document))
            .OrderByDescending(x => x.Score)
            .ToList();
    }
}