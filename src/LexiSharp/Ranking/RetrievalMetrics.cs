using LexiSharp.Core;

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
    /// <remarks>
    /// This is the exponential convention. The linear one — the convention the standard evaluation
    /// tool uses, and therefore the one behind the published figures this repository is compared
    /// against — is the <see cref="NdcgGain.Linear"/> overload. The two differ on graded corpora
    /// only; see <see cref="NdcgGain"/>.
    /// </remarks>
    public static double NdcgAtK(IReadOnlyCollection<string> retrievedIds, IReadOnlyDictionary<string, double> gradedRelevance, int k) =>
        NdcgAtK(retrievedIds, gradedRelevance, k, NdcgGain.Exponential);

    /// <summary>
    /// nDCG@k with graded relevance under an explicit <see cref="NdcgGain"/> convention, with the
    /// ideal ranking taken over the best possible ordering of the available relevance levels.
    /// Documents missing from the map count as relevance 0.
    /// </summary>
    public static double NdcgAtK(
        IReadOnlyCollection<string> retrievedIds,
        IReadOnlyDictionary<string, double> gradedRelevance,
        int k,
        NdcgGain gain)
    {
        ArgumentNullException.ThrowIfNull(retrievedIds);
        ArgumentNullException.ThrowIfNull(gradedRelevance);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);

        if (gradedRelevance.Count == 0)
            return 0;

        if (gradedRelevance.Values.Any(level => double.IsNaN(level) || double.IsInfinity(level) || level < 0))
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

            if (graded.TryGetValue(id, out var level) && level > 0)
                dcg += GainOf(level, gain) / Math.Log2(rank + 1);

            rank++;
        }

        double idcg = IdealDcg(graded.Values.OrderByDescending(level => level), k, gain);

        return idcg == 0 ? 0 : dcg / idcg;
    }

    private static double GainOf(double relevanceLevel, NdcgGain gain) =>
        gain == NdcgGain.Linear ? relevanceLevel : Math.Pow(2, relevanceLevel) - 1;

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
    /// within the first <c>k</c> positions, normalized by the number of relevant documents — the
    /// <c>trec_eval</c> <c>map_cut</c> and <c>ir_measures</c> convention. A perfect top-k is
    /// therefore not 1 when more relevant documents sit beyond <c>k</c>, which is the intended
    /// penalty: the metric measures the whole relevant set, not only the slice it saw. Average
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

        return precisionSum / relevantIds.Count;
    }

    /// <summary>
    /// nDCG@k under score ties: the exact expectation over uniform random tie-breaking, and the exact
    /// best and worst case over every admissible ordering.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The plain <see cref="NdcgAtK(IReadOnlyCollection{string}, IReadOnlyDictionary{string, double}, int, NdcgGain)"/>
    /// answers "what did the ordering the engine happened to produce score", which is well defined only
    /// when the scores are distinct. When two documents share a score the engine has no order to report,
    /// and the metric is a property of the tie-break instead: Ma and Cheng (RecSys 2026) show a
    /// compressed score turning a tie-break into the ranker, and Guo et al. (FRAME 2026) show the same
    /// run scoring 0.85 or 0.17 depending on it.
    /// </para>
    /// <para>
    /// <b>The expectation is exact, not sampled.</b> Every document of a score class is equally likely
    /// to occupy any of that class's positions, so each position inside the page receives the class's
    /// mean gain and the expected discounted gain is closed-form — the same sum the reference
    /// implementation computes for one positive item, generalised to graded relevance. The bounds come
    /// from the rearrangement inequality: the best ordering pairs the largest gains with the largest
    /// discounts. No enumeration and no seed, so the figures are reproducible across machines.
    /// </para>
    /// <para>
    /// <b>Pass the full ranking, not the page.</b> A class that extends past the cut contributes fewer
    /// positions inside the page than it has documents, and only the documents past the cut reveal that.
    /// A list truncated at <c>k</c> makes every class look smaller than it is and silently narrows every
    /// interval to a point.
    /// </para>
    /// <para>
    /// Documents missing from <paramref name="gradedRelevance"/>, and ids repeated after their first
    /// occurrence, count as relevance 0 — the same conventions as the plain metric, so the two agree
    /// exactly when no scores tie.
    /// </para>
    /// </remarks>
    /// <param name="ranked">The ranked results, scores included, best first.</param>
    /// <param name="gradedRelevance">Relevance level per document id; missing ids are relevance 0.</param>
    /// <param name="k">Cutoff: only the first <paramref name="k"/> positions are scored.</param>
    public static TieAwareMetric TieAwareNdcgAtK(
        IReadOnlyList<SearchResult> ranked,
        IReadOnlyDictionary<string, double> gradedRelevance,
        int k) =>
        TieAwareNdcgAtK(ranked, gradedRelevance, k, NdcgGain.Exponential);

    /// <summary>
    /// nDCG@k under score ties, with an explicit <see cref="NdcgGain"/> convention.
    /// </summary>
    /// <inheritdoc cref="TieAwareNdcgAtK(IReadOnlyList{SearchResult}, IReadOnlyDictionary{string, double}, int)"/>
    public static TieAwareMetric TieAwareNdcgAtK(
        IReadOnlyList<SearchResult> ranked,
        IReadOnlyDictionary<string, double> gradedRelevance,
        int k,
        NdcgGain gain)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(gradedRelevance);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(k);

        if (gradedRelevance.Count == 0)
            return new TieAwareMetric(0, 0, 0);

        if (gradedRelevance.Values.Any(level => double.IsNaN(level) || double.IsInfinity(level) || level < 0))
            throw new ArgumentOutOfRangeException(nameof(gradedRelevance), "Relevance gains must be non-negative and finite.");

        // Deduplicate exactly as the plain metric does. A repeated id contributes at its first rank,
        // and its later score must not be allowed to open a second class.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ids = new List<string>(ranked.Count);
        var scores = new List<double>(ranked.Count);

        foreach (var result in ranked)
        {
            if (seen.Add(result.DocumentId))
            {
                ids.Add(result.DocumentId);
                scores.Add(result.Score);
            }
        }

        if (ids.Count == 0)
            return new TieAwareMetric(0, 0, 0);

        // A class is the set of documents the score cannot tell apart, with the positions they occupy.
        // A class may straddle the cut, which is what makes the page's membership uncertain.
        var positionsByScore = new Dictionary<double, List<int>>(ids.Count);
        var classOrder = new List<double>();

        for (int i = 0; i < scores.Count; i++)
        {
            if (!positionsByScore.TryGetValue(scores[i], out var positions))
            {
                positionsByScore[scores[i]] = positions = new List<int>();
                classOrder.Add(scores[i]);
            }

            positions.Add(i);
        }

        double expected = 0, minimum = 0, maximum = 0;

        foreach (double score in classOrder)
        {
            List<int> positions = positionsByScore[score];
            var weights = new List<double>(positions.Count);
            var gains = new List<double>(positions.Count);

            foreach (int position in positions)
            {
                gains.Add(GainOf(gradedRelevance.GetValueOrDefault(ids[position], 0), gain));

                if (position < k)
                    weights.Add(1.0 / Math.Log2(position + 2));
            }

            if (weights.Count == 0)
                continue;

            weights.Sort();
            weights.Reverse();
            gains.Sort();

            double gainSum = 0;
            foreach (double g in gains)
                gainSum += g;

            double weightSum = 0;
            foreach (double w in weights)
                weightSum += w;

            // Every document of the class is equally likely at every one of its positions, so an
            // inside position receives the class's mean gain in expectation.
            expected += weightSum * (gainSum / gains.Count);

            // Rearrangement inequality: largest gain to largest discount is the best case, smallest
            // to largest the worst.
            for (int i = 0; i < weights.Count; i++)
            {
                maximum += weights[i] * gains[gains.Count - 1 - i];
                minimum += weights[i] * gains[i];
            }
        }

        double idcg = IdealDcg(gradedRelevance.Values.OrderByDescending(level => level), k, gain);

        return idcg == 0 ? new TieAwareMetric(0, 0, 0) : new TieAwareMetric(expected / idcg, minimum / idcg, maximum / idcg);
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

    /// <summary>
    /// The ideal DCG over the best <c>k</c> documents of a ranking ordered by descending relevance.
    /// </summary>
    /// <remarks>
    /// Only the documents that can contribute occupy a rank. A document whose level is 0 adds nothing,
    /// and letting it advance the rank would shrink the ideal — which raises every nDCG computed against
    /// it, by a different amount per query. The ideal ranking is the one that puts the available relevant
    /// documents first, so a level of 0 is not in it at any position.
    /// <para>
    /// The input is expected ordered, which is what makes "the best k" well defined; the caller sorts.
    /// </para>
    /// </remarks>
    private static double IdealDcg(IEnumerable<double> orderedRelevance, int k, NdcgGain gain)
    {
        double idcg = 0;
        int rank = 1;

        foreach (var level in orderedRelevance)
        {
            if (rank > k)
                break;

            if (level > 0)
            {
                idcg += GainOf(level, gain) / Math.Log2(rank + 1);
                rank++;
            }
        }

        return idcg;
    }
}

/// <summary>
/// A metric's value over every ordering the scores leave admissible: the expectation under uniform
/// random tie-breaking, and the exact best and worst case.
/// </summary>
/// <param name="Expected">Mean value over the admissible orderings.</param>
/// <param name="Minimum">Value of the least favourable admissible ordering.</param>
/// <param name="Maximum">Value of the most favourable admissible ordering.</param>
/// <remarks>
/// <see cref="Minimum"/> and <see cref="Maximum"/> are the interval a single reported number hides,
/// and <see cref="IsDetermined"/> says whether there is anything to hide. A caller that reports
/// <see cref="Expected"/> without the width is reporting a mean over an arbitrary choice it did not
/// make — the practice the tie-aware protocol exists to end.
/// </remarks>
public readonly record struct TieAwareMetric(double Expected, double Minimum, double Maximum)
{
    /// <summary>How far the metric can move under tie-breaking alone.</summary>
    public double Width => Maximum - Minimum;

    /// <summary>Every admissible ordering agrees: no tie reached a position that is scored.</summary>
    // The definition, not a tolerance: the two are the extreme values the same ties can produce,
    // and "equal" is what "every admissible ordering agrees" means.
    public bool IsDetermined => Maximum == Minimum; // NOSONAR:S1244
}