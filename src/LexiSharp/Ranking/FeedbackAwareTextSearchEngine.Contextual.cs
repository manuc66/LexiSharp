using LexiSharp.Core;

namespace LexiSharp.Ranking;

/// <summary>
/// A decorator engine that augments a primary search engine with a learned query → document
/// feedback channel, choosing the history per search from the caller's payload.
/// </summary>
/// <remarks>
/// <para>
/// The distinction from the non-generic <see cref="FeedbackAwareTextSearchEngine"/> is one
/// history per search rather than one history per engine. What a user has chosen before is
/// evidence about that user's queries, and a history shared across users is evidence about none
/// of them in particular — the wrong user's choices promote documents in a ranking they did not
/// shape. One engine, many histories, selected by payload.
/// </para>
/// <para>
/// The boost is additive and bounded by <c>maxBoost</c>: the primary score stays the ranking and
/// the history only promotes among what it already returned, which is what lets this compose with
/// a boosted or hybrid engine rather than replace one.
/// </para>
/// </remarks>
/// <typeparam name="TPayload">
/// The caller's per-search state, constrained to a reference type — see
/// <see cref="IContextualSearchEngine{TPayload}"/>.
/// </typeparam>
public sealed class FeedbackAwareTextSearchEngine<TPayload> : IContextualSearchEngine<TPayload>, IQueryCostProbe
    where TPayload : class
{
    private readonly ITextSearchEngine _inner;
    private readonly Func<TPayload?, QueryFeedbackHistory?> _historyFor;
    private readonly double _maxBoost;
    private readonly double _minSimilarity;
    private readonly bool _requirePayload;

    /// <summary>
    /// Name this engine reports to <see cref="RetrievalTelemetry"/> and its metrics sinks.
    /// </summary>
    public const string EngineName = "FeedbackAwareTextSearchEngine";

    /// <param name="inner">The primary search engine.</param>
    /// <param name="historyFor">
    /// Picks the history to consult for a payload. Returning <c>null</c> means this search has no
    /// history — a user who has not answered anything yet — and the search is served unchanged.
    /// </param>
    /// <param name="maxBoost">
    /// The largest additive boost a document can receive from the feedback channel. Default: 1.0.
    /// </param>
    /// <param name="minSimilarity">
    /// Minimum token-overlap similarity for a recorded query to reach the current one. Default: 0.3.
    /// </param>
    /// <param name="requirePayload">
    /// Whether a search arriving through <see cref="ITextSearchEngine.Search(string, SearchOptions?)"/>
    /// should throw instead of running without a payload. Default <c>false</c>.
    /// <para>
    /// Set it when the history is chosen by payload. Serving such a search with the single
    /// fallback of "no history" would quietly return the unboosted ranking, and the results still
    /// look plausible — the same reasoning that puts <c>requirePayload</c> on
    /// <see cref="BoostedTextSearchEngine{TPayload}"/>, and the same default, so an engine that
    /// does not need a payload keeps working through the plain surface.
    /// </para>
    /// </param>
    public FeedbackAwareTextSearchEngine(
        ITextSearchEngine inner,
        Func<TPayload?, QueryFeedbackHistory?> historyFor,
        double maxBoost = 1.0,
        double minSimilarity = 0.3,
        bool requirePayload = false)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(historyFor);

        if (maxBoost < 0)
            throw new ArgumentOutOfRangeException(nameof(maxBoost), maxBoost, "Max boost must be non-negative.");

        _inner = inner;
        _historyFor = historyFor;
        _maxBoost = maxBoost;
        _minSimilarity = minSimilarity;
        _requirePayload = requirePayload;
    }

    /// <inheritdoc />
    public void Index(IEnumerable<SearchDocument> documents) => _inner.Index(documents);

    /// <inheritdoc />
    public void Add(SearchDocument document) => _inner.Add(document);

    /// <inheritdoc />
    public bool Remove(string documentId) => _inner.Remove(documentId);

    /// <inheritdoc />
    public void Clear() => _inner.Clear();

    /// <summary>
    /// Searches with no payload. The engine's <c>requirePayload</c> decides whether that throws or
    /// serves the search with no history.
    /// </summary>
    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null) =>
        Search(query, options, default!, payloadSupplied: false);

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options, TPayload payload) =>
        Search(query, options, payload, payloadSupplied: true);

    private IReadOnlyList<SearchResult> Search(
        string query,
        SearchOptions? options,
        TPayload payload,
        bool payloadSupplied)
    {
        ArgumentNullException.ThrowIfNull(query);

        options ??= SearchOptions.Default;

        if (options.IsEmpty)
            return Array.Empty<SearchResult>();

        // After the empty-request check and before the inner engine runs: an impossible request is
        // not a payload mistake, and a misrouted call should cost nothing.
        if (_requirePayload && !payloadSupplied)
        {
            throw new InvalidOperationException(
                $"FeedbackAwareTextSearchEngine<{typeof(TPayload).Name}> was constructed with " +
                "requirePayload: true, so it cannot serve a search that offers no payload. A " +
                $"caller holding an {nameof(ITextSearchEngine)} cannot reach " +
                $"{nameof(IContextualSearchEngine<TPayload>)}.{nameof(IContextualSearchEngine<TPayload>.Search)} — " +
                "pass the payload at the call site, or construct the engine with " +
                "requirePayload: false if one history serves every search.");
        }

        var history = _historyFor(payload);

        var results = _inner.Search(query, options);

        if (history is null || history.Count == 0 || results.Count == 0)
            return results;

        // Only the ids this page can actually ask about: an association carrying four hundred
        // documents, of which a page of ten will consult ten, is four hundred inserts and a
        // dictionary sized to four hundred on every search. Restricting it also gives the
        // dictionary its size, which is where the growth cost was.
        var pageIds = new HashSet<string>(results.Count, StringComparer.Ordinal);

        foreach (var result in results)
            pageIds.Add(result.DocumentId);

        var associations = history.GetFuzzyAssociationScores(query, _minSimilarity, pageIds);

        if (associations.Count == 0)
            return results;

        var boosted = new List<SearchResult>(results.Count);

        foreach (var result in results)
        {
            boosted.Add(
                associations.TryGetValue(result.DocumentId, out double strength)
                    ? result with { Score = result.Score + (strength * _maxBoost) }
                    : result);
        }

        // The inner engine was handed the caller's Offset and Limit, so `results` is already the
        // page: re-sort it, and do not page it again or the offset would be applied twice.
        return ResultOrdering.SortAndPage(boosted, options.TieBreak, offset: 0, limit: boosted.Count);
    }

    /// <summary>
    /// Records that the payload's owner chose <paramref name="documentId"/> for
    /// <paramref name="query"/>.
    /// </summary>
    /// <returns>
    /// <c>false</c> when the history selector mapped this payload to no history, so there was
    /// nowhere to record the choice; <c>true</c> when the association was recorded.
    /// </returns>
    public bool Learn(TPayload payload, string query, string documentId)
    {
        var history = _historyFor(payload);

        if (history is null)
            return false;

        history.Record(query, documentId);
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Consulting the history does not change which candidates the query touches, so the estimate
    /// is delegated to the inner engine when it can estimate; an inner without
    /// <see cref="IQueryCostProbe"/> makes the decorator a routing last resort
    /// (<see cref="long.MaxValue"/>).
    /// </remarks>
    public long EstimateCandidateCount(ReadOnlySpan<char> query, SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return _inner is IQueryCostProbe probe ? probe.EstimateCandidateCount(query, options) : long.MaxValue;
    }
}
