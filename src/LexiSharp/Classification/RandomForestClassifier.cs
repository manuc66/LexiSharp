using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Classification;

/// <summary>
/// A bagged forest of <see cref="DecisionTreeClassifier{TItem}"/> trees: each tree sees a bootstrap
/// sample and a random subset of the features, and the forest averages their distributions.
/// </summary>
/// <remarks>
/// <para>
/// This is the accuracy companion to the single tree, not a replacement for it. A greedy tree on
/// sparse binary text plateaus below a linear model — measured on 20 Newsgroups, a single tree
/// reaches roughly 0.34 accuracy against Naive Bayes's 0.58 — because one axis-aligned path cannot
/// use the whole vocabulary. A forest decorrelates many such paths and votes, which is the
/// standard remedy; it is still made of the same explainable questions, but the ensemble's answer
/// is no longer a single path, so this type deliberately has no <c>Explain</c>.
/// </para>
/// <para>
/// Each tree is trained on a Poisson/multinomial bootstrap of the items and on a random subset of
/// the vocabulary, with a seed derived from <see cref="RandomForestOptions.Seed"/> and the tree
/// index. Training is therefore reproducible, and adding a tree does not change the trees before
/// it. Weights are multiplied into the bootstrap counts, so a caller's recency weight still applies.
/// </para>
/// <para>
/// Prediction averages the per-tree distributions over the forest's categories and renormalizes.
/// A category a particular tree never saw contributes zero from that tree, not a missing mass.
/// </para>
/// </remarks>
/// <typeparam name="TItem">The item type being classified.</typeparam>
public sealed class RandomForestClassifier<TItem>
{
    private readonly IFeatureSource<TItem> _features;
    private readonly Func<TItem, string?> _categoryOf;
    private readonly Func<TItem, double> _weightOf;
    private readonly RandomForestOptions _options;
    private readonly List<DecisionTreeClassifier<Item>> _trees = new();

    private IReadOnlyList<string> _categories = Array.Empty<string>();

    /// <param name="features">The feature space the forest reads.</param>
    /// <param name="categoryOf">Returns an item's label, or null/empty to skip it during training.</param>
    /// <param name="weightOf">Returns an item's non-negative sample weight; null means <c>1</c> for every item.</param>
    /// <param name="options">Tunable knobs; when null, <see cref="RandomForestOptions.Default"/> applies.</param>
    public RandomForestClassifier(
        IFeatureSource<TItem> features,
        Func<TItem, string?> categoryOf,
        Func<TItem, double>? weightOf = null,
        RandomForestOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(categoryOf);

        _features = features;
        _categoryOf = categoryOf;
        _weightOf = weightOf ?? (_ => 1.0);
        _options = options ?? RandomForestOptions.Default;

        if (!_options.IsValid)
            throw new ArgumentException("The random-forest options are not valid.", nameof(options));
    }

    /// <summary>How many trees the last training built.</summary>
    public int TreeCount => _trees.Count;

    /// <summary>The categories seen during training, sorted ordinal.</summary>
    public IReadOnlyList<string> Categories => _categories;

    /// <summary>Trains the forest on labelled items; items with a null or empty label are ignored.</summary>
    /// <param name="items">The labelled corpus.</param>
    /// <exception cref="ArgumentException">A sample weight is negative or not finite.</exception>
    public void Train(IEnumerable<TItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var materialized = new List<Item>();
        var categories = new SortedSet<string>(StringComparer.Ordinal);
        var vocabulary = new List<string>();
        var vocabularySeen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var value in items)
        {
            string? category = _categoryOf(value);

            if (string.IsNullOrEmpty(category))
                continue;

            double weight = _weightOf(value);

            if (!double.IsFinite(weight) || weight < 0)
                throw new ArgumentException("Sample weights must be finite and non-negative.", nameof(items));

            if (weight == 0)
                continue;

            categories.Add(category);
            IFeatureSet features = _features.FeaturesOf(value);
            materialized.Add(new Item(category, features, weight));

            foreach (string feature in features.Features)
            {
                if (vocabularySeen.Add(feature))
                    vocabulary.Add(feature);
            }
        }

        _categories = categories.ToArray();
        _trees.Clear();

        if (materialized.Count == 0)
            return;

        // A corpus with no features is not a corpus with no labels: each tree becomes a leaf that
        // reports the prior, which is what the single tree and the linear model already do.
        int subsetSize = vocabulary.Count == 0
            ? 0
            : Math.Clamp(
                (int)Math.Ceiling(vocabulary.Count * _options.FeatureSubsetFraction),
                1,
                vocabulary.Count);
        Item[] itemArray = materialized.ToArray();
        var weights = new double[itemArray.Length];
        var indices = new int[vocabulary.Count];

        for (int i = 0; i < indices.Length; i++)
            indices[i] = i;

        for (int treeIndex = 0; treeIndex < _options.Trees; treeIndex++)
        {
            var random = new Random(_options.Seed + treeIndex); // NOSONAR:S2245 (fixed seed: the forest is reproducible)

            Array.Clear(weights, 0, weights.Length);

            for (int i = 0; i < itemArray.Length; i++)
                weights[random.Next(itemArray.Length)]++;

            HashSet<string>? allowed = null;

            if (subsetSize > 0 && subsetSize < vocabulary.Count)
            {
                // A partial Fisher-Yates over a permutation reused across trees: the prefix is a
                // uniform draw without replacement, and the suffix is still a permutation. The
                // coupon-collector loop this replaces was O(V log V) per tree.
                for (int i = 0; i < subsetSize; i++)
                {
                    int j = random.Next(i, indices.Length);
                    (indices[i], indices[j]) = (indices[j], indices[i]);
                }

                allowed = new HashSet<string>(subsetSize, StringComparer.Ordinal);

                for (int i = 0; i < subsetSize; i++)
                    allowed.Add(vocabulary[indices[i]]);
            }

            for (int i = 0; i < itemArray.Length; i++)
                itemArray[i].Weight = itemArray[i].BaseWeight * weights[i];

            var tree = new DecisionTreeClassifier<Item>(
                new CachedFeatureSource(),
                item => item.Category,
                item => item.Weight,
                new DecisionTreeOptions
                {
                    MaxDepth = _options.MaxDepth,
                    MinDocumentsPerLeaf = _options.MinDocumentsPerLeaf,
                    MinDocumentFrequency = _options.MinDocumentFrequency,
                    Criterion = _options.Criterion,
                    Alpha = _options.Alpha,
                    QuestionCost = _options.QuestionCost,
                    ClassWeights = _options.ClassWeights,
                });

            tree.Train(itemArray, allowed);
            _trees.Add(tree);
        }
    }

    /// <summary>Predicts the most likely categories for an item, by decreasing probability.</summary>
    /// <param name="item">The item to classify.</param>
    /// <param name="limit">Maximum number of categories to return.</param>
    /// <param name="excludedCategories">Optional categories to hide without retraining; probabilities renormalize over the rest.</param>
    /// <returns>The averaged distribution; empty for an untrained forest or a non-positive limit.</returns>
    public IReadOnlyList<ClassificationResult> Predict(
        TItem item,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null)
    {
        if (limit <= 0 || _trees.Count == 0)
            return Array.Empty<ClassificationResult>();

        var query = new Item(string.Empty, _features.FeaturesOf(item), 1.0);
        var totals = new Dictionary<string, double>(_categories.Count, StringComparer.Ordinal);

        foreach (string category in _categories)
            totals[category] = 0;

        foreach (var tree in _trees)
        {
            foreach (var result in tree.Predict(query, limit: _categories.Count))
            {
                if (totals.ContainsKey(result.Category))
                    totals[result.Category] += result.Probability;
            }
        }

        var ranked = new List<ClassificationResult>(_categories.Count);
        double sum = 0;

        foreach (var (category, total) in totals)
        {
            if (excludedCategories?.Contains(category) == true)
                continue;

            ranked.Add(new ClassificationResult(category, total));
            sum += total;
        }

        if (ranked.Count == 0 || sum <= 0)
            return Array.Empty<ClassificationResult>();

        ranked.Sort(static (left, right) =>
        {
            int byProbability = right.Probability.CompareTo(left.Probability);

            return byProbability != 0 ? byProbability : string.CompareOrdinal(left.Category, right.Category);
        });

        for (int i = 0; i < ranked.Count; i++)
            ranked[i] = ranked[i] with { Probability = ranked[i].Probability / sum };

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

    private sealed class Item
    {
        public Item(string category, IFeatureSet features, double weight)
        {
            Category = category;
            Features = features;
            BaseWeight = weight;
            Weight = weight;
        }

        public string Category { get; }

        public IFeatureSet Features { get; }

        public double BaseWeight { get; }

        public double Weight { get; set; }
    }

    /// <summary>Hands the tree the feature set the forest already materialized, without copying it.</summary>
    private sealed class CachedFeatureSource : IFeatureSource<Item>
    {
        public IFeatureSet FeaturesOf(Item item) => item.Features;
    }
}

/// <summary>
/// The text convenience over <see cref="RandomForestClassifier{TItem}"/>: a
/// <see cref="SearchDocument"/> forest over <see cref="SearchDocument.Text"/>.
/// </summary>
public sealed class RandomForestClassifier : ITextClassifier
{
    private readonly RandomForestClassifier<SearchDocument> _inner;

    /// <summary>Creates a text forest over <see cref="SearchDocument.Text"/>.</summary>
    /// <param name="tokenizer">Tokenizer used both at training and prediction time (default: <see cref="Tokenizer.Default"/>).</param>
    /// <param name="options">Tunable knobs; when null, <see cref="RandomForestOptions.Default"/> applies.</param>
    public RandomForestClassifier(ITokenizer? tokenizer = null, RandomForestOptions? options = null)
        : this(new TextFeatureSource(tokenizer), options)
    {
    }

    /// <summary>Creates a text forest over a caller-supplied document feature space.</summary>
    /// <param name="features">The feature space to read from a document.</param>
    /// <param name="options">Tunable knobs; when null, <see cref="RandomForestOptions.Default"/> applies.</param>
    public RandomForestClassifier(IFeatureSource<SearchDocument> features, RandomForestOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(features);

        _inner = new RandomForestClassifier<SearchDocument>(
            features,
            document => document.Category,
            weightOf: null,
            options);
    }

    /// <summary>How many trees the last training built.</summary>
    public int TreeCount => _inner.TreeCount;

    /// <summary>The categories seen during training, sorted ordinal.</summary>
    public IReadOnlyList<string> Categories => _inner.Categories;

    /// <inheritdoc />
    public void Train(IEnumerable<SearchDocument> documents) => _inner.Train(documents);

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
}
