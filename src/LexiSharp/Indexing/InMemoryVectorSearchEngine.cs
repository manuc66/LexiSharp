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
/// <para>
/// Vectors are stored as float32 unless <see cref="InMemoryVectorSearchOptions.Quantization"/> asks
/// for int8, which keeps a quarter of the payload and scans the same block in the quantised domain.
/// A quantised vector's norm is the norm of the row the scan reads — <c>Σq²</c> over the stored
/// components, computed once at insert — not the float norm it was made from, so a cosine is still
/// a cosine. Quantisation
/// is opt-in because the ranking change is a property of the embedding distribution; the dense page
/// carries the recall it was measured at.
/// </para>
/// </remarks>
public sealed class InMemoryVectorSearchEngine : ITextSearchEngine, IQuerySyntaxSupport, IQueryCostProbe
{
    /// <inheritdoc />
    public QueryFeatures SupportedQueryFeatures => QueryFeatures.None;

    private readonly IEmbeddingProvider _embeddings;
    private readonly Dictionary<string, SearchDocument> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _slotById = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether the scan reads <see cref="_quantizedData"/> instead of <see cref="_vectorData"/>.
    /// Captured once from the options: a vector block that changed representation mid-corpus would
    /// silently mix two scales in one ranking.
    /// </summary>
    private readonly bool _quantized;

    // Memoized query embeddings, invalidated on every mutation. Bounded: once full it is cleared
    // rather than evicted entry-by-entry, which keeps the hot path allocation-free.
    private readonly Dictionary<string, float[]> _queryEmbeddingCache = new(StringComparer.Ordinal);
    private const int QueryEmbeddingCacheCapacity = 64;

    /// <summary>
    /// Document id → dense slot. The vectors themselves live in one contiguous
    /// <see cref="_vectorData"/> block of <see cref="_stride"/>-strided rows, so a scan walks
    /// memory linearly instead of chasing a dictionary entry and a separate array per document.
    /// </summary>
    private SearchDocument?[] _slotDocument = Array.Empty<SearchDocument?>();

    /// <summary>The scan buffer, <c>slot * _stride</c> being a document's vector start.</summary>
    /// <remarks>Empty in the quantised representation: the float payload is not retained.</remarks>
    private float[] _vectorData = Array.Empty<float>();

    /// <summary>
    /// The int8 scan buffer, allocated only when the engine quantises. One <see cref="sbyte"/> per
    /// component, <c>slot * _stride</c> being a document's vector start; the value at index
    /// <c>i</c> means <c>_quantizedScales[slot] · data[i]</c>.
    /// </summary>
    private sbyte[] _quantizedData = Array.Empty<sbyte>();

    /// <summary>
    /// Per-slot dequantisation scale: <c>max|vector| / 127</c>, so the largest component maps to
    /// <c>±127</c>. Zero only for a zero vector, which never matches.
    /// </summary>
    private float[] _quantizedScales = Array.Empty<float>();

    /// <summary>
    /// Per-slot squared Euclidean norm, computed once when the vector is stored. Cosine similarity
    /// needs both norms, and a document's own norm is the same on every query, so a scan reduces to
    /// one dot product per document instead of a dot product plus two norm accumulations.
    /// </summary>
    private float[] _squaredNorms = Array.Empty<float>();

    /// <summary>Slots vacated by <see cref="Remove"/>, reused before the buffer grows.</summary>
    private readonly List<int> _freeSlots = [];

    /// <summary>The document living in a slot, for the page window's ties and page cut.</summary>
    private readonly Func<int, SearchDocument?> _resolveDocument;

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
    /// <param name="options">Storage options (defaults: float32 vectors, unchanged ranking).</param>
    /// <exception cref="ArgumentNullException"><paramref name="embeddings"/> is null.</exception>
    public InMemoryVectorSearchEngine(IEmbeddingProvider embeddings, InMemoryVectorSearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        _embeddings = embeddings;
        _resolveDocument = slot => _slotDocument[slot];
        Options = options ?? InMemoryVectorSearchOptions.Default;
        _quantized = Options.Quantization == VectorQuantization.Int8;
    }

    /// <summary>The storage options this engine was constructed with.</summary>
    public InMemoryVectorSearchOptions Options { get; }

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

    /// <summary>
    /// The provider's embedding signature, when it implements
    /// <see cref="IEmbeddingSignatureProvider"/>; <see langword="null"/> otherwise.
    /// </summary>
    public string? EmbeddingSignature => (_embeddings as IEmbeddingSignatureProvider)?.EmbeddingSignature;

    /// <inheritdoc />
    public void Index(IEnumerable<SearchDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        IReadOnlyList<SearchDocument> materialized = documents as IReadOnlyList<SearchDocument>
            ?? documents.ToList();

        IndexAsync(materialized).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Replaces the corpus, embedding the whole batch in one provider call when the provider
    /// implements <see cref="IBatchEmbeddingProvider"/>. Prefer this over the blocking
    /// <see cref="Index"/>.
    /// </summary>
    /// <param name="documents">The new corpus; the current one is cleared first.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentNullException"><paramref name="documents"/> is <see langword="null"/>.</exception>
    public async Task IndexAsync(
        IReadOnlyList<SearchDocument> documents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);

        if (Options.EmbeddingBatchSize < 1)
            throw new InvalidOperationException("EmbeddingBatchSize must be at least 1.");

        for (int i = 0; i < documents.Count; i++)
            ArgumentNullException.ThrowIfNull(documents[i]);

        // Embed everything before touching the live corpus. A failed provider call therefore
        // leaves the previous index searchable, and a large corpus is embedded in bounded chunks
        // rather than one allocation per token in the whole input.
        ReadOnlyMemory<float>[] vectors = new ReadOnlyMemory<float>[documents.Count];

        if (_embeddings is IBatchEmbeddingProvider batch)
        {
            for (int start = 0; start < documents.Count; start += Options.EmbeddingBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int count = Math.Min(Options.EmbeddingBatchSize, documents.Count - start);
                string[] texts = new string[count];

                for (int i = 0; i < count; i++)
                    texts[i] = documents[start + i].Text;

                IReadOnlyList<ReadOnlyMemory<float>> embedded = await batch
                    .GetTextEmbeddingsAsync(texts, EmbeddingUse.Passage, cancellationToken)
                    .ConfigureAwait(false);

                if (embedded.Count != count)
                    throw new InvalidOperationException(
                        $"The provider returned {embedded.Count} embeddings for {count} documents.");

                for (int i = 0; i < count; i++)
                    vectors[start + i] = embedded[i];
            }
        }
        else
        {
            for (int i = 0; i < documents.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                vectors[i] = await _embeddings
                    .GetTextEmbeddingAsync(documents[i].Text, EmbeddingUse.Passage, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        // Validate before mutating: AddVector would reject a wrong shape, but by then the old
        // corpus would already be gone.
        for (int i = 0; i < vectors.Length; i++)
        {
            if (vectors[i].Length != Dimension)
                throw new InvalidOperationException(
                    $"The provider returned a {vectors[i].Length}D vector but the engine expects {Dimension}D.");
        }

        Clear();

        for (int i = 0; i < documents.Count; i++)
            AddVector(documents[i], vectors[i]);
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
    public bool Remove(string documentId)
    {
        ArgumentNullException.ThrowIfNull(documentId);

        if (!_slotById.Remove(documentId, out int slot))
            return false;

        _queryEmbeddingCache.Clear();

        _documents.Remove(documentId);

        // Clear the reference so a removed document can be collected; the float payload needs no
        // clearing because nothing reads a slot that is not in _slotCount and occupied.
        _slotDocument[slot] = null;
        _freeSlots.Add(slot);
        return true;
    }

    /// <inheritdoc />
    public void Clear()
    {
        _documents.Clear();
        _slotById.Clear();
        _queryEmbeddingCache.Clear();
        _slotDocument = Array.Empty<SearchDocument?>();
        _vectorData = Array.Empty<float>();
        _quantizedData = Array.Empty<sbyte>();
        _quantizedScales = Array.Empty<float>();
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
            // the engine is later mutated, so a handed-out view must not alias it. A quantised
            // engine hands out the dequantised vector — the one its scan actually compares — so a
            // reload through Import reproduces the same ranking rather than a mixture of the two.
            entries.Add(new VectorIndexEntry(document, ReadVectorCopy(slot)));
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

    /// <summary>
    /// Exports the corpus with the embedding signature, so a later import can refuse vectors from
    /// a different model or preprocessing.
    /// </summary>
    public VectorIndexSnapshot ExportSnapshot() => new(Export(), EmbeddingSignature);

    /// <summary>
    /// Replaces the corpus from a snapshot, refusing a signature that differs from this engine's
    /// provider unless <paramref name="allowSignatureMismatch"/> is set. A snapshot with no
    /// signature (an older file, or a provider that cannot identify itself) is accepted.
    /// </summary>
    /// <param name="snapshot">The exported entries and signature.</param>
    /// <param name="allowSignatureMismatch">When true, imports anyway; the caller is asserting the vectors are compatible.</param>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The signatures differ and <paramref name="allowSignatureMismatch"/> is false.</exception>
    public void ImportSnapshot(VectorIndexSnapshot snapshot, bool allowSignatureMismatch = false)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!allowSignatureMismatch
            && EmbeddingSignature is not null
            && snapshot.EmbeddingSignature is not null
            && !string.Equals(EmbeddingSignature, snapshot.EmbeddingSignature, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The snapshot was produced by a different embedding pipeline: " +
                $"'{snapshot.EmbeddingSignature}' against '{EmbeddingSignature}'. Re-embed the corpus, " +
                "or pass allowSignatureMismatch: true if the vectors are known to be compatible.");
        }

        Import(snapshot.Entries);
    }

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Search(
        string query,
        SearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return SearchAsync(query, options, cancellationToken).GetAwaiter().GetResult();
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

        // ParseQuerySyntax: false is the caller saying "literal text, do not consult the query
        // parser" — and EnsureSupported is a parser call.
        if (options.ParseQuerySyntax)
            QuerySyntax.EnsureSupported(query, SupportedQueryFeatures, nameof(InMemoryVectorSearchEngine), options.KnownFields);

        if (_documents.Count == 0)
            return Array.Empty<SearchResult>();

        float[] queryVectorArray;

        if (_queryEmbeddingCache.TryGetValue(query, out var cachedQueryVector))
        {
            queryVectorArray = cachedQueryVector;
        }
        else
        {
            var computed = await _embeddings
                .GetTextEmbeddingAsync(query, EmbeddingUse.Query, cancellationToken)
                .ConfigureAwait(false);

            queryVectorArray = computed.ToArray();

            if (_queryEmbeddingCache.Count >= QueryEmbeddingCacheCapacity)
                _queryEmbeddingCache.Clear();

            _queryEmbeddingCache[query] = queryVectorArray;
        }

        var queryVector = new ReadOnlyMemory<float>(queryVectorArray);

        if (queryVector.Length != _embeddings.Dimension)
        {
            throw new InvalidOperationException(
                $"The embedding provider returned a {queryVector.Length}-dimension query vector but declares Dimension={_embeddings.Dimension}.");
        }

        var querySpan = queryVector.Span;

        for (int i = 0; i < querySpan.Length; i++)
        {
            if (!float.IsFinite(querySpan[i]))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(query),
                    $"The embedding provider returned a non-finite query value ({querySpan[i]}) at index {i}.");
            }
        }

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
        var top = new TopRankedWindow(options.WindowFor(Count), TieBreak.DocumentId, _resolveDocument);
        int stride = _stride;
        var data = _vectorData;
        var norms = _squaredNorms;

        // One representation for the whole corpus, so the branch is on the engine rather than on
        // the slot: the quantised block is walked with the same loop, offsets and result window as
        // the float one, which is what makes the two scans comparable.
        bool quantized = _quantized;
        var quantizedData = _quantizedData;

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
            double score = quantized
                ? VectorSimilarity.QuantizedDotProduct(
                      quantizedData.AsSpan(slot * stride, stride), querySpan)
                  / (queryNorm * documentNorm)
                : VectorSimilarity.DotProduct(querySpan, ReadVectorAt(data, slot, stride))
                  / (queryNorm * documentNorm);

            if (!(score > 0) || score < options.MinimumScore)
                continue;

            top.Add(score, slot);
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

        for (int i = 0; i < vector.Length; i++)
        {
            if (!float.IsFinite(vector.Span[i]))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(vector),
                    $"The embedding provider returned a non-finite value ({vector.Span[i]}) at index {i} for '{document.Id}'.");
            }
        }

        _queryEmbeddingCache.Clear();

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

        if (_quantized)
        {
            StoreQuantized(slot, source);
        }
        else
        {
            source.CopyTo(_vectorData.AsSpan(slot * _stride, _stride));
            _squaredNorms[slot] = VectorSimilarity.NormSquared(source);
        }
    }

    /// <summary>
    /// Writes <paramref name="vector"/> into slot <paramref name="slot"/> as int8 components plus a
    /// per-vector scale, and records the squared norm of <b>that</b> vector.
    /// </summary>
    /// <remarks>
    /// The norm decision is the one that changes ranking without changing the vectors. A cosine is
    /// <c>q·v / (‖q‖·‖v‖)</c> for the vectors the scan actually reads, so the document norm must
    /// come from the stored components, not from the float vector they were rounded from. Reusing
    /// the float norm would divide by a length the stored row does not have: every score would be
    /// off by a factor the corpus cannot see, and a document compared with itself could score above
    /// 1. The quantity stored is <c>Σqᵢ²</c> — the squared norm of the integer row, exact in a
    /// <see cref="long"/> — because the per-vector scale is a common factor of the dot product and
    /// the norm and cancels; the scale itself still defines the dequantised row <see cref="Export"/>
    /// hands back.
    /// </remarks>
    private void StoreQuantized(int slot, ReadOnlySpan<float> vector)
    {
        float maxAbs = 0;

        for (int i = 0; i < vector.Length; i++)
            maxAbs = MathF.Max(maxAbs, MathF.Abs(vector[i]));

        // max|v| maps to ±127, the largest magnitude an sbyte holds: the representable component
        // spacing is as fine as a per-vector scale can make it.
        float scale = maxAbs / sbyte.MaxValue;

        var destination = _quantizedData.AsSpan(slot * _stride, _stride);
        long sumOfSquares = 0;

        if (scale == 0)
        {
            // The zero vector: no direction, so it must never match. The float path stores a zero
            // norm for it and the scan skips it; the same zero norm is what it gets here. This is
            // also where a row whose largest component is below max/127's smallest representable
            // float lands, and treating it as no direction is the same convention rather than a
            // scale that would dequantise to a different vector.
            destination.Clear();
        }
        else
        {
            // Double, not float: a row whose largest component sits near the bottom of the float
            // range has a scale whose reciprocal overflows a float, and the components would be
            // garbage. The product below is at most 127 by construction, so the double is only
            // there to keep the reciprocal finite.
            double inverse = 1.0 / scale;

            for (int i = 0; i < vector.Length; i++)
            {
                int component = (int)Math.Round(vector[i] * inverse, MidpointRounding.AwayFromZero);

                // Only the mathematical maximum can round past the range, and it belongs at the
                // edge rather than at an undefined cast.
                component = Math.Clamp(component, -sbyte.MaxValue, sbyte.MaxValue);

                destination[i] = (sbyte)component;
                sumOfSquares += component * component;
            }
        }

        _quantizedScales[slot] = scale;
        _squaredNorms[slot] = sumOfSquares;
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
        // dimension rather than doubling on its own. Only the block in use grows: the quantised
        // representation does not retain the float payload, and the float representation does not
        // allocate the int8 one.
        if (_quantized)
        {
            Array.Resize(ref _quantizedData, capacity * _stride);
            Array.Resize(ref _quantizedScales, capacity);
        }
        else
        {
            Array.Resize(ref _vectorData, capacity * _stride);
        }

        Array.Resize(ref _squaredNorms, capacity);
    }

    /// <summary>One stored vector, as a span over the shared block.</summary>
    private ReadOnlySpan<float> ReadVector(int slot) =>
        _vectorData.AsSpan(slot * _stride, _stride);

    /// <summary>
    /// One stored vector as a fresh <c>float[]</c>: the block's row in the float representation, or
    /// the dequantised row in the int8 one — <c>scale · q</c>, which is the vector the scan divides
    /// by and compares.
    /// </summary>
    private float[] ReadVectorCopy(int slot)
    {
        if (!_quantized)
            return ReadVector(slot).ToArray();

        var copy = new float[_stride];
        float scale = _quantizedScales[slot];
        ReadOnlySpan<sbyte> row = _quantizedData.AsSpan(slot * _stride, _stride);

        for (int i = 0; i < copy.Length; i++)
            copy[i] = row[i] * scale;

        return copy;
    }

    /// <summary>One stored vector from an already-captured block, so the scan avoids re-reading fields.</summary>
    private static ReadOnlySpan<float> ReadVectorAt(float[] data, int slot, int stride) =>
        data.AsSpan(slot * stride, stride);
}
