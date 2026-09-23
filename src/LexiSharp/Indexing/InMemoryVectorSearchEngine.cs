using LexiSharp.Core;
using LexiSharp.Hybrid;

namespace LexiSharp.Indexing;

/// <summary>
/// A document together with its stored dense embedding — the atomic unit of
/// <see cref="InMemoryVectorSearchEngine.Export"/> / <see cref="InMemoryVectorSearchEngine.Import"/>,
/// letting a corpus be reloaded without re-embedding it.
/// </summary>
/// <param name="Document">The indexed document.</param>
/// <param name="Vector">The embedding the engine stored for the document (finite, unit-length when the provider normalizes).</param>
public sealed record VectorIndexEntry(SearchDocument Document, ReadOnlyMemory<float> Vector);

/// <summary>
/// In-memory dense (vector) search engine: embeds documents with an <see cref="IEmbeddingProvider"/>
/// and answers queries by cosine similarity over the whole corpus — a linear scan, no ANN index.
/// </summary>
/// <remarks>
/// <para>
/// This is the in-process counterpart of the PostgreSQL <c>pgvector</c> engine: embeddings are
/// still computed externally, LexiSharp only stores them and does the similarity math. It makes
/// dense retrieval usable without a database and lets a lexical engine and a dense engine be
/// fused by <see cref="HybridTextSearchEngine"/> and its
/// <see cref="ReciprocalRankFusionMerger"/> — the recommended way to combine BM25 and cosine,
/// whose scales are unrelated.
/// </para>
/// <para>
/// Cosine similarity lies in <c>[−1, 1]</c>, but LexiSharp's convention is that a score of
/// exactly <c>0</c> means "not a match": negative and orthogonal scores are clamped to <c>0</c>
/// and dropped. Documents whose embedding is the zero vector therefore never match. A linear
/// scan touches the entire corpus, so this engine is meant for small/medium corpora — reach for
/// the PostgreSQL provider (HNSW ANN) beyond that.
/// </para>
/// <para>
/// Because embedding computation is async (often remote) while <see cref="ITextSearchEngine"/> is
/// synchronous, the wrapper methods (<see cref="Add"/>, <see cref="Search"/>, ...) block on the
/// underlying async work; prefer <see cref="AddAsync"/> and <see cref="SearchAsync"/>. Mutations
/// are not thread-safe; synchronize externally.
/// </para>
/// </remarks>
public sealed class InMemoryVectorSearchEngine : ITextSearchEngine, IQuerySyntaxSupport, IQueryCostProbe
{
    /// <inheritdoc />
    public QueryFeature SupportedQueryFeatures => QueryFeature.None;

    private readonly IEmbeddingProvider _embeddings;
    private readonly Dictionary<string, SearchDocument> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float[]> _vectors = new(StringComparer.Ordinal);

    /// <param name="embeddings">External embedding producer; never implemented inside LexiSharp.</param>
    /// <exception cref="ArgumentNullException"><paramref name="embeddings"/> is null.</exception>
    public InMemoryVectorSearchEngine(IEmbeddingProvider embeddings)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        _embeddings = embeddings;
    }

    /// <summary>Documents currently indexed, unordered.</summary>
    public IReadOnlyCollection<SearchDocument> Documents => _documents.Values;

    /// <summary>Number of indexed documents.</summary>
    public int Count => _documents.Count;

    /// <summary>Length of the vectors this engine stores, as declared by the provider.</summary>
    public int Dimension => _embeddings.Dimension;

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
            .GetTextEmbeddingAsync(document.Text, EmbeddingUse.Passage, cancellationToken)
            .ConfigureAwait(false);

        AddVector(document, vector);
    }

    /// <inheritdoc />
    public void Remove(string documentId)
    {
        ArgumentNullException.ThrowIfNull(documentId);

        _documents.Remove(documentId);
        _vectors.Remove(documentId);
    }

    /// <inheritdoc />
    public void Clear()
    {
        _documents.Clear();
        _vectors.Clear();
    }

    /// <summary>
    /// Materializes the corpus as its stored documents and embeddings — what a persistence
    /// backend serializes and <see cref="Import"/> restores without re-embedding anything.
    /// </summary>
    public IReadOnlyList<VectorIndexEntry> Export()
    {
        var entries = new List<VectorIndexEntry>(_documents.Count);

        foreach (var (documentId, document) in _documents)
            entries.Add(new VectorIndexEntry(document, _vectors[documentId]));

        return entries;
    }

    /// <summary>
    /// Replaces the whole corpus with the given documents and embeddings, bypassing the provider:
    /// the vectors were already computed once, so a reloaded corpus must not be embedded again.
    /// </summary>
    /// <param name="entries">Documents with their embeddings, as produced by <see cref="Export"/>.</param>
    public void Import(IEnumerable<VectorIndexEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        Clear();

        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(entry.Document);

            AddVector(entry.Document, entry.Vector);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        return SearchAsync(query, options).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Embeds the query and returns the documents ranked by cosine similarity. Prefer this over
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

        QuerySyntax.EnsureSupported(query, SupportedQueryFeatures, nameof(InMemoryVectorSearchEngine));

        if (_documents.Count == 0)
            return Array.Empty<SearchResult>();

        var queryVector = await _embeddings
            .GetTextEmbeddingAsync(query, EmbeddingUse.Query, cancellationToken)
            .ConfigureAwait(false);

        if (queryVector.Length != _embeddings.Dimension)
        {
            throw new InvalidOperationException(
                $"The embedding provider returned a {queryVector.Length}-dimension query vector but declares Dimension={_embeddings.Dimension}.");
        }

        var results = new List<SearchResult>(_documents.Count);
        var querySpan = queryVector.Span;

        foreach (var (documentId, vector) in _vectors)
        {
            // Cosine ∈ [−1, 1]; clamp negatives/orthogonal to 0 = "no match" (LexiSharp convention).
            double score = VectorSimilarity.CosineSimilarity(querySpan, vector);

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

    /// <summary>
    /// A dense scan touches every document, so the cost is the corpus size for any non-empty
    /// request — the honest signal a <see cref="RoutedSearchEngine"/> compares against the
    /// lexical engines' term-overlap estimates.
    /// </summary>
    public long EstimateCandidateCount(ReadOnlySpan<char> query, SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.IsEmpty ? 0 : _documents.Count;
    }

    private void AddVector(SearchDocument document, ReadOnlyMemory<float> vector)
    {
        if (vector.Length != _embeddings.Dimension)
        {
            throw new InvalidOperationException(
                $"The embedding provider returned a {vector.Length}-dimension vector for '{document.Id}' but declares Dimension={_embeddings.Dimension}.");
        }

        if (_documents.ContainsKey(document.Id))
            Remove(document.Id);

        _documents[document.Id] = document;
        _vectors[document.Id] = vector.ToArray();
    }
}
