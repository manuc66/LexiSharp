using System.Text;
using System.Diagnostics.CodeAnalysis;
using LexiSharp.Core;
using LexiSharp.Expansion;
using LexiSharp.Highlighting;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;

namespace LexiSharp;

/// <summary>
/// The drop-in entry point of the library: an opinionated, typed search index that maps your
/// own documents to the engine internals and back — from "a list of objects" to "working
/// search" in a few lines.
/// </summary>
/// <example>
/// <code>
/// var search = new LexiSharpIndex&lt;MyDocument&gt;(o =&gt;
/// {
///     o.Id = d =&gt; d.Id;
///     o.Text = d =&gt; d.Body;
/// });
///
/// search.Add(documents);
/// IReadOnlyList&lt;LexiSharpHit&lt;MyDocument&gt;&gt; hits = search.Search("architecture distributed systems");
/// </code>
/// </example>
/// <typeparam name="TDocument">The caller's document type; must be a reference type.</typeparam>
/// <remarks>
/// <para>
/// The same single-writer contract as <see cref="ITextSearchEngine"/> applies:
/// <see cref="Add"/>, <see cref="AddRange"/>, <see cref="Index"/>, <see cref="Remove"/>,
/// <see cref="Clear"/>, <see cref="Commit"/> and <see cref="Merge"/> must not run concurrently with
/// each other or with a <see cref="Search(string, LexiSharpQueryOptions?)"/>; concurrent searches are safe.
/// </para>
/// <para>
/// With <see cref="LexiSharpIndexOptions{TDocument}.Directory"/> set the index is a
/// <see cref="SegmentedTextIndex"/>: <see cref="Commit"/> writes the pending writes as one segment and
/// <see cref="Merge"/> reclaims the deleted ones, each returning a new store that this facade then
/// wraps. <see cref="Engine"/> and <see cref="TextIndex"/> report the current ones, so a caller that
/// captured either before a commit must read it again — <see cref="Search(string, LexiSharpQueryOptions?)"/> reads the current pipeline
/// itself and needs nothing. A <see cref="SegmentSourceMode.Mapped"/> index holds views over its
/// segment files and must be <see cref="Dispose"/>d.
/// </para>
/// </remarks>
public sealed class LexiSharpIndex<TDocument> : IDisposable
    where TDocument : class
{
    private readonly Func<TDocument, string> _id;
    private readonly Func<TDocument, string> _text;
    private readonly Func<TDocument, string?> _category;
    private readonly Func<TDocument, IReadOnlyDictionary<string, string>?> _fields;
    private readonly Func<TDocument, IReadOnlyDictionary<string, string>?> _textFields;
    private readonly bool _enableFuzzy;
    private readonly bool _fuzzyOnlyOutOfVocabulary;

    private readonly ITokenizer _tokenizer;
    private readonly ISpanTokenizer? _spanTokenizer;
    private readonly ITextScorer _scorer;
    private readonly SynonymMap? _synonyms;
    private readonly RetrievalTelemetry? _telemetry;
    private readonly IReranker? _reranker;
    private readonly int _rerankerMaxCandidates;
    private readonly IQueryTransformer? _queryTransformer;
    private readonly IChunkContextEnricher? _contextEnricher;
    private readonly Func<string, TDocument?>? _load;

    private readonly Dictionary<string, TDocument> _documents = new(StringComparer.Ordinal);

    // A store-backed facade owns a SegmentedTextIndex and the pipeline built over it. Both change
    // identity on Commit()/Merge(), which return a new store rather than mutating this one, so
    // neither is readonly: the facade rebuilds the pipeline around the new store in one step.
    private SegmentedTextIndex? _store;
    private ITextIndex _index;
    private RankedTextSearchEngine _baseEngine;
    private ITextSearchEngine _engine;

    /// <param name="configure">
    /// Optional configuration. <see cref="LexiSharpIndexOptions{TDocument}.Id"/> and
    /// <see cref="LexiSharpIndexOptions{TDocument}.Text"/> default to the obvious mapping when
    /// <typeparamref name="TDocument"/> is <see cref="string"/> or <see cref="SearchDocument"/>;
    /// for any other type both are required.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <typeparamref name="TDocument"/> is neither <see cref="string"/> nor
    /// <see cref="SearchDocument"/> and <see cref="LexiSharpIndexOptions{TDocument}.Id"/> or
    /// <see cref="LexiSharpIndexOptions{TDocument}.Text"/> is not configured.
    /// </exception>
    public LexiSharpIndex(Action<LexiSharpIndexOptions<TDocument>>? configure = null)
    {
        var options = new LexiSharpIndexOptions<TDocument>();
        configure?.Invoke(options);

        _id = options.Id ?? DefaultId() ?? throw NewMappingError("Id");
        _text = options.Text ?? DefaultText() ?? throw NewMappingError("Text");
        _category = options.Category ?? DefaultCategory() ?? NoCategory;
        _fields = options.Fields ?? DefaultFields() ?? NoFields;
        _textFields = options.TextFields ?? DefaultTextFields() ?? NoFields;

        _enableFuzzy = options.EnableFuzzy;
        _fuzzyOnlyOutOfVocabulary = options.FuzzyOnlyOutOfVocabulary;

        var tokenizer = options.Tokenizer ?? BuildDefaultTokenizer(options);
        _spanTokenizer = tokenizer as ISpanTokenizer;
        _tokenizer = tokenizer;

        _scorer = options.Scorer;
        _synonyms = options.Synonyms;
        _telemetry = options.Telemetry;
        _reranker = options.Reranker;
        _rerankerMaxCandidates = options.RerankerMaxCandidates;
        _queryTransformer = options.QueryTransformer;
        _contextEnricher = options.ContextEnricher;

        if (options.Directory is not null)
        {
            if (options.TermExpander is not null)
            {
                throw new ArgumentException(
                    "a persistent index cannot carry an index-time term expander: ExpansionTextIndex builds its "
                    + "own in-memory base index and cannot wrap a store, so the expanded terms would never reach "
                    + "disk. Use an in-memory index, or apply the expansion at query time.",
                    nameof(configure));
            }

            _load = options.Load ?? (typeof(TDocument) == typeof(SearchDocument) ? LoadFromIndex : null);
            _store = new SegmentedTextIndex(
                options.Directory, tokenizer, options.SourceMode, options.DecodedCacheEntries);
            _index = Decorate(_store);
        }
        else
        {
            // Index-time document transformation composes in layers: expansion (index-time
            // widening) is applied first, inside the enriching index, so the enricher's own text
            // is what the expander sees — the two never fight over the same document.
            ITextIndex baseIndex = options.TermExpander is null
                ? new InMemoryTextIndex(tokenizer)
                : new ExpansionTextIndex(options.TermExpander, tokenizer);
            _index = options.ContextEnricher is null
                ? baseIndex
                : new ContextEnrichingIndex(baseIndex, options.ContextEnricher);
        }

        BuildPipeline();
    }

    /// <summary>
    /// Builds the search pipeline over <c>_index</c>: the ranked engine, then the optional reranker
    /// and query transformer, in the order they decorate.
    /// </summary>
    /// <remarks>
    /// A method rather than constructor-only code because <see cref="Commit"/> and <see cref="Merge"/>
    /// replace the index with a new store, and every layer above it holds a reference to the one it
    /// was built with. Rebuilding here is what keeps <see cref="Engine"/> from serving the store the
    /// commit replaced.
    /// </remarks>
    [MemberNotNull(nameof(_baseEngine), nameof(_engine))]
    private void BuildPipeline()
    {
        _baseEngine = new RankedTextSearchEngine(
            _index, _scorer, _tokenizer, _synonyms, _telemetry);
        _engine = _reranker is null
            ? _baseEngine
            : new RerankedTextSearchEngine(
                _baseEngine, _reranker, _rerankerMaxCandidates, _telemetry);

        // The transformer decorates last, so every variant runs the full inner pipeline
        // (retrieval, rerank, routing) and the fusion sees each one's final page pool.
        if (_queryTransformer is not null)
        {
            _engine = new TransformingTextSearchEngine(
                _engine, _queryTransformer, telemetry: _telemetry);
        }
    }

    private ITextIndex Decorate(ITextIndex baseIndex) =>
        _contextEnricher is null ? baseIndex : new ContextEnrichingIndex(baseIndex, _contextEnricher);

    private TDocument? LoadFromIndex(string documentId) =>
        _index.TryGetDocument(documentId, out var document) ? (TDocument)(object)document : null;


    /// <summary>Number of indexed documents.</summary>
    public int Count => _index.Count;

    /// <summary>
    /// The ids of every indexed document. In insertion order for an in-memory index; the store's own
    /// live order for a persistent one, which is what a reopened store can report.
    /// </summary>
    public IReadOnlyCollection<string> DocumentIds =>
        _store is null
            ? _documents.Keys
            : _index.Documents.Select(document => document.Id).ToArray();

    /// <summary>A snapshot of the corpus statistics (documents, vocabulary, tokens, ...).</summary>
    public TextIndexStatistics Statistics => _index.GetStatistics();

    /// <summary>
    /// The underlying index, for callers that need per-field statistics or the raw contract.
    /// </summary>
    /// <remarks>
    /// Named for what it holds rather than <c>Index</c>, which is the batch-indexing method. Exposed
    /// so a field-aware scorer can be built over the same index the facade searches, rather than a
    /// second one the caller has to keep in step by hand.
    /// </remarks>
    public ITextIndex TextIndex => _index;

    /// <summary>The underlying search engine, for callers that need the raw interface.</summary>
    /// <remarks>
    /// <b>Re-read this after <see cref="Commit"/> or <see cref="Merge"/>.</b> Both replace the store
    /// with a new one, and this facade rebuilds the pipeline around it; an engine captured before the
    /// call still points at the store that was replaced and would serve the page that store held.
    /// <see cref="Search(string, LexiSharpQueryOptions?)"/> always reads the current one, so a caller that searches through the facade
    /// needs nothing here.
    /// </remarks>
    public ITextSearchEngine Engine => _engine;

    /// <summary>
    /// Whether this index is a <see cref="SegmentedTextIndex"/> over
    /// <see cref="LexiSharpIndexOptions{TDocument}.Directory"/> rather than an in-memory index.
    /// </summary>
    public bool IsPersistent => _store is not null;

    /// <summary>
    /// Whether the tombstone count has passed the share of documents that makes a merge worth it.
    /// Always <c>false</c> for an in-memory index.
    /// </summary>
    public bool ShouldMerge() => _store?.ShouldMerge() ?? false;

    /// <summary>
    /// Makes the pending writes durable: the overlay and the tombstones are compacted and written as
    /// one segment, and the pipeline is rebuilt around the store that results.
    /// </summary>
    /// <remarks>
    /// This is the point at which the corpus statistics stop being frozen — the new segment's
    /// <c>N</c>, average length and document frequencies are its own and no longer count the deleted
    /// documents — so <b>every surviving score changes</b>. It is a batch operation, not something a
    /// query triggers. A no-op on an in-memory index, which is already durable in the only sense it
    /// has.
    /// </remarks>
    public void Commit()
    {
        if (_store is not null)
            SwapStore(_store.Commit());
    }

    /// <summary>
    /// Compacts the store in memory and rebuilds the pipeline around the result, without writing to
    /// disk. A no-op on an in-memory index.
    /// </summary>
    /// <remarks>
    /// Same effect on the scores as <see cref="Commit"/> — the statistics stop being frozen — without
    /// the segment write. Use it when the tombstones are worth reclaiming but the bytes are not worth
    /// writing yet.
    /// </remarks>
    public void Merge()
    {
        if (_store is not null)
            SwapStore(_store.Merge());
    }

    /// <summary>
    /// Replaces the store with <paramref name="next"/>, rebuilds the pipeline around it, and releases
    /// the mappings the previous store held.
    /// </summary>
    /// <remarks>
    /// The previous store is disposed because a <see cref="SegmentSourceMode.Mapped"/> store holds a
    /// view over the segment files and nothing reads it once the pipeline points at the new one. The
    /// caller's single-writer contract covers this: a commit must not run concurrently with a search,
    /// so no search is reading the pipeline being replaced.
    /// </remarks>
    private void SwapStore(SegmentedTextIndex next)
    {
        var previous = _store;

        _store = next;
        _index = Decorate(next);
        BuildPipeline();

        if (!ReferenceEquals(previous, next))
            previous?.Dispose();
    }

    /// <summary>
    /// Releases the segment mappings a <see cref="SegmentSourceMode.Mapped"/> store holds. Idempotent,
    /// and a no-op on an in-memory index or an <see cref="SegmentSourceMode.Array"/> store, neither of
    /// which holds anything that needs releasing.
    /// </summary>
    public void Dispose()
    {
        var store = _store;

        _store = null;
        store?.Dispose();
    }

    /// <summary>Adds a document to the index. Replacing an existing id is allowed.</summary>
    public void Add(TDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var searchDocument = Map(document);
        _index.Add(searchDocument);
        _documents[searchDocument.Id] = document;
    }

    /// <summary>Adds every document to the index, accumulating.</summary>
    public void AddRange(IEnumerable<TDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        foreach (var document in documents)
            Add(document);
    }

    /// <summary>Replaces the whole index with the given documents.</summary>
    public void Index(IEnumerable<TDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        Clear();

        foreach (var document in documents)
            Add(document);
    }

    /// <summary>Removes the document with the given id, if present.</summary>
    public void Remove(string documentId)
    {
        ArgumentNullException.ThrowIfNull(documentId);

        _index.Remove(documentId);
        _documents.Remove(documentId);
    }

    /// <summary>Drops every document from the index.</summary>
    public void Clear()
    {
        _index.Clear();
        _documents.Clear();
    }

    /// <summary>
    /// Searches the index and returns the top-ranked documents as typed hits.
    /// </summary>
    /// <param name="query">Raw query text; it is tokenized internally. When
    /// <see cref="LexiSharpIndexOptions{TDocument}.EnableFuzzy"/> is active, plain terms behave
    /// like fuzzy matches (<c>term~</c>).</param>
    /// <param name="options">Optional per-search options.</param>
    public IReadOnlyList<LexiSharpHit<TDocument>> Search(string query, LexiSharpQueryOptions? options = null) =>
        Search(query, options, CancellationToken.None);

    /// <summary>
    /// Searches the index and returns the top-ranked documents as typed hits, stopping when
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    /// <remarks>
    /// An overload rather than a defaulted parameter on <see cref="Search(string, LexiSharpQueryOptions?)"/>, so a caller compiled
    /// against the two-argument member keeps resolving to a member that exists: changing a
    /// signature is a binary break, and this is the kind of change that should not be one.
    /// <para>
    /// The engine already checked the token in its per-candidate loops. What was missing is the way
    /// to supply one from the outside, which matters wherever a search outlives the caller's
    /// patience — an HTTP request whose client has disconnected is the canonical case, and the
    /// mapped endpoint now passes <c>RequestAborted</c> through this member.
    /// </para>
    /// </remarks>
    /// <param name="query">Raw query text; it is tokenized internally.</param>
    /// <param name="options">Optional per-search options.</param>
    /// <param name="cancellationToken">Token observed between and during the ranking passes.</param>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public IReadOnlyList<LexiSharpHit<TDocument>> Search(
        string query,
        LexiSharpQueryOptions? options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var effectiveQuery = Fuzzify(query);
        var coreOptions = (options ?? LexiSharpQueryOptions.Default).ToCore()
            with { FuzzyOnlyOutOfVocabulary = _fuzzyOnlyOutOfVocabulary };

        if (coreOptions.IsEmpty)
            return Array.Empty<LexiSharpHit<TDocument>>();

        IReadOnlyList<string>? queryTerms = null;

        if (options?.Highlight == true && _spanTokenizer is not null)
            queryTerms = QueryParser.Parse(effectiveQuery, _tokenizer).AllTerms;

        return MapHits(_engine.Search(effectiveQuery, coreOptions, cancellationToken), queryTerms, options?.HighlightOptions);
    }

    /// <summary>
    /// Same search as <see cref="Search(string, LexiSharpQueryOptions?)"/>, plus one facet bucket per requested field over the
    /// whole match set. Buckets reflect the underlying match gate and every document that
    /// matched — independently of the page window.
    /// </summary>
    /// <param name="query">Raw query text.</param>
    /// <param name="options">Optional per-search options.</param>
    /// <param name="facetFields">
    /// Fields to count; null/empty yields no buckets. Null or empty entries and duplicates are
    /// dropped; bucket order follows the first occurrence of each distinct field.
    /// </param>
    public LexiSharpFacetedResult<TDocument> SearchWithFacets(
        string query,
        LexiSharpQueryOptions? options = null,
        IReadOnlyList<string>? facetFields = null)
    {
        ArgumentNullException.ThrowIfNull(query);

        var effectiveQuery = Fuzzify(query);
        var coreOptions = (options ?? LexiSharpQueryOptions.Default).ToCore()
            with { FuzzyOnlyOutOfVocabulary = _fuzzyOnlyOutOfVocabulary };

        if (coreOptions.IsEmpty)
            return new LexiSharpFacetedResult<TDocument>(Array.Empty<LexiSharpHit<TDocument>>(), Array.Empty<FacetBucket>());

        var buckets = _baseEngine.SearchWithFacets(effectiveQuery, coreOptions, facetFields).Buckets;
        var results = MapHits(_engine.Search(effectiveQuery, coreOptions), null, options?.HighlightOptions);

        return new LexiSharpFacetedResult<TDocument>(results, buckets);
    }

    /// <summary>
    /// Same search as <see cref="Search(string, LexiSharpQueryOptions?)"/>, plus string-facet buckets and numeric ranges or
    /// histograms over the whole match set.
    /// </summary>
    /// <param name="query">Raw query text.</param>
    /// <param name="options">Optional per-search options.</param>
    /// <param name="facetFields">Fields to count by value; null/empty yields no string buckets.</param>
    /// <param name="numericFacets">
    /// Numeric facets to count; null/empty yields no numeric buckets. Order is preserved.
    /// </param>
    public LexiSharpFacetedResult<TDocument> SearchWithNumericFacets(
        string query,
        LexiSharpQueryOptions? options = null,
        IReadOnlyList<string>? facetFields = null,
        IReadOnlyList<NumericFacetRequest>? numericFacets = null)
    {
        ArgumentNullException.ThrowIfNull(query);

        var effectiveQuery = Fuzzify(query);
        var coreOptions = (options ?? LexiSharpQueryOptions.Default).ToCore()
            with { FuzzyOnlyOutOfVocabulary = _fuzzyOnlyOutOfVocabulary };

        if (coreOptions.IsEmpty)
            return new LexiSharpFacetedResult<TDocument>(Array.Empty<LexiSharpHit<TDocument>>(), Array.Empty<FacetBucket>());

        var faceted = _baseEngine.SearchWithNumericFacets(effectiveQuery, coreOptions, facetFields, numericFacets);
        var results = MapHits(_engine.Search(effectiveQuery, coreOptions), null, options?.HighlightOptions);

        return new LexiSharpFacetedResult<TDocument>(results, faceted.Buckets)
        {
            NumericBuckets = faceted.NumericBuckets,
        };
    }

    /// <summary>
    /// Explains why a document received its score for a query, term by term. Delegates to the
    /// underlying scorer's <see cref="IScoreExplainer"/>; returns <c>null</c> when the active
    /// scorer cannot explain itself or the document is unknown.
    /// </summary>
    /// <param name="documentId">Id of the document to explain.</param>
    /// <param name="query">The raw query, parsed and resolved like <see cref="Search(string, LexiSharpQueryOptions?)"/>.</param>
    public ScoreExplanation? Explain(string documentId, string query)
    {
        ArgumentNullException.ThrowIfNull(documentId);
        ArgumentNullException.ThrowIfNull(query);

        return _baseEngine.Explain(documentId, Fuzzify(query));
    }

    /// <summary>
    /// When <see cref="LexiSharpIndexOptions{TDocument}.EnableFuzzy"/> is active, rewrites a
    /// plain query (no explicit <c>*</c>/<c>~</c> operators, no quoted phrase) so every
    /// normalized term carries a default fuzzy operator. Queries with explicit syntax are left
    /// untouched so the caller keeps full control.
    /// </summary>
    private string Fuzzify(string query)
    {
        if (!_enableFuzzy || query.IndexOfAny(['~', '*', '"']) >= 0)
            return query;

        var terms = _tokenizer.Tokenize(query);

        if (terms.Count == 0)
            return query;

        var builder = new StringBuilder();

        for (int i = 0; i < terms.Count; i++)
        {
            if (i > 0)
                builder.Append(' ');

            builder.Append(terms[i]);
            builder.Append('~');
        }

        return builder.ToString();
    }

    /// <summary>Converts a caller document into the engine's <see cref="SearchDocument"/>.</summary>
    private SearchDocument Map(TDocument document) =>
        new(
            _id(document),
            _text(document),
            _fields(document),
            _category(document),
            _textFields(document));

    /// <summary>Wraps engine results into typed hits, optionally highlighting each hit's text.</summary>
    private IReadOnlyList<LexiSharpHit<TDocument>> MapHits(
        IReadOnlyList<SearchResult> results,
        IReadOnlyList<string>? queryTerms,
        LexiSharp.Highlighting.HighlightOptions? highlightOptions)
    {
        var hits = new LexiSharpHit<TDocument>[results.Count];

        for (int i = 0; i < results.Count; i++)
        {
            var result = results[i];
            string? highlighted = queryTerms is null || _spanTokenizer is null
                ? null
                : TextHighlighter.HighlightFull(
                    result.Document.Text, queryTerms, _spanTokenizer, highlightOptions);

            hits[i] = new LexiSharpHit<TDocument>(
                result.DocumentId,
                result.Score,
                Resolve(result.DocumentId),
                highlighted);
        }

        return hits;
    }

    /// <summary>
    /// The caller's document for a hit: the one this facade holds, or the one
    /// <see cref="LexiSharpIndexOptions{TDocument}.Load"/> resolves for a store it did not index.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Neither holds the id, so a hit cannot carry a document. A store that was reopened carries
    /// documents this session never added, and mapping those back is the caller's to provide.
    /// </exception>
    private TDocument Resolve(string documentId)
    {
        if (_documents.TryGetValue(documentId, out var held))
            return held;

        if (_load?.Invoke(documentId) is { } loaded)
            return loaded;

        throw new InvalidOperationException(
            $"The index returned '{documentId}', which this facade does not hold and "
            + $"LexiSharpIndexOptions<{typeof(TDocument).Name}>.Load did not resolve. Set Load to map an id "
            + "back to the caller's document when a store carries documents this session did not add.");
    }

    private static ITokenizer BuildDefaultTokenizer(LexiSharpIndexOptions<TDocument> options)
    {
        if (!options.RemoveStopWords && options.Stemmer is null && options.NGramMax <= 1)
            return Tokenizer.Default;

        return new Tokenizer(new TokenizerOptions
        {
            RemoveStopWords = options.RemoveStopWords,
            Stemmer = options.Stemmer,
            NGramMax = options.NGramMax,
        });
    }

    private static Func<TDocument, string>? DefaultId()
    {
        if (typeof(TDocument) == typeof(string))
            return static document => (string)(object)document;

        if (typeof(TDocument) == typeof(SearchDocument))
            return static document => ((SearchDocument)(object)document).Id;

        return null;
    }

    private static Func<TDocument, string>? DefaultText()
    {
        if (typeof(TDocument) == typeof(string))
            return static document => (string)(object)document;

        if (typeof(TDocument) == typeof(SearchDocument))
            return static document => ((SearchDocument)(object)document).Text;

        return null;
    }

    private static Func<TDocument, string?>? DefaultCategory()
    {
        if (typeof(TDocument) != typeof(SearchDocument))
            return null;

        return static document => ((SearchDocument)(object)document).Category;
    }

    private static Func<TDocument, IReadOnlyDictionary<string, string>?>? DefaultFields()
    {
        if (typeof(TDocument) != typeof(SearchDocument))
            return null;

        return static document => ((SearchDocument)(object)document).Fields;
    }

    private static Func<TDocument, IReadOnlyDictionary<string, string>?>? DefaultTextFields()
    {
        if (typeof(TDocument) != typeof(SearchDocument))
            return null;

        return static document => ((SearchDocument)(object)document).TextFields;
    }

    // The `_` parameters are fixed by the Func<TDocument, ...> these are assigned to: a selector
    // over a document type has to accept the document, whether or not it reads anything from it.
    private static string? NoCategory(TDocument _) => null; // NOSONAR:S1172

    private static IReadOnlyDictionary<string, string>? NoFields(TDocument _) => null; // NOSONAR:S1172

    private static ArgumentException NewMappingError(string selector) =>
        new($"LexiSharpIndex<TDocument> requires an explicit {selector} selector for this document type. Set options.{selector} in the constructor delegate.");
}