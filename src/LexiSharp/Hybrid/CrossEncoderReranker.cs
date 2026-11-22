using LexiSharp.Core;

namespace LexiSharp.Hybrid;

/// <summary>
/// Precision-oriented reranker: re-scores every candidate with a pairwise (cross-encoder)
/// model and reorders the list by the model's judgment.
/// </summary>
/// <remarks>
/// <para>
/// Cross-encoders concatenate query and document into a single model input, so they are far more
/// accurate than the cheap first-stage retrieval but too slow to run over a whole corpus. The
/// canonical topology therefore runs one on the shortlist a recall engine already produced —
/// exactly what <see cref="IReranker"/> is for. The cost is <c>O(candidates)</c> model calls per
/// query, so this reranker is meant for the last, smallest stage of a cascade.
/// </para>
/// <para>
/// The returned results carry the model's score (the incoming scores are replaced); a score of
/// exactly <c>0</c> means "not a match" and NaN/infinite scores are dropped, as elsewhere in
/// LexiSharp. The input list is never mutated. Ties keep the incoming order.
/// </para>
/// </remarks>
public sealed class CrossEncoderReranker : IReranker, IRerankerExplainer
{
    private readonly ICrossEncoderScorer _scorer;
    private readonly int? _limit;
    private readonly double _minimumScore;

    /// <param name="scorer">Pairwise relevance model driving the rerank.</param>
    /// <param name="limit">
    /// Optional final cut: only the first <paramref name="limit"/> re-ranked results are returned.
    /// Defaults to keeping every candidate.
    /// </param>
    /// <param name="minimumScore">Results scoring below this after the rerank are dropped.</param>
    public CrossEncoderReranker(
        ICrossEncoderScorer scorer,
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
    public string Name => $"CrossEncoder({_scorer.Name})";

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Rerank(string query, IReadOnlyList<SearchResult> candidates)
        => RerankCore(query, candidates, null).Results;

    private (IReadOnlyList<SearchResult> Results, Dictionary<string, double> Scores) RerankCore(
        string query,
        IReadOnlyList<SearchResult> candidates,
        Dictionary<string, double>? scores)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
            return (Array.Empty<SearchResult>(), scores ?? new Dictionary<string, double>());

        var reranked = new List<SearchResult>(candidates.Count);
        var captured = scores ?? new Dictionary<string, double>();

        foreach (var candidate in candidates)
        {
            double score = _scorer.Score(query, candidate.Document);

            if (double.IsNaN(score) || double.IsInfinity(score) || score == 0)
                continue;

            if (score < _minimumScore)
                continue;

            captured[candidate.DocumentId] = score;
            reranked.Add(candidate with { Score = score });
        }

        var results = reranked
            .OrderByDescending(x => x.Score)
            .Take(_limit ?? reranked.Count)
            .ToList();

        return (results, captured);
    }

    /// <inheritdoc />
    public RerankExplanation? ExplainRerank(string query, IReadOnlyList<SearchResult> candidates, string documentId)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidates);

        if (!candidates.Any(c => c.DocumentId == documentId))
            return null;

        var scores = new Dictionary<string, double>();
        var (results, _) = RerankCore(query, candidates, scores);

        if (!scores.TryGetValue(documentId, out double score))
            return null;

        int rankBefore = 0;
        for (int i = 0; i < candidates.Count; i++)
            if (candidates[i].DocumentId == documentId) { rankBefore = i + 1; break; }

        int rankAfter = 0;
        for (int i = 0; i < results.Count; i++)
            if (results[i].DocumentId == documentId) { rankAfter = i + 1; break; }

        var signals = new List<RerankSignal>
        {
            new("cross-encoder-score", 1.0, score, score),
        };

        string summary = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"CrossEncoder scored '{documentId}' = {score:0.0000}; rank {rankBefore} → {rankAfter}.");

        return new RerankExplanation(documentId, score, signals, summary);
    }
}