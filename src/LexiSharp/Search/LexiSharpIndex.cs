using System.Text;
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
/// The same single-writer contract as <see cref="ITextSearchEngine"/> applies:
/// <see cref="Add"/>, <see cref="AddRange"/>, <see cref="Index"/>, <see cref="Remove"/> and
/// <see cref="Clear"/> must not run concurrently with each other or with a
/// <see cref="Search"/>; concurrent searches are safe.
/// </remarks>
public sealed class LexiSharpIndex<TDocument>
    where TDocument : class
{
    private readonly Func<TDocument, string> _id;
    private readonly Func<TDocument, string> _text;
    private readonly Func<TDocument, string?> _category;
    private readonly Func<TDocument, IReadOnlyDictionary<string, string>?> _fields;
    private readonly bool _enableFuzzy;
    private readonly bool _fuzzyOnlyOutOfVocabulary;

    private readonly ITextIndex _index;
    private readonly ITokenizer _tokenizer;
    private readonly RankedTextSearchEngine _baseEngine;
    private readonly ITextSearchEngine _engine;
    private readonly ISpanTokenizer? _spanTokenizer;

    private readonly Dictionary<string, TDocument> _documents = new(StringComparer.Ordinal);

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

        _enableFuzzy = options.EnableFuzzy;
        _fuzzyOnlyOutOfVocabulary = options.FuzzyOnlyOutOfVocabulary;

        var tokenizer = options.Tokenizer ?? BuildDefaultTokenizer(options);
        _spanTokenizer = tokenizer as ISpanTokenizer;
        _tokenizer = tokenizer;

        _index = options.TermExpander is null
            ? new InMemoryTextIndex(tokenizer)
            : new ExpansionTextIndex(options.TermExpander, tokenizer);
        _baseEngine = new RankedTextSearchEngine(_index, options.Scorer, tokenizer, options.Synonyms);
        _engine = options.Reranker is null
            ? _baseEngine
            : new RerankedTextSearchEngine(_baseEngine, options.Reranker, options.RerankerMaxCandidates);
    }

    /// <summary>Number of indexed documents.</summary>
    public int Count => _index.Count;

    /// <summary>The ids of every indexed document, in insertion order.</summary>
    public IReadOnlyCollection<string> DocumentIds => _documents.Keys;

    /// <summary>A snapshot of the corpus statistics (documents, vocabulary, tokens, ...).</summary>
    public TextIndexStatistics Statistics => _index.GetStatistics();

    /// <summary>The underlying search engine, for callers that need the raw interface.</summary>
    public ITextSearchEngine Engine => _engine;

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
    public IReadOnlyList<LexiSharpHit<TDocument>> Search(string query, LexiSharpQueryOptions? options = null)
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

        return MapHits(_engine.Search(effectiveQuery, coreOptions), queryTerms);
    }

    /// <summary>
    /// Same search as <see cref="Search"/>, plus one facet bucket per requested field over the
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
        var results = MapHits(_engine.Search(effectiveQuery, coreOptions), null);

        return new LexiSharpFacetedResult<TDocument>(results, buckets);
    }

    /// <summary>
    /// Explains why a document received its score for a query, term by term. Delegates to the
    /// underlying scorer's <see cref="IScoreExplainer"/>; returns <c>null</c> when the active
    /// scorer cannot explain itself or the document is unknown.
    /// </summary>
    /// <param name="documentId">Id of the document to explain.</param>
    /// <param name="query">The raw query, parsed and resolved like <see cref="Search"/>.</param>
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
            _category(document));

    /// <summary>Wraps engine results into typed hits, optionally highlighting each hit's text.</summary>
    private IReadOnlyList<LexiSharpHit<TDocument>> MapHits(
        IReadOnlyList<SearchResult> results,
        IReadOnlyList<string>? queryTerms)
    {
        var hits = new LexiSharpHit<TDocument>[results.Count];

        for (int i = 0; i < results.Count; i++)
        {
            var result = results[i];
            string? highlighted = queryTerms is null || _spanTokenizer is null
                ? null
                : TextHighlighter.HighlightFull(result.Document.Text, queryTerms, _spanTokenizer);

            hits[i] = new LexiSharpHit<TDocument>(
                result.DocumentId,
                result.Score,
                _documents[result.DocumentId],
                highlighted);
        }

        return hits;
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

    private static string? NoCategory(TDocument _) => null;

    private static IReadOnlyDictionary<string, string>? NoFields(TDocument _) => null;

    private static ArgumentException NewMappingError(string selector) =>
        new($"LexiSharpIndex<TDocument> requires an explicit {selector} selector for this document type. Set options.{selector} in the constructor delegate.");
}