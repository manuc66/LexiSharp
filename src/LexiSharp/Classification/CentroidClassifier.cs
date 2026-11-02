using LexiSharp.Core;
using LexiSharp.Hybrid;

namespace LexiSharp.Classification;

/// <summary>
/// Nearest-centroid classifier over caller-supplied vectors: one mean per category, ranked by
/// cosine similarity, with a softmax over the cosines as the posterior.
/// </summary>
/// <remarks>
/// <para>
/// Training ignores an item whose category is null or empty, exactly as
/// <see cref="NaiveBayesClassifier"/> does, and replaces any previous model. An item whose vector
/// is empty means "no embedding" and is skipped in <see cref="Train"/>, <see cref="Learn"/>,
/// <see cref="Unlearn"/> and <see cref="Predict"/>; it is not a zero vector. The centroid of a
/// category is the elementwise mean of its training vectors; a category with no vector is absent,
/// and a category whose vectors are all zero keeps a zero centroid, which scores <c>0</c> against
/// everything rather than being dropped silently.
/// </para>
/// <para>
/// This is a <i>symmetric</i> classifier: the labelled examples and the item being predicted play
/// the same role. It knows nothing about text, tokens or an embedding model — the vector source is
/// the caller's — which is what lets the same class classify a window title, a time bucket or an
/// image descriptor. <see cref="CentroidClassifier"/> is the text wrapper over an
/// <see cref="IEmbeddingProvider"/>.
/// </para>
/// <para>
/// The posterior is <c>softmax(cos(c, centroid) / Temperature)</c>. Cosine order does not depend
/// on the temperature, so it changes the reported confidence and the blend weight in
/// <see cref="FusedTextClassifier"/>, never the ranking. Categories with equal probability are
/// ordered by category name (ordinal), so the output is deterministic.
/// </para>
/// <para>
/// Thread-safety: concurrent <see cref="Predict(ReadOnlyMemory{float}, int, IReadOnlySet{string})"/>
/// calls are supported. <see cref="Train"/> must not run concurrently with a prediction or another
/// training; it replaces the centroid table in one assignment.
/// </para>
/// </remarks>
/// <typeparam name="TItem">The item type; the caller supplies the vector and the label.</typeparam>
public sealed class CentroidClassifier<TItem>
{
    private readonly Func<TItem, ReadOnlyMemory<float>> _vectorOf;
    private readonly Func<TItem, string?> _categoryOf;
    private readonly CentroidClassifierOptions _options;

    // Sums are carried in double precision so that learning and unlearning a document does not
    // accumulate a float rounding error into the class mean. The exposed centroids stay float,
    // because that is the precision the cosine arithmetic and the provider use.
    private readonly Dictionary<string, double[]> _sums = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private double[] _globalSum = Array.Empty<double>();
    private int _globalCount;
    private float[] _globalMean = Array.Empty<float>();

    private IReadOnlyDictionary<string, ReadOnlyMemory<float>> _centroids =
        new Dictionary<string, ReadOnlyMemory<float>>(StringComparer.Ordinal);

    private IReadOnlyDictionary<string, float> _centroidNorms =
        new Dictionary<string, float>(StringComparer.Ordinal);

    // The hot prediction loop reads category, vector and norm together; a dictionary lookup for
    // the norm per category per query was pure overhead, and the array is ordered by category.
    private CentroidEntry[] _centroidEntries = Array.Empty<CentroidEntry>();

    private int _dimension;
    private bool _dirty;

    /// <summary>Creates a classifier over a caller-supplied vector source and label.</summary>
    /// <param name="vectorOf">Produces the embedding of one item. Every vector must have the same length.</param>
    /// <param name="categoryOf">Returns the item's category, or null/empty to skip it during training.</param>
    /// <param name="options">Tunable knobs; when null, <see cref="CentroidClassifierOptions.Default"/> applies.</param>
    /// <exception cref="ArgumentNullException">A delegate is null.</exception>
    /// <exception cref="ArgumentException">The options are not valid.</exception>
    public CentroidClassifier(
        Func<TItem, ReadOnlyMemory<float>> vectorOf,
        Func<TItem, string?> categoryOf,
        CentroidClassifierOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(vectorOf);
        ArgumentNullException.ThrowIfNull(categoryOf);

        _vectorOf = vectorOf;
        _categoryOf = categoryOf;
        _options = options ?? CentroidClassifierOptions.Default;

        if (!_options.IsValid)
            throw new ArgumentException("The centroid options are not valid.", nameof(options));
    }

    /// <summary>The class means, keyed by category; empty until training.</summary>
    /// <remarks>
    /// The values are the means the classifier actually compares against. When
    /// <see cref="CentroidClassifierOptions.CenterOnGlobalMean"/> is set they are already centered;
    /// the arrays behind them are never mutated after a rebuild.
    /// </remarks>
    public IReadOnlyDictionary<string, ReadOnlyMemory<float>> Centroids
    {
        get
        {
            EnsureBuilt();
            return _centroids;
        }
    }

    /// <summary>Vector length the model was trained on, or <c>0</c> before training.</summary>
    public int Dimension => _dimension;

    /// <summary>Number of categories in the model, or <c>0</c> before training.</summary>
    public int CategoryCount => _counts.Count;

    /// <summary>The softmax temperature applied to cosine similarities.</summary>
    public double Temperature => _options.Temperature;

    /// <summary>Whether the class means are centered on the corpus mean before the cosine.</summary>
    public bool CenterOnGlobalMean => _options.CenterOnGlobalMean;

    /// <summary>Trains the model on labelled items, replacing any previous model.</summary>
    /// <param name="items">The training items. Unlabelled items are skipped.</param>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is null or contains null.</exception>
    /// <exception cref="ArgumentException">A non-empty vector is non-finite or has a length different from the first one.</exception>
    public void Train(IEnumerable<TItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var sums = new Dictionary<string, double[]>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int dimension = -1;

        foreach (var item in items)
        {
            if (item is null)
                throw new ArgumentNullException(nameof(items), "The training sequence contains a null item.");

            string? category = _categoryOf(item);

            if (string.IsNullOrEmpty(category))
                continue;

            ReadOnlyMemory<float> vector = _vectorOf(item);

            // Empty means "no embedding for this text", not a zero vector: the example is skipped
            // exactly as an unlabelled item is.
            if (vector.Length == 0)
                continue;

            ValidateShape(vector, ref dimension, nameof(items));
            ValidateFinite(vector, category, nameof(items));

            if (!sums.TryGetValue(category, out double[]? sum))
            {
                sum = new double[dimension];
                sums[category] = sum;
                counts[category] = 0;
            }

            Accumulate(vector.Span, sum);
            counts[category]++;
        }

        _sums.Clear();
        _counts.Clear();

        foreach (var (category, sum) in sums)
        {
            _sums[category] = sum;
            _counts[category] = counts[category];
        }

        _dimension = dimension < 0 ? 0 : dimension;
        _globalSum = new double[_dimension];
        _globalCount = 0;

        foreach (var (category, sum) in _sums)
        {
            for (int i = 0; i < _globalSum.Length; i++)
                _globalSum[i] += sum[i];

            _globalCount += _counts[category];
        }

        _dirty = true;
        EnsureBuilt();
    }

    /// <summary>
    /// Adds one labelled item to the model, as though it had been part of the last
    /// <see cref="Train"/> call. A null or empty category is ignored, exactly as in
    /// <see cref="Train"/>.
    /// </summary>
    /// <remarks>
    /// The vector is added to the category's sum, the category's document count and the corpus
    /// mean. It is not a retraining: no previous vector is revisited. Learning the same item
    /// twice weighs it twice, matching two copies in the training corpus.
    /// </remarks>
    /// <param name="item">The labelled item to learn.</param>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is null.</exception>
    /// <exception cref="ArgumentException">A non-empty vector is non-finite or has the wrong length; an empty vector is skipped.</exception>
    public void Learn(TItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        string? category = _categoryOf(item);

        if (string.IsNullOrEmpty(category))
            return;

        ReadOnlyMemory<float> vector = _vectorOf(item);

        if (vector.Length == 0)
            return;

        if (_counts.Count == 0 && _dimension == 0)
            _dimension = vector.Length;

        if (vector.Length != _dimension)
        {
            throw new ArgumentException(
                $"The vector has {vector.Length} dimensions but the model was trained on {_dimension}.",
                nameof(item));
        }

        ValidateFinite(vector, category, nameof(item));

        if (!_sums.TryGetValue(category, out double[]? sum))
        {
            sum = new double[_dimension];
            _sums[category] = sum;
        }

        Accumulate(vector.Span, sum);
        _counts.TryGetValue(category, out int count);
        _counts[category] = count + 1;

        if (_globalSum.Length != _dimension)
            _globalSum = new double[_dimension];

        AddToGlobal(vector.Span);
        _globalCount++;
        _dirty = true;
    }

    /// <summary>
    /// Removes one labelled item's contribution: the category loses the vector from its sum and a
    /// document from its count, and a category with no document left is retired.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is arithmetic, not bookkeeping. It does not verify that the item was ever learned:
    /// tracking that would cost memory proportional to the corpus. Unlearning a document the model
    /// never saw is well defined — the category loses a document and the vector is subtracted — and
    /// the same caveat as <see cref="IIncrementalTextClassifier"/> applies.
    /// </para>
    /// <para>
    /// The vector is re-read from the source, so a non-deterministic provider makes the inverse
    /// approximate. With a deterministic provider it is exact up to the double-to-float conversion
    /// of the exposed centroid.
    /// </para>
    /// </remarks>
    /// <param name="item">The item to retract.</param>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is null.</exception>
    /// <exception cref="ArgumentException">A non-empty vector is non-finite or has the wrong length; an empty vector is skipped.</exception>
    public void Unlearn(TItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        string? category = _categoryOf(item);

        if (string.IsNullOrEmpty(category) || !_counts.TryGetValue(category, out int count))
            return;

        ReadOnlyMemory<float> vector = _vectorOf(item);

        if (vector.Length == 0)
            return;

        if (vector.Length != _dimension)
        {
            throw new ArgumentException(
                $"The vector has {vector.Length} dimensions but the model was trained on {_dimension}.",
                nameof(item));
        }

        ValidateFinite(vector, category, nameof(item));

        Subtract(vector.Span, _sums[category]);
        SubtractFromGlobal(vector.Span);
        count--;
        _globalCount = Math.Max(0, _globalCount - 1);

        if (count <= 0)
        {
            _sums.Remove(category);
            _counts.Remove(category);
        }
        else
        {
            _counts[category] = count;
        }

        if (_globalCount == 0)
            Array.Clear(_globalSum);

        _dirty = true;
    }

    /// <summary>Predicts the most likely categories for a vector, by decreasing probability.</summary>
    /// <param name="vector">The vector to classify; its length must match the training vectors.</param>
    /// <param name="limit">Maximum number of categories to return; a non-positive value returns nothing.</param>
    /// <param name="excludedCategories">Optional set of categories to hide; probabilities are renormalized over the rest.</param>
    /// <exception cref="ArgumentException">A non-empty vector is non-finite or has the wrong length; an empty vector is skipped.</exception>
    public IReadOnlyList<ClassificationResult> Predict(
        ReadOnlyMemory<float> vector,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null)
    {
        if (limit <= 0 || _counts.Count == 0)
            return Array.Empty<ClassificationResult>();

        EnsureBuilt();

        if (vector.Length == 0)
            return Array.Empty<ClassificationResult>();

        if (vector.Length != _dimension)
        {
            throw new ArgumentException(
                $"The query vector has {vector.Length} dimensions but the model was trained on {_dimension}.",
                nameof(vector));
        }

        ValidateFinite(vector, null, nameof(vector));

        return PredictValidated(vector, limit, excludedCategories);
    }

    /// <summary>
    /// The ranking half of <see cref="Predict"/>, for a caller that has already checked the length
    /// and finiteness of the vector. The text wrapper validates the provider's output once and
    /// then calls this, rather than scanning the same vector a second time.
    /// </summary>
    internal IReadOnlyList<ClassificationResult> PredictValidated(
        ReadOnlyMemory<float> vector,
        int limit,
        IReadOnlySet<string>? excludedCategories)
    {
        if (limit <= 0 || vector.Length == 0)
            return Array.Empty<ClassificationResult>();

        EnsureBuilt();

        if (_centroidEntries.Length == 0)
            return Array.Empty<ClassificationResult>();

        ReadOnlySpan<float> query = vector.Span;
        float[]? centered = null;

        if (_options.CenterOnGlobalMean && _globalCount > 0)
        {
            centered = new float[_dimension];

            for (int i = 0; i < centered.Length; i++)
                centered[i] = query[i] - _globalMean[i];

            query = centered;
        }

        var scores = new Score[_centroidEntries.Length];
        int count = 0;
        double highest = double.NegativeInfinity;
        float queryNorm = VectorSimilarity.Norm(query);

        foreach (CentroidEntry entry in _centroidEntries)
        {
            if (excludedCategories?.Contains(entry.Category) == true)
                continue;

            double cosine = VectorSimilarity.CosineSimilarity(
                query,
                entry.Vector.Span,
                queryNorm,
                entry.Norm);

            scores[count++] = new Score(entry.Category, cosine, 0);
            highest = Math.Max(highest, cosine);
        }

        if (count == 0)
            return Array.Empty<ClassificationResult>();

        double total = 0;

        for (int i = 0; i < count; i++)
        {
            double weight = Math.Exp((scores[i].Cosine - highest) / _options.Temperature);
            scores[i] = scores[i] with { Weight = weight };
            total += weight;
        }

        scores.AsSpan(0, count).Sort(ScoreComparer.Instance);

        int take = Math.Min(limit, count);
        var ranked = new ClassificationResult[take];

        for (int i = 0; i < take; i++)
            ranked[i] = new ClassificationResult(scores[i].Category, scores[i].Weight / total);

        return ranked;
    }

    /// <summary>Returns the single most likely category, or null when nothing was trained.</summary>
    /// <param name="vector">The vector to classify.</param>
    /// <param name="excludedCategories">Optional set of categories to hide.</param>
    public string? PredictBest(ReadOnlyMemory<float> vector, IReadOnlySet<string>? excludedCategories = null)
    {
        var results = Predict(vector, limit: 1, excludedCategories);
        return results.Count > 0 ? results[0].Category : null;
    }

    private void EnsureBuilt()
    {
        if (!_dirty)
            return;

        var centroids = new Dictionary<string, ReadOnlyMemory<float>>(_sums.Count, StringComparer.Ordinal);
        var norms = new Dictionary<string, float>(_sums.Count, StringComparer.Ordinal);
        var entries = new List<CentroidEntry>(_sums.Count);
        _globalMean = new float[_dimension];

        if (_options.CenterOnGlobalMean && _globalCount > 0)
        {
            for (int i = 0; i < _globalMean.Length; i++)
                _globalMean[i] = (float)(_globalSum[i] / _globalCount);
        }

        foreach (string category in _sums.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            int count = _counts[category];

            if (count <= 0)
                continue;

            double[] sum = _sums[category];
            var mean = new float[_dimension];

            for (int i = 0; i < mean.Length; i++)
            {
                double value = sum[i] / count;

                if (_options.CenterOnGlobalMean && _globalCount > 0)
                    value -= _globalMean[i];

                mean[i] = (float)value;
            }

            float norm = VectorSimilarity.Norm(mean);
            centroids[category] = mean;
            norms[category] = norm;
            entries.Add(new CentroidEntry(category, mean, norm));
        }

        _centroids = centroids;
        _centroidNorms = norms;
        _centroidEntries = entries.ToArray();
        _dirty = false;
    }

    private void AddToGlobal(ReadOnlySpan<float> vector)
    {
        if (_globalSum.Length != _dimension)
            _globalSum = new double[_dimension];

        for (int i = 0; i < vector.Length; i++)
            _globalSum[i] += vector[i];
    }

    private void SubtractFromGlobal(ReadOnlySpan<float> vector)
    {
        for (int i = 0; i < vector.Length; i++)
            _globalSum[i] -= vector[i];
    }

    private static void Accumulate(ReadOnlySpan<float> vector, Span<double> sum)
    {
        for (int i = 0; i < vector.Length; i++)
            sum[i] += vector[i];
    }

    private static void Subtract(ReadOnlySpan<float> vector, Span<double> sum)
    {
        for (int i = 0; i < vector.Length; i++)
            sum[i] -= vector[i];
    }

    /// <summary>Whether the model currently holds a category; used to skip a no-op unlearn.</summary>
    internal bool HoldsCategory(string category) => _counts.ContainsKey(category);

    /// <summary>Writes the options and the corpus state; the caller writes the envelope.</summary>
    internal void WriteState(BinaryWriter writer)
    {
        EnsureBuilt();

        writer.Write(_options.Temperature);
        writer.Write((byte)_options.TrainingUse);
        writer.Write((byte)_options.PredictionUse);
        writer.Write(_options.CenterOnGlobalMean);
        writer.Write(_dimension);

        string[] categories = _sums.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
        writer.Write(categories.Length);

        foreach (string category in categories)
        {
            ModelFormat.WriteString(writer, category);
            writer.Write(_counts[category]);
            ModelFormat.WriteDoubles(writer, _sums[category]);
        }

        writer.Write(_globalCount);
        ModelFormat.WriteDoubles(writer, _globalSum);
    }

    /// <summary>Replaces the corpus state from a persisted model.</summary>
    internal void LoadState(
        int dimension,
        Dictionary<string, double[]> sums,
        Dictionary<string, int> counts,
        double[] globalSum,
        int globalCount)
    {
        ArgumentNullException.ThrowIfNull(sums);
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentNullException.ThrowIfNull(globalSum);

        if (dimension < 0 || globalSum.Length != dimension)
            throw new InvalidDataException("The persisted centroid state is inconsistent.");

        if (counts.Keys.Any(category => !sums.TryGetValue(category, out double[]? sum) || sum.Length != dimension)
            || sums.Keys.Any(category => !counts.ContainsKey(category)))
        {
            throw new InvalidDataException("A persisted centroid has no matching sum and count.");
        }

        _sums.Clear();
        _counts.Clear();

        foreach (var (category, sum) in sums)
        {
            _sums[category] = sum;
            _counts[category] = counts[category];
        }

        _dimension = dimension;
        _globalSum = globalSum;
        _globalCount = globalCount;
        _dirty = true;
        EnsureBuilt();
    }

    private static void ValidateShape(
        ReadOnlyMemory<float> vector,
        ref int dimension,
        string parameterName)
    {
        if (dimension < 0)
        {
            dimension = vector.Length;
        }
        else if (vector.Length != dimension)
        {
            throw new ArgumentException(
                $"All vectors must have the same length (got {vector.Length} after {dimension}).",
                parameterName);
        }
    }

    private readonly record struct Score(string Category, double Cosine, double Weight);

    private sealed class ScoreComparer : IComparer<Score>
    {
        public static readonly ScoreComparer Instance = new();

        public int Compare(Score left, Score right)
        {
            int byCosine = right.Cosine.CompareTo(left.Cosine);

            return byCosine != 0
                ? byCosine
                : string.CompareOrdinal(left.Category, right.Category);
        }
    }

    private readonly record struct CentroidEntry(
        string Category,
        ReadOnlyMemory<float> Vector,
        float Norm);

    private static void ValidateFinite(ReadOnlyMemory<float> vector, string? category, string parameterName)
    {
        ReadOnlySpan<float> span = vector.Span;
        int index = VectorSimilarity.IndexOfNonFinite(span);

        if (index < 0)
            return;

        string subject = category is null ? "query" : $"category '{category}'";
        throw new ArgumentOutOfRangeException(
            parameterName,
            $"The vector source returned a non-finite value ({span[index]}) at index {index} for {subject}.");
    }
}

/// <summary>
/// Text wrapper over the generic nearest-centroid model: embeds documents with an
/// <see cref="IEmbeddingProvider"/>, computes one centroid per <see cref="SearchDocument.Category"/>,
/// and classifies a text by cosine similarity to those centroids.
/// </summary>
/// <remarks>
/// <para>
/// This is the semantic counterpart of <see cref="NaiveBayesClassifier"/>: it shares no term with
/// the input, only a vector, so it can recognise a paraphrase the lexical model never saw. It is
/// also weaker on rare identifiers and exactly spelled names, which is why
/// <see cref="FusedTextClassifier"/> exists.
/// </para>
/// <para>
/// LexiSharp does not run an embedding model. Supply an <see cref="IEmbeddingProvider"/> —
/// <c>LexiSharp.Onnx.OnnxTextEncoder</c> with an E5 export is the intended path — and the wrapper
/// asks it for <see cref="CentroidClassifierOptions.TrainingUse"/> vectors while training and
/// <see cref="CentroidClassifierOptions.PredictionUse"/> vectors when classifying. Train and
/// predict on the same side unless the provider is genuinely asymmetric; mixing the two embeds
/// the centroids and the input in different spaces.
/// </para>
/// <para>
/// <see cref="Train"/> and <see cref="Predict"/> block on the provider, because
/// <see cref="ITextClassifier"/> is synchronous. Prefer <see cref="TrainAsync"/>,
/// <see cref="PredictAsync"/>, <see cref="LearnAsync"/> and <see cref="UnlearnAsync"/> when the
/// provider is remote or runs on ONNX Runtime.
/// </para>
/// <para>
/// <see cref="Learn"/> and <see cref="Unlearn"/> are exact inverses with a deterministic provider:
/// they add or subtract the document's vector, its document count and its contribution to the
/// corpus mean. A category that loses its last document is retired. The model is arithmetic, not a
/// bookkeeping table, so unlearning a document that was never learned is well defined but not
/// verified; the same caveat as <see cref="IIncrementalTextClassifier"/> applies.
/// </para>
/// </remarks>
public sealed class CentroidClassifier : ITextClassifier, IIncrementalTextClassifier
{
    private readonly IEmbeddingProvider _embeddings;
    private readonly CentroidClassifierOptions _options;
    private readonly CentroidClassifier<LabelledVector> _inner;

    /// <summary>Creates a text classifier over an embedding provider.</summary>
    /// <param name="embeddings">External embedding producer; never implemented inside LexiSharp.</param>
    /// <param name="options">Tunable knobs; when null, <see cref="CentroidClassifierOptions.Default"/> applies.</param>
    /// <exception cref="ArgumentNullException"><paramref name="embeddings"/> is null.</exception>
    /// <exception cref="ArgumentException">The options are not valid.</exception>
    public CentroidClassifier(IEmbeddingProvider embeddings, CentroidClassifierOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(embeddings);

        _embeddings = embeddings;
        _options = options ?? CentroidClassifierOptions.Default;

        if (!_options.IsValid)
            throw new ArgumentException("The centroid options are not valid.", nameof(options));

        _inner = new CentroidClassifier<LabelledVector>(
            static entry => entry.Vector,
            static entry => entry.Category,
            _options);
    }

    /// <summary>The class means, keyed by category; empty until training.</summary>
    public IReadOnlyDictionary<string, ReadOnlyMemory<float>> Centroids => _inner.Centroids;

    /// <summary>Vector length the model was trained on, or <c>0</c> before training.</summary>
    public int Dimension => _inner.Dimension;

    /// <summary>Number of categories in the model, or <c>0</c> before training.</summary>
    public int CategoryCount => _inner.CategoryCount;

    /// <summary>The softmax temperature applied to cosine similarities.</summary>
    public double Temperature => _inner.Temperature;

    /// <summary>Whether the class means are centered on the corpus mean before the cosine.</summary>
    public bool CenterOnGlobalMean => _inner.CenterOnGlobalMean;

    /// <inheritdoc />
    public void Train(IEnumerable<SearchDocument> documents) =>
        TrainAsync(documents).GetAwaiter().GetResult();

    /// <summary>
    /// Embeds every labelled document and replaces the model, using one batch call when the
    /// provider implements <see cref="IBatchEmbeddingProvider"/>.
    /// </summary>
    /// <remarks>
    /// The input is materialized and embedded before the existing model is replaced, so a failed
    /// provider call leaves the previous centroids in place.
    /// </remarks>
    /// <param name="documents">The labelled documents; those without a category are skipped.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task TrainAsync(
        IEnumerable<SearchDocument> documents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);

        var labelled = new List<SearchDocument>();

        foreach (var document in documents)
        {
            ArgumentNullException.ThrowIfNull(document);

            if (!string.IsNullOrEmpty(document.Category))
                labelled.Add(document);
        }

        if (labelled.Count == 0)
        {
            _inner.Train(Array.Empty<LabelledVector>());
            return;
        }

        var entries = new LabelledVector[labelled.Count];

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

            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = new LabelledVector(
                    Validate(vectors[i], labelled[i].Id),
                    labelled[i].Category);
            }
        }
        else
        {
            for (int i = 0; i < entries.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ReadOnlyMemory<float> vector = await _embeddings
                    .GetTextEmbeddingAsync(labelled[i].Text, _options.TrainingUse, cancellationToken)
                    .ConfigureAwait(false);

                entries[i] = new LabelledVector(Validate(vector, labelled[i].Id), labelled[i].Category);
            }
        }

        _inner.Train(entries);
    }

    /// <summary>
    /// Embeds one labelled document and adds it to the model, as though it had been part of the
    /// last <see cref="Train"/> call. A document with no category is ignored.
    /// </summary>
    /// <param name="document">The labelled document to learn.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public void Learn(SearchDocument document) => LearnAsync(document).GetAwaiter().GetResult();

    /// <summary>Embeds one labelled document and adds it to the model.</summary>
    /// <param name="document">The labelled document to learn.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task LearnAsync(
        SearchDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (string.IsNullOrEmpty(document.Category))
            return;

        ReadOnlyMemory<float> vector = await _embeddings
            .GetTextEmbeddingAsync(document.Text, _options.TrainingUse, cancellationToken)
            .ConfigureAwait(false);

        _inner.Learn(new LabelledVector(Validate(vector, document.Id), document.Category));
    }

    /// <summary>
    /// Embeds one document and removes its contribution. A document with no category, or one whose
    /// category the model does not hold, is ignored without an embedding call.
    /// </summary>
    /// <param name="document">The document to retract.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public void Unlearn(SearchDocument document) => UnlearnAsync(document).GetAwaiter().GetResult();

    /// <summary>Embeds one document and removes its contribution.</summary>
    /// <param name="document">The document to retract.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task UnlearnAsync(
        SearchDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (string.IsNullOrEmpty(document.Category) || !_inner.HoldsCategory(document.Category))
            return;

        ReadOnlyMemory<float> vector = await _embeddings
            .GetTextEmbeddingAsync(document.Text, _options.TrainingUse, cancellationToken)
            .ConfigureAwait(false);

        _inner.Unlearn(new LabelledVector(Validate(vector, document.Id), document.Category));
    }

    /// <inheritdoc />
    public IReadOnlyList<ClassificationResult> Predict(
        string text,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        // An untrained model has nothing to embed, so it must not pay for a provider call.
        if (limit <= 0 || _inner.CategoryCount == 0)
            return Array.Empty<ClassificationResult>();

        Task<ReadOnlyMemory<float>> embedding = _embeddings.GetTextEmbeddingAsync(
            text, _options.PredictionUse, CancellationToken.None);

        // A provider that answers synchronously (the hashing baseline, a cached embedding) does
        // not need the async state machine or a thread-pool hop to reach the ranking.
        if (embedding.IsCompletedSuccessfully)
            return Rank(embedding.Result, limit, excludedCategories);

        return PredictAsyncCore(embedding, limit, excludedCategories).GetAwaiter().GetResult();
    }

    /// <summary>Embeds a text and ranks the category centroids by cosine similarity.</summary>
    /// <param name="text">Raw text to classify.</param>
    /// <param name="limit">Maximum number of categories to return.</param>
    /// <param name="excludedCategories">Optional set of categories to hide without retraining.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<IReadOnlyList<ClassificationResult>> PredictAsync(
        string text,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        // An untrained model has nothing to embed, so it must not pay for a provider call.
        if (limit <= 0 || _inner.CategoryCount == 0)
            return Array.Empty<ClassificationResult>();

        Task<ReadOnlyMemory<float>> embedding = _embeddings.GetTextEmbeddingAsync(
            text, _options.PredictionUse, cancellationToken);

        return await PredictAsyncCore(embedding, limit, excludedCategories).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ClassificationResult>> PredictAsyncCore(
        Task<ReadOnlyMemory<float>> embedding,
        int limit,
        IReadOnlySet<string>? excludedCategories)
    {
        ReadOnlyMemory<float> vector = await embedding.ConfigureAwait(false);
        return Rank(vector, limit, excludedCategories);
    }

    private IReadOnlyList<ClassificationResult> Rank(
        ReadOnlyMemory<float> vector,
        int limit,
        IReadOnlySet<string>? excludedCategories) =>
        _inner.PredictValidated(Validate(vector, documentId: null), limit, excludedCategories);

    /// <inheritdoc />
    public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null)
    {
        var results = Predict(text, limit: 1, excludedCategories);
        return results.Count > 0 ? results[0].Category : null;
    }

    /// <summary>Writes the options and the corpus state; the caller writes the envelope.</summary>
    internal void WriteState(BinaryWriter writer) => _inner.WriteState(writer);

    /// <summary>Reads a text model's options and state, pairing it with the caller's provider.</summary>
    internal static CentroidClassifier ReadState(BinaryReader reader, IEmbeddingProvider embeddings)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(embeddings);

        (CentroidClassifierOptions options, int dimension) = ReadOptions(reader, embeddings.Dimension);
        (Dictionary<string, double[]> sums, Dictionary<string, int> counts, double[] globalSum, int globalCount) =
            ReadCorpusState(reader, dimension);

        var model = new CentroidClassifier(embeddings, options);
        model._inner.LoadState(dimension, sums, counts, globalSum, globalCount);
        return model;
    }

    private static (CentroidClassifierOptions Options, int Dimension) ReadOptions(
        BinaryReader reader,
        int providerDimension)
    {
        double temperature = reader.ReadDouble();
        var trainingUse = (EmbeddingUse)reader.ReadByte();
        var predictionUse = (EmbeddingUse)reader.ReadByte();

        if (!Enum.IsDefined(trainingUse) || !Enum.IsDefined(predictionUse))
            throw new InvalidDataException("A stored embedding role is invalid.");

        bool center = reader.ReadBoolean();
        int dimension = reader.ReadInt32();

        if (dimension < 0 || dimension > 1_000_000)
            throw new InvalidDataException($"A centroid dimension is invalid ({dimension}).");

        if (dimension != providerDimension)
        {
            throw new InvalidDataException(
                $"The stored centroid model has {dimension} dimensions but the provider declares " +
                $"{providerDimension}.");
        }

        var options = new CentroidClassifierOptions
        {
            Temperature = temperature,
            TrainingUse = trainingUse,
            PredictionUse = predictionUse,
            CenterOnGlobalMean = center,
        };

        if (!options.IsValid)
            throw new InvalidDataException("The stored centroid options are not valid.");

        return (options, dimension);
    }

    private static (
        Dictionary<string, double[]> Sums,
        Dictionary<string, int> Counts,
        double[] GlobalSum,
        int GlobalCount) ReadCorpusState(BinaryReader reader, int dimension)
    {
        int categoryCount = ModelFormat.ReadCount(reader);
        var sums = new Dictionary<string, double[]>(categoryCount, StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(categoryCount, StringComparer.Ordinal);

        for (int i = 0; i < categoryCount; i++)
        {
            string category = ModelFormat.ReadString(reader)
                ?? throw new InvalidDataException("A centroid category is null.");

            if (sums.ContainsKey(category))
                throw new InvalidDataException($"The centroid category '{category}' appears twice.");

            int count = reader.ReadInt32();

            if (count <= 0)
                throw new InvalidDataException($"The centroid category '{category}' has a non-positive count.");

            double[] sum = ModelFormat.ReadDoubles(reader);

            if (sum.Length != dimension)
            {
                throw new InvalidDataException(
                    $"The centroid category '{category}' has {sum.Length} values for dimension {dimension}.");
            }

            sums[category] = sum;
            counts[category] = count;
        }

        int globalCount = reader.ReadInt32();

        if (globalCount < 0)
            throw new InvalidDataException($"A global centroid count is invalid ({globalCount}).");

        double[] globalSum = ModelFormat.ReadDoubles(reader);

        if (globalSum.Length != dimension)
        {
            throw new InvalidDataException(
                $"The global centroid sum has {globalSum.Length} values for dimension {dimension}.");
        }

        return (sums, counts, globalSum, globalCount);
    }

    private ReadOnlyMemory<float> Validate(ReadOnlyMemory<float> vector, string? documentId)
    {
        if (vector.Length == 0)
            return vector;

        if (vector.Length != _embeddings.Dimension)
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

    private readonly record struct LabelledVector(ReadOnlyMemory<float> Vector, string? Category);
}
