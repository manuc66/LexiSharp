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

    private readonly Dictionary<string, SparseVector> _vectorsByDocument =
        new(StringComparer.Ordinal);

    /// <summary>A stored vector as aligned arrays: the terms in the order the provider produced them.</summary>
    private sealed class SparseVector(string[] terms, float[] weights)
    {
        public string[] Terms { get; } = terms;

        public float[] Weights { get; } = weights;
    }

    private static readonly SparseVector EmptyVector = new([], []);

    /// <summary>First capacity of a posting list created by <see cref="Add"/>, which has no count to size from.</summary>
    private const int ExpectedPostingCapacity = 4;

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
            foreach (string term in vector.Terms)
            {
                if (!_postings.TryGetValue(term, out var postings))
                    continue;

                postings.Remove(ordinal);

                if (postings.LiveCount == 0)
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
            entries.Add(new SparseIndexEntry(document, Materialize(_vectorsByDocument[documentId])));

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

        // Counted before anything is stored, so each posting list can be allocated once at its
        // final size instead of doubling from four entries and copying its contents on the way.
        // The list is materialized because that count is a second enumeration; the entries array
        // the callers pass is already one, and a lazy source is cheaper to hold than to re-read.
        IReadOnlyList<SparseIndexEntry> list = entries as IReadOnlyList<SparseIndexEntry> ?? [.. entries];
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var entry in list)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(entry.Document);
            ArgumentNullException.ThrowIfNull(entry.Weights);

            foreach (var (term, weight) in entry.Weights)
            {
                if (IsStorable(term, weight))
                    counts[term] = counts.TryGetValue(term, out int count) ? count + 1 : 1;
            }
        }

        Clear();

        foreach (var (term, count) in counts)
            _postings[term] = new PostingList(count);

        foreach (var entry in list)
            AddVector(entry.Document, entry.Weights);
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

            if (postings.HasTombstones)
            {
                for (int i = 0; i < postings.Count; i++)
                {
                    float weight = postings.WeightAt(i);

                    if (weight > 0)
                        scores.Record(postings.OrdinalAt(i), (double)queryWeight * weight);
                }
            }
            else
            {
                for (int i = 0; i < postings.Count; i++)
                    scores.Record(postings.OrdinalAt(i), (double)queryWeight * postings.WeightAt(i));
            }
        }

        // The window is sized to the page the caller asked for, never to the candidate set. A
        // candidate costs one comparison against the worst kept row; a SearchResult is built only
        // for the rows that survive.
        //
        // The ordinal handed to the window is the document's position in the ordinal space, which is
        // what TieBreak.InsertionOrder compares — the same capability the lexical engine declares.
        // Without it the window falls back to the order the candidates arrived in, which here is the
        // order the accumulator first touched them, i.e. the query's term order: two spellings of
        // the same query would then hand two different pages for a tie.
        int window = Math.Min(options.Window, scores.Count);
        var top = new TopRankedWindow(window, options.TieBreak, DocumentAt, IdAt, ordinalsAreInsertionPositions: true);

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

            top.Add(score, ordinal, document);
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

        // A dictionary cannot repeat a key, so the stored vector needs no deduplication; it does
        // need the invalid weights dropped and a copy the caller cannot mutate afterwards. Two
        // aligned arrays do that without re-hashing every term into a second dictionary.
        string[] terms = Array.Empty<string>();
        float[] weights = Array.Empty<float>();

        if (vector is { Count: > 0 })
        {
            terms = new string[vector.Count];
            weights = new float[vector.Count];
            int kept = 0;

            foreach (var (term, weight) in vector)
            {
                if (!IsStorable(term, weight))
                    continue;

                terms[kept] = term;
                weights[kept] = weight;
                kept++;
            }

            if (kept != terms.Length)
            {
                Array.Resize(ref terms, kept);
                Array.Resize(ref weights, kept);
            }
        }

        for (int i = 0; i < terms.Length; i++)
        {
            if (!_postings.TryGetValue(terms[i], out var postings))
            {
                postings = new PostingList(ExpectedPostingCapacity);
                _postings[terms[i]] = postings;
            }

            postings.Add(ordinal, weights[i]);
        }

        _vectorsByDocument[document.Id] = terms.Length == 0 ? EmptyVector : new SparseVector(terms, weights);
    }

    private SearchDocument? DocumentAt(int ordinal) =>
        ordinal >= 0 && ordinal < _byOrdinal.Count ? _byOrdinal[ordinal] : null;

    private string? IdAt(int ordinal) => DocumentAt(ordinal)?.Id;

    private static bool IsStorable(string term, float weight) =>
        !string.IsNullOrEmpty(term) && float.IsFinite(weight) && weight > 0;

    private static IReadOnlyDictionary<string, float> Materialize(SparseVector vector)
    {
        var weights = new Dictionary<string, float>(vector.Terms.Length, StringComparer.Ordinal);

        for (int i = 0; i < vector.Terms.Length; i++)
            weights[vector.Terms[i]] = vector.Weights[i];

        return weights;
    }

    /// <summary>
    /// One term's postings as contiguous arrays: ordinals and weights aligned. There is no sort
    /// order — a new document appends its ordinal, and a freed ordinal reused by a later add is
    /// appended below ones already stored — so the array must not be read as ascending. Removal
    /// preserves the relative order of the rest, so two searches over an unchanged corpus visit the
    /// entries in the same sequence and sum the same doubles.
    /// </summary>
    private sealed class PostingList
    {
        private int[] _ordinals;
        private float[] _weights;
        private int _liveCount;
        private bool _sorted = true;
        private bool _hasTombstones;

        public PostingList(int capacity)
        {
            _ordinals = new int[capacity];
            _weights = new float[capacity];
        }

        public int Count { get; private set; }

        /// <summary>Entries that still belong to a document; the rest are removal tombstones.</summary>
        public int LiveCount => _liveCount;

        /// <summary>Whether any entry has been tombstoned and not yet compacted away.</summary>
        public bool HasTombstones => _hasTombstones;

        public int OrdinalAt(int index) => _ordinals[index];

        public float WeightAt(int index) => _weights[index];

        public void Add(int ordinal, float weight)
        {
            if (Count == _ordinals.Length)
            {
                int capacity = Math.Max(_ordinals.Length * 2, 4);
                Array.Resize(ref _ordinals, capacity);
                Array.Resize(ref _weights, capacity);
            }

            // Import and ordinary appends grow the ordinal, so the array is ascending and a removal
            // can binary-search it. A reused freed ordinal is smaller and drops that property; the
            // list then falls back to a linear scan, which is what it had before.
            if (_sorted && Count > 0 && _ordinals[Count - 1] >= ordinal)
                _sorted = false;

            _ordinals[Count] = ordinal;
            _weights[Count] = weight;
            Count++;
            _liveCount++;
        }

        /// <summary>
        /// Tombstones every live entry for <paramref name="ordinal"/>, then compacts when half the
        /// array is dead. This is what makes a removal independent of the list's length.
        /// </summary>
        /// <remarks>
        /// The arrays are append-only between compactions, so a search still visits the tombstone:
        /// the engine's loop skips a non-positive weight, and only the lists that have any pay that
        /// branch. Order is preserved, so two searches over an unchanged corpus still visit the same
        /// entries in the same sequence.
        /// </remarks>
        public bool Remove(int ordinal)
        {
            bool removed = _sorted ? TombstoneSorted(ordinal) : TombstoneLinear(ordinal);

            if (!removed)
                return false;

            _hasTombstones = true;

            if (_liveCount * 2 < Count)
                Compact();

            return true;
        }

        private bool TombstoneSorted(int ordinal)
        {
            int low = 0;
            int high = Count;

            while (low < high)
            {
                int middle = low + ((high - low) >> 1);

                if (_ordinals[middle] < ordinal)
                    low = middle + 1;
                else
                    high = middle;
            }

            bool removed = false;

            for (int i = low; i < Count && _ordinals[i] == ordinal; i++)
            {
                if (_weights[i] <= 0)
                    continue;

                _weights[i] = 0;
                _liveCount--;
                removed = true;
            }

            return removed;
        }

        private bool TombstoneLinear(int ordinal)
        {
            bool removed = false;

            for (int i = 0; i < Count; i++)
            {
                if (_ordinals[i] != ordinal || _weights[i] <= 0)
                    continue;

                _weights[i] = 0;
                _liveCount--;
                removed = true;
            }

            return removed;
        }

        private void Compact()
        {
            if (_liveCount == 0)
            {
                Count = 0;
                _hasTombstones = false;

                return;
            }

            var ordinals = new int[_liveCount];
            var weights = new float[_liveCount];
            int kept = 0;

            for (int i = 0; i < Count; i++)
            {
                if (_weights[i] <= 0)
                    continue;

                ordinals[kept] = _ordinals[i];
                weights[kept] = _weights[i];
                kept++;
            }

            _ordinals = ordinals;
            _weights = weights;
            Count = kept;
            _hasTombstones = false;
        }
    }
}
