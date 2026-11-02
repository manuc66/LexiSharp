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

        IReadOnlyList<Neighbor> neighbors = _bm25?.Search(text, _options.K, excludedCategories)
            ?? Array.Empty<Neighbor>();

        return BuildResults(neighbors, limit);
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

        IReadOnlyList<Neighbor> neighbors = _dense.Search(vector, _options.K, excludedCategories);

        return BuildResults(neighbors, limit);
    }

    /// <inheritdoc />
    public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null)
    {
        IReadOnlyList<ClassificationResult> results = Predict(text, limit: 1, excludedCategories);
        return results.Count > 0 ? results[0].Category : null;
    }

    private IReadOnlyList<ClassificationResult> BuildResults(
        IReadOnlyList<Neighbor> neighbors,
        int limit)
    {
        int k = Math.Min(_options.K, neighbors.Count);

        if (k <= 0)
            return Array.Empty<ClassificationResult>();

        var weights = new double[k];
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
            for (int i = 0; i < k; i++)
                weights[i] = 1.0;

            total = k;
        }

        var scores = new Dictionary<string, double>(StringComparer.Ordinal);

        for (int i = 0; i < k; i++)
        {
            scores.TryGetValue(neighbors[i].Category, out double current);
            scores[neighbors[i].Category] = current + (weights[i] / total);
        }

        var ranked = new List<(string Category, double Probability)>(scores.Count);

        foreach (var (category, probability) in scores)
            ranked.Add((category, probability));

        ranked.Sort(static (left, right) =>
        {
            int byProbability = right.Probability.CompareTo(left.Probability);
            return byProbability != 0
                ? byProbability
                : string.CompareOrdinal(left.Category, right.Category);
        });

        if (ranked.Count == 0 || ranked[0].Probability < _options.MinProbability)
            return Array.Empty<ClassificationResult>();

        int take = Math.Min(limit, ranked.Count);
        var results = new ClassificationResult[take];

        for (int i = 0; i < take; i++)
            results[i] = new ClassificationResult(ranked[i].Category, ranked[i].Probability);

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

        for (int i = 0; i < span.Length; i++)
        {
            if (float.IsFinite(span[i]))
                continue;

            string subject = documentId is null ? "the input text" : $"'{documentId}'";
            throw new InvalidOperationException(
                $"The embedding provider returned a non-finite value ({span[i]}) at index {i} for {subject}.");
        }

        return vector;
    }

    private readonly record struct Neighbor(string Id, string Category, double Score);

    /// <summary>
    /// A bounded, sorted top-k buffer. Keeping the best k in a fixed array is what stops a
    /// prediction from allocating one object per stored document; the insertion is O(k) only when
    /// a candidate actually beats the current worst, which is rare once the buffer is full.
    /// </summary>
    private sealed class TopKBuffer
    {
        private readonly Neighbor[] _items;
        private int _count;

        public TopKBuffer(int k) => _items = new Neighbor[k];

        public void Add(Neighbor candidate)
        {
            int index;

            if (_count < _items.Length)
            {
                index = _count++;
            }
            else
            {
                if (!IsBetter(candidate, _items[^1]))
                    return;

                index = _items.Length - 1;
            }

            while (index > 0 && IsBetter(candidate, _items[index - 1]))
            {
                _items[index] = _items[index - 1];
                index--;
            }

            _items[index] = candidate;
        }

        public IReadOnlyList<Neighbor> ToList() => _items[.._count];

        private static bool IsBetter(Neighbor left, Neighbor right)
        {
            int byScore = left.Score.CompareTo(right.Score);

            return byScore != 0
                ? byScore > 0
                : string.CompareOrdinal(left.Id, right.Id) < 0;
        }
    }

    private sealed class Bm25Store
    {
        private readonly ITokenizer _tokenizer;
        private readonly NearestNeighborOptions _options;
        private readonly Dictionary<string, Document> _documents = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);
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
            var terms = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (string token in tokens)
            {
                terms.TryGetValue(token, out int count);
                terms[token] = count + 1;
            }

            foreach (string term in terms.Keys)
            {
                _documentFrequency.TryGetValue(term, out int frequency);
                _documentFrequency[term] = frequency + 1;
            }

            _documents[document.Id] = new Document(document.Category!, tokens.Count, terms);
            _totalLength += tokens.Count;
        }

        public bool Remove(string id)
        {
            if (!_documents.TryGetValue(id, out Document? document))
                return false;

            foreach (string term in document.Terms.Keys)
            {
                _documentFrequency.TryGetValue(term, out int frequency);

                if (frequency <= 1)
                    _documentFrequency.Remove(term);
                else
                    _documentFrequency[term] = frequency - 1;
            }

            _totalLength -= document.Length;
            _documents.Remove(id);
            return true;
        }

        public IReadOnlyList<Neighbor> Search(
            string text,
            int k,
            IReadOnlySet<string>? excludedCategories)
        {
            if (k <= 0 || _documents.Count == 0)
                return Array.Empty<Neighbor>();

            var queryTerms = new HashSet<string>(StringComparer.Ordinal);

            foreach (string token in _tokenizer.Tokenize(text))
                queryTerms.Add(token);

            if (queryTerms.Count == 0)
                return Array.Empty<Neighbor>();

            double averageLength = (double)_totalLength / _documents.Count;

            if (averageLength <= 0)
                return Array.Empty<Neighbor>();

            double k1 = _options.Bm25.K1;
            double b = _options.Bm25.B;
            var idf = new Dictionary<string, double>(StringComparer.Ordinal);

            foreach (string term in queryTerms)
            {
                if (!_documentFrequency.TryGetValue(term, out int frequency) || frequency == 0)
                    continue;

                idf[term] = Math.Log(1 + (((double)_documents.Count - frequency + 0.5) / (frequency + 0.5)));
            }

            if (idf.Count == 0)
                return Array.Empty<Neighbor>();

            var top = new TopKBuffer(k);

            foreach (var (id, document) in _documents)
            {
                if (excludedCategories?.Contains(document.Category) == true)
                    continue;

                double score = 0;
                double normalization = k1 * (1 - b + (b * document.Length / averageLength));

                foreach (var (term, termIdf) in idf)
                {
                    if (!document.Terms.TryGetValue(term, out int frequency))
                        continue;

                    score += termIdf * frequency * (k1 + 1) / (frequency + normalization);
                }

                if (score > 0)
                    top.Add(new Neighbor(id, document.Category, score));
            }

            return top.ToList();
        }

        private sealed record Document(string Category, int Length, IReadOnlyDictionary<string, int> Terms);
    }

    private sealed class DenseStore
    {
        private readonly Dictionary<string, Entry> _documents = new(StringComparer.Ordinal);

        public int Count => _documents.Count;

        public void Add(string id, string category, ReadOnlyMemory<float> vector)
        {
            if (string.IsNullOrEmpty(id))
                throw new ArgumentException("A document id must be non-empty.", nameof(id));

            if (_documents.ContainsKey(id))
                throw new ArgumentException($"The document id '{id}' is already in the model.", nameof(id));

            float[] stored = vector.ToArray();
            _documents[id] = new Entry(category, stored, VectorSimilarity.Norm(stored));
        }

        public bool Remove(string id) => _documents.Remove(id);

        public IReadOnlyList<Neighbor> Search(
            ReadOnlyMemory<float> query,
            int k,
            IReadOnlySet<string>? excludedCategories)
        {
            var top = new TopKBuffer(k);
            float queryNorm = VectorSimilarity.Norm(query.Span);

            foreach (var (id, entry) in _documents)
            {
                if (excludedCategories?.Contains(entry.Category) == true)
                    continue;

                top.Add(new Neighbor(
                    id,
                    entry.Category,
                    VectorSimilarity.CosineSimilarity(query.Span, entry.Vector, queryNorm, entry.Norm)));
            }

            return top.ToList();
        }

        private sealed record Entry(string Category, float[] Vector, float Norm);
    }
}
