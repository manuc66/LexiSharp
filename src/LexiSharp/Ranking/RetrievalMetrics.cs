namespace LexiSharp.Ranking;

/// <summary>
/// Standard top-<c>k</c> retrieval metrics over a ranked list of document ids, used to
/// evaluate (and tune) ranking quality against a validation set.
/// </summary>
/// <remarks>
/// Conventions:
/// <list type="bullet">
/// <item><description><c>retrievedIds</c> is the ranked list returned by an engine (best first), already truncated at <c>k</c>.</description></item>
/// <item><description>Precision counts unfilled result slots as misses: <c>P@k = |retrieved ∩ relevant| / k</c>.</description></item>
/// <item><description>Queries with an empty relevant set score 0 on every metric; callers that average over a
/// validation set typically skip them (as <see cref="Bm25ParameterTuner"/> does).</description></item>
/// </list>
/// </remarks>
public static class RetrievalMetrics
{
    /// <summary>Precision@k: the fraction of the first <c>k</c> retrieved documents that are relevant.</summary>
    public static double PrecisionAtK(IReadOnlyCollection<string> retrievedIds, IReadOnlyCollection<string> relevantIds, int k)
    {
        ArgumentNullException.ThrowIfNull(retrievedIds);
        ArgumentNullException.ThrowIfNull(relevantIds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);

        return CountMatches(retrievedIds, relevantIds, k) / (double)k;
    }

    /// <summary>Recall@k: the fraction of relevant documents retrieved within the first <c>k</c> positions.</summary>
    public static double RecallAtK(IReadOnlyCollection<string> retrievedIds, IReadOnlyCollection<string> relevantIds, int k)
    {
        ArgumentNullException.ThrowIfNull(retrievedIds);
        ArgumentNullException.ThrowIfNull(relevantIds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);

        if (relevantIds.Count == 0)
            return 0;

        return CountMatches(retrievedIds, relevantIds, k) / (double)relevantIds.Count;
    }

    /// <summary>
    /// F1@k: harmonic mean of precision and recall at <c>k</c>; 0 when both are 0.
    /// </summary>
    public static double F1AtK(IReadOnlyCollection<string> retrievedIds, IReadOnlyCollection<string> relevantIds, int k)
    {
        double precision = PrecisionAtK(retrievedIds, relevantIds, k);
        double recall = RecallAtK(retrievedIds, relevantIds, k);

        return precision + recall == 0 ? 0 : 2.0 * precision * recall / (precision + recall);
    }

    /// <summary>
    /// nDCG@k with binary relevance: the discounted cumulative gain of the retrieved ranking
    /// divided by the ideal gain of a perfectly ordered list.
    /// </summary>
    public static double NdcgAtK(IReadOnlyCollection<string> retrievedIds, IReadOnlyCollection<string> relevantIds, int k)
    {
        ArgumentNullException.ThrowIfNull(retrievedIds);
        ArgumentNullException.ThrowIfNull(relevantIds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);

        if (relevantIds.Count == 0)
            return 0;

        var relevant = new HashSet<string>(relevantIds, StringComparer.Ordinal);

        double dcg = 0;
        int depth = Math.Min(k, retrievedIds.Count);

        for (int rank = 1; rank <= depth; rank++)
        {
            if (relevant.Contains(retrievedIds.ElementAt(rank - 1)))
                dcg += 1.0 / Math.Log2(rank + 1);
        }

        double idcg = IdealDcg(Math.Min(k, relevantIds.Count));

        return idcg == 0 ? 0 : dcg / idcg;
    }

    private static int CountMatches(IReadOnlyCollection<string> retrievedIds, IReadOnlyCollection<string> relevantIds, int k)
    {
        var relevant = new HashSet<string>(relevantIds, StringComparer.Ordinal);
        int matches = 0;

        foreach (var id in retrievedIds.Take(k))
        {
            if (relevant.Contains(id))
                matches++;
        }

        return matches;
    }

    private static double IdealDcg(int relevantCount)
    {
        double idcg = 0;

        for (int rank = 1; rank <= relevantCount; rank++)
            idcg += 1.0 / Math.Log2(rank + 1);

        return idcg;
    }
}