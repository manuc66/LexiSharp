using LexiSharp.Core;

namespace LexiSharp.Expansion;

/// <summary>
/// A decorator engine that runs an <see cref="IQueryTransformer"/> on the raw query, searches
/// the inner engine for every variant, and fuses the rankings — the query-side counterpart of
/// <see cref="ExpandingTextSearchEngine"/>, generalized from "widen with terms" to "arbitrary
/// rewrite, decomposition and generated variants" (HyDE included).
/// </summary>
/// <remarks>
/// <para>
/// Every variant is searched against the <b>same</b> inner engine, so one scorer's scale
/// compares them all: the fusion is each document's best score across the variants, followed
/// by the standard page cut. The window math follows <c>HybridTextSearchEngine</c> — each
/// variant is pooled to <c>Offset + max(Limit, minCandidatesPerVariant)</c> with <c>Offset</c>
/// disabled, then the page is cut only after the fusion, so a document ranked deep in one
/// variant can still own a page slot on the fused ranking.
/// </para>
/// <para>
/// A broken transformer never breaks a search (the <see cref="IQueryRouter"/> rule): a return
/// that is null, empty, blank-only, or a <c>Transform</c> that throws, falls back to the
/// untransformed query. Variants are deduplicated, and each variant search records its own
/// <see cref="SearchTrace"/> score stage; this engine adds one <c>merge</c> stage per page
/// document carrying the fused score.
/// </para>
/// <para>
/// Writes are forwarded to the inner engine unchanged, and the decorator owns no index, like
/// <see cref="ExpandingTextSearchEngine"/>. The capability surface is the same too: this engine
/// does not offer facets, detailed results or explanations, whose per-document contracts do not
/// survive a multi-variant fusion.
/// </para>
/// </remarks>
public sealed class TransformingTextSearchEngine : ITextSearchEngine, IQueryCostProbe
{
    /// <summary>Name this engine reports to <see cref="RetrievalTelemetry"/> and its metrics sinks.</summary>
    public const string EngineName = "TransformingTextSearchEngine";

    private readonly ITextSearchEngine _inner;
    private readonly IQueryTransformer _transformer;
    private readonly int _minCandidatesPerVariant;
    private readonly RetrievalTelemetry _telemetry;

    /// <param name="inner">The engine that ultimately scores each variant's search.</param>
    /// <param name="transformer">Produces the query variants to search.</param>
    /// <param name="minCandidatesPerVariant">
    /// Minimum number of candidates pooled from each variant so the fusion has enough material
    /// to rank meaningfully; defaults to 50. Never used below the caller's own <c>Limit</c>.
    /// </param>
    /// <param name="telemetry">
    /// Optional observability sink. Reports the search plus one <c>variant-N</c> stage per
    /// variant and one <c>merge</c> stage, so a slow search is attributable to a variant.
    /// Defaults to <see cref="RetrievalTelemetry.None"/>.
    /// </param>
    public TransformingTextSearchEngine(
        ITextSearchEngine inner,
        IQueryTransformer transformer,
        int minCandidatesPerVariant = 50,
        RetrievalTelemetry? telemetry = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(transformer);

        _inner = inner;
        _transformer = transformer;
        _minCandidatesPerVariant = Math.Max(1, minCandidatesPerVariant);
        _telemetry = telemetry ?? RetrievalTelemetry.None;
    }

    /// <summary>The transformer applied to every query.</summary>
    public IQueryTransformer Transformer => _transformer;

    /// <inheritdoc />
    public void Index(IEnumerable<SearchDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        _inner.Index(documents);
    }

    /// <inheritdoc />
    public void Add(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _inner.Add(document);
    }

    /// <inheritdoc />
    public void Remove(string documentId) => _inner.Remove(documentId);

    /// <inheritdoc />
    public void Clear() => _inner.Clear();

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Search(query.AsSpan(), options);
    }

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Search(ReadOnlySpan<char> query, SearchOptions? options = null)
    {
        var resolved = options ?? SearchOptions.Default;

        if (resolved.IsEmpty)
            return Array.Empty<SearchResult>();

        bool instrumented = _telemetry.IsEnabled;
        long started = instrumented ? RetrievalTelemetry.StartTimer() : 0;

        var variants = ResolveVariants(query.ToString());

        // Pool each variant up to the final window line, offset disabled, then cut the page
        // only after the fusion — the canonical hybrid window, applied to query variants.
        int basePool = Math.Max(resolved.Limit, _minCandidatesPerVariant);
        int candidateLimit = resolved.Offset > int.MaxValue - basePool
            ? int.MaxValue
            : resolved.Offset + basePool;

        // One scale (one inner engine, one scorer): keep each document's best score. A final
        // score of exactly 0 means "not a match" everywhere in LexiSharp, so it is dropped here
        // too, as are NaN/Infinity and anything below the caller's floor.
        var best = new Dictionary<string, (double Score, SearchDocument Document)>(StringComparer.Ordinal);

        for (int i = 0; i < variants.Count; i++)
        {
            long variantStarted = instrumented ? RetrievalTelemetry.StartTimer() : 0;
            var results = _inner.Search(variants[i], resolved with { Offset = 0, Limit = candidateLimit });

            if (instrumented)
                _telemetry.StageCompleted(EngineName, $"variant-{i}", results.Count, variantStarted);

            foreach (var result in results)
            {
                if (double.IsNaN(result.Score) || double.IsInfinity(result.Score)
                    || result.Score == 0 || result.Score < resolved.MinimumScore)
                {
                    continue;
                }

                if (!best.TryGetValue(result.DocumentId, out var existing) || result.Score > existing.Score)
                    best[result.DocumentId] = (result.Score, result.Document);
            }
        }

        // Best first, ties broken by ordinal document id — the same total order the stock engine
        // produces, so the fused ranking is reproducible and stable across machines.
        var ranked = new List<KeyValuePair<string, (double, SearchDocument)>>(best);
        ranked.Sort(static (left, right) =>
        {
            int byScore = right.Value.Item1.CompareTo(left.Value.Item1);
            return byScore != 0 ? byScore : string.CompareOrdinal(left.Key, right.Key);
        });

        int skip = Math.Min(resolved.Offset, ranked.Count);
        int count = Math.Min(ranked.Count - skip, resolved.Limit);
        var page = new SearchResult[count];

        for (int i = 0; i < count; i++)
        {
            var entry = ranked[skip + i];
            page[i] = new SearchResult(entry.Key, entry.Value.Item1, entry.Value.Item2);
        }

        RecordMergeStage(resolved.Trace, page);

        if (instrumented)
        {
            _telemetry.StageCompleted(EngineName, "merge", best.Count, started);
            _telemetry.SearchCompleted(EngineName, started, page.Length);
        }

        return page;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The estimate is the sum of the variants' estimates, because this engine runs every
    /// variant — its work is bounded by the union of theirs, not by any single variant.
    /// </remarks>
    public long EstimateCandidateCount(ReadOnlySpan<char> query, SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.IsEmpty || _inner is not IQueryCostProbe probe)
            return options.IsEmpty ? 0 : long.MaxValue;

        var variants = ResolveVariants(query.ToString());
        long total = 0;

        foreach (var variant in variants)
        {
            long estimate = probe.EstimateCandidateCount(variant, options);

            if (estimate < 0)
                estimate = 0;

            if (total > long.MaxValue - estimate)
                return long.MaxValue;

            total += estimate;
        }

        return total;
    }

    /// <summary>
    /// The variants to search: the transformer's output, deduplicated and stripped of blank
    /// entries — or the untransformed query when the transformer produced nothing usable or
    /// threw, because a broken transformer never breaks a search.
    /// </summary>
    private IReadOnlyList<string> ResolveVariants(string query)
    {
        IReadOnlyList<string> produced;

        try
        {
            produced = _transformer.Transform(query);
        }
        catch
        {
            return [query];
        }

        if (produced is null || produced.Count == 0)
            return [query];

        var variants = new List<string>(produced.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var variant in produced)
        {
            if (!string.IsNullOrWhiteSpace(variant) && seen.Add(variant))
                variants.Add(variant);
        }

        return variants.Count == 0 ? [query] : variants;
    }

    /// <summary>
    /// Records one <see cref="TraceStage.Merge"/> step per document of the fused page, so a
    /// trace that already holds each variant's score stage also holds the fusion that replaced
    /// them. The fused score is reported as both before and after: the fusion is a per-document
    /// best-over-variants, not a per-document transform, so there is no meaningful before-score.
    /// </summary>
    private void RecordMergeStage(SearchTrace? trace, IReadOnlyList<SearchResult> page)
    {
        if (trace is null)
            return;

        for (int i = 0; i < page.Count; i++)
        {
            SearchResult result = page[i];
            trace.Record(new TraceStep(
                TraceStage.Merge,
                result.DocumentId,
                result.Score,
                result.Score,
                _transformer.Name));
        }
    }
}