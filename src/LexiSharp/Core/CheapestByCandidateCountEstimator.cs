namespace LexiSharp.Core;

/// <summary>
/// Picks the engine with the smallest <see cref="IQueryCostProbe.EstimateCandidateCount"/>.
/// Engines that do not implement <see cref="IQueryCostProbe"/> cannot be costed and are only
/// used when no probed engine exists (then the first engine is chosen). Ties keep the earliest
/// engine — the order given to <see cref="RoutedSearchEngine"/> is the tie-breaker.
/// </summary>
public sealed class CheapestByCandidateCountEstimator : IQueryCostEstimator
{
    /// <summary>A shared, stateless instance (the estimator holds no configuration).</summary>
    public static CheapestByCandidateCountEstimator Instance { get; } = new();

    /// <inheritdoc />
    public int Select(IReadOnlyList<RoutedEngine> engines, ReadOnlySpan<char> query, SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(engines);
        ArgumentNullException.ThrowIfNull(options);

        int best = -1;
        long bestCost = long.MaxValue;

        for (int i = 0; i < engines.Count; i++)
        {
            if (engines[i].Engine is not IQueryCostProbe probe)
                continue;

            long cost = probe.EstimateCandidateCount(query, options);

            if (cost < 0)
                cost = 0;

            // Strictly less: an equal cost keeps the earlier engine.
            if (best < 0 || cost < bestCost)
            {
                best = i;
                bestCost = cost;
            }
        }

        // No engine can be costed: fall back to the first (probed engines are preferred).
        return best < 0 ? 0 : best;
    }
}
