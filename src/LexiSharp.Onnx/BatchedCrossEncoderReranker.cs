using LexiSharp.Core;

namespace LexiSharp.Onnx;

/// <summary>
/// The batched counterpart of <see cref="LexiSharp.Hybrid.CrossEncoderReranker"/>: it re-scores
/// the whole shortlist in <b>one</b> padded session call instead of one call per candidate, and
/// otherwise applies the same rules — drop scores of exactly <c>0</c>, NaN and infinities, apply
/// the optional minimum, order best-first, keep the incoming order on ties, never extend the list.
/// </summary>
/// <remarks>
/// It exists because <see cref="ICrossEncoderScorer.Score"/> is inherently one pair at a time while
/// <see cref="OnnxCrossEncoderScorer.ScoreBatch"/> is not: on CPU, batching the shortlist is the
/// difference between N session calls and one, and the harness's own reranker already batches 16.
/// </remarks>
public sealed class BatchedCrossEncoderReranker : IReranker
{
    private readonly OnnxCrossEncoderScorer _scorer;
    private readonly int? _limit;
    private readonly double _minimumScore;

    /// <param name="scorer">The batched pairwise model driving the rerank.</param>
    /// <param name="limit">Optional final cut; defaults to keeping every surviving candidate.</param>
    /// <param name="minimumScore">Candidates scoring below this after the rerank are dropped.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scorer"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="limit"/> is below 1.</exception>
    public BatchedCrossEncoderReranker(
        OnnxCrossEncoderScorer scorer,
        int? limit = null,
        double minimumScore = double.NegativeInfinity)
    {
        ArgumentNullException.ThrowIfNull(scorer);

        if (limit is < 1)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "limit must be at least 1.");

        _scorer = scorer;
        _limit = limit;
        _minimumScore = minimumScore;
    }

    /// <inheritdoc />
    public string Name => $"CrossEncoder({_scorer.Name}, batched)";

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Rerank(string query, IReadOnlyList<SearchResult> candidates)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
            return Array.Empty<SearchResult>();

        SearchDocument[] documents = new SearchDocument[candidates.Count];

        for (int i = 0; i < candidates.Count; i++)
            documents[i] = candidates[i].Document;

        IReadOnlyList<double> scores = _scorer.ScoreBatch(query, documents);
        var reranked = new List<SearchResult>(candidates.Count);

        for (int i = 0; i < candidates.Count; i++)
        {
            double score = scores[i];

            if (double.IsNaN(score) || double.IsInfinity(score) || score == 0)
                continue;

            if (score < _minimumScore)
                continue;

            reranked.Add(candidates[i] with { Score = score });
        }

        return reranked
            .OrderByDescending(result => result.Score)
            .Take(_limit ?? reranked.Count)
            .ToList();
    }
}
