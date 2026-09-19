using LexiSharp.Core;

namespace LexiSharp.Indexing;

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
/// </remarks>
public sealed class SparseTextSearchEngine : ITextSearchEngine
{
    private readonly ISparseEmbeddingProvider _embeddings;

    private readonly Dictionary<string, SearchDocument> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _termsByDocument = new(StringComparer.Ordinal);
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
            .GetSparseEmbeddingAsync(document.Text, cancellationToken)
            .ConfigureAwait(false);

        AddVector(document, vector);
    }

    /// <inheritdoc />
    public void Remove(string documentId)
    {
        ArgumentNullException.ThrowIfNull(documentId);

        if (!_documents.Remove(documentId))
            return;

        if (_termsByDocument.Remove(documentId, out var terms))
        {
            foreach (var term in terms)
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
        _termsByDocument.Clear();
        _postings.Clear();
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

        if (options.Limit <= 0 || string.IsNullOrWhiteSpace(query) || _documents.Count == 0)
            return Array.Empty<SearchResult>();

        var queryVector = await _embeddings
            .GetSparseEmbeddingAsync(query, cancellationToken)
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
            .Take(options.Limit)
            .ToList();
    }

    private void AddVector(SearchDocument document, IReadOnlyDictionary<string, float>? vector)
    {
        if (_documents.ContainsKey(document.Id))
            Remove(document.Id);

        _documents[document.Id] = document;

        var terms = new List<string>();

        foreach (var (term, weight) in vector ?? EmptyVector)
        {
            if (string.IsNullOrEmpty(term) || !float.IsFinite(weight) || weight <= 0)
                continue;

            if (!_postings.TryGetValue(term, out var postings))
            {
                postings = new Dictionary<string, float>(StringComparer.Ordinal);
                _postings[term] = postings;
            }

            postings[document.Id] = weight;
            terms.Add(term);
        }

        _termsByDocument[document.Id] = terms.ToArray();
    }

    private static IReadOnlyDictionary<string, float> EmptyVector { get; } =
        new Dictionary<string, float>();
}