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
/// <item><description>An id present more than once in <c>retrievedIds</c> only counts once (its first/earliest
/// occurrence), so nDCG never exceeds 1 for a consistent ranking.</description></item>
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
        var seen = new HashSet<string>(StringComparer.Ordinal);
        double dcg = 0;
        int rank = 1;

        foreach (var id in retrievedIds)
        {
            if (rank > k)
                break;

            if (!seen.Add(id))
                continue;

            if (relevant.Contains(id))
                dcg += 1.0 / Math.Log2(rank + 1);

            rank++;
        }

        double idcg = IdealDcg(Math.Min(k, relevantIds.Count));

        return idcg == 0 ? 0 : dcg / idcg;
    }

    /// <summary>
    /// nDCG@k with <b>graded</b> relevance: gains follow the exponential convention
    /// <c>2^rel − 1</c>, and the ideal ranking is the best possible ordering of the available
    /// relevance levels. Documents missing from the map count as relevance 0.
    /// </summary>
    public static double NdcgAtK(IReadOnlyCollection<string> retrievedIds, IReadOnlyDictionary<string, double> gradedRelevance, int k)
    {
        ArgumentNullException.ThrowIfNull(retrievedIds);
        ArgumentNullException.ThrowIfNull(gradedRelevance);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);

        if (gradedRelevance.Count == 0)
            return 0;

        if (gradedRelevance.Values.Any(gain => double.IsNaN(gain) || double.IsInfinity(gain) || gain < 0))
            throw new ArgumentOutOfRangeException(nameof(gradedRelevance), "Relevance gains must be non-negative and finite.");

        var graded = new Dictionary<string, double>(gradedRelevance, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        double dcg = 0;
        int rank = 1;

        foreach (var id in retrievedIds)
        {
            if (rank > k)
                break;

            if (!seen.Add(id))
                continue;

            if (graded.TryGetValue(id, out var gain) && gain > 0)
                dcg += (Math.Pow(2, gain) - 1) / Math.Log2(rank + 1);

            rank++;
        }

        double idcg = IdealDcg(graded.Values.OrderByDescending(gain => gain), k);

        return idcg == 0 ? 0 : dcg / idcg;
    }

    /// <summary>
    /// Reciprocal rank at <c>k</c>: <c>1 / rank</c> of the first relevant document within the
    /// first <c>k</c> positions, <c>0</c> when none appears. Average this over a validation set
    /// to get MRR (Mean Reciprocal Rank).
    /// </summary>
    public static double ReciprocalRankAtK(IReadOnlyCollection<string> retrievedIds, IReadOnlyCollection<string> relevantIds, int k)
    {
        ArgumentNullException.ThrowIfNull(retrievedIds);
        ArgumentNullException.ThrowIfNull(relevantIds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);

        if (relevantIds.Count == 0)
            return 0;

        var relevant = new HashSet<string>(relevantIds, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int rank = 1;

        foreach (var id in retrievedIds)
        {
            if (rank > k)
                break;

            if (!seen.Add(id))
                continue;

            if (relevant.Contains(id))
                return 1.0 / rank;

            rank++;
        }

        return 0;
    }

    /// <summary>
    /// Average precision at <c>k</c>: the mean of the precisions observed at every relevant hit
    /// within the first <c>k</c> positions, normalized by <c>min(|relevant|, k)</c>. Average
    /// this over a validation set to get MAP (Mean Average Precision).
    /// </summary>
    public static double AveragePrecisionAtK(IReadOnlyCollection<string> retrievedIds, IReadOnlyCollection<string> relevantIds, int k)
    {
        ArgumentNullException.ThrowIfNull(retrievedIds);
        ArgumentNullException.ThrowIfNull(relevantIds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);

        if (relevantIds.Count == 0)
            return 0;

        var relevant = new HashSet<string>(relevantIds, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int matches = 0;
        double precisionSum = 0;
        int rank = 1;

        foreach (var id in retrievedIds)
        {
            if (rank > k)
                break;

            if (!seen.Add(id))
                continue;

            if (relevant.Contains(id))
            {
                matches++;
                precisionSum += matches / (double)rank;
            }

            rank++;
        }

        return precisionSum / Math.Min(relevantIds.Count, k);
    }

    private static int CountMatches(IReadOnlyCollection<string> retrievedIds, IReadOnlyCollection<string> relevantIds, int k)
    {
        var relevant = new HashSet<string>(relevantIds, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int matches = 0;
        int rank = 0;

        foreach (var id in retrievedIds)
        {
            if (rank >= k)
                break;

            if (!seen.Add(id))
                continue;

            rank++;

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

    private static double IdealDcg(IEnumerable<double> orderedGains, int k)
    {
        double idcg = 0;
        int rank = 1;

        foreach (var gain in orderedGains)
        {
            if (rank > k)
                break;

            if (gain > 0)
                idcg += (Math.Pow(2, gain) - 1) / Math.Log2(rank + 1);

            rank++;
        }

        return idcg;
    }
}