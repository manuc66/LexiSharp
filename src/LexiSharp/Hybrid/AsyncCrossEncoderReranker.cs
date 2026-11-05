using LexiSharp.Core;

namespace LexiSharp.Hybrid;

/// <summary>
/// Asynchronous counterpart of <see cref="CrossEncoderReranker"/>: re-scores every candidate with
/// a pairwise (cross-encoder) model via <see cref="IAsyncCrossEncoderScorer"/> and reorders the
/// list by the model's judgment, without blocking the caller.
/// </summary>
public sealed class AsyncCrossEncoderReranker : IAsyncReranker
{
    private readonly IAsyncCrossEncoderScorer _scorer;
    private readonly int? _limit;
    private readonly double _minimumScore;

    /// <param name="scorer">Pairwise relevance model driving the rerank.</param>
    /// <param name="limit">Optional final cut.</param>
    /// <param name="minimumScore">Results scoring below this after the rerank are dropped.</param>
    public AsyncCrossEncoderReranker(
        IAsyncCrossEncoderScorer scorer,
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
    public string Name => $"AsyncCrossEncoder({_scorer.Name})";

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchResult>> RerankAsync(
        string query,
        IReadOnlyList<SearchResult> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
            return Array.Empty<SearchResult>();

        var reranked = new List<SearchResult>(candidates.Count);

        foreach (var candidate in candidates)
        {
            double score = await _scorer.ScoreAsync(query, candidate.Document, cancellationToken).ConfigureAwait(false);

            if (double.IsNaN(score) || double.IsInfinity(score) || score == 0)
                continue;

            if (score < _minimumScore)
                continue;

            reranked.Add(candidate with { Score = score });
        }

        return reranked
            .OrderByDescending(x => x.Score)
            .Take(_limit ?? reranked.Count)
            .ToList();
    }
}
