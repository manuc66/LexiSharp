using LexiSharp.Core;

namespace LexiSharp.Hybrid;

/// <summary>
/// Fusion merger that blends per-engine scores with <b>learned weights</b> instead of the fixed
/// weights of <see cref="WeightedScoreResultMerger"/> or the rank-based heuristic of
/// reciprocal-rank fusion.
/// </summary>
/// <remarks>
/// <para>
/// Each engine's scores are min-max normalised to [0, 1] before blending, so engines on
/// unrelated scales (BM25, ts_rank, cosine) become comparable. The final score is
/// <c>Σ_i w_i · norm_i(d)</c>, where <c>w_i</c> is the learned weight for engine <c>i</c>.
/// Candidates missing from an engine receive 0 for that engine.
/// </para>
/// <para>
/// The weights are supplied by the caller (typically produced by
/// <see cref="FusionWeightTuner"/> on a validation set). They are <b>not</b> re-learned at merge
/// time, so the hot path stays allocation-free apart from the merge itself. The merger is
/// explainable: <see cref="Name"/> carries the weights and <see cref="Weights"/> exposes them.
/// </para>
/// </remarks>
public sealed class LearnedFusionMerger : IResultMerger
{
    private readonly double[] _weights;

    /// <param name="weights">
    /// One weight per engine. Must have the same length as the number of engines at merge time.
    /// </param>
    public LearnedFusionMerger(double[] weights)
    {
        ArgumentNullException.ThrowIfNull(weights);
        if (weights.Length == 0)
            throw new ArgumentException("At least one weight is required.", nameof(weights));
        _weights = (double[])weights.Clone();
    }

    /// <summary>The learned weights, one per engine.</summary>
    public IReadOnlyList<double> Weights => _weights;

    /// <inheritdoc />
    public string Name => $"LearnedFusion(w=[{string.Join(",", _weights.Select(w => w.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)))}])";

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Merge(
        IReadOnlyList<IReadOnlyList<SearchResult>> perEngineResults,
        string query)
    {
        ArgumentNullException.ThrowIfNull(perEngineResults);
        ArgumentNullException.ThrowIfNull(query);

        if (perEngineResults.Count == 0)
            return Array.Empty<SearchResult>();

        if (perEngineResults.Count != _weights.Length)
            throw new ArgumentException(
                $"Expected {_weights.Length} engines but got {perEngineResults.Count}.",
                nameof(perEngineResults));

        // Min-max normalise each engine's scores to [0, 1].
        var normalised = new List<Dictionary<string, double>>(perEngineResults.Count);
        var allIds = new HashSet<string>(StringComparer.Ordinal);

        for (int e = 0; e < perEngineResults.Count; e++)
        {
            var engineResults = perEngineResults[e];
            var map = new Dictionary<string, double>(StringComparer.Ordinal);
            double min = double.MaxValue, max = double.MinValue;

            foreach (var r in engineResults)
            {
                if (r.Score < min) min = r.Score;
                if (r.Score > max) max = r.Score;
                allIds.Add(r.DocumentId);
            }

            double range = max - min;
            foreach (var r in engineResults)
                map[r.DocumentId] = range > 0 ? (r.Score - min) / range : 0;

            normalised.Add(map);
        }

        // Blend.
        var blended = new List<(string Id, double Score)>(allIds.Count);
        foreach (string id in allIds)
        {
            double score = 0;
            for (int e = 0; e < perEngineResults.Count; e++)
            {
                if (normalised[e].TryGetValue(id, out double v))
                    score += _weights[e] * v;
            }
            blended.Add((id, score));
        }

        // Preserve the document text from the first engine that has it.
        var textById = new Dictionary<string, SearchDocument>(StringComparer.Ordinal);
        foreach (var engineResults in perEngineResults)
            foreach (var r in engineResults)
                if (!textById.ContainsKey(r.DocumentId))
                    textById[r.DocumentId] = r.Document;

        return blended
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .Select(x => new SearchResult(x.Id, x.Score, textById.TryGetValue(x.Id, out var d) ? d : new SearchDocument(x.Id, string.Empty)))
            .ToList();
    }

}