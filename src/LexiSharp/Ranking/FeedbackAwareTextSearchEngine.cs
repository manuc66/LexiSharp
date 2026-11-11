using LexiSharp.Core;

namespace LexiSharp.Ranking;

/// <summary>
/// A decorator engine that augments a primary search engine with a learned query → document
/// feedback channel — one history for every search.
/// </summary>
/// <remarks>
/// <para>
/// This is the form to reach for when the history belongs to the corpus rather than to a user:
/// a dictionary of preferred terms, a set of documents a deployment considers canonical, anything
/// that is not scoped to who is asking.
/// </para>
/// <para>
/// Reach for <see cref="FeedbackAwareTextSearchEngine{TPayload}"/> when it is scoped to a user. A
/// history shared across users is evidence about none of them in particular, and serving someone
/// the choices of strangers promotes documents they did not shape.
/// </para>
/// </remarks>
public sealed class FeedbackAwareTextSearchEngine : ITextSearchEngine, IQueryCostProbe
{
    private readonly FeedbackAwareTextSearchEngine<Unit> _inner;
    private readonly IReadOnlyList<(QueryFeedbackHistory History, double Weight)> _histories;

    /// <summary>
    /// Name this engine reports to <see cref="RetrievalTelemetry"/>.
    /// </summary>
    public const string EngineName = FeedbackAwareTextSearchEngine<Unit>.EngineName;

    /// <summary>
    /// The constructor shape released in 0.10.0, kept so an assembly compiled against it keeps
    /// binding after the pool and boost-mode parameters were added.
    /// </summary>
    /// <param name="inner">The primary search engine.</param>
    /// <param name="history">The feedback history to learn from and contribute to.</param>
    /// <param name="maxBoost">The maximum boost a document can receive from the feedback channel.</param>
    /// <param name="minSimilarity">Minimum token-overlap similarity for a recorded query to contribute.</param>
    public FeedbackAwareTextSearchEngine(
        ITextSearchEngine inner,
        QueryFeedbackHistory history,
        double maxBoost,
        double minSimilarity)
        : this(inner, history, maxBoost, minSimilarity, candidatePool: 0, boostMode: FeedbackBoostMode.Additive)
    {
    }

    /// <summary>
    /// The constructor shape released in 0.10.0, kept for binary compatibility with callers that
    /// pass several feedback channels.
    /// </summary>
    /// <param name="inner">The primary search engine.</param>
    /// <param name="histories">The feedback channels, each with its weight.</param>
    /// <param name="maxBoost">The maximum boost a document can receive from the feedback channel.</param>
    /// <param name="minSimilarity">Minimum token-overlap similarity for a recorded query to contribute.</param>
    public FeedbackAwareTextSearchEngine(
        ITextSearchEngine inner,
        IEnumerable<(QueryFeedbackHistory History, double Weight)> histories,
        double maxBoost,
        double minSimilarity)
        : this(inner, histories, maxBoost, minSimilarity, candidatePool: 0, boostMode: FeedbackBoostMode.Additive)
    {
    }

    /// <param name="inner">The primary search engine.</param>
    /// <param name="history">The feedback history to learn from and contribute to.</param>
    /// <param name="maxBoost">
    /// The maximum additive boost a document can receive from the feedback channel. Default: 1.0.
    /// </param>
    /// <param name="minSimilarity">
    /// Minimum token-overlap similarity for a recorded query to reach the current one. Default: 0.3.
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
        QueryFeedbackHistory history,
        double maxBoost = 1.0,
        double minSimilarity = 0.3,
        int candidatePool = 0,
        FeedbackBoostMode boostMode = FeedbackBoostMode.Additive)
        : this(inner, new[] { (history, 1.0) }, maxBoost, minSimilarity, candidatePool, boostMode)
    {
        ArgumentNullException.ThrowIfNull(history);
    }

    /// <summary>
    /// Multi-signal constructor: several feedback channels (click, conversion, dwell, …) with
    /// their own weights. The boost is the weighted combination of their strengths.
    /// </summary>
    public FeedbackAwareTextSearchEngine(
        ITextSearchEngine inner,
        IEnumerable<(QueryFeedbackHistory History, double Weight)> histories,
        double maxBoost = 1.0,
        double minSimilarity = 0.3,
        int candidatePool = 0,
        FeedbackBoostMode boostMode = FeedbackBoostMode.Additive)
    {
        ArgumentNullException.ThrowIfNull(histories);

        var list = histories.ToList();

        if (list.Count == 0)
            throw new ArgumentException("At least one history is required.", nameof(histories));

        foreach (var (history, weight) in list)
        {
            ArgumentNullException.ThrowIfNull(history);

            if (weight < 0)
                throw new ArgumentOutOfRangeException(nameof(histories), weight, "Weights must be non-negative.");
        }

        _histories = list;
        _inner = FeedbackAwareTextSearchEngine<Unit>.CreateMulti(
            inner,
            _ => list,
            maxBoost,
            minSimilarity,
            candidatePool: candidatePool,
            boostMode: boostMode);
    }

    /// <summary>
    /// The feedback history this engine learns from and contributes to.
    /// </summary>
    public QueryFeedbackHistory History => _histories[0].History;

    /// <summary>
    /// The feedback channels this engine combines, in the order they were given.
    /// </summary>
    public IReadOnlyList<(QueryFeedbackHistory History, double Weight)> Histories => _histories;

    /// <inheritdoc />
    public void Index(IEnumerable<SearchDocument> documents) => _inner.Index(documents);

    /// <inheritdoc />
    public void Add(SearchDocument document) => _inner.Add(document);

    /// <inheritdoc />
    public bool Remove(string documentId) => _inner.Remove(documentId);

    /// <inheritdoc />
    public void Clear() => _inner.Clear();

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null, CancellationToken cancellationToken = default) =>
        _inner.Search(query, options, cancellationToken);

    /// <summary>
    /// Records that <paramref name="documentId"/> was chosen for <paramref name="query"/>, so a
    /// future similar query boosts it.
    /// </summary>
    public void Learn(string query, string documentId)
    {
        foreach (var (history, _) in _histories)
            history.Record(query, documentId);
    }

    /// <summary>
    /// Records that <paramref name="documentId"/> was chosen for <paramref name="query"/>, with
    /// an explicit reward (a reranker score, a query-passage similarity, a ROUGE on the generated
    /// answer, …). A reward of 1.0 is what the two-argument form adds.
    /// </summary>
    public void Learn(string query, string documentId, double reward)
    {
        foreach (var (history, _) in _histories)
            history.Record(query, documentId, reward);
    }

    /// <summary>
    /// Records a choice against a single channel, identified by its index in
    /// <see cref="Histories"/>. Use this when the reward belongs to one signal
    /// (a click, a conversion, a dwell time) rather than to all of them.
    /// </summary>
    public void Learn(string query, string documentId, int historyIndex, double reward = 1.0)
    {
        if (historyIndex < 0 || historyIndex >= _histories.Count)
            throw new ArgumentOutOfRangeException(nameof(historyIndex));

        _histories[historyIndex].History.Record(query, documentId, reward);
    }

    /// <inheritdoc />
    public long EstimateCandidateCount(ReadOnlySpan<char> query, SearchOptions options) =>
        _inner.EstimateCandidateCount(query, options);

    /// <summary>
    /// The payload of the engine underneath, which takes one: there is none, because one history
    /// serves every search. A private marker, so the name cannot be mistaken for a value that
    /// exists.
    /// </summary>
    private sealed class Unit;
}
