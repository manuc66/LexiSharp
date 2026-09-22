namespace LexiSharp.Core;

/// <summary>
/// A decorator engine that re-ranks the matches of an inner engine before they are returned.
/// </summary>
/// <remarks>
/// <para>
/// Writes (<see cref="Index"/>, <see cref="Add"/>, <see cref="Remove"/>, <see cref="Clear"/>)
/// are forwarded to the inner engine unchanged. <see cref="Search"/> runs the inner engine,
/// hands its candidates to the <see cref="IReranker"/>, then re-sorts, re-applies the
/// <see cref="SearchOptions.MinimumScore"/> and re-trims to <see cref="SearchOptions.Limit"/>.
/// </para>
/// <para>
/// The decorator only sees what the inner engine returns. To give candidates a chance to
/// surface during re-ranking, more results than the final limit are requested from the inner
/// engine (<c>maxCandidates</c>); a document ranked beyond that retrieval depth stays out of
/// reach no matter what the reranker thinks of it. This is the same recall/precision trade-off
/// every two-stage pipeline makes: the inner engine provides recall, the reranker precision.
/// </para>
/// <para>
/// <see cref="SearchOptions.MinimumScore"/> is not pre-applied on the inner search: a reranker
/// may replace scores entirely, so the threshold only applies to the <i>final</i> score, after
/// re-ranking — exactly like <see cref="BoostedTextSearchEngine"/> treats its boosted scores.
/// </para>
/// </remarks>
public sealed class RerankedTextSearchEngine : ITextSearchEngine, IQueryCostProbe
{
    private readonly ITextSearchEngine _inner;
    private readonly IReranker _reranker;
    private readonly int _maxCandidates;

    /// <param name="inner">The engine producing the base ranking (never disposed by this wrapper).</param>
    /// <param name="reranker">Second-stage strategy applied to the inner shortlist.</param>
    /// <param name="maxCandidates">
    /// Number of candidates requested from the inner engine so re-ranking has room to re-order;
    /// defaults to 50. Never below the final limit.
    /// </param>
    public RerankedTextSearchEngine(ITextSearchEngine inner, IReranker reranker, int maxCandidates = 50)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(reranker);

        _inner = inner;
        _reranker = reranker;
        _maxCandidates = Math.Max(1, maxCandidates);
    }

    /// <inheritdoc />
    public void Index(IEnumerable<SearchDocument> documents)
    {
        _inner.Index(documents);
    }

    /// <inheritdoc />
    public void Add(SearchDocument document)
    {
        _inner.Add(document);
    }

    /// <inheritdoc />
    public void Remove(string documentId)
    {
        _inner.Remove(documentId);
    }

    /// <inheritdoc />
    public void Clear()
    {
        _inner.Clear();
    }

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);

        options ??= SearchOptions.Default;

        if (options.IsEmpty)
            return Array.Empty<SearchResult>();

        // Same pooling rule as BoostedTextSearchEngine: the page is cut from the *reranked*
        // ordering, so the inner pool covers the skipped prefix plus maxCandidates of depth
        // beyond the page, and the inner call never applies Offset itself.
        int basePool = Math.Max(options.Limit, _maxCandidates);
        int candidateLimit = options.Offset > int.MaxValue - basePool ? int.MaxValue : options.Offset + basePool;

        // Do not pre-filter with MinimumScore here: it must apply to the *final* score, after
        // the reranker has spoken — a re-scored match can fall out, a promoted one can get in.
        var candidates = _inner.Search(query, options with
        {
            Offset = 0,
            Limit = candidateLimit,
            MinimumScore = double.NegativeInfinity,
        });

        if (candidates.Count == 0)
            return Array.Empty<SearchResult>();

        var reranked = _reranker.Rerank(query, candidates);

        // The reranker owns the order (best-first by contract); here we only drop broken or
        // non-matching scores, then cut the requested page, preserving its relative order.
        return reranked
            .Where(x => !double.IsNaN(x.Score) && !double.IsInfinity(x.Score)
                        && x.Score != 0 && x.Score >= options.MinimumScore)
            .Skip(options.Offset)
            .Take(options.Limit)
            .ToList();
    }

    /// <inheritdoc />
    /// <remarks>
    /// Re-ranking does not change how many candidates the query touches, so the estimate is
    /// delegated to the inner engine when it can estimate; an inner without
    /// <see cref="IQueryCostProbe"/> makes the decorator a routing last resort
    /// (<see cref="long.MaxValue"/>).
    /// </remarks>
    public long EstimateCandidateCount(ReadOnlySpan<char> query, SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return _inner is IQueryCostProbe probe ? probe.EstimateCandidateCount(query, options) : long.MaxValue;
    }
}
