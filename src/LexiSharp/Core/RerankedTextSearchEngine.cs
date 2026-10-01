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
    private readonly string _rerankerName;
    private readonly RetrievalTelemetry _telemetry;

    /// <summary>
    /// Name this engine reports to <see cref="RetrievalTelemetry"/> and its metrics sinks.
    /// </summary>
    public const string EngineName = "RerankedTextSearchEngine";

    /// <param name="inner">The engine producing the base ranking (never disposed by this wrapper).</param>
    /// <param name="reranker">Second-stage strategy applied to the inner shortlist.</param>
    /// <param name="maxCandidates">
    /// Number of candidates requested from the inner engine so re-ranking has room to re-order;
    /// defaults to 50. Never below the final limit.
    /// </param>
    /// <param name="telemetry">
    /// Optional observability sink. Reports a <c>retrieve</c> stage for the inner engine and a
    /// <c>rerank:&lt;name&gt;</c> stage for the second stage, which is where a cross-encoder's
    /// latency shows up. Defaults to <see cref="RetrievalTelemetry.None"/>.
    /// </param>
    public RerankedTextSearchEngine(
        ITextSearchEngine inner,
        IReranker reranker,
        int maxCandidates = 50,
        RetrievalTelemetry? telemetry = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(reranker);

        _inner = inner;
        _reranker = reranker;
        _maxCandidates = Math.Max(1, maxCandidates);
        // Read once: the name is a property on a consumer-supplied seam, and a trace step should
        // not call into it per candidate.
        _rerankerName = reranker.Name;
        _telemetry = telemetry ?? RetrievalTelemetry.None;
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

        bool instrumented = _telemetry.IsEnabled;
        long started = instrumented ? RetrievalTelemetry.StartTimer() : 0;

        if (options.IsEmpty)
            return Array.Empty<SearchResult>();

        // Same pooling rule as BoostedTextSearchEngine: the page is cut from the *reranked*
        // ordering, so the inner pool covers the skipped prefix plus maxCandidates of depth
        // beyond the page, and the inner call never applies Offset itself.
        int basePool = Math.Max(options.Limit, _maxCandidates);
        int candidateLimit = options.Offset > int.MaxValue - basePool ? int.MaxValue : options.Offset + basePool;

        // Do not pre-filter with MinimumScore here: it must apply to the *final* score, after
        // the reranker has spoken — a re-scored match can fall out, a promoted one can get in.
        long retrieveStarted = instrumented ? RetrievalTelemetry.StartTimer() : 0;
        var candidates = _inner.Search(query, options with
        {
            Offset = 0,
            Limit = candidateLimit,
            MinimumScore = double.NegativeInfinity,
        });

        if (instrumented)
            _telemetry.StageCompleted(EngineName, "retrieve", candidates.Count, retrieveStarted);

        if (candidates.Count == 0)
        {
            if (instrumented)
                _telemetry.Warning(EngineName, "retrieval produced no candidate: the reranker had nothing to re-order");

            return Array.Empty<SearchResult>();
        }

        long rerankStarted = instrumented ? RetrievalTelemetry.StartTimer() : 0;
        var reranked = _reranker.Rerank(query, candidates);

        if (instrumented)
        {
            double rerankMs = RetrievalTelemetry.ElapsedMs(rerankStarted);
            _telemetry.StageCompleted(EngineName, "rerank:" + _rerankerName, candidates.Count, rerankStarted);

            // A slow second stage is the single most useful production signal here: the first stage
            // is usually a memory scan, while a cross-encoder pays a model call per candidate. The
            // threshold is deliberately a constant rather than a guess tuned to any corpus.
            if (rerankMs >= 100)
                _telemetry.Warning(EngineName, $"rerank stage took {rerankMs:0.#} ms for {candidates.Count} candidate(s)");
        }

        // The reranker owns the order (best-first by contract); here we only drop broken or
        // non-matching scores, then cut the requested page, preserving its relative order.
        var page = reranked
            .Where(x => !double.IsNaN(x.Score) && !double.IsInfinity(x.Score)
                        && x.Score != 0 && x.Score >= options.MinimumScore)
            .Skip(options.Offset)
            .Take(options.Limit)
            .ToList();

        RecordRerankStage(options.Trace, candidates, page);

        if (instrumented)
            _telemetry.SearchCompleted(EngineName, started, page.Count);

        return page;
    }

    /// <summary>
    /// Records one <see cref="TraceStage.Rerank"/> step per document of the final page, pairing
    /// the reranker's score with the inner score it replaced. A reranker may also introduce a
    /// document the inner engine never returned, in which case the before-score is unknown and
    /// recorded as <see cref="double.NaN"/>.
    /// </summary>
    private void RecordRerankStage(
        SearchTrace? trace,
        IReadOnlyList<SearchResult> candidates,
        IReadOnlyList<SearchResult> page)
    {
        if (trace is null)
            return;

        // Built once, not once per page entry — the previous shape re-filled the map for every
        // result the page held, an O(page x candidates) waste that the `??=` only masked at the
        // allocation site. maxCandidates-bounded, not corpus-bounded: only the shortlist is
        // indexed, and each per-result lookup stays O(1).
        var before = new Dictionary<string, double>(candidates.Count, StringComparer.Ordinal);

        for (int j = 0; j < candidates.Count; j++)
        {
            // The guard keeps first-occurrence-wins semantics for a candidate list that is
            // off-contract and duplicates an id; search results are distinct by contract.
            string id = candidates[j].DocumentId;
            if (!before.ContainsKey(id))
            {
                before[id] = candidates[j].Score;
            }
        }

        for (int i = 0; i < page.Count; i++)
        {
            SearchResult result = page[i];

            double prior = before.TryGetValue(result.DocumentId, out double found) ? found : double.NaN;
            trace.Record(new TraceStep(TraceStage.Rerank, result.DocumentId, prior, result.Score, _rerankerName));
        }
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
