using LexiSharp.Core;

namespace LexiSharp.Indexing;

/// <summary>
/// A document together with its stored sparse vector — the atomic unit of
/// <see cref="SparseTextSearchEngine.Export"/> and <see cref="SparseTextSearchEngine.Import"/>,
/// and what a persistence backend serializes to reload a corpus without re-embedding it.
/// </summary>
/// <param name="Document">The indexed document.</param>
/// <param name="Weights">The learned term weights the engine stored for the document (positive, finite).</param>
public sealed record SparseIndexEntry(
    SearchDocument Document,
    IReadOnlyDictionary<string, float> Weights);

/// <summary>
/// In-memory sparse search engine: indexes documents as their sparse learned embeddings
/// (SPLADE, uniCOIL, ...) and answers queries by dot-product scoring.
/// </summary>
/// <remarks>
/// <para>
/// Learned sparse models — SPLADE and friends — do <b>not</b> replace the inverted-index
/// infrastructure, they replace the scoring function: instead of tf-idf/BM25 weights, each
/// (term, document) pair carries a learned weight. The engine therefore keeps a classic inverted
/// index over <c>term → document → weight</c>, and the score of a document for a query is simply
/// <c>score(q,d) = Σ_t w_q(t) · w_d(t, d)</c> over the terms both share — a dot product between
/// two sparse vectors, never a frequency statistic.
/// </para>
/// <para>
/// <b>Embeddings are computed externally</b>: the engine consumes an
/// <see cref="ISparseEmbeddingProvider"/> supplied by the caller (ONNX model, model server, HTTP
/// API, ...). LexiSharp only orchestrates the storage and the scoring. Weights are expected to be
/// non-negative; terms with a non-positive or non-finite weight are treated as absent. Since a
/// score of exactly <c>0</c> means the query and the document share no term (no match) — the
/// standard LexiSharp convention — the engine implements <see cref="ITextSearchEngine"/> and can be
/// merged with lexical/dense engines through the hybrid package. Documents sharing no term with
/// the query are never scored.
/// </para>
/// <para>
/// Because embedding computation is async (typically remote) but <see cref="ITextSearchEngine"/> is
/// synchronous, the synchronous wrapper methods (<see cref="Add"/>, <see cref="Search"/>, ...) block
/// on the underlying async work. Prefer the <see cref="AddAsync"/> and <see cref="SearchAsync"/>
/// overloads when you can. Mutations are not thread-safe; synchronize externally.
/// </para>
/// <para>
/// <see cref="Export"/> and <see cref="Import"/> decouple persistence from inference: a saved
/// corpus (e.g. via <c>LexiSharp.MessagePack</c>) is reloaded from its stored weights alone, and
/// only <i>queries</i> keep needing the model afterwards.
/// </para>
/// </remarks>
public sealed class SparseTextSearchEngine : ITextSearchEngine, IQuerySyntaxSupport
{
    /// <inheritdoc />
    public QueryFeature SupportedQueryFeatures => QueryFeature.None;

    private readonly ISparseEmbeddingProvider _embeddings;

    private readonly Dictionary<string, SearchDocument> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyDictionary<string, float>> _vectorsByDocument = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, float>> _postings = new(StringComparer.Ordinal);

    /// <param name="embeddings">External sparse embedding producer; never implemented inside LexiSharp.</param>
    public SparseTextSearchEngine(ISparseEmbeddingProvider embeddings)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        _embeddings = embeddings;
    }

    /// <summary>Documents currently indexed, unordered.</summary>
    public IReadOnlyCollection<SearchDocument> Documents => _documents.Values;

    /// <summary>Number of indexed documents.</summary>
    public int Count => _documents.Count;

    /// <summary>Number of distinct terms with at least one posting.</summary>
    public int VocabularySize => _postings.Count;

    /// <inheritdoc />
    public void Index(IEnumerable<SearchDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        Clear();

        foreach (var document in documents)
            Add(document);
    }

    /// <inheritdoc />
    public void Add(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        AddAsync(document).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Embeds and stores a single document. Prefer this over the blocking <see cref="Add"/>.
    /// </summary>
    public async Task AddAsync(SearchDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var vector = await _embeddings
            .GetSparseEmbeddingAsync(document.Text, EmbeddingUse.Passage, cancellationToken)
            .ConfigureAwait(false);

        AddVector(document, vector);
    }

    /// <inheritdoc />
    public void Remove(string documentId)
    {
        ArgumentNullException.ThrowIfNull(documentId);

        if (!_documents.Remove(documentId))
            return;

        if (_vectorsByDocument.Remove(documentId, out var vector))
        {
            foreach (var term in vector.Keys)
            {
                if (_postings.TryGetValue(term, out var postings) &&
                    postings.Remove(documentId) && postings.Count == 0)
                {
                    _postings.Remove(term);
                }
            }
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        _documents.Clear();
        _vectorsByDocument.Clear();
        _postings.Clear();
    }

    /// <summary>
    /// Materializes the whole corpus as its stored documents and sparse vectors — the payload a
    /// persistence backend (e.g. <c>LexiSharp.MessagePack</c>) serializes and that
    /// <see cref="Import"/> restores without re-embedding anything.
    /// </summary>
    public IReadOnlyList<SparseIndexEntry> Export()
    {
        var entries = new List<SparseIndexEntry>(_documents.Count);

        foreach (var (documentId, document) in _documents)
            entries.Add(new SparseIndexEntry(document, _vectorsByDocument[documentId]));

        return entries;
    }

    /// <summary>
    /// Replaces the whole corpus with the given documents and sparse vectors, bypassing the
    /// embedding provider. Rationale: the weights were already computed once at index time, so a
    /// reloaded corpus must not be embedded again.
    /// </summary>
    /// <param name="entries">Documents with their learned vectors, as produced by <see cref="Export"/>.</param>
    public void Import(IEnumerable<SparseIndexEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        Clear();

        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(entry.Document);
            ArgumentNullException.ThrowIfNull(entry.Weights);

            AddVector(entry.Document, entry.Weights);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        return SearchAsync(query, options).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Embeds the query and returns the documents ranked by sparse dot product. Prefer this over
    /// the blocking <see cref="Search"/>.
    /// </summary>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query,
        SearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        options ??= SearchOptions.Default;

        if (options.IsEmpty || string.IsNullOrWhiteSpace(query))
            return Array.Empty<SearchResult>();

        QuerySyntax.EnsureSupported(query, SupportedQueryFeatures, nameof(SparseTextSearchEngine));

        if (_documents.Count == 0)
            return Array.Empty<SearchResult>();

        var queryVector = await _embeddings
            .GetSparseEmbeddingAsync(query, EmbeddingUse.Query, cancellationToken)
            .ConfigureAwait(false);

        if (queryVector is null || queryVector.Count == 0)
            return Array.Empty<SearchResult>();

        // Accumulate dot products over the inverted index: only the terms the query actually
        // activated (and the documents posting to them) ever get visited.
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var (term, queryWeight) in queryVector)
        {
            if (!float.IsFinite(queryWeight) || queryWeight <= 0)
                continue;

            if (!_postings.TryGetValue(term, out var postings))
                continue;

            foreach (var (documentId, documentWeight) in postings)
            {
                scores[documentId] = scores.GetValueOrDefault(documentId)
                                     + (double)queryWeight * documentWeight;
            }
        }

        var results = new List<SearchResult>(scores.Count);

        foreach (var (documentId, score) in scores)
        {
            // A score of exactly 0 means the query and the document share no positive term.
            if (!(score > 0))
                continue;

            var document = _documents[documentId];

            if (!options.PassesFilters(document) || score < options.MinimumScore)
                continue;

            results.Add(new SearchResult(documentId, score, document));
        }

        return results
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.DocumentId, StringComparer.Ordinal)
            .Skip(options.Offset)
            .Take(options.Limit)
            .ToList();
    }

    private void AddVector(SearchDocument document, IReadOnlyDictionary<string, float>? vector)
    {
        if (_documents.ContainsKey(document.Id))
            Remove(document.Id);

        _documents[document.Id] = document;

        var weights = new Dictionary<string, float>(StringComparer.Ordinal);

        foreach (var (term, weight) in vector ?? EmptyVector)
        {
            if (string.IsNullOrEmpty(term) || !float.IsFinite(weight) || weight <= 0)
                continue;

            weights[term] = weight;

            if (!_postings.TryGetValue(term, out var postings))
            {
                postings = new Dictionary<string, float>(StringComparer.Ordinal);
                _postings[term] = postings;
            }

            postings[document.Id] = weight;
        }

        _vectorsByDocument[document.Id] = weights;
    }

    private static IReadOnlyDictionary<string, float> EmptyVector { get; } =
        new Dictionary<string, float>();
}