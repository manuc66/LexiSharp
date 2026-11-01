using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Classification;

/// <summary>One feature and the weight the linear model gives it for a category.</summary>
/// <param name="Feature">The feature name.</param>
/// <param name="Weight">The coefficient; positive pushes toward the category, negative away.</param>
public sealed record LinearFeatureWeight(string Feature, double Weight);

/// <summary>
/// A multinomial logistic regression over the same <see cref="IFeatureSource{TItem}"/> seam as the
/// question trees, trained with AdaGrad and a deterministic shuffle.
/// </summary>
/// <remarks>
/// <para>
/// This is the accuracy companion the trees cannot be: a single tree plateaus well below a linear
/// model on sparse text, and a forest narrows the gap without closing it. Logistic regression sees
/// every feature at once, so a term that is weakly informative on its own still contributes, and
/// the softmax gives a distribution that is at least a proper model of the labels. It is still
/// dependency-free, deterministic for a seed, and its coefficients are readable through
/// <see cref="TopFeatures(string, int)"/>.
/// </para>
/// <para>
/// The price is the path: there is no <c>Explain</c> chain of questions. Use
/// <see cref="DecisionTreeClassifier{TItem}"/> when the questions are the point and this when the
/// label is. The model is batch-trained; <see cref="Train"/> replaces the weights and the
/// vocabulary. <see cref="Learn(TItem)"/> folds one labelled item into the correction ledger and
/// returns a token that <see cref="Unlearn(long)"/> removes exactly, without retraining; the
/// coefficients are not rewound.
/// </para>
/// </remarks>
/// <typeparam name="TItem">The item type being classified.</typeparam>
public sealed class LinearClassifier<TItem>
{
    private const double Epsilon = 1e-8;

    private readonly IFeatureSource<TItem> _features;
    private readonly Func<TItem, string?> _categoryOf;
    private readonly Func<TItem, double> _weightOf;
    private readonly LinearClassifierOptions _options;

    private IReadOnlyList<string> _categories = Array.Empty<string>();
    private Dictionary<string, int> _categoryIndex = new(StringComparer.Ordinal);
    private Dictionary<string, int> _featureIds = new(StringComparer.Ordinal);
    private string[] _featuresById = Array.Empty<string>();
    private double[] _weights = Array.Empty<double>();
    private double[] _bias = Array.Empty<double>();
    private double[] _idf = Array.Empty<double>();
    private double[] _numericMeans = Array.Empty<double>();
    private double[] _numericStandardDeviations = Array.Empty<double>();
    private readonly Dictionary<string, Dictionary<string, double>> _reinforcement =
        new(StringComparer.Ordinal);
    private readonly Dictionary<long, LearnedItem> _learned = new();
    private readonly SortedSet<string> _virtualCategories = new(StringComparer.Ordinal);
    private long _nextToken;
    private bool _trained;

    /// <param name="features">The feature space the model reads.</param>
    /// <param name="categoryOf">Returns an item's label, or null/empty to skip it during training.</param>
    /// <param name="weightOf">Returns an item's non-negative sample weight; null means <c>1</c> for every item.</param>
    /// <param name="options">Tunable knobs; when null, <see cref="LinearClassifierOptions.Default"/> applies.</param>
    public LinearClassifier(
        IFeatureSource<TItem> features,
        Func<TItem, string?> categoryOf,
        Func<TItem, double>? weightOf = null,
        LinearClassifierOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(categoryOf);

        _features = features;
        _categoryOf = categoryOf;
        _weightOf = weightOf ?? (_ => 1.0);
        _options = options ?? LinearClassifierOptions.Default;

        if (!_options.IsValid)
            throw new ArgumentException("The linear-classifier options are not valid.", nameof(options));
    }

    /// <summary>The categories seen during training, sorted ordinal.</summary>
    public IReadOnlyList<string> Categories => _categories;

    /// <summary>Trains the model on labelled items; items with a null or empty label are ignored.</summary>
    /// <param name="items">The labelled corpus.</param>
    /// <exception cref="ArgumentException">A sample or class weight is invalid.</exception>
    public void Train(IEnumerable<TItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        _reinforcement.Clear();
        _learned.Clear();
        _virtualCategories.Clear();
        ValidateClassWeights();

        var (pending, categories, featuresById, documentFrequency) = Materialize(items);
        _categories = categories;
        _categoryIndex = new Dictionary<string, int>(_categories.Count, StringComparer.Ordinal);

        for (int i = 0; i < _categories.Count; i++)
            _categoryIndex[_categories[i]] = i;

        SelectVocabulary(featuresById, documentFrequency, pending.Count);
        _numericMeans = new double[_featuresById.Length];
        _numericStandardDeviations = new double[_featuresById.Length];
        _trained = false;

        if (pending.Count == 0 || _categories.Count == 0)
        {
            _weights = Array.Empty<double>();
            _bias = Array.Empty<double>();
            return;
        }

        if (_options.StandardizeNumericFeatures)
            Standardize(pending);

        List<SparseItem> sparse = BuildSparse(pending);
        TrainEpochs(sparse);
        _trained = true;
    }

    private void ValidateClassWeights()
    {
        if (_options.ClassWeights is not { } classWeights)
            return;

        foreach (var pair in classWeights)
        {
            if (!double.IsFinite(pair.Value) || pair.Value < 0)
                throw new ArgumentException("Class weights must be finite and non-negative.");
        }
    }

    private (
        List<PendingItem> Pending,
        string[] Categories,
        List<string> FeaturesById,
        List<int> DocumentFrequency) Materialize(IEnumerable<TItem> items)
    {
        var pending = new List<PendingItem>();
        var categories = new SortedSet<string>(StringComparer.Ordinal);
        var featureIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var featuresById = new List<string>();
        var documentFrequency = new List<int>();

        foreach (var item in items)
        {
            string? category = _categoryOf(item);

            if (string.IsNullOrEmpty(category))
                continue;

            double weight = _weightOf(item);

            if (!double.IsFinite(weight) || weight < 0)
                throw new ArgumentException("Sample weights must be finite and non-negative.", nameof(items));

            double classWeight = _options.ClassWeights is { } weights
                && weights.TryGetValue(category, out double configured)
                    ? configured
                    : 1.0;
            double effectiveWeight = weight * classWeight;

            if (!double.IsFinite(effectiveWeight))
                throw new ArgumentException("The effective sample weight must be finite.");

            if (effectiveWeight == 0)
                continue;

            categories.Add(category);
            IFeatureSet features = _features.FeaturesOf(item);
            pending.Add(new PendingItem(category, features, effectiveWeight));

            foreach (string feature in features.Features)
            {
                if (!featureIds.TryGetValue(feature, out int id))
                {
                    id = featuresById.Count;
                    featureIds[feature] = id;
                    featuresById.Add(feature);
                    documentFrequency.Add(0);
                }

                documentFrequency[id]++;
            }
        }

        return (pending, categories.ToArray(), featuresById, documentFrequency);
    }

    private void SelectVocabulary(
        List<string> featuresById,
        List<int> documentFrequency,
        int documentCount)
    {
        _featureIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var kept = new List<string>();
        var keptDocumentFrequency = new List<int>();

        for (int id = 0; id < featuresById.Count; id++)
        {
            if (documentFrequency[id] < _options.MinDocumentFrequency)
                continue;

            _featureIds[featuresById[id]] = kept.Count;
            kept.Add(featuresById[id]);
            keptDocumentFrequency.Add(documentFrequency[id]);
        }

        _featuresById = kept.ToArray();
        _idf = new double[kept.Count];

        for (int id = 0; id < kept.Count; id++)
        {
            _idf[id] = _options.IdfWeighting
                ? Math.Log(1 + ((double)documentCount / keptDocumentFrequency[id]))
                : 1.0;
        }
    }

    private void Standardize(List<PendingItem> pending)
    {
        var sums = new double[_featuresById.Length];
        var sumSquares = new double[_featuresById.Length];
        var counts = new int[_featuresById.Length];

        foreach (var item in pending)
        {
            foreach (string feature in item.Features.Features)
            {
                if (!_featureIds.TryGetValue(feature, out int id))
                    continue;

                if (item.Features.Kind(feature) != FeatureKind.Numeric)
                    continue;

                if (item.Features.NumericValue(feature) is not { } value || double.IsNaN(value))
                    continue;

                sums[id] += value;
                sumSquares[id] += value * value;
                counts[id]++;
            }
        }

        for (int id = 0; id < _featuresById.Length; id++)
        {
            if (counts[id] == 0)
                continue;

            // Every item is in the denominator, not only the items that carried a value: an
            // absent numeric feature is a zero in the vector, and a z-score that ignores those
            // zeros is not centered on the data the model actually sees.
            double mean = sums[id] / pending.Count;
            double variance = (sumSquares[id] / pending.Count) - (mean * mean);
            _numericMeans[id] = mean;
            _numericStandardDeviations[id] = variance > 0 ? Math.Sqrt(variance) : 0;
        }
    }

    private List<SparseItem> BuildSparse(List<PendingItem> pending)
    {
        var sparse = new List<SparseItem>(pending.Count);

        foreach (var item in pending)
        {
            var ids = new List<int>();
            var values = new List<double>();

            foreach (string feature in item.Features.Features)
            {
                if (!_featureIds.TryGetValue(feature, out int id))
                    continue;

                ids.Add(id);
                values.Add(ValueOf(id, feature, item.Features));
            }

            sparse.Add(new SparseItem(
                _categoryIndex[item.Category],
                ids.ToArray(),
                values.ToArray(),
                item.Weight));
        }

        return sparse;
    }

    private void TrainEpochs(List<SparseItem> sparse)
    {
        int classCount = _categories.Count;
        _weights = new double[_featuresById.Length * classCount];
        _bias = new double[classCount];

        var weightAccumulator = new double[_weights.Length];
        var biasAccumulator = new double[classCount];
        var logits = new double[classCount];
        var probabilities = new double[classCount];
        var errors = new double[classCount];
        var order = new int[sparse.Count];

        for (int i = 0; i < order.Length; i++)
            order[i] = i;

        var random = new Random(_options.Seed); // NOSONAR:S2245 (fixed seed: the model is reproducible)

        for (int epoch = 0; epoch < _options.Epochs; epoch++)
        {
            Shuffle(order, random);

            foreach (int index in order)
            {
                UpdateItem(
                    sparse[index],
                    logits,
                    probabilities,
                    errors,
                    biasAccumulator,
                    weightAccumulator);
            }
        }
    }

    private static void Shuffle(int[] order, Random random)
    {
        for (int i = order.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
    }

    private void UpdateItem(
        SparseItem item,
        double[] logits,
        double[] probabilities,
        double[] errors,
        double[] biasAccumulator,
        double[] weightAccumulator)
    {
        int classCount = _categories.Count;
        Array.Copy(_bias, logits, classCount);

        for (int k = 0; k < item.FeatureIds.Length; k++)
        {
            int offset = item.FeatureIds[k] * classCount;
            double value = item.Values[k];

            for (int c = 0; c < classCount; c++)
                logits[c] += _weights[offset + c] * value;
        }

        Softmax(logits, probabilities);

        for (int c = 0; c < classCount; c++)
        {
            double error = (probabilities[c] - (c == item.CategoryIndex ? 1 : 0)) * item.Weight;
            errors[c] = error;

            biasAccumulator[c] += error * error;
            _bias[c] -= _options.LearningRate * error / Math.Sqrt(biasAccumulator[c] + Epsilon);
        }

        for (int k = 0; k < item.FeatureIds.Length; k++)
        {
            int offset = item.FeatureIds[k] * classCount;
            double value = item.Values[k];

            for (int c = 0; c < classCount; c++)
            {
                int parameter = offset + c;
                double gradient = (errors[c] * value) + (_options.L2 * _weights[parameter]);
                weightAccumulator[parameter] += gradient * gradient;
                _weights[parameter] -=
                    _options.LearningRate * gradient / Math.Sqrt(weightAccumulator[parameter] + Epsilon);
            }
        }
    }

    /// <summary>Predicts the most likely categories for an item, by decreasing probability.</summary>
    /// <param name="item">The item to classify.</param>
    /// <param name="limit">Maximum number of categories to return.</param>
    /// <param name="excludedCategories">Optional categories to hide without retraining; probabilities renormalize over the rest.</param>
    /// <returns>The distribution; empty for an untrained model or a non-positive limit.</returns>
    public IReadOnlyList<ClassificationResult> Predict(
        TItem item,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null)
    {
        if (limit <= 0 || !_trained)
            return Array.Empty<ClassificationResult>();

        IFeatureSet features = _features.FeaturesOf(item);
        (double[] logits, string[] categories) = ComputeLogits(features);

        return Rank(logits, categories, excludedCategories, limit);
    }

    private (double[] Logits, string[] Categories) ComputeLogits(IFeatureSet features)
    {
        string[] categories = EffectiveCategories();
        int trainedCount = _categories.Count;
        var logits = new double[categories.Length];

        Array.Copy(_bias, logits, trainedCount);

        for (int c = trainedCount; c < logits.Length; c++)
            logits[c] = _options.UnseenCategoryBias;

        foreach (string feature in features.Features)
        {
            if (!_featureIds.TryGetValue(feature, out int id))
                continue;

            int offset = id * trainedCount;
            double value = ValueOf(id, feature, features);

            for (int c = 0; c < trainedCount; c++)
                logits[c] += _weights[offset + c] * value;
        }

        if (_reinforcement.Count > 0)
            ApplyReinforcement(features, logits, categories);

        return (logits, categories);
    }

    private void ApplyReinforcement(IFeatureSet features, double[] logits, string[] categories)
    {
        var index = new Dictionary<string, int>(categories.Length, StringComparer.Ordinal);

        for (int c = 0; c < categories.Length; c++)
            index[categories[c]] = c;

        foreach (var (category, deltas) in _reinforcement)
        {
            if (!index.TryGetValue(category, out int c))
                continue;

            foreach (string feature in features.Features)
            {
                if (!_featureIds.ContainsKey(feature) || !deltas.TryGetValue(feature, out double delta))
                    continue;

                double value = ValueOf(_featureIds[feature], feature, features);

                if (value != 0)
                    logits[c] += delta * value;
            }
        }
    }

    private string[] EffectiveCategories()
    {
        if (_virtualCategories.Count == 0)
            return _categories.ToArray();

        var categories = new string[_categories.Count + _virtualCategories.Count];

        for (int i = 0; i < _categories.Count; i++)
            categories[i] = _categories[i];

        _virtualCategories.CopyTo(categories, _categories.Count);
        return categories;
    }

    private IReadOnlyList<ClassificationResult> Rank(
        double[] logits,
        string[] categories,
        IReadOnlySet<string>? excludedCategories,
        int limit)
    {
        int classCount = categories.Length;
        var probabilities = new double[classCount];
        Softmax(logits, probabilities);

        var ranked = new List<ClassificationResult>(classCount);
        double total = 0;

        for (int c = 0; c < classCount; c++)
        {
            string category = categories[c];

            if (excludedCategories?.Contains(category) == true)
                continue;

            ranked.Add(new ClassificationResult(category, probabilities[c]));
            total += probabilities[c];
        }

        if (ranked.Count == 0 || total <= 0)
            return Array.Empty<ClassificationResult>();

        ranked.Sort(static (left, right) =>
        {
            int byProbability = right.Probability.CompareTo(left.Probability);

            return byProbability != 0 ? byProbability : string.CompareOrdinal(left.Category, right.Category);
        });

        for (int i = 0; i < ranked.Count; i++)
            ranked[i] = ranked[i] with { Probability = ranked[i].Probability / total };

        if (ranked[0].Probability < _options.MinProbability)
            return Array.Empty<ClassificationResult>();

        if (ranked.Count > limit)
            ranked.RemoveRange(limit, ranked.Count - limit);

        return ranked;
    }

    /// <summary>Returns the single most likely category, or null when nothing was trained.</summary>
    /// <param name="item">The item to classify.</param>
    /// <param name="excludedCategories">Optional categories to hide without retraining.</param>
    /// <returns>The winning category, or null.</returns>
    public string? PredictBest(TItem item, IReadOnlySet<string>? excludedCategories = null)
    {
        var results = Predict(item, limit: 1, excludedCategories);

        return results.Count > 0 ? results[0].Category : null;
    }

    /// <summary>Records a signed correction on an item's features for a category, without retraining.</summary>
    /// <param name="item">The item whose features are corrected.</param>
    /// <param name="category">A category seen during training.</param>
    /// <param name="weight">The positive mass to add. Default: <c>1</c>.</param>
    public void Reinforce(TItem item, string category, double weight = 1.0) =>
        AdjustReinforcement(item, category, weight, sign: 1.0);

    /// <summary>Removes a correction previously added with <see cref="Reinforce"/>.</summary>
    /// <param name="item">The item whose features are corrected.</param>
    /// <param name="category">The corrected category.</param>
    /// <param name="weight">The positive mass to remove. Default: <c>1</c>.</param>
    public void Unreinforce(TItem item, string category, double weight = 1.0) =>
        AdjustReinforcement(item, category, weight, sign: -1.0);

    /// <summary>Clears every correction; the model returns to its training weights.</summary>
    public void ForgetReinforcement() => _reinforcement.Clear();

    /// <summary>
    /// Folds one labelled item into the model's correction ledger and returns a token for
    /// <see cref="Unlearn(long)"/>. The coefficients are not retrained: the item adds signed mass
    /// on its known features for its category.
    /// </summary>
    /// <param name="item">The labelled item; its category must have been seen during training.</param>
    /// <returns>A token that removes exactly this contribution.</returns>
    /// <exception cref="InvalidOperationException">The model is untrained.</exception>
    /// <exception cref="ArgumentException">The item has no category, an unseen category, or a non-positive weight.</exception>
    public long Learn(TItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!_trained)
            throw new InvalidOperationException("The model is untrained.");

        string? category = _categoryOf(item);

        if (string.IsNullOrEmpty(category))
            throw new ArgumentException("The item has no category.", nameof(item));

        double weight = _weightOf(item) * _options.CorrectionWeight;

        if (!double.IsFinite(weight) || weight <= 0)
            throw new ArgumentException("The effective correction weight must be finite and positive.", nameof(item));

        if (!_categoryIndex.ContainsKey(category))
            _virtualCategories.Add(category);

        IFeatureSet features = _features.FeaturesOf(item);
        var known = features.Features.Where(_featureIds.ContainsKey).ToArray();

        AdjustFeatures(known, category, weight, sign: 1.0);

        long token = _nextToken++;
        _learned[token] = new LearnedItem(known, category, weight);

        return token;
    }

    /// <summary>Removes the exact contribution a previous <see cref="Learn(TItem)"/> added.</summary>
    /// <param name="token">The token returned by <see cref="Learn(TItem)"/>.</param>
    /// <returns><c>true</c> when the contribution existed and was removed; <c>false</c> otherwise.</returns>
    public bool Unlearn(long token)
    {
        if (!_learned.TryGetValue(token, out LearnedItem? learned))
            return false;

        _learned.Remove(token);
        AdjustFeatures(learned.Features, learned.Category, learned.Weight, sign: -1.0);

        if (_virtualCategories.Contains(learned.Category)
            && !_reinforcement.ContainsKey(learned.Category)
            && !_learned.Values.Any(item => string.Equals(item.Category, learned.Category, StringComparison.Ordinal)))
        {
            _virtualCategories.Remove(learned.Category);
        }

        return true;
    }

    private void AdjustReinforcement(TItem item, string category, double weight, double sign)
    {
        ArgumentNullException.ThrowIfNull(category);

        if (!_trained)
            throw new InvalidOperationException("The model is untrained.");

        if (!double.IsFinite(weight) || weight <= 0)
            throw new ArgumentException("The reinforcement weight must be finite and positive.", nameof(weight));

        if (!_categoryIndex.ContainsKey(category))
            throw new ArgumentException("The category was not seen during training.", nameof(category));

        IFeatureSet features = _features.FeaturesOf(item);
        AdjustFeatures(features.Features, category, weight, sign);
    }

    private void AdjustFeatures(
        IReadOnlyCollection<string> features,
        string category,
        double weight,
        double sign)
    {
        if (!_reinforcement.TryGetValue(category, out Dictionary<string, double>? deltas))
        {
            deltas = new Dictionary<string, double>(StringComparer.Ordinal);
            _reinforcement[category] = deltas;
        }

        foreach (string feature in features)
        {
            if (!_featureIds.ContainsKey(feature))
                continue;

            double updated = deltas.GetValueOrDefault(feature) + (sign * weight);

            if (Math.Abs(updated) <= 1e-12)
                deltas.Remove(feature);
            else
                deltas[feature] = updated;
        }

        if (deltas.Count == 0)
            _reinforcement.Remove(category);
    }

    /// <summary>
    /// Returns the features with the largest training coefficient for a category. The reinforcement
    /// ledger is not included: this is what the corpus taught the model, not what feedback added.
    /// </summary>
    /// <param name="category">A category seen during training.</param>
    /// <param name="count">Maximum number of features to return.</param>
    /// <returns>The features, by decreasing coefficient then ordinal name.</returns>
    public IReadOnlyList<LinearFeatureWeight> TopFeatures(string category, int count = 10)
    {
        ArgumentNullException.ThrowIfNull(category);

        if (!_trained)
            return Array.Empty<LinearFeatureWeight>();

        if (!_categoryIndex.TryGetValue(category, out int categoryIndex))
        {
            // A category introduced by Learn has correction mass but no trained coefficient;
            // returning an empty list is more honest than inventing a training explanation.
            if (_virtualCategories.Contains(category))
                return Array.Empty<LinearFeatureWeight>();

            throw new ArgumentException("The category was not seen during training.", nameof(category));
        }

        int classCount = _categories.Count;
        var ranked = new List<LinearFeatureWeight>(_featuresById.Length);

        for (int featureId = 0; featureId < _featuresById.Length; featureId++)
        {
            double weight = _weights[(featureId * classCount) + categoryIndex];

            if (weight != 0)
                ranked.Add(new LinearFeatureWeight(_featuresById[featureId], weight));
        }

        ranked.Sort(static (left, right) =>
        {
            int byWeight = right.Weight.CompareTo(left.Weight);

            return byWeight != 0 ? byWeight : string.CompareOrdinal(left.Feature, right.Feature);
        });

        if (count < 0)
            count = 0;

        if (ranked.Count > count)
            ranked.RemoveRange(count, ranked.Count - count);

        return ranked;
    }

    private double ValueOf(int featureId, string feature, IFeatureSet features)
    {
        double? numeric = features.NumericValue(feature);
        FeatureKind kind = features.Kind(feature);

        if (kind == FeatureKind.Numeric)
        {
            if (numeric is not { } value || double.IsNaN(value))
                return 0;

            if (!_options.StandardizeNumericFeatures)
                return value;

            double deviation = _numericStandardDeviations[featureId];

            return deviation > 0 ? (value - _numericMeans[featureId]) / deviation : 0;
        }

        double idf = _options.IdfWeighting ? _idf[featureId] : 1.0;

        if (kind == FeatureKind.Count)
        {
            double count = numeric is { } countValue && !double.IsNaN(countValue)
                ? Math.Max(countValue, 1)
                : 1;

            return (1 + Math.Log(count)) * idf;
        }

        return idf;
    }

    private static void Softmax(double[] logits, double[] probabilities)
    {
        double max = logits[0];

        for (int i = 1; i < logits.Length; i++)
            max = Math.Max(max, logits[i]);

        double sum = 0;

        for (int i = 0; i < logits.Length; i++)
        {
            double value = Math.Exp(logits[i] - max);
            probabilities[i] = value;
            sum += value;
        }

        for (int i = 0; i < probabilities.Length; i++)
            probabilities[i] /= sum;
    }

    internal string FeatureSpaceHash => ModelFormat.Hash(_featuresById);

    internal static (bool IdfWeighting, bool StandardizeNumericFeatures, double MinProbability,
        double CorrectionWeight, double UnseenCategoryBias)
        ReadPredictionOptions(BinaryReader reader) =>
        (reader.ReadBoolean(), reader.ReadBoolean(), reader.ReadDouble(), reader.ReadDouble(), reader.ReadDouble());

    internal void WriteState(BinaryWriter writer)
    {
        writer.Write(_options.IdfWeighting);
        writer.Write(_options.StandardizeNumericFeatures);
        writer.Write(_options.MinProbability);
        writer.Write(_options.CorrectionWeight);
        writer.Write(_options.UnseenCategoryBias);
        ModelFormat.WriteStrings(writer, _categories);
        ModelFormat.WriteStrings(writer, _virtualCategories.ToArray());
        ModelFormat.WriteStrings(writer, _featuresById);
        ModelFormat.WriteDoubles(writer, _weights);
        ModelFormat.WriteDoubles(writer, _bias);
        ModelFormat.WriteDoubles(writer, _idf);
        ModelFormat.WriteDoubles(writer, _numericMeans);
        ModelFormat.WriteDoubles(writer, _numericStandardDeviations);
        writer.Write(_trained);
        writer.Write(_reinforcement.Count);

        foreach (var pair in _reinforcement.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            ModelFormat.WriteString(writer, pair.Key);
            writer.Write(pair.Value.Count);

            foreach (var weight in pair.Value.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                ModelFormat.WriteString(writer, weight.Key);
                writer.Write(weight.Value);
            }
        }

        writer.Write(_nextToken);
        writer.Write(_learned.Count);

        foreach (var pair in _learned.OrderBy(pair => pair.Key))
        {
            writer.Write(pair.Key);
            ModelFormat.WriteStrings(writer, pair.Value.Features);
            ModelFormat.WriteString(writer, pair.Value.Category);
            writer.Write(pair.Value.Weight);
        }
    }

    internal void ReadBody(BinaryReader reader)
    {
        _categories = ModelFormat.ReadStrings(reader);
        _categoryIndex = new Dictionary<string, int>(_categories.Count, StringComparer.Ordinal);

        for (int i = 0; i < _categories.Count; i++)
            _categoryIndex[_categories[i]] = i;

        _virtualCategories.Clear();
        string[] virtualCategories = ModelFormat.ReadStrings(reader);

        foreach (string category in virtualCategories)
        {
            if (string.IsNullOrEmpty(category) || _categoryIndex.ContainsKey(category))
                throw new InvalidDataException("A virtual category is empty or duplicated in the trained set.");

            _virtualCategories.Add(category);
        }

        _featuresById = ModelFormat.ReadStrings(reader);
        _featureIds = new Dictionary<string, int>(_featuresById.Length, StringComparer.Ordinal);

        for (int i = 0; i < _featuresById.Length; i++)
            _featureIds[_featuresById[i]] = i;

        _weights = ModelFormat.ReadDoubles(reader);
        _bias = ModelFormat.ReadDoubles(reader);
        _idf = ModelFormat.ReadDoubles(reader);
        _numericMeans = ModelFormat.ReadDoubles(reader);
        _numericStandardDeviations = ModelFormat.ReadDoubles(reader);
        _trained = reader.ReadBoolean();
        _reinforcement.Clear();

        int reinforcedFeatures = ModelFormat.ReadCount(reader);

        for (int i = 0; i < reinforcedFeatures; i++)
        {
            string feature = ModelFormat.ReadString(reader)
                ?? throw new InvalidDataException("A reinforcement entry has no feature.");
            int categories = ModelFormat.ReadCount(reader);
            var weights = new Dictionary<string, double>(categories, StringComparer.Ordinal);

            for (int c = 0; c < categories; c++)
            {
                string category = ModelFormat.ReadString(reader)
                    ?? throw new InvalidDataException("A reinforcement entry has no category.");
                weights[category] = reader.ReadDouble();
            }

            _reinforcement[feature] = weights;
        }

        _nextToken = reader.ReadInt64();
        int learnedCount = ModelFormat.ReadCount(reader);

        for (int i = 0; i < learnedCount; i++)
        {
            long token = reader.ReadInt64();
            string[] features = ModelFormat.ReadStrings(reader);
            string category = ModelFormat.ReadString(reader)
                ?? throw new InvalidDataException("A learned entry has no category.");
            double weight = reader.ReadDouble();

            _learned[token] = new LearnedItem(features, category, weight);
            _nextToken = Math.Max(_nextToken, token + 1);
        }

        int expectedWeights = _featuresById.Length * _categories.Count;

        if (_weights.Length != expectedWeights
            || _bias.Length != _categories.Count
            || _idf.Length != _featuresById.Length
            || _numericMeans.Length != _featuresById.Length
            || _numericStandardDeviations.Length != _featuresById.Length)
        {
            throw new InvalidDataException("The linear model's arrays do not match its vocabulary.");
        }
    }

    private sealed record PendingItem(string Category, IFeatureSet Features, double Weight);

    private sealed record LearnedItem(string[] Features, string Category, double Weight);

    private sealed record SparseItem(
        int CategoryIndex,
        int[] FeatureIds,
        double[] Values,
        double Weight);
}

/// <summary>
/// The text convenience over <see cref="LinearClassifier{TItem}"/>: a
/// <see cref="SearchDocument"/> model over <see cref="SearchDocument.Text"/>.
/// </summary>
public sealed class LinearClassifier : ITextClassifier
{
    private readonly LinearClassifier<SearchDocument> _inner;

    /// <summary>Creates a text model over <see cref="SearchDocument.Text"/>.</summary>
    /// <param name="tokenizer">Tokenizer used both at training and prediction time (default: <see cref="Tokenizer.Default"/>).</param>
    /// <param name="options">Tunable knobs; when null, <see cref="LinearClassifierOptions.Default"/> applies.</param>
    public LinearClassifier(ITokenizer? tokenizer = null, LinearClassifierOptions? options = null)
        : this(new TextFeatureSource(tokenizer), options)
    {
    }

    /// <summary>Creates a text model over a caller-supplied document feature space.</summary>
    /// <param name="features">The feature space to read from a document.</param>
    /// <param name="options">Tunable knobs; when null, <see cref="LinearClassifierOptions.Default"/> applies.</param>
    public LinearClassifier(IFeatureSource<SearchDocument> features, LinearClassifierOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(features);

        _inner = new LinearClassifier<SearchDocument>(
            features,
            document => document.Category,
            weightOf: null,
            options);
    }

    /// <summary>The categories seen during training, sorted ordinal.</summary>
    public IReadOnlyList<string> Categories => _inner.Categories;

    /// <inheritdoc />
    public void Train(IEnumerable<SearchDocument> documents) => _inner.Train(documents);

    /// <summary>Folds one labelled document into the correction ledger, without retraining.</summary>
    /// <param name="document">The labelled document.</param>
    /// <returns>A token that removes exactly this contribution.</returns>
    public long Learn(SearchDocument document) => _inner.Learn(document);

    /// <summary>Folds one labelled text into the correction ledger, without retraining.</summary>
    /// <param name="text">The raw text.</param>
    /// <param name="category">A category seen during training.</param>
    /// <returns>A token that removes exactly this contribution.</returns>
    public long Learn(string text, string category) =>
        _inner.Learn(new SearchDocument(string.Empty, text, Category: category));

    /// <summary>Removes the exact contribution a previous <see cref="Learn(SearchDocument)"/> added.</summary>
    /// <param name="token">The token returned by <see cref="Learn(SearchDocument)"/>.</param>
    /// <returns><c>true</c> when the contribution existed and was removed.</returns>
    public bool Unlearn(long token) => _inner.Unlearn(token);

    /// <inheritdoc />
    public IReadOnlyList<ClassificationResult> Predict(
        string text,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        return _inner.Predict(new SearchDocument(string.Empty, text), limit, excludedCategories);
    }

    /// <inheritdoc />
    public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        return _inner.PredictBest(new SearchDocument(string.Empty, text), excludedCategories);
    }

    /// <summary>Records a correction on a text's features for a category, without retraining.</summary>
    /// <param name="text">Raw text; tokenized internally with this model's tokenizer.</param>
    /// <param name="category">A category seen during training.</param>
    /// <param name="weight">The positive mass to add. Default: <c>1</c>.</param>
    public void Reinforce(string text, string category, double weight = 1.0)
    {
        ArgumentNullException.ThrowIfNull(text);

        _inner.Reinforce(new SearchDocument(string.Empty, text), category, weight);
    }

    /// <summary>Removes a correction previously added with <see cref="Reinforce"/>.</summary>
    /// <param name="text">Raw text; tokenized internally with this model's tokenizer.</param>
    /// <param name="category">The corrected category.</param>
    /// <param name="weight">The positive mass to remove. Default: <c>1</c>.</param>
    public void Unreinforce(string text, string category, double weight = 1.0)
    {
        ArgumentNullException.ThrowIfNull(text);

        _inner.Unreinforce(new SearchDocument(string.Empty, text), category, weight);
    }

    /// <summary>Clears every correction; the model returns to its training weights.</summary>
    public void ForgetReinforcement() => _inner.ForgetReinforcement();

    /// <summary>Returns the features with the largest coefficient for a category.</summary>
    /// <param name="category">A category seen during training.</param>
    /// <param name="count">Maximum number of features to return.</param>
    /// <returns>The features, by decreasing coefficient then ordinal name.</returns>
    public IReadOnlyList<LinearFeatureWeight> TopFeatures(string category, int count = 10) =>
        _inner.TopFeatures(category, count);

    internal string FeatureSpaceHash => _inner.FeatureSpaceHash;

    internal void WriteState(BinaryWriter writer) => _inner.WriteState(writer);

    internal static LinearClassifier ReadState(
        BinaryReader reader,
        IFeatureSource<SearchDocument>? features)
    {
        var (idfWeighting, standardizeNumeric, minProbability, correctionWeight, unseenCategoryBias) =
            LinearClassifier<SearchDocument>.ReadPredictionOptions(reader);
        var model = new LinearClassifier(
            features ?? new TextFeatureSource(),
            new LinearClassifierOptions
            {
                IdfWeighting = idfWeighting,
                StandardizeNumericFeatures = standardizeNumeric,
                MinProbability = minProbability,
                CorrectionWeight = correctionWeight,
                UnseenCategoryBias = unseenCategoryBias,
            });

        model._inner.ReadBody(reader);
        return model;
    }
}
