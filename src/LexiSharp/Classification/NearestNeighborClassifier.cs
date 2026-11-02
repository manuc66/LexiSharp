using System.Runtime.InteropServices;

using LexiSharp.Core;
using LexiSharp.Hybrid;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;

namespace LexiSharp.Classification;

/// <summary>
/// K-nearest-neighbour text classifier: BM25 over terms, or cosine over embeddings, with a
/// configurable vote.
/// </summary>
/// <remarks>
/// <para>
/// On the dense path, an empty vector means "no embedding for this text": <see cref="TrainAsync"/>
/// and <see cref="LearnAsync"/> skip the example and <see cref="PredictAsync"/> returns nothing.
/// The lexical path has no such notion; an empty text is simply a document with no terms.
/// </para>
/// <para>
/// The lexical path stores each document's term frequencies and the corpus document frequencies;
/// a prediction scores every stored document against the query terms with the same Okapi BM25
/// formula as the search engine, then keeps the <see cref="NearestNeighborOptions.K"/> best. The
/// dense path stores one vector per document and ranks by cosine. Neither path is an inverted
/// index or an ANN: a prediction is O(N) in the corpus, which is the price of exactness and the
/// reason this classifier is not the default for a large corpus.
/// </para>
/// <para>
/// <see cref="Train"/> replaces the model; <see cref="Learn"/> adds one document and
/// <see cref="Unlearn"/> removes it by <see cref="SearchDocument.Id"/>. The id is the key: a
/// duplicate id is rejected by <see cref="Learn"/> and <see cref="Train"/>, and unlearning an id
/// the model does not hold is a no-op. The stored text is authoritative on removal, not the text
/// passed to <see cref="Unlearn"/>.
/// </para>
/// <para>
/// Excluded categories are filtered before the top-k cut, so an excluded neighbour does not
/// consume a place. The vote becomes a normalized distribution; ties are ordered by category name
/// (ordinal). <see cref="NearestNeighborOptions.MinProbability"/> withholds an answer whose top
/// probability is too low.
/// </para>
/// <para>
/// Thread-safety: concurrent <see cref="Predict(string, int, IReadOnlySet{string})"/> calls are
/// supported. <see cref="Train"/>, <see cref="Learn"/> and <see cref="Unlearn"/> mutate the store
/// and must not overlap a prediction or another mutation;
/// <see cref="SynchronizedTextClassifier"/> provides that lock.
/// </para>
/// </remarks>
public sealed class NearestNeighborClassifier : ITextClassifier, IIncrementalTextClassifier
{
    private readonly ITokenizer? _tokenizer;
    private readonly IEmbeddingProvider? _embeddings;
    private readonly NearestNeighborOptions _options;

    private Bm25Store? _bm25;
    private DenseStore? _dense;

    /// <summary>Creates a lexical (BM25) classifier over a tokenizer.</summary>
    /// <param name="tokenizer">Tokenizer used at training and prediction time; null means <see cref="Tokenizer.Default"/>.</param>
    /// <param name="options">Tunable knobs; when null, <see cref="NearestNeighborOptions.Default"/> applies.</param>
    /// <exception cref="ArgumentException">The options are not valid.</exception>
    public NearestNeighborClassifier(ITokenizer? tokenizer = null, NearestNeighborOptions? options = null)
    {
        _tokenizer = tokenizer ?? Tokenizer.Default;
        _options = options ?? NearestNeighborOptions.Default;

        if (!_options.IsValid)
            throw new ArgumentException("The nearest-neighbour options are not valid.", nameof(options));
    }

    /// <summary>Creates a dense classifier over an embedding provider.</summary>
    /// <param name="embeddings">External embedding producer; never implemented inside LexiSharp.</param>
    /// <param name="options">Tunable knobs; when null, <see cref="NearestNeighborOptions.Default"/> applies.</param>
    /// <exception cref="ArgumentNullException"><paramref name="embeddings"/> is null.</exception>
    /// <exception cref="ArgumentException">The options are not valid.</exception>
    public NearestNeighborClassifier(IEmbeddingProvider embeddings, NearestNeighborOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(embeddings);

        _embeddings = embeddings;
        _options = options ?? NearestNeighborOptions.Default;

        if (!_options.IsValid)
            throw new ArgumentException("The nearest-neighbour options are not valid.", nameof(options));
    }

    /// <summary>The options this classifier was created with.</summary>
    public NearestNeighborOptions Options => _options;

    /// <summary>Whether this classifier uses the dense embedding path.</summary>
    public bool IsDense => _embeddings is not null;

    /// <summary>Number of stored documents, or <c>0</c> before training.</summary>
    public int Count => _embeddings is null ? _bm25?.Count ?? 0 : _dense?.Count ?? 0;

    /// <inheritdoc />
    public void Train(IEnumerable<SearchDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        if (_embeddings is not null)
        {
            TrainAsync(documents).GetAwaiter().GetResult();
            return;
        }

        var store = new Bm25Store(_tokenizer!, _options);

        foreach (SearchDocument document in documents)
        {
            ArgumentNullException.ThrowIfNull(document);

            if (string.IsNullOrEmpty(document.Category))
                continue;

            store.Add(document);
        }

        _bm25 = store;
    }

    /// <summary>Embeds every labelled document and replaces the dense model.</summary>
    /// <param name="documents">The labelled documents; those without a category are skipped.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task TrainAsync(
        IEnumerable<SearchDocument> documents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);

        if (_embeddings is null)
        {
            Train(documents);
            return;
        }

        var labelled = new List<SearchDocument>();

        foreach (SearchDocument document in documents)
        {
            ArgumentNullException.ThrowIfNull(document);

            if (!string.IsNullOrEmpty(document.Category))
                labelled.Add(document);
        }

        var store = new DenseStore();

        if (labelled.Count > 0)
        {
            store.Reserve(labelled.Count, _embeddings.Dimension);

            if (_embeddings is IBatchEmbeddingProvider batch)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var texts = new string[labelled.Count];

                for (int i = 0; i < texts.Length; i++)
                    texts[i] = labelled[i].Text;

                IReadOnlyList<ReadOnlyMemory<float>> vectors = await batch
                    .GetTextEmbeddingsAsync(texts, _options.TrainingUse, cancellationToken)
                    .ConfigureAwait(false);

                if (vectors.Count != texts.Length)
                {
                    throw new InvalidOperationException(
                        $"The provider returned {vectors.Count} embeddings for {texts.Length} documents.");
                }

                for (int i = 0; i < labelled.Count; i++)
                {
                    ReadOnlyMemory<float> vector = Validate(vectors[i], labelled[i].Id);

                    if (vector.Length == 0)
                        continue;

                    store.Add(labelled[i].Id, labelled[i].Category!, vector);
                }
            }
            else
            {
                for (int i = 0; i < labelled.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    ReadOnlyMemory<float> vector = await _embeddings
                        .GetTextEmbeddingAsync(labelled[i].Text, _options.TrainingUse, cancellationToken)
                        .ConfigureAwait(false);

                    vector = Validate(vector, labelled[i].Id);

                    if (vector.Length == 0)
                        continue;

                    store.Add(labelled[i].Id, labelled[i].Category!, vector);
                }
            }
        }

        _dense = store;
    }

    /// <inheritdoc />
    public void Learn(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (string.IsNullOrEmpty(document.Category))
            return;

        if (_embeddings is not null)
        {
            LearnAsync(document).GetAwaiter().GetResult();
            return;
        }

        _bm25 ??= new Bm25Store(_tokenizer!, _options);
        _bm25.Add(document);
    }

    /// <summary>Embeds one labelled document and adds it to the dense model.</summary>
    /// <param name="document">The labelled document to learn.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task LearnAsync(
        SearchDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (string.IsNullOrEmpty(document.Category))
            return;

        if (_embeddings is null)
        {
            Learn(document);
            return;
        }

        ReadOnlyMemory<float> vector = await _embeddings
            .GetTextEmbeddingAsync(document.Text, _options.TrainingUse, cancellationToken)
            .ConfigureAwait(false);

        vector = Validate(vector, document.Id);

        if (vector.Length == 0)
            return;

        _dense ??= new DenseStore();
        _dense.Add(document.Id, document.Category, vector);
    }

    /// <inheritdoc />
    public void Unlearn(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (string.IsNullOrEmpty(document.Category))
            return;

        if (_embeddings is null)
            _bm25?.Remove(document.Id);
        else
            _dense?.Remove(document.Id);
    }

    /// <summary>Removes a document from the dense model; no embedding call is needed.</summary>
    /// <param name="document">The document to retract.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task UnlearnAsync(SearchDocument document, CancellationToken cancellationToken = default)
    {
        Unlearn(document);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IReadOnlyList<ClassificationResult> Predict(
        string text,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (limit <= 0 || _options.K <= 0)
            return Array.Empty<ClassificationResult>();

        if (_embeddings is not null)
            return PredictAsync(text, limit, excludedCategories).GetAwaiter().GetResult();

        TopK<Neighbor>? neighbors = _bm25?.Search(text, _options.K, excludedCategories);

        return neighbors is null
            ? Array.Empty<ClassificationResult>()
            : BuildResults(neighbors, limit);
    }

    /// <summary>Embeds a text and ranks the stored neighbours by cosine similarity.</summary>
    /// <param name="text">Raw text to classify.</param>
    /// <param name="limit">Maximum number of categories to return.</param>
    /// <param name="excludedCategories">Optional set of categories to hide.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<ClassificationResult>> PredictAsync(
        string text,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (limit <= 0 || _options.K <= 0)
            return Array.Empty<ClassificationResult>();

        if (_embeddings is null)
            return Predict(text, limit, excludedCategories);

        // An untrained model has nothing to compare against, so it must not pay for a provider call.
        if (_dense is null || _dense.Count == 0)
            return Array.Empty<ClassificationResult>();

        ReadOnlyMemory<float> vector = await _embeddings
            .GetTextEmbeddingAsync(text, _options.PredictionUse, cancellationToken)
            .ConfigureAwait(false);

        vector = Validate(vector, documentId: null);

        if (vector.Length == 0)
            return Array.Empty<ClassificationResult>();

        TopK<Neighbor> neighbors = _dense.Search(vector, _options.K, excludedCategories);

        return BuildResults(neighbors, limit);
    }

    /// <inheritdoc />
    public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null)
    {
        IReadOnlyList<ClassificationResult> results = Predict(text, limit: 1, excludedCategories);
        return results.Count > 0 ? results[0].Category : null;
    }

    private IReadOnlyList<ClassificationResult> BuildResults(TopK<Neighbor> neighbors, int limit)
    {
        int k = neighbors.Count;

        if (k <= 0)
            return Array.Empty<ClassificationResult>();

        Span<double> weights = k <= 64 ? stackalloc double[k] : new double[k];
        double total = 0;

        switch (_options.Vote)
        {
            case NeighborVote.Rank:
                for (int i = 0; i < k; i++)
                {
                    weights[i] = 1.0 / (i + 1);
                    total += weights[i];
                }

                break;

            case NeighborVote.Temperature:
                double highest = double.NegativeInfinity;

                for (int i = 0; i < k; i++)
                    highest = Math.Max(highest, neighbors[i].Score);

                for (int i = 0; i < k; i++)
                {
                    weights[i] = Math.Exp((neighbors[i].Score - highest) / _options.Temperature);
                    total += weights[i];
                }

                break;

            default:
                for (int i = 0; i < k; i++)
                {
                    weights[i] = Math.Max(0, neighbors[i].Score);
                    total += weights[i];
                }

                break;
        }

        if (total <= 0)
        {
            // No similarity carried any weight (a BM25 query with no overlapping term, or a dense
            // score set that is entirely non-positive under Score). Every selected neighbour votes
            // once rather than the model abstaining arbitrarily.
            weights.Fill(1.0);
            total = k;
        }

        // Aggregate in the array the search already allocated: a category repeats only when two
        // neighbours share it, so the distinct prefix stays short and a scan beats a dictionary.
        Span<Neighbor> items = neighbors.Items;
        int distinct = 0;

        for (int i = 0; i < k; i++)
        {
            Neighbor neighbor = items[i];
            double weighted = weights[i] / total;
            int found = -1;

            for (int j = 0; j < distinct; j++)
            {
                if (string.Equals(items[j].Category, neighbor.Category, StringComparison.Ordinal))
                {
                    found = j;
                    break;
                }
            }

            if (found >= 0)
            {
                items[found] = items[found] with { Score = items[found].Score + weighted };
            }
            else
            {
                items[distinct++] = new Neighbor(neighbor.Category, neighbor.Category, weighted);
            }
        }

        items[..distinct].Sort(ProbabilityComparer.Instance);

        if (items[0].Score < _options.MinProbability)
            return Array.Empty<ClassificationResult>();

        int take = Math.Min(limit, distinct);
        var results = new ClassificationResult[take];

        for (int i = 0; i < take; i++)
            results[i] = new ClassificationResult(items[i].Category, items[i].Score);

        return results;
    }

    private ReadOnlyMemory<float> Validate(ReadOnlyMemory<float> vector, string? documentId)
    {
        if (vector.Length == 0)
            return vector;

        if (vector.Length != _embeddings!.Dimension)
        {
            string subject = documentId is null ? "the input text" : $"'{documentId}'";
            throw new InvalidOperationException(
                $"The embedding provider returned a {vector.Length}-dimension vector for {subject} " +
                $"but declares Dimension={_embeddings.Dimension}.");
        }

        ReadOnlySpan<float> span = vector.Span;
        int index = VectorSimilarity.IndexOfNonFinite(span);

        if (index >= 0)
        {
            string subject = documentId is null ? "the input text" : $"'{documentId}'";
            throw new InvalidOperationException(
                $"The embedding provider returned a non-finite value ({span[index]}) at index {index} for {subject}.");
        }

        return vector;
    }

    private readonly record struct Neighbor(string Id, string Category, double Score);

    private sealed class NeighborComparer : IComparer<Neighbor>
    {
        public static readonly NeighborComparer Instance = new();

        public int Compare(Neighbor left, Neighbor right)
        {
            int byScore = right.Score.CompareTo(left.Score);

            return byScore != 0
                ? byScore
                : string.CompareOrdinal(left.Id, right.Id);
        }
    }

    private sealed class ProbabilityComparer : IComparer<Neighbor>
    {
        public static readonly ProbabilityComparer Instance = new();

        public int Compare(Neighbor left, Neighbor right) =>
            ClassificationRanking.Compare(left.Score, left.Category, right.Score, right.Category);
    }

    private sealed class Bm25Store
    {
        private readonly ITokenizer _tokenizer;
        private readonly NearestNeighborOptions _options;
        private readonly Dictionary<string, Document> _documents = new(StringComparer.Ordinal);

        // One id per term, so a document stores integers rather than a dictionary of strings. The
        // reverse list lets Remove release a term no document holds any more; ids are not reused,
        // so a dead slot costs one int and one null reference.
        private readonly Dictionary<string, int> _termIds = new(StringComparer.Ordinal);
        private readonly List<string?> _termText = new();
        private readonly List<int> _documentFrequency = new();
        private readonly Dictionary<int, int> _scratch = new();
        private long _totalLength;

        public Bm25Store(ITokenizer tokenizer, NearestNeighborOptions options)
        {
            _tokenizer = tokenizer;
            _options = options;
        }

        public int Count => _documents.Count;

        public void Add(SearchDocument document)
        {
            if (string.IsNullOrEmpty(document.Id))
                throw new ArgumentException("A document id must be non-empty.", nameof(document));

            if (_documents.ContainsKey(document.Id))
                throw new ArgumentException($"The document id '{document.Id}' is already in the model.", nameof(document));

            IReadOnlyList<string> tokens = _tokenizer.Tokenize(document.Text);
            _scratch.Clear();

            foreach (string token in tokens)
            {
                int termId = GetOrAddTerm(token);
                ref int count = ref CollectionsMarshal.GetValueRefOrAddDefault(_scratch, termId, out _);
                count++;
            }

            var terms = new TermFrequency[_scratch.Count];
            int index = 0;

            foreach (var (termId, count) in _scratch)
                terms[index++] = new TermFrequency(termId, count);

            Array.Sort(terms, static (left, right) => left.TermId.CompareTo(right.TermId));

            _documents[document.Id] = new Document(document.Category!, tokens.Count, terms);

            foreach (TermFrequency term in terms)
                _documentFrequency[term.TermId]++;

            _totalLength += tokens.Count;
        }

        public bool Remove(string id)
        {
            if (!_documents.TryGetValue(id, out Document document))
                return false;

            foreach (TermFrequency term in document.Terms)
            {
                int frequency = _documentFrequency[term.TermId];

                if (frequency <= 1)
                {
                    _documentFrequency[term.TermId] = 0;
                    ReleaseTerm(term.TermId);
                }
                else
                {
                    _documentFrequency[term.TermId] = frequency - 1;
                }
            }

            _totalLength -= document.Length;
            _documents.Remove(id);
            return true;
        }

        public TopK<Neighbor>? Search(
            string text,
            int k,
            IReadOnlySet<string>? excludedCategories)
        {
            if (k <= 0 || _documents.Count == 0)
                return null;

            var queryTerms = new List<(int TermId, double Weight)>();
            var seen = new HashSet<int>();

            foreach (string token in _tokenizer.Tokenize(text))
            {
                if (!_termIds.TryGetValue(token, out int termId) || !seen.Add(termId))
                    continue;

                int frequency = _documentFrequency[termId];

                if (frequency == 0)
                    continue;

                double idf = Math.Log(1 + (((double)_documents.Count - frequency + 0.5) / (frequency + 0.5)));
                queryTerms.Add((termId, idf * (_options.Bm25.K1 + 1)));
            }

            if (queryTerms.Count == 0)
                return null;

            queryTerms.Sort(static (left, right) => left.TermId.CompareTo(right.TermId));

            double averageLength = (double)_totalLength / _documents.Count;

            if (averageLength <= 0)
                return null;

            double k1 = _options.Bm25.K1;
            double b = _options.Bm25.B;
            double fixedPart = k1 * (1 - b);
            double lengthPart = k1 * b / averageLength;
            var top = new TopK<Neighbor>(k, NeighborComparer.Instance);

            foreach (var (id, document) in _documents)
            {
                if (excludedCategories?.Contains(document.Category) == true)
                    continue;

                double score = 0;
                double normalization = fixedPart + (document.Length * lengthPart);
                ReadOnlySpan<TermFrequency> documentTerms = document.Terms;
                int d = 0;
                int q = 0;

                while (d < documentTerms.Length && q < queryTerms.Count)
                {
                    TermFrequency documentTerm = documentTerms[d];
                    (int termId, double weight) = queryTerms[q];

                    if (documentTerm.TermId < termId)
                    {
                        d++;
                    }
                    else if (documentTerm.TermId > termId)
                    {
                        q++;
                    }
                    else
                    {
                        score += weight * documentTerm.Count / (documentTerm.Count + normalization);
                        d++;
                        q++;
                    }
                }

                if (score > 0)
                    top.Add(new Neighbor(id, document.Category, score));
            }

            return top;
        }

        private int GetOrAddTerm(string term)
        {
            ref int termId = ref CollectionsMarshal.GetValueRefOrAddDefault(_termIds, term, out bool exists);

            if (exists)
                return termId;

            termId = _documentFrequency.Count;
            _documentFrequency.Add(0);
            _termText.Add(term);
            return termId;
        }

        private void ReleaseTerm(int termId)
        {
            string? term = _termText[termId];

            if (term is null)
                return;

            _termIds.Remove(term);
            _termText[termId] = null;
        }

        private readonly record struct Document(
            string Category,
            int Length,
            TermFrequency[] Terms);
    }

    private readonly record struct TermFrequency(int TermId, int Count);

    private sealed class DenseStore
    {
        private readonly Dictionary<string, int> _indexById = new(StringComparer.Ordinal);

        // The vectors live in one buffer and an entry is its index, so a prediction walks
        // contiguous memory instead of chasing one array per document. Remove copies the last
        // vector into the hole, which keeps the first Count slots dense.
        private Entry[] _entries = new Entry[16];
        private float[] _vectors = Array.Empty<float>();
        private int _count;
        private int _dimension;

        public int Count => _count;

        /// <summary>Sizes the two buffers once, before a training loop adds every vector individually.</summary>
        public void Reserve(int count, int dimension)
        {
            if (count <= 0 || dimension <= 0)
                return;

            _dimension = dimension;

            if (_entries.Length < count)
                Array.Resize(ref _entries, count);

            int required = count * dimension;

            if (_vectors.Length < required)
                Array.Resize(ref _vectors, required);
        }

        public void Add(string id, string category, ReadOnlyMemory<float> vector)
        {
            if (string.IsNullOrEmpty(id))
                throw new ArgumentException("A document id must be non-empty.", nameof(id));

            if (_indexById.ContainsKey(id))
                throw new ArgumentException($"The document id '{id}' is already in the model.", nameof(id));

            if (_dimension == 0)
                _dimension = vector.Length;
            else if (vector.Length != _dimension)
                throw new InvalidOperationException(
                    $"The vector has {vector.Length} dimensions but the store holds {_dimension}.");

            EnsureCapacity(_count + 1);

            int offset = _count * _dimension;
            vector.Span.CopyTo(_vectors.AsSpan(offset, _dimension));
            ReadOnlySpan<float> stored = _vectors.AsSpan(offset, _dimension);

            _entries[_count] = new Entry(id, category, VectorSimilarity.Norm(stored));
            _indexById[id] = _count;
            _count++;
        }

        public bool Remove(string id)
        {
            if (!_indexById.Remove(id, out int index))
                return false;

            int last = _count - 1;

            if (index != last)
            {
                Entry moved = _entries[last];
                _entries[index] = moved;
                _indexById[moved.Id] = index;
                _vectors.AsSpan(last * _dimension, _dimension)
                    .CopyTo(_vectors.AsSpan(index * _dimension, _dimension));
            }

            _count--;
            return true;
        }

        public TopK<Neighbor> Search(
            ReadOnlyMemory<float> query,
            int k,
            IReadOnlySet<string>? excludedCategories)
        {
            if (k <= 0 || _count == 0)
                return new TopK<Neighbor>(0, NeighborComparer.Instance);

            var top = new TopK<Neighbor>(k, NeighborComparer.Instance);
            ReadOnlySpan<float> querySpan = query.Span;
            float queryNorm = VectorSimilarity.Norm(querySpan);

            for (int i = 0; i < _count; i++)
            {
                Entry entry = _entries[i];

                if (excludedCategories?.Contains(entry.Category) == true)
                    continue;

                ReadOnlySpan<float> vector = _vectors.AsSpan(i * _dimension, _dimension);
                top.Add(new Neighbor(
                    entry.Id,
                    entry.Category,
                    VectorSimilarity.CosineSimilarity(querySpan, vector, queryNorm, entry.Norm)));
            }

            return top;
        }

        private void EnsureCapacity(int count)
        {
            if (_entries.Length < count)
                Array.Resize(ref _entries, Math.Max(16, _entries.Length * 2));

            int required = count * _dimension;

            if (_vectors.Length < required)
                Array.Resize(ref _vectors, Math.Max(_vectors.Length * 2, required));
        }

        private readonly record struct Entry(string Id, string Category, float Norm);
    }
}
