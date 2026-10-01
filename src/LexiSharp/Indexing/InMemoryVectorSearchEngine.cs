using LexiSharp.Core;
using LexiSharp.Hybrid;
using LexiSharp.Ranking;

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
    private readonly Dictionary<string, int> _slotById = new(StringComparer.Ordinal);

    /// <summary>
    /// Document id → dense slot. The vectors themselves live in one contiguous
    /// <see cref="_vectorData"/> block of <see cref="_stride"/>-strided rows, so a scan walks
    /// memory linearly instead of chasing a dictionary entry and a separate array per document.
    /// </summary>
    private SearchDocument?[] _slotDocument = Array.Empty<SearchDocument?>();

    /// <summary>The scan buffer, <c>slot * _stride</c> being a document's vector start.</summary>
    private float[] _vectorData = Array.Empty<float>();

    /// <summary>
    /// Per-slot squared Euclidean norm, computed once when the vector is stored. Cosine similarity
    /// needs both norms, and a document's own norm is the same on every query, so a scan reduces to
    /// one dot product per document instead of a dot product plus two norm accumulations.
    /// </summary>
    private float[] _squaredNorms = Array.Empty<float>();

    /// <summary>Slots vacated by <see cref="Remove"/>, reused before the buffer grows.</summary>
    private readonly List<int> _freeSlots = [];

    private int _slotCount;

    /// <summary>
    /// Row stride of <see cref="_vectorData"/>, in floats: where slot <c>n</c>'s vector starts.
    /// Not the same thing as <see cref="Dimension"/>, and deliberately not named like it. The
    /// stride is a property of the buffer, so it is 0 until a vector is stored; the dimension is
    /// the provider's declared contract, which is answerable on an engine that holds nothing.
    /// Once a vector has been stored the two are equal, because <see cref="AddVector"/> refuses
    /// any vector the provider's dimension does not match.
    /// </summary>
    private int _stride;

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

    /// <summary>
    /// Length of the vectors this engine stores, as declared by the provider.
    /// </summary>
    /// <remarks>
    /// The provider, not <see cref="_stride"/>. A caller sizing a buffer or validating a vector it
    /// produced asks this before the engine has indexed anything, and the buffer stride is 0 until
    /// the first <see cref="Add"/> — the two are equal afterwards, since <see cref="AddVector"/>
    /// rejects a vector whose length is not the declared dimension.
    /// </remarks>
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

        if (!_slotById.Remove(documentId, out int slot))
            return;

        _documents.Remove(documentId);

        // Clear the reference so a removed document can be collected; the float payload needs no
        // clearing because nothing reads a slot that is not in _slotCount and occupied.
        _slotDocument[slot] = null;
        _freeSlots.Add(slot);
    }

    /// <inheritdoc />
    public void Clear()
    {
        _documents.Clear();
        _slotById.Clear();
        _slotDocument = Array.Empty<SearchDocument?>();
        _vectorData = Array.Empty<float>();
        _squaredNorms = Array.Empty<float>();
        _freeSlots.Clear();
        _slotCount = 0;
    }

    /// <summary>
    /// Materializes the corpus as its stored documents and embeddings — what a persistence
    /// backend serializes and <see cref="Import"/> restores without re-embedding anything.
    /// </summary>
    public IReadOnlyList<VectorIndexEntry> Export()
    {
        var entries = new List<VectorIndexEntry>(_documents.Count);

        foreach (var (documentId, document) in _documents)
        {
            int slot = _slotById[documentId];

            // Copied out of the shared block: Export is a snapshot, and the buffer is reused when
            // the engine is later mutated, so a handed-out view must not alias it.
            entries.Add(new VectorIndexEntry(document, ReadVector(slot).ToArray()));
        }

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

        var querySpan = queryVector.Span;

        // A zero query vector is orthogonal to everything: every document would score 0, which is
        // the « not a match » convention, so there is nothing to rank.
        float queryNorm = VectorSimilarity.Norm(querySpan);

        if (queryNorm == 0 || _documents.Count == 0)
            return Array.Empty<SearchResult>();

        // Bounded window rather than materializing and sorting every document that scores above
        // zero: a dense scan routinely matches most of the corpus, and the page is `Window` rows.
        // The slot is not the insertion order: slots are vacated by Remove and reused, so a slot number
// orders by where a vector happens to sit now. Tie-breaking on that would be neither stable nor
// meaningful, so this engine keeps the default and passes no ordinal.
        var top = new TopRankedWindow(options.Window);
        int stride = _stride;
        var data = _vectorData;
        var norms = _squaredNorms;

        for (int slot = 0; slot < _slotCount; slot++)
        {
            var document = _slotDocument[slot];

            if (document is null)
                continue; // slot vacated by Remove

            // Structured filters gate the corpus before any relevance math is paid for, exactly as
            // the lexical engine does: an unsatisfying document costs no dot product.
            if (!options.PassesFilters(document))
                continue;

            float documentNorm = MathF.Sqrt(norms[slot]);

            if (documentNorm == 0)
                continue; // zero vector: never a match

            // Cosine ∈ [−1, 1]; clamp negatives/orthogonal to 0 = "no match" (LexiSharp convention).
            double score = VectorSimilarity.DotProduct(querySpan, ReadVectorAt(data, slot, stride))
                           / (queryNorm * documentNorm);

            if (!(score > 0) || score < options.MinimumScore)
                continue;

            top.Add(score, document);
        }

        int skip = Math.Min(options.Offset, top.Count);
        int count = top.Count - skip;
        var results = new SearchResult[count];
        top.CopyBestTo(results, skip, count);

        // Recorded on the cut page, not per scanned document: a dense scan touches the whole
        // corpus, so tracing the scan itself would grow with the index.
        options.Trace?.RecordScoreStage("Dense", results);

        return results;
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

        // Validated above, so the stride cannot change under the buffer: a vector of another
        // length never reaches the copy.
        _stride = vector.Length;
        int slot = AllocateSlot();

        _documents[document.Id] = document;
        _slotById[document.Id] = slot;
        _slotDocument[slot] = document;

        var source = vector.Span;
        source.CopyTo(_vectorData.AsSpan(slot * _stride, _stride));
        _squaredNorms[slot] = VectorSimilarity.NormSquared(source);
    }

    /// <summary>
    /// Reserves a dense slot, reusing a vacated one before growing the buffers. Reuse is LIFO: a
    /// removed slot is the most likely to still be in cache.
    /// </summary>
    private int AllocateSlot()
    {
        if (_freeSlots.Count > 0)
        {
            int reused = _freeSlots[^1];
            _freeSlots.RemoveAt(_freeSlots.Count - 1);
            return reused;
        }

        if (_slotCount == _slotDocument.Length)
            Grow();

        return _slotCount++;
    }

    private void Grow()
    {
        int capacity = _slotDocument.Length == 0 ? 16 : _slotDocument.Length * 2;

        Array.Resize(ref _slotDocument, capacity);

        // The vector block is sized in floats, so it grows with the slot capacity times the
        // dimension rather than doubling on its own.
        Array.Resize(ref _vectorData, capacity * _stride);
        Array.Resize(ref _squaredNorms, capacity);
    }

    /// <summary>One stored vector, as a span over the shared block.</summary>
    private ReadOnlySpan<float> ReadVector(int slot) =>
        _vectorData.AsSpan(slot * _stride, _stride);

    /// <summary>One stored vector from an already-captured block, so the scan avoids re-reading fields.</summary>
    private static ReadOnlySpan<float> ReadVectorAt(float[] data, int slot, int stride) =>
        data.AsSpan(slot * stride, stride);
}
