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
/// <see cref="NaiveBayesClassifier"/> does, and replaces any previous model. The centroid of a
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
    private readonly double _temperature;

    private IReadOnlyDictionary<string, ReadOnlyMemory<float>> _centroids =
        new Dictionary<string, ReadOnlyMemory<float>>(StringComparer.Ordinal);

    private int _dimension;

    /// <summary>Creates a classifier over a caller-supplied vector source and label.</summary>
    /// <param name="vectorOf">Produces the embedding of one item. Every vector must have the same length.</param>
    /// <param name="categoryOf">Returns the item's category, or null/empty to skip it during training.</param>
    /// <param name="options">Tunable knobs; when null, <see cref="CentroidClassifierOptions.Default"/> applies.</param>
    /// <exception cref="ArgumentNullException">A delegate is null.</exception>
    /// <exception cref="ArgumentException">The temperature is not a finite positive number.</exception>
    public CentroidClassifier(
        Func<TItem, ReadOnlyMemory<float>> vectorOf,
        Func<TItem, string?> categoryOf,
        CentroidClassifierOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(vectorOf);
        ArgumentNullException.ThrowIfNull(categoryOf);

        _vectorOf = vectorOf;
        _categoryOf = categoryOf;

        var resolved = options ?? CentroidClassifierOptions.Default;

        if (!resolved.IsValid)
            throw new ArgumentException("Temperature must be a finite number greater than 0.", nameof(options));

        _temperature = resolved.Temperature;
    }

    /// <summary>The class means, keyed by category; empty until training.</summary>
    /// <remarks>
    /// The values are the means the classifier actually compares against. They are exposed for
    /// inspection and for a caller that persists the model; the arrays behind them are never
    /// mutated after <see cref="Train"/>.
    /// </remarks>
    public IReadOnlyDictionary<string, ReadOnlyMemory<float>> Centroids => _centroids;

    /// <summary>Vector length the model was trained on, or <c>0</c> before training.</summary>
    public int Dimension => _dimension;

    /// <summary>Number of categories in the model, or <c>0</c> before training.</summary>
    public int CategoryCount => _centroids.Count;

    /// <summary>The softmax temperature applied to cosine similarities.</summary>
    public double Temperature => _temperature;

    /// <summary>Trains the model on labelled items, replacing any previous model.</summary>
    /// <param name="items">The training items. Unlabelled items are skipped.</param>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is null or contains null.</exception>
    /// <exception cref="ArgumentException">A vector is empty, non-finite, or has a length different from the first one.</exception>
    public void Train(IEnumerable<TItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var sums = new Dictionary<string, float[]>(StringComparer.Ordinal);
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

            if (vector.Length == 0)
                throw new ArgumentException("The vector source returned an empty vector.", nameof(items));

            if (dimension < 0)
            {
                dimension = vector.Length;
            }
            else if (vector.Length != dimension)
            {
                throw new ArgumentException(
                    $"All vectors must have the same length (got {vector.Length} after {dimension}).",
                    nameof(items));
            }

            ValidateFinite(vector, category, nameof(items));

            if (!sums.TryGetValue(category, out float[]? sum))
            {
                sum = new float[dimension];
                sums[category] = sum;
                counts[category] = 0;
            }

            Accumulate(vector.Span, sum);
            counts[category]++;
        }

        var centroids = new Dictionary<string, ReadOnlyMemory<float>>(sums.Count, StringComparer.Ordinal);

        foreach (var (category, sum) in sums)
        {
            int count = counts[category];
            var mean = new float[dimension];
            float reciprocal = 1f / count;

            for (int i = 0; i < mean.Length; i++)
                mean[i] = sum[i] * reciprocal;

            centroids[category] = mean;
        }

        _centroids = centroids;
        _dimension = dimension < 0 ? 0 : dimension;
    }

    /// <summary>Predicts the most likely categories for a vector, by decreasing probability.</summary>
    /// <param name="vector">The vector to classify; its length must match the training vectors.</param>
    /// <param name="limit">Maximum number of categories to return; a non-positive value returns nothing.</param>
    /// <param name="excludedCategories">Optional set of categories to hide; probabilities are renormalized over the rest.</param>
    /// <exception cref="ArgumentException">The vector is empty, non-finite, or has the wrong length.</exception>
    public IReadOnlyList<ClassificationResult> Predict(
        ReadOnlyMemory<float> vector,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null)
    {
        if (limit <= 0 || _centroids.Count == 0)
            return Array.Empty<ClassificationResult>();

        if (vector.Length != _dimension)
        {
            throw new ArgumentException(
                $"The query vector has {vector.Length} dimensions but the model was trained on {_dimension}.",
                nameof(vector));
        }

        ValidateFinite(vector, null, nameof(vector));

        var categories = new List<string>(_centroids.Count);
        var cosines = new List<double>(_centroids.Count);
        double highest = double.NegativeInfinity;

        foreach (var (category, centroid) in _centroids)
        {
            if (excludedCategories?.Contains(category) == true)
                continue;

            double cosine = VectorSimilarity.CosineSimilarity(vector.Span, centroid.Span);
            categories.Add(category);
            cosines.Add(cosine);

            if (cosine > highest)
                highest = cosine;
        }

        if (categories.Count == 0)
            return Array.Empty<ClassificationResult>();

        var weights = new double[cosines.Count];
        double total = 0;

        for (int i = 0; i < weights.Length; i++)
        {
            // Subtracting the maximum is the usual softmax shift: exp stays in [0, 1] and the
            // largest term is exactly 1, so a corpus of mutually orthogonal vectors cannot underflow.
            double weight = Math.Exp((cosines[i] - highest) / _temperature);
            weights[i] = weight;
            total += weight;
        }

        var ranked = new List<ClassificationResult>(categories.Count);

        for (int i = 0; i < categories.Count; i++)
            ranked.Add(new ClassificationResult(categories[i], weights[i] / total));

        ranked.Sort(static (left, right) =>
        {
            int byProbability = right.Probability.CompareTo(left.Probability);
            return byProbability != 0
                ? byProbability
                : string.CompareOrdinal(left.Category, right.Category);
        });

        if (ranked.Count > limit)
            ranked.RemoveRange(limit, ranked.Count - limit);

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

    private static void Accumulate(ReadOnlySpan<float> vector, Span<float> sum)
    {
        for (int i = 0; i < vector.Length; i++)
            sum[i] += vector[i];
    }

    private static void ValidateFinite(ReadOnlyMemory<float> vector, string? category, string parameterName)
    {
        ReadOnlySpan<float> span = vector.Span;

        for (int i = 0; i < span.Length; i++)
        {
            if (float.IsFinite(span[i]))
                continue;

            string subject = category is null ? "query" : $"category '{category}'";
            throw new ArgumentOutOfRangeException(
                parameterName,
                $"The vector source returned a non-finite value ({span[i]}) at index {i} for {subject}.");
        }
    }
}

/// <summary>
/// Text wrapper over <see cref="CentroidClassifier{TItem}"/>: embeds documents with an
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
/// <see cref="ITextClassifier"/> is synchronous. Prefer <see cref="TrainAsync"/> and
/// <see cref="PredictAsync"/> when the provider is remote or runs on ONNX Runtime.
/// </para>
/// </remarks>
public sealed class CentroidClassifier : ITextClassifier
{
    private readonly IEmbeddingProvider _embeddings;
    private readonly CentroidClassifierOptions _options;
    private readonly CentroidClassifier<LabelledVector> _inner;

    /// <summary>Creates a text classifier over an embedding provider.</summary>
    /// <param name="embeddings">External embedding producer; never implemented inside LexiSharp.</param>
    /// <param name="options">Tunable knobs; when null, <see cref="CentroidClassifierOptions.Default"/> applies.</param>
    /// <exception cref="ArgumentNullException"><paramref name="embeddings"/> is null.</exception>
    /// <exception cref="ArgumentException">The temperature is not a finite positive number.</exception>
    public CentroidClassifier(IEmbeddingProvider embeddings, CentroidClassifierOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(embeddings);

        _embeddings = embeddings;
        _options = options ?? CentroidClassifierOptions.Default;

        if (!_options.IsValid)
            throw new ArgumentException("Temperature must be a finite number greater than 0.", nameof(options));

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

    /// <inheritdoc />
    public IReadOnlyList<ClassificationResult> Predict(
        string text,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null) =>
        PredictAsync(text, limit, excludedCategories).GetAwaiter().GetResult();

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

        ReadOnlyMemory<float> vector = await _embeddings
            .GetTextEmbeddingAsync(text, _options.PredictionUse, cancellationToken)
            .ConfigureAwait(false);

        return _inner.Predict(Validate(vector, documentId: null), limit, excludedCategories);
    }

    /// <inheritdoc />
    public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null)
    {
        var results = Predict(text, limit: 1, excludedCategories);
        return results.Count > 0 ? results[0].Category : null;
    }

    private ReadOnlyMemory<float> Validate(ReadOnlyMemory<float> vector, string? documentId)
    {
        if (vector.Length != _embeddings.Dimension)
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

    private readonly record struct LabelledVector(ReadOnlyMemory<float> Vector, string? Category);
}
