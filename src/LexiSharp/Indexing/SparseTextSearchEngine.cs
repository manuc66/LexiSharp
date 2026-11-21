using LexiSharp.Core;
using LexiSharp.Ranking;

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
/// <b>The inner loop is ordinal-based.</b> A document gets an integer ordinal when it is added, and
/// each term keeps contiguous arrays of <c>(ordinal, weight)</c> entries instead of a dictionary
/// keyed by document id. A query accumulates into the same generation-stamped, ordinal-indexed
/// buffer the lexical engine uses, then fills a bounded top-<c>k</c> window. The learned weights and
/// the public surface are unchanged; what changed is that a posting entry no longer hashes a
/// document id, allocates a result object, or sorts the whole candidate set.
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
    public QueryFeatures SupportedQueryFeatures => QueryFeatures.None;

    private readonly ISparseEmbeddingProvider _embeddings;

    /// <summary>Document id → the document, for the public read surface.</summary>
    private readonly Dictionary<string, SearchDocument> _documents = new(StringComparer.Ordinal);

    /// <summary>Document id → its ordinal. The one map the postings are addressed through.</summary>
    private readonly Dictionary<string, int> _ordinals = new(StringComparer.Ordinal);

    /// <summary>Ordinal → document, with <c>null</c> in a freed slot.</summary>
    private readonly List<SearchDocument?> _byOrdinal = [];

    /// <summary>Slots freed by a removal, reused before the ordinal space grows.</summary>
    private readonly List<int> _freeOrdinals = [];

    private readonly Dictionary<string, IReadOnlyDictionary<string, float>> _vectorsByDocument =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, PostingList> _postings = new(StringComparer.Ordinal);

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
    public bool Remove(string documentId)
    {
        ArgumentNullException.ThrowIfNull(documentId);

        if (!_documents.Remove(documentId))
            return false;

        int ordinal = _ordinals[documentId];
        _ordinals.Remove(documentId);

        if (_vectorsByDocument.Remove(documentId, out var vector))
        {
            foreach (var term in vector.Keys)
            {
                if (!_postings.TryGetValue(term, out var postings))
                    continue;

                postings.Remove(ordinal);

                if (postings.Count == 0)
                    _postings.Remove(term);
            }
        }

        _byOrdinal[ordinal] = null;
        _freeOrdinals.Add(ordinal);

        return true;
    }

    /// <inheritdoc />
    public void Clear()
    {
        _documents.Clear();
        _ordinals.Clear();
        _byOrdinal.Clear();
        _freeOrdinals.Clear();
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
    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return SearchAsync(query, options, cancellationToken).GetAwaiter().GetResult();
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

        // ParseQuerySyntax: false is the caller saying "literal text, do not consult the query
        // parser" — and EnsureSupported is a parser call.
        if (options.ParseQuerySyntax)
            QuerySyntax.EnsureSupported(query, SupportedQueryFeatures, nameof(SparseTextSearchEngine), options.KnownFields);

        if (_documents.Count == 0)
            return Array.Empty<SearchResult>();

        var queryVector = await _embeddings
            .GetSparseEmbeddingAsync(query, EmbeddingUse.Query, cancellationToken)
            .ConfigureAwait(false);

        if (queryVector is null || queryVector.Count == 0)
            return Array.Empty<SearchResult>();

        // Accumulate dot products over the inverted index: only the terms the query actually
        // activated (and the documents posting to them) ever get visited. The accumulator is the
        // same ordinal-indexed, generation-stamped buffer the lexical pass uses, so a search pays
        // for the candidates it touches rather than for a corpus-sized clear.
        using var scores = ScoreAccumulator.Rent(_byOrdinal.Count);

        foreach (var (term, queryWeight) in queryVector)
        {
            if (!float.IsFinite(queryWeight) || queryWeight <= 0)
                continue;

            if (!_postings.TryGetValue(term, out var postings))
                continue;

            for (int i = 0; i < postings.Count; i++)
                scores.Record(postings.OrdinalAt(i), (double)queryWeight * postings.WeightAt(i));
        }

        // The window is sized to the page the caller asked for, never to the candidate set. A
        // candidate costs one comparison against the worst kept row; a SearchResult is built only
        // for the rows that survive.
        int window = Math.Min(options.Window, scores.Count);
        var top = new TopRankedWindow(window, options.TieBreak, DocumentAt, IdAt);

        for (int i = 0; i < scores.Count; i++)
        {
            int ordinal = scores.OrdinalAt(i);
            double score = scores[ordinal];

            // A score of exactly 0 means the query and the document share no positive term.
            if (!(score > 0) || score < options.MinimumScore)
                continue;

            var document = _byOrdinal[ordinal];

            if (document is null || !options.PassesFilters(document))
                continue;

            top.Add(score, ordinal, document, i);
        }

        int skip = Math.Min(options.Offset, top.Count);
        int count = Math.Min(options.Limit, top.Count - skip);
        var page = new SearchResult[count];

        top.CopyBestTo(page, skip, count);

        // Recorded on the cut page, not per scored term.
        options.Trace?.RecordScoreStage("Sparse", page);

        return page;
    }

    private void AddVector(SearchDocument document, IReadOnlyDictionary<string, float>? vector)
    {
        if (_documents.ContainsKey(document.Id))
            Remove(document.Id);

        int ordinal;

        if (_freeOrdinals.Count > 0)
        {
            ordinal = _freeOrdinals[^1];
            _freeOrdinals.RemoveAt(_freeOrdinals.Count - 1);
            _byOrdinal[ordinal] = document;
        }
        else
        {
            ordinal = _byOrdinal.Count;
            _byOrdinal.Add(document);
        }

        _documents[document.Id] = document;
        _ordinals[document.Id] = ordinal;

        // Build the stored vector first: a term repeated in the input must reach the posting list
        // once, with its last weight, not once per occurrence. The old dictionary write happened to
        // collapse a duplicate; contiguous arrays would append it.
        var weights = new Dictionary<string, float>(StringComparer.Ordinal);

        foreach (var (term, weight) in vector ?? EmptyVector)
        {
            if (string.IsNullOrEmpty(term) || !float.IsFinite(weight) || weight <= 0)
                continue;

            weights[term] = weight;
        }

        foreach (var (term, weight) in weights)
        {
            if (!_postings.TryGetValue(term, out var postings))
            {
                postings = new PostingList();
                _postings[term] = postings;
            }

            postings.Add(ordinal, weight);
        }

        _vectorsByDocument[document.Id] = weights;
    }

    private SearchDocument? DocumentAt(int ordinal) =>
        ordinal >= 0 && ordinal < _byOrdinal.Count ? _byOrdinal[ordinal] : null;

    private string? IdAt(int ordinal) => DocumentAt(ordinal)?.Id;

    private static IReadOnlyDictionary<string, float> EmptyVector { get; } =
        new Dictionary<string, float>();

    /// <summary>
    /// One term's postings as contiguous arrays: ordinals ascending, weights aligned. Appending and
    /// removing preserve the order, so two searches over an unchanged corpus visit entries in the
    /// same sequence and sum the same doubles.
    /// </summary>
    private sealed class PostingList
    {
        private int[] _ordinals = new int[4];
        private float[] _weights = new float[4];

        public int Count { get; private set; }

        public int OrdinalAt(int index) => _ordinals[index];

        public float WeightAt(int index) => _weights[index];

        public void Add(int ordinal, float weight)
        {
            if (Count == _ordinals.Length)
            {
                Array.Resize(ref _ordinals, _ordinals.Length * 2);
                Array.Resize(ref _weights, _weights.Length * 2);
            }

            _ordinals[Count] = ordinal;
            _weights[Count] = weight;
            Count++;
        }

        public bool Remove(int ordinal)
        {
            for (int i = 0; i < Count; i++)
            {
                if (_ordinals[i] != ordinal)
                    continue;

                int move = Count - i - 1;

                if (move > 0)
                {
                    Array.Copy(_ordinals, i + 1, _ordinals, i, move);
                    Array.Copy(_weights, i + 1, _weights, i, move);
                }

                Count--;
                return true;
            }

            return false;
        }
    }
}
