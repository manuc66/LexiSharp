namespace LexiSharp.Core;

/// <summary>
/// A decorator engine that boosts or damps the matches of an inner engine after ranking.
/// </summary>
/// <remarks>
/// <para>
/// Writes (<see cref="Index"/>, <see cref="Add"/>, <see cref="Remove"/>, <see cref="Clear"/>)
/// are forwarded to the inner engine unchanged. <see cref="Search"/> runs the inner engine,
/// applies the caller-supplied <see cref="ScoreBoost"/> to every candidate score
/// (<c>score * Multiply + Add</c>), then re-sorts, re-applies the
/// <see cref="SearchOptions.MinimumScore"/> and re-trims to <see cref="SearchOptions.Limit"/>.
/// </para>
/// <para>
/// The boost is a function of the whole <see cref="SearchResult"/> — score and document — so it
/// can read <see cref="SearchDocument.Fields"/>/<see cref="SearchDocument.Category"/> (which no
/// ranking engine alone uses) to implement e.g. "weight the title field twice" or "boost this
/// category". Both <b>positive and negative</b> boosts are first-class: a factor above 1 or a
/// positive offset raises a match, a factor below 1 (damp) or a negative offset (penalty) lowers
/// it, factor 0 drops the document entirely (score-0 convention). A negative factor would invert
/// the ranking and is rejected, as are NaN/Infinite values.
/// </para>
/// <para>
/// The decorator only sees what the inner engine returns. To give boosted documents a chance
/// to surface, more candidates than the final limit are requested from the inner engine
/// (<c>maxCandidates</c>); a document ranked beyond that retrieval depth stays out
/// of reach no matter its boost.
/// </para>
/// <para>
/// This decorator does <b>not</b> forward the inner engine's capability interfaces
/// (<see cref="IFacetedSearchEngine"/>, <see cref="IDetailedSearchEngine"/>,
/// <see cref="IExplainableSearchEngine"/>): a facet page, a detail breakdown or an explanation
/// would describe the <i>pre-boost</i> scores, and a caller who reads them as the ranking this
/// engine returned would be misled, not informed. Wrap the inner engine <i>after</i> boosting if
/// those surfaces are needed.
/// </para>
/// </remarks>
public sealed class BoostedTextSearchEngine : ITextSearchEngine, IQueryCostProbe
{
    private readonly ITextSearchEngine _inner;
    private readonly Func<SearchResult, ScoreBoost> _boost;
    private readonly int _maxCandidates;
    private readonly RetrievalTelemetry _telemetry;

    /// <summary>
    /// Name this engine reports to <see cref="RetrievalTelemetry"/> and its metrics sinks.
    /// </summary>
    public const string EngineName = "BoostedTextSearchEngine";

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
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(boost);

        _inner = inner;
        _boost = boost;
        _maxCandidates = Math.Max(1, maxCandidates);
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
    public bool Remove(string documentId) => _inner.Remove(documentId);

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

        // The final page is [Offset, Offset + Limit) of the boosted ranking, and boosting can
        // promote any inner candidate into it — so the inner engine is asked for a pool covering
        // the skipped prefix plus the deepest candidate the boost is allowed to reach
        // (maxCandidates beyond the page).
        int basePool = Math.Max(options.Limit, _maxCandidates);
        int candidateLimit = options.Offset > int.MaxValue - basePool ? int.MaxValue : options.Offset + basePool;

        // Do not pre-filter with MinimumScore here: it must apply to the *boosted* score so a
        // damped match can fall out and a boosted one can get in. Same for Offset: the inner
        // ranking is only a candidate pool; the skip is cut from the boosted ordering.
        long retrieveStarted = instrumented ? RetrievalTelemetry.StartTimer() : 0;
        var candidates = _inner.Search(query, options with
        {
            Offset = 0,
            Limit = candidateLimit,
            MinimumScore = double.NegativeInfinity,
        });

        if (instrumented)
            _telemetry.StageCompleted(EngineName, "retrieve", candidates.Count, retrieveStarted);

        long boostStarted = instrumented ? RetrievalTelemetry.StartTimer() : 0;
        var results = new List<SearchResult>(candidates.Count);

        foreach (var candidate in candidates)
        {
            var boost = _boost(candidate);

            if (double.IsNaN(boost.Add) || double.IsInfinity(boost.Add)
                || double.IsNaN(boost.Multiply) || double.IsInfinity(boost.Multiply))
                throw new ArgumentException(
                    $"The boost must be finite, but got ({boost.Add}, {boost.Multiply}) for document '{candidate.DocumentId}'.");

            if (boost.Multiply < 0)
                throw new ArgumentException(
                    $"A negative multiplicative factor ({boost.Multiply}) would invert the ranking for document '{candidate.DocumentId}'. Use a damp in (0, 1) or a negative offset instead.");

            double boostedScore = (candidate.Score * boost.Multiply) + boost.Add;

            if (double.IsNaN(boostedScore) || double.IsInfinity(boostedScore) || boostedScore == 0)
                continue;

            if (boostedScore < options.MinimumScore)
                continue;

            results.Add(candidate with { Score = boostedScore });

            // Recorded per surviving candidate, so the step count follows maxCandidates - not the
            // corpus. A document the boost dropped (score 0 or below MinimumScore) records nothing:
            // it is absent from the ranking, so there is no after-score to show.
            options.Trace?.Record(new TraceStep(
                TraceStage.Boost,
                candidate.DocumentId,
                candidate.Score,
                boostedScore,
                SearchTrace.FormatBoost(boost)));
        }

        var page = results
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.DocumentId)
            .Skip(options.Offset)
            .Take(options.Limit)
            .ToList();

        if (instrumented)
        {
            // The boost stage consumed every candidate, while the search returned the cut page:
            // the two counts are deliberately different numbers.
            _telemetry.StageCompleted(EngineName, "boost", candidates.Count, boostStarted);
            _telemetry.SearchCompleted(EngineName, started, page.Count);
        }

        return page;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Boosting does not change how many candidates the query touches, so the estimate is
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