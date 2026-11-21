using LexiSharp.Core;

namespace LexiSharp.Hybrid;

/// <summary>
/// Merges per-engine results by normalizing each engine's scores by its query upper bound
/// and combining them with the naive Bayes rule — the same combination as
/// <see cref="BayesianFusionMerger"/>, but without needing a trained calibrator.
/// </summary>
/// <remarks>
/// <para>
/// Where <see cref="BayesianFusionMerger"/> needs a <see cref="LexiSharp.Ranking.CalibratedScoreConfidence"/>
/// fitted on labelled data, this merger uses the scorer's own theoretical upper bound
/// (<see cref="IScoreBoundScorer"/>) to convert raw scores to fractions of what the query
/// could have scored. The combination rule is the same naive Bayes:
/// </para>
/// <code>
/// P = (p1 * p2) / (p1 * p2 + (1 - p1) * (1 - p2))
/// </code>
/// <para>
/// with <c>p_i = score_i / upperBound_i</c> (clamped to [0, 1]).
/// </para>
/// <para>
/// Falls back to page-maximum normalization when an engine cannot report a bound, so the
/// merge still produces a ranking — just a looser one.
/// </para>
/// </remarks>
public sealed class UpperBoundBayesianFusionMerger : IContextualResultMerger
{
    /// <inheritdoc />
    public string Name => "UpperBoundBayesianFusion";

    /// <inheritdoc />
    public bool UsesContexts => true;

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Merge(
        IReadOnlyList<IReadOnlyList<SearchResult>> perEngineResults,
        string query)
        => Merge(perEngineResults, query, null);

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Merge(
        IReadOnlyList<IReadOnlyList<SearchResult>> perEngineResults,
        string query,
        IReadOnlyList<ScoreNormalizationContext?>? contexts)
    {
        ArgumentNullException.ThrowIfNull(perEngineResults);
        _ = query;

        int engineCount = perEngineResults.Count;

        if (engineCount == 0)
            return Array.Empty<SearchResult>();

        // Compute per-engine normalization divisors.
        var divisors = new double[engineCount];

        for (int i = 0; i < engineCount; i++)
        {
            if (contexts is not null && i < contexts.Count && contexts[i] is ScoreNormalizationContext ctx &&
                ctx.QueryUpperBound is double ub && ub > 0 && double.IsFinite(ub))
            {
                // Guard against a bound below an observed score (rounding).
                double observed = 0;
                foreach (var r in perEngineResults[i])
                    if (double.IsFinite(r.Score) && r.Score > observed) observed = r.Score;
                divisors[i] = Math.Max(ub, observed);
            }
            else
            {
                // Fallback: page maximum.
                double max = 0;
                foreach (var r in perEngineResults[i])
                    if (double.IsFinite(r.Score) && r.Score > max) max = r.Score;
                divisors[i] = max > 0 ? max : 1;
            }
        }

        // Collect all document IDs.
        var allDocIds = new HashSet<string>(StringComparer.Ordinal);
        var docMap = new Dictionary<string, SearchDocument>(StringComparer.Ordinal);

        for (int i = 0; i < engineCount; i++)
        {
            foreach (var result in perEngineResults[i])
            {
                allDocIds.Add(result.DocumentId);
                if (!docMap.ContainsKey(result.DocumentId))
                    docMap[result.DocumentId] = result.Document;
            }
        }

        var scored = new List<(string DocId, double Score)>();

        foreach (string docId in allDocIds)
        {
            double product = 1.0;
            double complement = 1.0;
            bool drop = false;

            for (int i = 0; i < engineCount; i++)
            {
                var found = perEngineResults[i].FirstOrDefault(r => r.DocumentId == docId);
                bool exists = perEngineResults[i].Any(r => r.DocumentId == docId);

                if (!exists)
                {
                    // Absent from this engine: no signal (p = 0.5).
                    product *= 0.5;
                    complement *= 0.5;
                    continue;
                }

                if (!double.IsFinite(found.Score))
                {
                    drop = true;
                    break;
                }

                double p = divisors[i] > 0 ? Math.Clamp(found.Score / divisors[i], 0, 1) : 0.5;

                product *= p;
                complement *= (1.0 - p);
            }

            if (drop)
                continue;

            double denominator = product + complement;
            double fused = denominator > 0 ? product / denominator : 0.5;

            if (double.IsFinite(fused) && fused > 0)
                scored.Add((docId, fused));
        }

        return scored
            .OrderByDescending(x => x.Score)
            .Select(x => new SearchResult(x.DocId, x.Score, docMap[x.DocId]))
            .ToList();
    }
}