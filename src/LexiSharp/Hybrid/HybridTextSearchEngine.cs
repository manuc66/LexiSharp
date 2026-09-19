using LexiSharp.Core;

namespace LexiSharp.Hybrid;

/// <summary>
/// A federated search engine: queries a list of delegate engines, then merges their results
/// into one coherent ordering through an <see cref="IResultMerger"/>.
/// </summary>
/// <remarks>
/// Typical topology: a <b>hot</b> in-memory engine over a recent subset answers rapid-fire
/// requests, a <b>cold</b> persistent engine (e.g. the PostgreSQL provider) covers the long
/// tail, and the hybrid yields the intersection-friendly global ranking.
/// <para>
/// <b>Writes fan out</b>: <see cref="Index"/>, <see cref="Add"/>, <see cref="Remove"/> and
/// <see cref="Clear"/> are forwarded to every delegate unchanged. For selective routing
/// (e.g. "only the cold engine stores this huge document"), drive the delegate engines
/// directly instead — reading through the hybrid still works.
/// </para>
/// <para>
/// The merge is the recommended default; it re-scores the union of candidates with a single
/// scorer so all sources end up on the same numeric scale. A document whose final score is
/// <c>0</c> (convention: "not a match") is dropped, as are NaN/Infinity scores.
/// </para>
/// </remarks>
public sealed class HybridTextSearchEngine : ITextSearchEngine
{
    private readonly IReadOnlyList<ITextSearchEngine> _engines;
    private readonly IResultMerger _merger;
    private readonly int _minCandidatesPerEngine;

    /// <param name="engines">Source engines, queried in order. At least one is required.</param>
    /// <param name="merger">Merger strategy (default: <see cref="RerankingResultMerger"/>).</param>
    /// <param name="minCandidatesPerEngine">
    /// Minimum number of candidates requested from each engine so the merger has enough
    /// material to re-rank meaningfully; defaults to 50. Never below the final limit.
    /// </param>
    public HybridTextSearchEngine(
        IEnumerable<ITextSearchEngine> engines,
        IResultMerger? merger = null,
        int minCandidatesPerEngine = 50)
    {
        ArgumentNullException.ThrowIfNull(engines);

        _engines = engines.ToList();

        if (_engines.Count == 0)
            throw new ArgumentException("At least one engine is required.", nameof(engines));

        if (_engines.Distinct().Count() != _engines.Count)
            throw new ArgumentException("Engine instances must be distinct.", nameof(engines));

        _merger = merger ?? new RerankingResultMerger();
        _minCandidatesPerEngine = Math.Max(1, minCandidatesPerEngine);
    }

    /// <inheritdoc />
    public void Index(IEnumerable<SearchDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        foreach (var engine in _engines)
            engine.Index(documents);
    }

    /// <inheritdoc />
    public void Add(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        foreach (var engine in _engines)
            engine.Add(document);
    }

    /// <inheritdoc />
    public void Remove(string documentId)
    {
        foreach (var engine in _engines)
            engine.Remove(documentId);
    }

    /// <inheritdoc />
    public void Clear()
    {
        foreach (var engine in _engines)
            engine.Clear();
    }

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);

        options ??= SearchOptions.Default;

        if (options.Limit <= 0)
            return Array.Empty<SearchResult>();

        int candidateLimit = Math.Max(options.Limit, _minCandidatesPerEngine);

        var perEngine = new List<IReadOnlyList<SearchResult>>(_engines.Count);

        foreach (var engine in _engines)
        {
            perEngine.Add(engine.Search(query, options with { Limit = candidateLimit }));
        }

        var merged = _merger.Merge(perEngine, query);

        return merged
            .Where(x => !double.IsNaN(x.Score) && !double.IsInfinity(x.Score)
                        && x.Score >= options.MinimumScore && x.Score != 0)
            .OrderByDescending(x => x.Score)
            .Take(options.Limit)
            .ToList();
    }
}