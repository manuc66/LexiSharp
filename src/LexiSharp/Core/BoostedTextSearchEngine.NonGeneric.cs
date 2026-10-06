namespace LexiSharp.Core;

/// <summary>
/// A decorator engine that boosts or damps the matches of an inner engine after ranking, from a
/// function of the candidate alone.
/// </summary>
/// <remarks>
/// <para>
/// This is the form to reach for when the boost can be decided from the document and its score —
/// "weight the title field twice", "boost this category", "damp anything stale". It forwards to
/// <see cref="BoostedTextSearchEngine{TPayload}"/> with no payload, so there is one ranking
/// implementation rather than two.
/// </para>
/// <para>
/// Reach for <see cref="BoostedTextSearchEngine{TPayload}"/> instead when the boost must see the
/// search as a whole — whether the base ranking was already clear, or what the caller's payload
/// says. A boost here cannot decline based on the ranking, because it is not shown the ranking.
/// </para>
/// </remarks>
public sealed class BoostedTextSearchEngine : ITextSearchEngine, IQueryCostProbe
{
    private readonly BoostedTextSearchEngine<Nothing> _inner;

    /// <summary>
    /// Name this engine reports to <see cref="RetrievalTelemetry"/> and its metrics sinks.
    /// </summary>
    public const string EngineName = BoostedTextSearchEngine<Nothing>.EngineName;

    /// <param name="inner">The engine producing the base ranking (never disposed by this wrapper).</param>
    /// <param name="boost">Signed score adjustment per <see cref="SearchResult"/>.</param>
    /// <param name="maxCandidates">
    /// Number of candidates requested from the inner engine so boosting has room to re-order;
    /// defaults to 50. Never below the final limit.
    /// </param>
    /// <param name="telemetry">
    /// Optional observability sink. Reports a <c>retrieve</c> stage for the inner engine and a
    /// <c>boost</c> stage. Defaults to <see cref="RetrievalTelemetry.None"/>.
    /// </param>
    public BoostedTextSearchEngine(
        ITextSearchEngine inner,
        Func<SearchResult, ScoreBoost> boost,
        int maxCandidates = 50,
        RetrievalTelemetry? telemetry = null)
    {
        ArgumentNullException.ThrowIfNull(boost);

        _inner = new BoostedTextSearchEngine<Nothing>(
            inner,
            (context, candidate) => boost(candidate),
            maxCandidates,
            telemetry);
    }

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

    /// <inheritdoc />
    /// <remarks>
    /// Boosting does not change how many candidates the query touches, so the estimate is
    /// delegated to the inner engine when it can estimate; an inner without
    /// <see cref="IQueryCostProbe"/> makes the decorator a routing last resort
    /// (<see cref="long.MaxValue"/>).
    /// </remarks>
    public long EstimateCandidateCount(ReadOnlySpan<char> query, SearchOptions options) =>
        _inner.EstimateCandidateCount(query, options);

    /// <summary>
    /// The payload type of the engine underneath, which takes one: there is none. A private
    /// marker, so the name cannot be mistaken for a value that exists.
    /// </summary>
    private sealed class Nothing;
}
