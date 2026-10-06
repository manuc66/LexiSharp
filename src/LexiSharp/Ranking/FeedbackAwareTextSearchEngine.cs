using LexiSharp.Core;

namespace LexiSharp.Ranking;

/// <summary>
/// A decorator engine that augments a primary search engine with a learned
/// query → document feedback channel.
/// </summary>
/// <remarks>
/// Wraps any <see cref="ITextSearchEngine"/> and boosts documents that the
/// <see cref="QueryFeedbackHistory"/> associates with the current query.
/// <para>
/// The feedback boost is <b>additive and bounded</b>: it never reorders the
/// primary ranking on its own, it only gives a bonus to documents that the
/// history supports. The boost is proportional to the association strength
/// (how often this document was chosen for similar queries).
/// </para>
/// <para>
/// The decorator also provides a <see cref="Learn"/> method to record new
/// associations: when the user selects a document, the application calls
/// <see cref="Learn"/> to feed that choice back into the history.
/// </para>
/// </remarks>
public sealed class FeedbackAwareTextSearchEngine : ITextSearchEngine
{
    private readonly ITextSearchEngine _inner;
    private readonly QueryFeedbackHistory _history;
    private readonly double _maxBoost;
    private readonly double _minSimilarity;

    /// <param name="inner">The primary search engine.</param>
    /// <param name="history">The feedback history to learn from and contribute to.</param>
    /// <param name="maxBoost">
    /// The maximum additive boost a document can receive from the feedback channel.
    /// Default: 1.0. The boost is added to the primary score after ranking.
    /// </param>
    /// <param name="minSimilarity">
    /// Minimum token-overlap similarity for fuzzy matching in the feedback history.
    /// Default: 0.3.
    /// </param>
    public FeedbackAwareTextSearchEngine(
        ITextSearchEngine inner,
        QueryFeedbackHistory history,
        double maxBoost = 1.0,
        double minSimilarity = 0.3)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(history);

        if (maxBoost < 0)
            throw new ArgumentOutOfRangeException(nameof(maxBoost), maxBoost,
                "Max boost must be non-negative.");

        _inner = inner;
        _history = history;
        _maxBoost = maxBoost;
        _minSimilarity = minSimilarity;
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
    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);

        options ??= SearchOptions.Default;

        if (options.IsEmpty)
            return Array.Empty<SearchResult>();

        var results = _inner.Search(query, options);

        if (results.Count == 0 || _history.Count == 0)
            return results;

        // Get fuzzy associations for this query
        var associations = _history.GetFuzzyAssociations(query, _minSimilarity);

        if (associations.Count == 0)
            return results;

        // Build a lookup for fast access
        var associationMap = associations.ToDictionary(x => x.DocumentId, x => x.Strength);

        // Apply boosts
        var boostedResults = new List<SearchResult>(results.Count);

        foreach (var result in results)
        {
            if (associationMap.TryGetValue(result.DocumentId, out double strength))
            {
                double boost = strength * _maxBoost;
                boostedResults.Add(result with { Score = result.Score + boost });
            }
            else
            {
                boostedResults.Add(result);
            }
        }

        // Re-sort by boosted score, then by document id for stability
        return boostedResults
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.DocumentId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Records that the user chose <paramref name="documentId"/> for
    /// <paramref name="query"/>. This feeds back into the history so future
    /// similar queries will boost this document.
    /// </summary>
    public void Learn(string query, string documentId)
    {
        _history.Record(query, documentId);
    }
}
