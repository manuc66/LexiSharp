using System.Globalization;
using LexiSharp.Core;

// CA1859 ("use a concrete type instead of the interface") is suppressed on the lines
// below. The interface is the published return type: a List<T> or an array in its place
// would hand callers a mutable collection through a contract that says they cannot have
// one, and what it saves is a single interface dispatch per call, which no measurement in
// docs/benchmarks.md attributes time to.

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
public sealed class HybridTextSearchEngine : ITextSearchEngine, IDetailedSearchEngine
{
    private readonly IReadOnlyList<ITextSearchEngine> _engines; // NOSONAR:CA1859
    private readonly IReadOnlyList<string> _sourceNames;
    private readonly IResultMerger _merger;
    private readonly int _minCandidatesPerEngine;
    private readonly RetrievalTelemetry _telemetry;

    /// <summary>
    /// Name this engine reports to <see cref="RetrievalTelemetry"/> and its metrics sinks.
    /// </summary>
    public const string EngineName = "HybridTextSearchEngine";

    /// <param name="engines">Source engines, queried in order. At least one is required.</param>
    /// <param name="merger">Merger strategy (default: <see cref="RerankingResultMerger"/>).</param>
    /// <param name="minCandidatesPerEngine">
    /// Minimum number of candidates requested from each engine so the merger has enough
    /// material to re-rank meaningfully; defaults to 50. Never below the final limit.
    /// </param>
    /// <param name="sourceNames">
    /// Labels identifying each engine in <see cref="DetailedSearchResult.Contributions"/> from
    /// <see cref="SearchWithDetails"/> (e.g. <c>["lexical", "semantic"]</c>). Must match
    /// <paramref name="engines"/> in count and be unique; defaults to <c>"engine-0"</c>, <c>"engine-1"</c>, ...
    /// </param>
    /// <param name="telemetry">
    /// Optional observability sink. Reports the search itself plus one <c>source</c> stage per
    /// delegate engine and one <c>merge</c> stage, so a slow fusion is attributable to a source.
    /// Defaults to <see cref="RetrievalTelemetry.None"/>.
    /// </param>
    public HybridTextSearchEngine(
        IEnumerable<ITextSearchEngine> engines,
        IResultMerger? merger = null,
        int minCandidatesPerEngine = 50,
        IReadOnlyList<string>? sourceNames = null,
        RetrievalTelemetry? telemetry = null)
    {
        ArgumentNullException.ThrowIfNull(engines);

        _engines = engines.ToList();

        if (_engines.Count == 0)
            throw new ArgumentException("At least one engine is required.", nameof(engines));

        if (_engines.Distinct().Count() != _engines.Count)
            throw new ArgumentException("Engine instances must be distinct.", nameof(engines));

        if (sourceNames is not null)
        {
            if (sourceNames.Count != _engines.Count)
                throw new ArgumentException("sourceNames must contain exactly one label per engine.", nameof(sourceNames));

            if (sourceNames.Distinct(StringComparer.Ordinal).Count() != sourceNames.Count)
                throw new ArgumentException("sourceNames must not contain duplicates.", nameof(sourceNames));

            if (sourceNames.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("sourceNames must not contain empty labels.", nameof(sourceNames));

            _sourceNames = sourceNames;
        }
        else
        {
            _sourceNames = Enumerable.Range(0, _engines.Count)
                .Select(i => $"engine-{i}")
                .ToList();
        }

        _merger = merger ?? new RerankingResultMerger();
        _minCandidatesPerEngine = Math.Max(1, minCandidatesPerEngine);
        _telemetry = telemetry ?? RetrievalTelemetry.None;
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
    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null) =>
        SearchWithDetails(query, options)
            .Select(x => x.ToSearchResult())
            .ToList();

    /// <summary>
    /// Same query as <see cref="Search"/>, but every result also carries the per-source score
    /// breakdown that fed the final rank (see <see cref="DetailedSearchResult.Contributions"/>).
    /// Part of the opt-in <see cref="IDetailedSearchEngine"/> capability.
    /// </summary>
    /// <remarks>
    /// The contributions are the raw scores each delegate engine assigned <i>before</i> merging,
    /// keyed by the <c>sourceNames</c> labels configured at construction. A source that did not
    /// return the document is absent from the dictionary. Scores come from heterogeneous scales;
    /// the breakdown is an instrument for debugging and surface UI, not a substitute for the
    /// merged ordering.
    /// </remarks>
    public IReadOnlyList<DetailedSearchResult> SearchWithDetails(string query, SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);

        options ??= SearchOptions.Default;

        bool instrumented = _telemetry.IsEnabled;
        long started = instrumented ? RetrievalTelemetry.StartTimer() : 0;

        if (options.IsEmpty)
            return Array.Empty<DetailedSearchResult>();

        // The page is cut from the merged global ranking, not from each delegate's ordering:
        // pool every engine up to Offset + max(Limit, minCandidates) with Offset disabled, then
        // skip/trim after the merge so a document ranked deep locally can still own a global
        // page slot.
        int basePool = Math.Max(options.Limit, _minCandidatesPerEngine);
        int candidateLimit = options.Offset > int.MaxValue - basePool ? int.MaxValue : options.Offset + basePool;

        var perEngine = new List<IReadOnlyList<SearchResult>>(_engines.Count);

        for (int i = 0; i < _engines.Count; i++)
        {
            long sourceStarted = instrumented ? RetrievalTelemetry.StartTimer() : 0;
            var results = _engines[i].Search(query, options with { Offset = 0, Limit = candidateLimit });

            if (instrumented)
            {
                // Labelled by the caller's sourceNames, so a slow lane is named the way the
                // application named it rather than by a position in the list.
                _telemetry.StageCompleted(EngineName, _sourceNames[i], results.Count, sourceStarted);
            }

            perEngine.Add(results);
        }

        var contributionsByDocument = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);

        for (int i = 0; i < _engines.Count; i++)
        {
            string source = _sourceNames[i];

            foreach (var result in perEngine[i])
            {
                if (!contributionsByDocument.TryGetValue(result.DocumentId, out var sources))
                {
                    sources = new Dictionary<string, double>(StringComparer.Ordinal);
                    contributionsByDocument[result.DocumentId] = sources;
                }

                sources[source] = result.Score;
            }
        }

        long mergeStarted = instrumented ? RetrievalTelemetry.StartTimer() : 0;
        var merged = _merger.Merge(perEngine, query);

        var page = merged
            .Where(x => !double.IsNaN(x.Score) && !double.IsInfinity(x.Score)
                        && x.Score >= options.MinimumScore && x.Score != 0)
            .OrderByDescending(x => x.Score)
            .Skip(options.Offset)
            .Take(options.Limit)
            .Select(x => new DetailedSearchResult(
                x.DocumentId,
                x.Score,
                x.Document,
                (IReadOnlyDictionary<string, double>)
                    (contributionsByDocument.GetValueOrDefault(x.DocumentId) ?? new Dictionary<string, double>())))
            .ToList();

        RecordMergeStage(options.Trace, page);

        if (instrumented)
        {
            // The merge consumes every source's pool, so its input size is the candidates that
            // came back from the lanes, not the size of the final page.
            int mergedInput = 0;
            for (int i = 0; i < perEngine.Count; i++)
                mergedInput += perEngine[i].Count;

            _telemetry.StageCompleted(EngineName, "merge", mergedInput, mergeStarted);
            _telemetry.SearchCompleted(EngineName, started, page.Count);
        }

        return page;
    }

    /// <summary>
    /// Records one <see cref="TraceStage.Merge"/> step per document of the final page, with the
    /// per-source scores that fed the merge as the step detail. The merged score is reported as
    /// both before and after: a merger is not a per-document transform but a re-ranking over the
    /// union, so there is no meaningful single before-score.
    /// </summary>
    private void RecordMergeStage(SearchTrace? trace, IReadOnlyList<DetailedSearchResult> page) // NOSONAR:CA1859
    {
        if (trace is null)
            return;

        for (int i = 0; i < page.Count; i++)
        {
            DetailedSearchResult result = page[i];
            trace.Record(new TraceStep(
                TraceStage.Merge,
                result.DocumentId,
                result.Score,
                result.Score,
                FormatContributions(result.Contributions)));
        }
    }

    /// <summary>
    /// Renders per-source scores as an invariant, stable-order detail string (sources are sorted
    /// by label so a trace is comparable and assertable).
    /// </summary>
    private static string FormatContributions(IReadOnlyDictionary<string, double> contributions)
    {
        if (contributions.Count == 0)
            return string.Empty;

        var labels = new List<string>(contributions.Keys);
        labels.Sort(StringComparer.Ordinal);

        var builder = new System.Text.StringBuilder();

        for (int i = 0; i < labels.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(labels[i])
                .Append('=')
                .Append(contributions[labels[i]].ToString("0.####", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}