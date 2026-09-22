using LexiSharp.Core;

namespace LexiSharp.Hybrid;

/// <summary>
/// Diversity-preserving reranker: greedily re-orders candidates so that each next pick is both
/// relevant to the query <b>and</b> different from what has already been picked.
/// </summary>
/// <remarks>
/// <para>
/// Classic Maximal Marginal Relevance (Carbonell &amp; Goldstein, 1998). At every step the
/// candidate maximizing
/// <c>MMR(d) = λ · rel(d) − (1 − λ) · max sim(d, selected)</c>
/// is selected, where <c>rel(d)</c> is the candidate's incoming score normalized by the best
/// incoming score, and <c>sim</c> is cosine similarity between the document vectors. A λ of
/// <c>1</c> degenerates to the incoming (relevance) order; a λ of <c>0</c> is pure diversity;
/// <c>0.7</c> is a common default.
/// </para>
/// <para>
/// Vectors come from the caller (typically pre-computed from an <see cref="Core.IEmbeddingProvider"/>
/// at index time). Candidates without a vector are never penalized for redundancy
/// (<c>sim = 0</c>) and never excluded — they compete on relevance alone. Scores of the returned
/// results are the <b>original</b> ones: MMR is an order-only strategy, it does not re-score.
/// </para>
/// <para>
/// Ties are broken deterministically by keeping the earliest candidate in the incoming order,
/// so equal inputs always yield equal outputs. Negative incoming scores compete too: relevance
/// turns negative below the best score, and when no score exceeds <c>0</c> every relevance is
/// forced to <c>0</c> so selection falls back to pure diversity order. The candidate list is
/// quadratic work in the worst case, which is fine for the shortlists reranking is meant for.
/// </para>
/// </remarks>
public sealed class MaximalMarginalRelevanceReranker : IReranker
{
    private readonly IReadOnlyDictionary<string, ReadOnlyMemory<float>> _vectors;
    private readonly double _lambda;
    private readonly int? _limit;

    /// <param name="vectors">Embedding per document id; entries may be missing (see remarks).</param>
    /// <param name="lambda">Relevance/diversity trade-off in <c>[0, 1]</c>.</param>
    /// <param name="limit">
    /// Optional final cut: only the first <paramref name="limit"/> picks are returned
    /// (diversity-aware shortlisting). Defaults to keeping every candidate.
    /// </param>
    public MaximalMarginalRelevanceReranker(
        IReadOnlyDictionary<string, ReadOnlyMemory<float>> vectors,
        double lambda = 0.5,
        int? limit = null)
    {
        ArgumentNullException.ThrowIfNull(vectors);

        if (double.IsNaN(lambda) || lambda < 0 || lambda > 1)
            throw new ArgumentOutOfRangeException(nameof(lambda), lambda, "lambda must be in [0, 1].");

        if (limit is < 1)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "limit must be at least 1.");

        _vectors = vectors;
        _lambda = lambda;
        _limit = limit;
    }

    /// <inheritdoc />
    public string Name => "MMR";

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Rerank(string query, IReadOnlyList<SearchResult> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        _ = query;

        if (candidates.Count == 0)
            return Array.Empty<SearchResult>();

        double maxScore = candidates
            .Where(c => double.IsFinite(c.Score))
            .Select(c => c.Score)
            .DefaultIfEmpty(0)
            .Max();

        var pool = candidates
            .Where(c => double.IsFinite(c.Score) && c.Score != 0)
            .ToList();

        var selected = new List<SearchResult>(pool.Count);
        var selectedVectors = new List<ReadOnlyMemory<float>>();

        while (pool.Count > 0 && (_limit is null || selected.Count < _limit))
        {
            int bestIndex = 0;
            double bestMmr = double.NegativeInfinity;

            for (int i = 0; i < pool.Count; i++)
            {
                double relevance = maxScore > 0 ? pool[i].Score / maxScore : 0;
                double redundancy = selectedVectors.Count == 0
                    ? 0
                    : MaxSimilarity(pool[i], selectedVectors);

                double mmr = _lambda * relevance - (1 - _lambda) * redundancy;

                // Strictly greater keeps the earliest candidate in the incoming order on ties.
                if (mmr > bestMmr)
                {
                    bestMmr = mmr;
                    bestIndex = i;
                }
            }

            var pick = pool[bestIndex];
            pool.RemoveAt(bestIndex);
            selected.Add(pick);

            if (_vectors.TryGetValue(pick.DocumentId, out var vector))
                selectedVectors.Add(vector);
        }

        return selected;
    }

    private double MaxSimilarity(SearchResult candidate, List<ReadOnlyMemory<float>> selectedVectors)
    {
        if (!_vectors.TryGetValue(candidate.DocumentId, out var vector))
            return 0;

        double best = 0;

        foreach (var selectedVector in selectedVectors)
        {
            double similarity = VectorSimilarity.CosineSimilarity(vector.Span, selectedVector.Span);

            if (similarity > best)
                best = similarity;
        }

        return best;
    }
}
