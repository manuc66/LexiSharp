using LexiSharp.Core;

namespace LexiSharp.Hybrid;

/// <summary>
/// Blends the native per-engine scores instead of re-ranking. Every engine's scores are put on a
/// comparable scale by an <see cref="IScoreNormalizer"/> — by default divided by that engine's
/// largest score — and a weighted sum is computed across the engines that returned the document.
/// </summary>
/// <remarks>
/// Use this when the source rankings are trusted as-is (e.g. PostgreSQL <c>ts_rank</c>) and you
/// only need a sensible global ordering, or when the engines sit on deliberately different
/// semantics (lexical vs vector similarity). Weights default to <c>1</c> for every engine.
/// A per-engine score that is NaN or infinite never poisons the blend: it is dropped from that
/// engine's normalization and from every document it would have contributed to.
/// <para>
/// <b>The normalizer is the part worth choosing.</b> Dividing by the maximum makes each engine's top
/// document worth <c>1</c>, whatever its score means; <see cref="MinMaxScoreNormalizer"/> and
/// <see cref="SumScoreNormalizer"/> answer different questions (see each type). The default is the
/// historical behaviour, so passing none changes nothing about an existing pipeline's scores.
/// </para>
/// </remarks>
public sealed class WeightedScoreResultMerger : IResultMerger, IMergeExplainer
{
    private readonly double[] _weights;
    private readonly IScoreNormalizer _normalizer;

    /// <param name="weights">
    /// One non-negative weight per engine, in the same order as the engines passed to the
    /// <see cref="HybridTextSearchEngine"/>. Defaults to <c>1</c> for every engine.
    /// </param>
    public WeightedScoreResultMerger(params double[] weights)
        : this(new MaxScoreNormalizer(), weights)
    {
    }

    /// <summary>
    /// Blends with the given normalizer and weights.
    /// </summary>
    /// <param name="normalizer">
    /// How each engine's scores become comparable. <see cref="MaxScoreNormalizer"/> is the historical
    /// behaviour and what the single-argument constructor uses.
    /// </param>
    /// <param name="weights">
    /// One non-negative weight per engine, in the same order as the engines passed to the
    /// <see cref="HybridTextSearchEngine"/>. Defaults to <c>1</c> for every engine.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="normalizer"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A weight is negative, NaN or infinite.</exception>
    public WeightedScoreResultMerger(IScoreNormalizer normalizer, params double[] weights)
    {
        ArgumentNullException.ThrowIfNull(normalizer);

        if (weights.Any(w => double.IsNaN(w) || double.IsInfinity(w) || w < 0))
            throw new ArgumentOutOfRangeException(nameof(weights), "Weights must be non-negative and finite.");

        _normalizer = normalizer;
        _weights = weights.Length > 0 ? weights : new[] { 1.0 };
    }

    /// <inheritdoc />
    public string Name => "WeightedScore";

    /// <inheritdoc />
    public string NormalizationName => _normalizer.Name;

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

        var merged = new Dictionary<string, (SearchDocument Document, double Score)>(StringComparer.Ordinal);

        for (int i = 0; i < engineCount; i++)
            AccumulateEngine(merged, perEngineResults[i], weights[i], _normalizer.Measure(perEngineResults[i]));

        return Rank(merged);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The normalizer is asked to measure the same lists <see cref="Merge"/> measured, so the
    /// explanation and the merge cannot disagree about a divisor: a second implementation of "the
    /// largest score" would be a second source of truth for the only numbers this type exists to
    /// report.
    /// </remarks>
    public MergeExplanation Explain(IReadOnlyList<IReadOnlyList<SearchResult>> perEngineResults)
    {
        ArgumentNullException.ThrowIfNull(perEngineResults);

        int engineCount = perEngineResults.Count;

        // Merge returns an empty list for no engines rather than refusing the input, so the
        // explanation of that merge is empty rather than an error.
        if (engineCount == 0)
            return new MergeExplanation(Array.Empty<double>(), Array.Empty<double>());

        var scales = new double[engineCount];
        var offsets = new double[engineCount];
        bool shifted = false;

        for (int i = 0; i < engineCount; i++)
        {
            ScoreNormalization normalization = _normalizer.Measure(perEngineResults[i]);
            scales[i] = normalization.Scale;
            offsets[i] = normalization.Offset;
            shifted |= normalization.Offset != 0;
        }

        var explanation = new MergeExplanation(ResolveWeights(engineCount), scales);

        // Left empty when nothing shifts, which is the documented reading of "empty means offset 0"
        // and keeps the historical report of a max-normalized merge byte for byte what it was.
        return shifted ? explanation with { Offsets = offsets } : explanation;
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

    private static void AccumulateEngine(
        Dictionary<string, (SearchDocument Document, double Score)> merged,
        IReadOnlyList<SearchResult> results,
        double weight,
        ScoreNormalization normalization)
    {
        foreach (var result in results)
        {
            // A per-engine score that is NaN or infinite never poisons the blend.
            if (!double.IsFinite(result.Score))
                continue;

            double contribution = weight * normalization.Apply(result.Score);

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
