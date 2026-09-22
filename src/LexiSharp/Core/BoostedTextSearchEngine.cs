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
/// </remarks>
public sealed class BoostedTextSearchEngine : ITextSearchEngine
{
    private readonly ITextSearchEngine _inner;
    private readonly Func<SearchResult, ScoreBoost> _boost;
    private readonly int _maxCandidates;

    /// <param name="inner">The engine producing the base ranking (never disposed by this wrapper).</param>
    /// <param name="boost">Signed score adjustment per <see cref="SearchResult"/>.</param>
    /// <param name="maxCandidates">
    /// Number of candidates requested from the inner engine so boosting has room to re-order;
    /// defaults to 50. Never below the final limit.
    /// </param>
    public BoostedTextSearchEngine(ITextSearchEngine inner, Func<SearchResult, ScoreBoost> boost, int maxCandidates = 50)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(boost);

        _inner = inner;
        _boost = boost;
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

        // The final page is [Offset, Offset + Limit) of the boosted ranking, and boosting can
        // promote any inner candidate into it — so the inner engine is asked for a pool covering
        // the skipped prefix plus the deepest candidate the boost is allowed to reach
        // (maxCandidates beyond the page).
        int basePool = Math.Max(options.Limit, _maxCandidates);
        int candidateLimit = options.Offset > int.MaxValue - basePool ? int.MaxValue : options.Offset + basePool;

        // Do not pre-filter with MinimumScore here: it must apply to the *boosted* score so a
        // damped match can fall out and a boosted one can get in. Same for Offset: the inner
        // ranking is only a candidate pool; the skip is cut from the boosted ordering.
        var candidates = _inner.Search(query, options with
        {
            Offset = 0,
            Limit = candidateLimit,
            MinimumScore = double.NegativeInfinity,
        });

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

            double boostedScore = candidate.Score * boost.Multiply + boost.Add;

            if (double.IsNaN(boostedScore) || double.IsInfinity(boostedScore) || boostedScore == 0)
                continue;

            if (boostedScore < options.MinimumScore)
                continue;

            results.Add(candidate with { Score = boostedScore });
        }

        return results
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.DocumentId)
            .Skip(options.Offset)
            .Take(options.Limit)
            .ToList();
    }
}