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
/// The boost is additive by default and bounded by <c>maxBoost</c>: the primary score stays the
/// ranking and the history only promotes among what it already returned, which is what lets this
/// compose with a boosted or hybrid engine rather than replace one. A <c>candidatePool</c> can
/// over-fetch from the inner engine so a boosted document is not limited to the caller's page, and
/// <see cref="FeedbackBoostMode.Multiplicative"/> makes the boost relative to the base score
/// instead of a number of score points.
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
    private readonly Func<TPayload?, IReadOnlyList<(QueryFeedbackHistory History, double Weight)>?> _historiesFor;
    private readonly double _maxBoost;
    private readonly double _minSimilarity;
    private readonly bool _requirePayload;
    private readonly int _candidatePool;
    private readonly FeedbackBoostMode _boostMode;

    /// <summary>
    /// Name this engine reports to <see cref="RetrievalTelemetry"/> and its metrics sinks.
    /// </summary>
    public const string EngineName = "FeedbackAwareTextSearchEngine";

    /// <summary>
    /// The constructor shape released in 0.10.0, kept so an assembly compiled against it keeps
    /// binding after the pool and boost-mode parameters were added.
    /// </summary>
    /// <param name="inner">The primary search engine.</param>
    /// <param name="historyFor">Picks the history to consult for a payload.</param>
    /// <param name="maxBoost">The largest boost a document can receive from the feedback channel.</param>
    /// <param name="minSimilarity">Minimum token-overlap similarity for a recorded query to contribute.</param>
    /// <param name="requirePayload">Whether a payload-less search should throw.</param>
    public FeedbackAwareTextSearchEngine(
        ITextSearchEngine inner,
        Func<TPayload?, QueryFeedbackHistory?> historyFor,
        double maxBoost,
        double minSimilarity,
        bool requirePayload)
        : this(inner, historyFor, maxBoost, minSimilarity, requirePayload, candidatePool: 0, boostMode: FeedbackBoostMode.Additive)
    {
    }

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
    /// Whether a search arriving through <see cref="ITextSearchEngine.Search(string, SearchOptions?, CancellationToken)"/>
    /// should throw instead of running without a payload. Default <c>false</c>.
    /// <para>
    /// Set it when the history is chosen by payload. Serving such a search with the single
    /// fallback of "no history" would quietly return the unboosted ranking, and the results still
    /// look plausible — the same reasoning that puts <c>requirePayload</c> on
    /// <see cref="BoostedTextSearchEngine{TPayload}"/>, and the same default, so an engine that
    /// does not need a payload keeps working through the plain surface.
    /// </para>
    /// </param>
    /// <param name="candidatePool">
    /// How many candidates to request from the inner engine before the final page is cut. Default:
    /// <c>0</c>, which keeps the caller's page and leaves <see cref="SearchOptions.Offset"/> and
    /// <see cref="SearchOptions.Limit"/> to the inner engine. A positive value over-fetches so a
    /// boosted document can enter the page from beyond the caller's limit; the page is then cut
    /// again after the boost.
    /// </param>
    /// <param name="boostMode">
    /// How the feedback strength is applied. <see cref="FeedbackBoostMode.Additive"/> is the
    /// historical behaviour; <see cref="FeedbackBoostMode.Multiplicative"/> makes
    /// <paramref name="maxBoost"/> a relative change independent of the scorer's score scale.
    /// </param>
    public FeedbackAwareTextSearchEngine(
        ITextSearchEngine inner,
        Func<TPayload?, QueryFeedbackHistory?> historyFor,
        double maxBoost = 1.0,
        double minSimilarity = 0.3,
        bool requirePayload = false,
        int candidatePool = 0,
        FeedbackBoostMode boostMode = FeedbackBoostMode.Additive)
        : this(inner, payload => historyFor(payload) is { } h ? new[] { (h, 1.0) } : null, maxBoost, minSimilarity, requirePayload, candidatePool, boostMode, 0)
    {
    }

    private FeedbackAwareTextSearchEngine(
        ITextSearchEngine inner,
        Func<TPayload?, IReadOnlyList<(QueryFeedbackHistory History, double Weight)>?> historiesFor,
        double maxBoost,
        double minSimilarity,
        bool requirePayload,
        int candidatePool,
        FeedbackBoostMode boostMode,
        int multi)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(historiesFor);

        if (maxBoost < 0)
            throw new ArgumentOutOfRangeException(nameof(maxBoost), maxBoost, "Max boost must be non-negative.");

        if (candidatePool < 0)
            throw new ArgumentOutOfRangeException(nameof(candidatePool), candidatePool, "Candidate pool must be non-negative.");

        if (!Enum.IsDefined(typeof(FeedbackBoostMode), boostMode))
            throw new ArgumentOutOfRangeException(nameof(boostMode), boostMode, "Unknown feedback boost mode.");

        _inner = inner;
        _historiesFor = historiesFor;
        _maxBoost = maxBoost;
        _minSimilarity = minSimilarity;
        _requirePayload = requirePayload;
        _candidatePool = candidatePool;
        _boostMode = boostMode;
    }

    /// <summary>
    /// The factory shape released in 0.10.0, kept for binary compatibility with callers that build
    /// a multi-signal engine.
    /// </summary>
    /// <param name="inner">The primary search engine.</param>
    /// <param name="historiesFor">Picks the feedback channels for a payload.</param>
    /// <param name="maxBoost">The largest boost a document can receive from the feedback channel.</param>
    /// <param name="minSimilarity">Minimum token-overlap similarity for a recorded query to contribute.</param>
    /// <param name="requirePayload">Whether a payload-less search should throw.</param>
    public static FeedbackAwareTextSearchEngine<TPayload> CreateMulti(
        ITextSearchEngine inner,
        Func<TPayload?, IReadOnlyList<(QueryFeedbackHistory History, double Weight)>?> historiesFor,
        double maxBoost,
        double minSimilarity,
        bool requirePayload)
    {
        return CreateMulti(
            inner, historiesFor, maxBoost, minSimilarity, requirePayload,
            candidatePool: 0, boostMode: FeedbackBoostMode.Additive);
    }

    /// <summary>
    /// Multi-signal factory: a payload may resolve to several feedback channels, each with its own
    /// weight. The boost is the weighted sum of the channels' strengths.
    /// </summary>
    public static FeedbackAwareTextSearchEngine<TPayload> CreateMulti(
        ITextSearchEngine inner,
        Func<TPayload?, IReadOnlyList<(QueryFeedbackHistory History, double Weight)>?> historiesFor,
        double maxBoost = 1.0,
        double minSimilarity = 0.3,
        bool requirePayload = false,
        int candidatePool = 0,
        FeedbackBoostMode boostMode = FeedbackBoostMode.Additive)
    {
        return new FeedbackAwareTextSearchEngine<TPayload>(
            inner, historiesFor, maxBoost, minSimilarity, requirePayload, candidatePool, boostMode, 1);
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
    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null, CancellationToken cancellationToken = default) =>
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

        var histories = _historiesFor(payload);

        // With a candidate pool the page is cut after the boost, so the inner engine must not
        // apply Offset or MinimumScore: a boosted document can enter the page from the skipped
        // prefix, and a damped one can leave it. Without a pool the caller's options are forwarded
        // unchanged, which is the pre-existing behaviour.
        bool pooled = _candidatePool > 0;
        SearchOptions innerOptions = options;

        if (pooled)
        {
            int basePool = Math.Max(options.Limit, _candidatePool);
            int candidateLimit = options.Offset > int.MaxValue - basePool
                ? int.MaxValue
                : options.Offset + basePool;

            innerOptions = options with
            {
                Offset = 0,
                Limit = candidateLimit,
                MinimumScore = double.NegativeInfinity,
            };
        }

        var results = _inner.Search(query, innerOptions);

        if (results.Count == 0)
            return results;

        bool hasHistory = false;

        if (histories is not null)
        {
            foreach (var (history, weight) in histories)
            {
                if (history is not null && history.Count > 0 && weight != 0)
                {
                    hasHistory = true;
                    break;
                }
            }
        }

        // No history for this search, and no pool to re-cut: the inner engine already produced
        // the requested page, so return it untouched without building the lookup structures.
        if (!hasHistory && !pooled)
            return results;

        Dictionary<string, double>? combined = null;

        if (hasHistory)
        {
            // Only the ids this pool can actually ask about.
            var pageIds = new HashSet<string>(results.Count, StringComparer.Ordinal);

            foreach (var result in results)
                pageIds.Add(result.DocumentId);

            // Combine the channels: a weighted sum of each history's strength, clamped to [-1, 1]
            // so a document boosted by several channels cannot exceed the single-channel ceiling.
            combined = new Dictionary<string, double>(pageIds.Count, StringComparer.Ordinal);

            foreach (var (history, weight) in histories!)
            {
                if (history is null || history.Count == 0 || weight == 0)
                    continue;

                var scores = history.GetFuzzyAssociationScores(query, _minSimilarity, pageIds);

                foreach (var kvp in scores)
                {
                    combined.TryGetValue(kvp.Key, out double existing);
                    combined[kvp.Key] = Math.Clamp(existing + (weight * kvp.Value), -1.0, 1.0);
                }
            }

            // A history exists but contributed nothing, and the page was not re-cut: the inner
            // engine's answer is already the caller's page.
            if (combined.Count == 0 && !pooled)
                return results;
        }

        var boosted = new List<SearchResult>(results.Count);

        foreach (var result in results)
        {
            if (combined is null || !combined.TryGetValue(result.DocumentId, out double strength))
            {
                if (pooled && result.Score < options.MinimumScore)
                    continue;

                boosted.Add(result);
                continue;
            }

            double score = ApplyBoost(result.Score, strength);

            if (!double.IsFinite(score))
                continue;

            if (pooled && score < options.MinimumScore)
                continue;

            boosted.Add(result with { Score = score });
        }

        // The pool path owns Offset and Limit (the inner call did not apply them); the legacy path
        // was handed the caller's page and must not page it a second time.
        return pooled
            ? ResultOrdering.SortAndPage(boosted, options.TieBreak, options.Offset, options.Limit)
            : ResultOrdering.SortAndPage(boosted, options.TieBreak, offset: 0, limit: boosted.Count);
    }

    /// <summary>
    /// Applies one combined strength to one base score. Additive keeps the historical
    /// <c>score + strength * maxBoost</c>; multiplicative makes the same <c>maxBoost</c> a relative
    /// change and never turns a factor negative (a factor of zero drops the score to zero, which
    /// the engine convention reads as "not a match").
    /// </summary>
    private double ApplyBoost(double score, double strength)
    {
        if (_boostMode == FeedbackBoostMode.Multiplicative)
        {
            double factor = 1.0 + (strength * _maxBoost);
            return score * (factor > 0 ? factor : 0);
        }

        return score + (strength * _maxBoost);
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
        var histories = _historiesFor(payload);

        if (histories is null || histories.Count == 0)
            return false;

        foreach (var (history, _) in histories)
            history?.Record(query, documentId);

        return true;
    }

    /// <summary>
    /// Records that the payload's owner chose <paramref name="documentId"/> for
    /// <paramref name="query"/>, with an explicit reward.
    /// </summary>
    public bool Learn(TPayload payload, string query, string documentId, double reward)
    {
        var histories = _historiesFor(payload);

        if (histories is null || histories.Count == 0)
            return false;

        foreach (var (history, _) in histories)
            history?.Record(query, documentId, reward);

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
