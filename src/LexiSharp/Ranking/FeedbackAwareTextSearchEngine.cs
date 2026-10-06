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
    private readonly QueryFeedbackHistory _history;

    /// <summary>
    /// Name this engine reports to <see cref="RetrievalTelemetry"/>.
    /// </summary>
    public const string EngineName = FeedbackAwareTextSearchEngine<Unit>.EngineName;

    /// <param name="inner">The primary search engine.</param>
    /// <param name="history">The feedback history to learn from and contribute to.</param>
    /// <param name="maxBoost">
    /// The maximum additive boost a document can receive from the feedback channel. Default: 1.0.
    /// </param>
    /// <param name="minSimilarity">
    /// Minimum token-overlap similarity for a recorded query to reach the current one. Default: 0.3.
    /// </param>
    public FeedbackAwareTextSearchEngine(
        ITextSearchEngine inner,
        QueryFeedbackHistory history,
        double maxBoost = 1.0,
        double minSimilarity = 0.3)
    {
        ArgumentNullException.ThrowIfNull(history);

        _history = history;
        _inner = new FeedbackAwareTextSearchEngine<Unit>(inner, _ => history, maxBoost, minSimilarity);
    }

    /// <summary>
    /// The feedback history this engine learns from and contributes to.
    /// </summary>
    public QueryFeedbackHistory History => _history;

    /// <inheritdoc />
    public void Index(IEnumerable<SearchDocument> documents) => _inner.Index(documents);

    /// <inheritdoc />
    public void Add(SearchDocument document) => _inner.Add(document);

    /// <inheritdoc />
    public bool Remove(string documentId) => _inner.Remove(documentId);

    /// <inheritdoc />
    public void Clear() => _inner.Clear();

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null) =>
        _inner.Search(query, options);

    /// <summary>
    /// Records that <paramref name="documentId"/> was chosen for <paramref name="query"/>, so a
    /// future similar query boosts it.
    /// </summary>
    public void Learn(string query, string documentId) => _history.Record(query, documentId);

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
