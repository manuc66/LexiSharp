using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Classification;

/// <summary>A node of a trained decision tree: either a question or a leaf.</summary>
public abstract record DecisionTreeNode;

/// <summary>
/// An internal node: a question, the two subtrees it selects between, and the score the split
/// earned when the tree was built.
/// </summary>
/// <param name="Question">The yes/no question asked at this node.</param>
/// <param name="TrueBranch">The subtree followed when the answer is yes.</param>
/// <param name="FalseBranch">The subtree followed when the answer is no.</param>
/// <param name="Metrics">The gain and document counts measured when this split was chosen.</param>
public sealed record DecisionBranch(
    FeatureQuestion Question,
    DecisionTreeNode TrueBranch,
    DecisionTreeNode FalseBranch,
    DecisionSplitMetrics Metrics) : DecisionTreeNode;

/// <summary>
/// A leaf: the category the tree predicts and the smoothed class distribution behind it.
/// </summary>
/// <param name="Category">The most likely category at this leaf.</param>
/// <param name="Probabilities">The leaf's smoothed distribution over every trained category.</param>
/// <param name="DocumentCount">How many training documents reached this leaf.</param>
/// <param name="Weight">The summed sample weight of those documents.</param>
public sealed record DecisionLeaf(
    string Category,
    IReadOnlyDictionary<string, double> Probabilities,
    int DocumentCount,
    double Weight) : DecisionTreeNode
{
    /// <summary>The unsmoothed class counts behind <see cref="Probabilities"/>, for the ledger.</summary>
    internal double[] RawCounts { get; init; } = Array.Empty<double>();
}

/// <summary>The measurements behind one chosen split.</summary>
/// <param name="Gain">The weighted impurity reduction the split achieved.</param>
/// <param name="GainRatio">The gain divided by the split's own entropy; <c>0</c> when that entropy is zero.</param>
/// <param name="ImpurityBefore">Weighted entropy or Gini of the parent, depending on the criterion.</param>
/// <param name="ImpurityAfter">The weight-weighted impurity of the two children.</param>
/// <param name="DocumentsTrue">How many documents answered yes.</param>
/// <param name="DocumentsFalse">How many documents answered no.</param>
/// <param name="WeightTrue">The summed sample weight of the documents that answered yes.</param>
/// <param name="WeightFalse">The summed sample weight of the documents that answered no.</param>
/// <param name="Cost">The cost of the chosen question; <c>1</c> when no cost was configured.</param>
public sealed record DecisionSplitMetrics(
    double Gain,
    double GainRatio,
    double ImpurityBefore,
    double ImpurityAfter,
    int DocumentsTrue,
    int DocumentsFalse,
    double WeightTrue,
    double WeightFalse,
    double Cost);

/// <summary>One question and the answer a particular item gave it.</summary>
/// <param name="Question">The question asked.</param>
/// <param name="Answer">The answer the item produced.</param>
public sealed record QuestionAnswer(FeatureQuestion Question, bool Answer);

/// <summary>The path an item took through a tree, and the leaf it reached.</summary>
/// <param name="Path">The questions asked, in order.</param>
/// <param name="Outcome">The leaf the answers selected.</param>
public sealed record DecisionExplanation(IReadOnlyList<QuestionAnswer> Path, DecisionLeaf Outcome);

/// <summary>
/// A supervised classifier over any feature space: at each node it asks the feature whose
/// presence best separates the training labels, so the most discriminative question comes first.
/// </summary>
/// <remarks>
/// <para>
/// The tree knows only features and weights. A feature is an opaque name produced by an
/// <see cref="IFeatureSource{TItem}"/>; the caller decides what a name means. Text is one such
/// source (<see cref="TextFeatureSource"/>), but a time bucket, a transition, a process identifier
/// or a derived "generic"/"excluded" flag is just another name. The core never learns the domain,
/// which is what lets the same tree serve all of them.
/// </para>
/// <para>
/// This is the explainable sibling of <see cref="NaiveBayesClassifier"/>. Naive Bayes is linear in
/// log space and cannot represent a conjunction; a tree can — "apple" then "red" — and every
/// prediction carries the list of questions that produced it through
/// <see cref="Explain(TItem)"/>.
/// </para>
/// <para>
/// The tree is built greedily. At each node every candidate feature is scored by
/// <see cref="DecisionTreeOptions.Criterion"/> over the labels still present, the best positive
/// gain wins (ties are broken by ordinal feature order, so a corpus always produces the same
/// tree), and the node recurses. Splits stop at <see cref="DecisionTreeOptions.MaxDepth"/>, when a
/// side would fall below <see cref="DecisionTreeOptions.MinDocumentsPerLeaf"/>, or when no split
/// beats <see cref="DecisionTreeOptions.MinGain"/>.
/// </para>
/// <para>
/// A feature may carry a numeric value (<see cref="IFeatureSet.NumericValue"/>): the tree also
/// scores thresholds over the node's values, so it can learn <c>hour &gt;= 14</c> rather than only
/// the presence of a bucket, and a value it has not seen — 13 or 17 — is placed by the same
/// inequality. A missing value answers no, so an item whose source could not provide the value
/// takes the false branch rather than being treated as zero. The threshold search is capped by
/// <see cref="DecisionTreeOptions.MaxThresholdsPerFeature"/>.
/// </para>
/// <para>
/// Greedy is not optimal: finding the smallest tree that separates a labelled corpus is NP-hard,
/// and a zero-gain corpus — labels that depend on an exclusive-or of two features, for example —
/// is not split at all. The tree is also not a probability model. A leaf's distribution is the
/// smoothed label mix of the training documents that reached it; it is not a calibrated
/// probability, and an item whose features are all unknown still follows the false branches to a
/// leaf and gets an answer.
/// </para>
/// <para>
/// <b>Weights.</b> Every item carries a non-negative sample weight, by default <c>1</c>. The gain,
/// the impurity, the leaf distribution and the corpus divider's profiles are all weighted, so a
/// caller can make recent observations count more without the core knowing why. A half-life is
/// the caller's policy — <c>weightOf = item =&gt; Math.Pow(0.5, age(item) / halfLife)</c> — not an
/// option here: the library sees a number, not a clock.
/// </para>
/// <para>
/// The model is batch-trained. <see cref="Learn(TItem)"/> folds one labelled item into the leaf it
/// reaches and returns a token that <see cref="Unlearn(long)"/> removes exactly, without rebuilding
/// the structure; <see cref="Train(IEnumerable{TItem})"/> rebuilds and clears both ledgers. Concurrent
/// <see cref="Predict(TItem, int, IReadOnlySet{string})"/> calls are
/// safe once training has finished; training must not overlap a prediction.
/// </para>
/// </remarks>
/// <typeparam name="TItem">The item type being classified.</typeparam>
public sealed class DecisionTreeClassifier<TItem>
{
    /// <summary>
    /// Below this, a gain is floating-point dust rather than a split worth asking. It is the same
    /// threshold the ordinal tie-break uses, so "equal" and "useful" cannot disagree.
    /// </summary>
    private const double SplitFloor = 1e-12;

    private readonly IFeatureSource<TItem> _features;
    private readonly Func<TItem, string?> _categoryOf;
    private readonly Func<TItem, double> _weightOf;
    private readonly DecisionTreeOptions _options;

    private DecisionTreeNode? _root;
    private IReadOnlyList<string> _categories = Array.Empty<string>();
    private Dictionary<string, int> _categoryIndex = new(StringComparer.Ordinal);
    private string[] _featuresById = Array.Empty<string>();
    private double[] _featureCosts = Array.Empty<double>();

    private readonly Dictionary<DecisionLeaf, Dictionary<string, double>> _reinforcement =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<long, LearnedItem> _learned = new();
    private long _nextToken;

    /// <summary>Creates a classifier over a feature space.</summary>
    /// <param name="features">The feature space the tree reads.</param>
    /// <param name="categoryOf">Returns an item's label, or null/empty to skip it during training.</param>
    /// <param name="weightOf">Returns an item's non-negative sample weight; null means <c>1</c> for every item.</param>
    /// <param name="options">Tunable knobs; when null, <see cref="DecisionTreeOptions.Default"/> applies.</param>
    public DecisionTreeClassifier(
        IFeatureSource<TItem> features,
        Func<TItem, string?> categoryOf,
        Func<TItem, double>? weightOf = null,
        DecisionTreeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(categoryOf);

        _features = features;
        _categoryOf = categoryOf;
        _weightOf = weightOf ?? (_ => 1.0);
        _options = options ?? DecisionTreeOptions.Default;

        if (!_options.IsValid)
            throw new ArgumentException("The decision-tree options are not valid.", nameof(options));
    }

    /// <summary>The root of the trained tree, or null when nothing has been trained.</summary>
    public DecisionTreeNode? Root => _root;

    /// <summary>The categories seen during training, sorted ordinal.</summary>
    public IReadOnlyList<string> Categories => _categories;

    /// <summary>Trains the tree on labelled items; items with a null or empty label are ignored.</summary>
    /// <param name="items">The labelled corpus.</param>
    /// <exception cref="ArgumentException">A sample weight is negative or not finite.</exception>
    public void Train(IEnumerable<TItem> items) => Train(items, featureSubset: null);

    /// <summary>
    /// Trains the tree, optionally over a subset of the feature space. The forest uses the subset
    /// to decorrelate its trees; a public caller has no reason to, so the overload is internal.
    /// </summary>
    internal void Train(IEnumerable<TItem> items, IReadOnlySet<string>? featureSubset)
    {
        ArgumentNullException.ThrowIfNull(items);

        _reinforcement.Clear();
        _learned.Clear();

        if (_options.ClassWeights is { } classWeights)
        {
            foreach (var pair in classWeights)
            {
                if (!double.IsFinite(pair.Value) || pair.Value < 0)
                    throw new ArgumentException("Class weights must be finite and non-negative.");
            }
        }

        var pending = new List<PendingItem>();
        var categories = new SortedSet<string>(StringComparer.Ordinal);

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
            pending.Add(new PendingItem(category, _features.FeaturesOf(item), effectiveWeight));
        }

        _categories = categories.ToArray();
        _categoryIndex = new Dictionary<string, int>(_categories.Count, StringComparer.Ordinal);

        for (int i = 0; i < _categories.Count; i++)
            _categoryIndex[_categories[i]] = i;

        if (pending.Count == 0)
        {
            _root = null;
            _featuresById = Array.Empty<string>();
            _featureCosts = Array.Empty<double>();
            return;
        }

        // The corpus vocabulary is assigned an integer id once. The split search then compares
        // ints in its innermost loop instead of hashing feature names per occurrence; the name is
        // recovered only when a split is chosen or a tie is broken.
        var featureIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var featuresById = new List<string>();
        var documentFrequency = new List<int>();

        foreach (var item in pending)
        {
            foreach (string feature in item.Features.Features)
            {
                if (featureSubset is not null && !featureSubset.Contains(feature))
                    continue;

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

        _featuresById = featuresById.ToArray();
        _featureCosts = Array.Empty<double>();

        if (_options.QuestionCost is { } questionCost)
        {
            _featureCosts = new double[_featuresById.Length];

            for (int id = 0; id < _featuresById.Length; id++)
            {
                double cost = questionCost(_featuresById[id]);

                if (!double.IsFinite(cost) || cost <= 0)
                    throw new ArgumentException("Question costs must be finite and positive.");

                _featureCosts[id] = cost;
            }
        }

        var candidate = new bool[_featuresById.Length];

        for (int id = 0; id < documentFrequency.Count; id++)
            candidate[id] = documentFrequency[id] >= _options.MinDocumentFrequency;

        // Each document owns a slice of one flat int[]: its candidate feature ids. Two passes
        // (count, then fill) avoid a per-document array object.
        var counts = new int[pending.Count];
        int total = 0;

        for (int i = 0; i < pending.Count; i++)
        {
            int count = 0;

            foreach (string feature in pending[i].Features.Features)
            {
                if (featureIds.TryGetValue(feature, out int id) && candidate[id])
                    count++;
            }

            counts[i] = count;
            total += count;
        }

        var flat = new int[total];
        var flatValues = new double[total];
        var training = new List<TrainingItem>(pending.Count);
        int offset = 0;

        for (int i = 0; i < pending.Count; i++)
        {
            var item = pending[i];
            int start = offset;

            foreach (string feature in item.Features.Features)
            {
                if (!featureIds.TryGetValue(feature, out int id) || !candidate[id])
                    continue;

                flat[offset] = id;
                flatValues[offset] = item.Features.NumericValue(feature) ?? double.NaN;
                offset++;
            }

            training.Add(new TrainingItem(
                _categoryIndex[item.Category],
                start,
                counts[i],
                item.Weight));
        }

        // The build reads the flat ids and the names, so the source's feature sets are no longer
        // needed once the slices exist.
        pending.Clear();

        var scratch = new SplitScratch(_featuresById.Length);

        try
        {
            _root = Build(training, flat, flatValues, depth: 0, scratch);
        }
        finally
        {
            scratch.Release();
        }
    }

    /// <summary>Predicts the most likely categories for an item, by decreasing probability.</summary>
    /// <param name="item">The item to classify.</param>
    /// <param name="limit">Maximum number of categories to return.</param>
    /// <param name="excludedCategories">Optional categories to hide without retraining; probabilities renormalize over the rest.</param>
    /// <returns>The ranked distribution; empty for an untrained model or a non-positive limit.</returns>
    public IReadOnlyList<ClassificationResult> Predict(
        TItem item,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null)
    {
        if (limit <= 0 || _root is null)
            return Array.Empty<ClassificationResult>();

        var leaf = ApplyReinforcement(Walk(item));
        var ranked = new List<ClassificationResult>(leaf.Probabilities.Count);
        double total = 0;

        foreach (var (category, probability) in leaf.Probabilities)
        {
            if (excludedCategories?.Contains(category) == true)
                continue;

            ranked.Add(new ClassificationResult(category, probability));
            total += probability;
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

        if (leaf.Weight < _options.MinLeafSupport)
            return Array.Empty<ClassificationResult>();

        if (ranked[0].Probability < _options.MinLeafProbability)
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

    /// <summary>Records a signed correction at the leaf an item reaches, without retraining.</summary>
    /// <param name="item">The item whose leaf is corrected.</param>
    /// <param name="category">A category seen during training.</param>
    /// <param name="weight">The positive mass to add. Default: <c>1</c>.</param>
    public void Reinforce(TItem item, string category, double weight = 1.0) =>
        AdjustReinforcement(item, category, weight, sign: 1.0);

    /// <summary>Removes a correction previously added with <see cref="Reinforce"/>.</summary>
    /// <param name="item">The item whose leaf is corrected.</param>
    /// <param name="category">The corrected category.</param>
    /// <param name="weight">The positive mass to remove. Default: <c>1</c>.</param>
    public void Unreinforce(TItem item, string category, double weight = 1.0) =>
        AdjustReinforcement(item, category, weight, sign: -1.0);

    /// <summary>Clears every correction; the tree returns to its training distribution.</summary>
    public void ForgetReinforcement() => _reinforcement.Clear();

    /// <summary>
    /// Folds one labelled item into the leaf it reaches and returns a token for
    /// <see cref="Unlearn(long)"/>. The tree structure is not rebuilt: the item changes the
    /// reached leaf's distribution only.
    /// </summary>
    /// <param name="item">The labelled item; its category must have been seen during training.</param>
    /// <returns>A token that removes exactly this contribution.</returns>
    /// <exception cref="InvalidOperationException">The model is untrained.</exception>
    /// <exception cref="ArgumentException">The item has no category, an unseen category, or a non-positive weight.</exception>
    public long Learn(TItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (_root is null)
            throw new InvalidOperationException("The model is untrained.");

        string? category = _categoryOf(item);

        if (string.IsNullOrEmpty(category))
            throw new ArgumentException("The item has no category.", nameof(item));

        if (!_categoryIndex.ContainsKey(category))
            throw new ArgumentException("The category was not seen during training.", nameof(item));

        double weight = _weightOf(item);

        if (!double.IsFinite(weight) || weight <= 0)
            throw new ArgumentException("The sample weight must be finite and positive.", nameof(item));

        DecisionLeaf leaf = WalkWithPath(item, out bool[] path);
        AdjustLeaf(leaf, category, weight, sign: 1.0);

        long token = _nextToken++;
        _learned[token] = new LearnedItem(leaf, path, category, weight);

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
        AdjustLeaf(learned.Leaf, learned.Category, learned.Weight, sign: -1.0);
        return true;
    }

    private void AdjustReinforcement(TItem item, string category, double weight, double sign)
    {
        ArgumentNullException.ThrowIfNull(category);

        if (_root is null)
            throw new InvalidOperationException("The model is untrained.");

        if (!double.IsFinite(weight) || weight <= 0)
            throw new ArgumentException("The reinforcement weight must be finite and positive.", nameof(weight));

        if (!_categoryIndex.ContainsKey(category))
            throw new ArgumentException("The category was not seen during training.", nameof(category));

        AdjustLeaf(Walk(item), category, weight, sign);
    }

    private void AdjustLeaf(DecisionLeaf leaf, string category, double weight, double sign)
    {
        if (!_reinforcement.TryGetValue(leaf, out Dictionary<string, double>? adjustments))
        {
            adjustments = new Dictionary<string, double>(StringComparer.Ordinal);
            _reinforcement[leaf] = adjustments;
        }

        double updated = adjustments.GetValueOrDefault(category) + sign * weight;

        if (Math.Abs(updated) <= 1e-12)
            adjustments.Remove(category);
        else
            adjustments[category] = updated;

        if (adjustments.Count == 0)
            _reinforcement.Remove(leaf);
    }

    private DecisionLeaf ApplyReinforcement(DecisionLeaf leaf)
    {
        if (!_reinforcement.TryGetValue(leaf, out Dictionary<string, double>? adjustments)
            || adjustments.Count == 0)
        {
            return leaf;
        }

        double[] counts = leaf.RawCounts;
        var adjusted = new double[counts.Length];
        double total = 0;

        for (int c = 0; c < counts.Length; c++)
        {
            double value = Math.Max(0, counts[c] + adjustments.GetValueOrDefault(_categories[c]));
            adjusted[c] = value;
            total += value;
        }

        return CreateLeaf(adjusted, total, leaf.DocumentCount, leaf.Weight);
    }

    /// <summary>Returns the questions an item answered on its way to a leaf, and the leaf it reached.</summary>
    /// <param name="item">The item to explain.</param>
    /// <returns>The path and outcome, or null when the model is untrained.</returns>
    public DecisionExplanation? Explain(TItem item)
    {
        if (_root is null)
            return null;

        IFeatureSet features = _features.FeaturesOf(item);
        var path = new List<QuestionAnswer>();
        DecisionTreeNode node = _root;

        while (node is DecisionBranch branch)
        {
            bool answer = branch.Question.Matches(features);
            path.Add(new QuestionAnswer(branch.Question, answer));
            node = answer ? branch.TrueBranch : branch.FalseBranch;
        }

        return new DecisionExplanation(path, ApplyReinforcement((DecisionLeaf)node));
    }

    /// <summary>Renders the trained tree as indented text, or <c>(untrained)</c> before training.</summary>
    /// <returns>A deterministic, human-readable rendering of the questions and leaves.</returns>
    public string Describe()
    {
        if (_root is null)
            return "(untrained)";

        var builder = new StringBuilder();
        DescribeNode(_root, builder, indent: string.Empty, prefix: string.Empty);

        return builder.ToString();
    }

    private static void DescribeNode(DecisionTreeNode node, StringBuilder builder, string indent, string prefix)
    {
        builder.Append(indent).Append(prefix);

        if (node is DecisionLeaf leaf)
        {
            builder.Append("-> ").Append(leaf.Category)
                .Append(" (").Append(leaf.DocumentCount.ToString(CultureInfo.InvariantCulture)).Append(')')
                .AppendLine();
            return;
        }

        var branch = (DecisionBranch)node;

        builder.Append(branch.Question.Describe()).AppendLine();
        DescribeNode(branch.TrueBranch, builder, indent + "  ", "yes: ");
        DescribeNode(branch.FalseBranch, builder, indent + "  ", "no:  ");
    }

    private DecisionTreeNode Build(
        List<TrainingItem> items,
        int[] featureIds,
        double[] featureValues,
        int depth,
        SplitScratch scratch)
    {
        double[] counts = CountByCategory(items, out double totalWeight);

        if (depth >= _options.MaxDepth || items.Count < 2 * _options.MinDocumentsPerLeaf)
            return MakeLeaf(counts, totalWeight, items.Count);

        SplitChoice? best = FindBestSplit(
            items, featureIds, featureValues, counts, totalWeight, scratch);

        if (best is null)
            return MakeLeaf(counts, totalWeight, items.Count);

        SplitChoice choice = best.Value;
        string feature = _featuresById[choice.FeatureId];
        FeatureQuestion question = double.IsNaN(choice.Threshold)
            ? new FeaturePresenceQuestion(feature)
            : new NumericThresholdQuestion(feature, choice.Threshold);
        var trueItems = new List<TrainingItem>(choice.DocumentsTrue);
        var falseItems = new List<TrainingItem>(choice.DocumentsFalse);

        foreach (var item in items)
        {
            bool hasFeature = double.IsNaN(choice.Threshold)
                ? Array.IndexOf(featureIds, choice.FeatureId, item.FeatureStart, item.FeatureCount) >= 0
                : HasNumericAtLeast(
                    featureIds,
                    featureValues,
                    choice.FeatureId,
                    choice.Threshold,
                    item.FeatureStart,
                    item.FeatureCount);

            (hasFeature ? trueItems : falseItems).Add(item);
        }

        var metrics = new DecisionSplitMetrics(
            choice.Gain,
            choice.GainRatio,
            choice.ImpurityBefore,
            choice.ImpurityAfter,
            choice.DocumentsTrue,
            choice.DocumentsFalse,
            choice.WeightTrue,
            choice.WeightFalse,
            choice.Cost);

        return new DecisionBranch(
            question,
            Build(trueItems, featureIds, featureValues, depth + 1, scratch),
            Build(falseItems, featureIds, featureValues, depth + 1, scratch),
            metrics);
    }

    private double[] CountByCategory(List<TrainingItem> items, out double totalWeight)
    {
        var counts = new double[_categories.Count];
        totalWeight = 0;

        foreach (var item in items)
        {
            counts[item.CategoryIndex] += item.Weight;
            totalWeight += item.Weight;
        }

        return counts;
    }

    private SplitChoice? FindBestSplit(
        List<TrainingItem> items,
        int[] featureIds,
        double[] featureValues,
        double[] parentCounts,
        double parentWeight,
        SplitScratch scratch)
    {
        int classCount = _categories.Count;
        int version = scratch.NextVersion();
        scratch.ResetNode();

        var slotByFeature = scratch.SlotByFeature;
        var slotVersion = scratch.SlotVersion;
        var nodeFeatureIds = scratch.NodeFeatureIds;
        var documentCounts = scratch.DocumentCounts;
        Dictionary<int, List<NumericPoint>>? numericPoints = null;

        // One flat buffer per training run, reused by every node, instead of one double[] per
        // feature per node: the counts for feature slot s live at
        // [s * classCount, (s + 1) * classCount). Feature ids are ints here, so the innermost loop
        // does no string hashing; the version stamp replaces clearing a vocabulary-sized map.
        foreach (var item in items)
        {
            int end = item.FeatureStart + item.FeatureCount;

            for (int i = item.FeatureStart; i < end; i++)
            {
                int featureId = featureIds[i];
                int slot;

                if (slotVersion[featureId] != version)
                {
                    slotVersion[featureId] = version;
                    slot = documentCounts.Count;
                    slotByFeature[featureId] = slot;
                    nodeFeatureIds.Add(featureId);
                    documentCounts.Add(0);

                    scratch.EnsureCapacity((slot + 1) * classCount);

                    // A pooled buffer carries stale values from its previous owner; a slot is
                    // only meaningful once its segment has been cleared.
                    Array.Clear(scratch.Weights, slot * classCount, classCount);
                }
                else
                {
                    slot = slotByFeature[featureId];
                }

                documentCounts[slot]++;
                scratch.Weights[slot * classCount + item.CategoryIndex] += item.Weight;

                double value = featureValues[i];

                if (double.IsNaN(value))
                    continue;

                numericPoints ??= new Dictionary<int, List<NumericPoint>>();

                if (!numericPoints.TryGetValue(featureId, out List<NumericPoint>? points))
                {
                    points = new List<NumericPoint>();
                    numericPoints[featureId] = points;
                }

                points.Add(new NumericPoint(value, item.CategoryIndex, item.Weight));
            }
        }

        double impurityBefore = Impurity(parentCounts, parentWeight);
        bool gini = _options.Criterion == SplitCriterion.Gini;
        SplitChoice? best = null;
        double bestScore = double.NegativeInfinity;

        void Consider(
            int featureId,
            double threshold,
            double gain,
            double ratio,
            double impurityAfter,
            int documentsTrue,
            int documentsFalse,
            double weightTrue,
            double weightFalse)
        {
            double baseScore = _options.Criterion == SplitCriterion.GainRatio ? ratio : gain;
            double cost = CostOf(featureId);
            double score = baseScore / cost;

            bool better = best is null
                || score > bestScore + SplitFloor
                || (Math.Abs(score - bestScore) <= SplitFloor
                    && IsTieBreakBetter(featureId, threshold, best.Value));

            if (!better)
                return;

            best = new SplitChoice(
                featureId,
                threshold,
                cost,
                gain,
                ratio,
                impurityBefore,
                impurityAfter,
                documentsTrue,
                documentsFalse,
                weightTrue,
                weightFalse);
            bestScore = score;
        }

        foreach (int featureId in nodeFeatureIds)
        {
            int slot = slotByFeature[featureId];
            int documentsTrue = documentCounts[slot];
            int documentsFalse = items.Count - documentsTrue;

            if (documentsTrue < _options.MinDocumentsPerLeaf || documentsFalse < _options.MinDocumentsPerLeaf)
                continue;

            int offset = slot * classCount;
            double weightTrue = 0;

            for (int c = 0; c < classCount; c++)
                weightTrue += scratch.Weights[offset + c];

            double weightFalse = parentWeight - weightTrue;

            if (weightTrue <= 0 || weightFalse <= 0)
                continue;

            double impurityAfter = ChildImpurity(
                parentCounts, scratch.Weights, offset, weightTrue, weightFalse, gini);
            double gain = impurityBefore - impurityAfter;

            if (gain <= Math.Max(_options.MinGain, SplitFloor))
                continue;

            double probability = weightTrue / parentWeight;
            double splitInfo = -probability * Math.Log2(probability) - (1 - probability) * Math.Log2(1 - probability);
            double ratio = splitInfo > 0 ? gain / splitInfo : 0;

            Consider(
                featureId,
                double.NaN,
                gain,
                ratio,
                impurityAfter,
                documentsTrue,
                documentsFalse,
                weightTrue,
                weightFalse);
        }

        if (numericPoints is not null)
        {
            foreach (var (featureId, points) in numericPoints)
            {
                points.Sort(static (left, right) => left.Value.CompareTo(right.Value));

                var boundaries = new List<double>();

                for (int i = 0; i < points.Count; i++)
                {
                    if (i == 0 || points[i].Value != points[i - 1].Value)
                        boundaries.Add(points[i].Value);
                }

                int step = Math.Max(
                    1,
                    (boundaries.Count + _options.MaxThresholdsPerFeature - 1)
                    / _options.MaxThresholdsPerFeature);

                for (int b = 0; b < boundaries.Count; b += step)
                    EvaluateNumeric(featureId, points, boundaries[b]);

                if (boundaries.Count > 0 && (boundaries.Count - 1) % step != 0)
                    EvaluateNumeric(featureId, points, boundaries[^1]);
            }
        }

        void EvaluateNumeric(int featureId, List<NumericPoint> points, double threshold)
        {
            int first = LowerBound(points, threshold);
            int documentsTrue = points.Count - first;

            if (documentsTrue < _options.MinDocumentsPerLeaf
                || items.Count - documentsTrue < _options.MinDocumentsPerLeaf)
                return;

            var trueCounts = new double[classCount];
            double weightTrue = 0;

            for (int i = first; i < points.Count; i++)
            {
                trueCounts[points[i].CategoryIndex] += points[i].Weight;
                weightTrue += points[i].Weight;
            }

            double weightFalse = parentWeight - weightTrue;

            if (weightTrue <= 0 || weightFalse <= 0)
                return;

            double impurityAfter = ChildImpurity(
                parentCounts, trueCounts, 0, weightTrue, weightFalse, gini);
            double gain = impurityBefore - impurityAfter;

            if (gain <= Math.Max(_options.MinGain, SplitFloor))
                return;

            double probability = weightTrue / parentWeight;
            double splitInfo = -probability * Math.Log2(probability) - (1 - probability) * Math.Log2(1 - probability);
            double ratio = splitInfo > 0 ? gain / splitInfo : 0;

            Consider(
                featureId,
                threshold,
                gain,
                ratio,
                impurityAfter,
                documentsTrue,
                items.Count - documentsTrue,
                weightTrue,
                weightFalse);
        }

        return best;
    }

    /// <summary>
    /// Ordinal feature-name order, then presence before a threshold, then the smaller threshold:
    /// the same candidate set always selects the same split, whatever the dictionary order was.
    /// </summary>
    private double CostOf(int featureId) =>
        _featureCosts.Length == 0 ? 1.0 : _featureCosts[featureId];

    private bool IsTieBreakBetter(int featureId, double threshold, SplitChoice best)
    {
        int byName = string.CompareOrdinal(_featuresById[featureId], _featuresById[best.FeatureId]);

        if (byName != 0)
            return byName < 0;

        if (double.IsNaN(threshold))
            return !double.IsNaN(best.Threshold);

        if (double.IsNaN(best.Threshold))
            return false;

        return threshold < best.Threshold;
    }

    /// <summary>The first index whose value is at or above <paramref name="threshold"/>.</summary>
    private static int LowerBound(List<NumericPoint> points, double threshold)
    {
        int low = 0;
        int high = points.Count;

        while (low < high)
        {
            int middle = low + ((high - low) >> 1);

            if (points[middle].Value < threshold)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    /// <summary>Whether an item's numeric feature is present and at or above the threshold.</summary>
    private static bool HasNumericAtLeast(
        int[] featureIds,
        double[] featureValues,
        int featureId,
        double threshold,
        int start,
        int count)
    {
        int index = Array.IndexOf(featureIds, featureId, start, count);

        if (index < 0)
            return false;

        double value = featureValues[index];

        return !double.IsNaN(value) && value >= threshold;
    }

    /// <summary>
    /// The document-weighted impurity of the two children, read straight from the flat count
    /// buffer: the false side is <c>parent - true</c>, so no second array is needed.
    /// </summary>
    private static double ChildImpurity(
        double[] parentCounts,
        double[] trueWeights,
        int offset,
        double weightTrue,
        double weightFalse,
        bool gini)
    {
        double trueImpurity = 0;
        double falseImpurity = 0;

        for (int c = 0; c < parentCounts.Length; c++)
        {
            double trueCount = trueWeights[offset + c];

            if (trueCount > 0)
            {
                double probability = trueCount / weightTrue;
                trueImpurity += gini ? probability * probability : -probability * Math.Log2(probability);
            }

            double falseCount = parentCounts[c] - trueCount;

            if (falseCount > 0)
            {
                double probability = falseCount / weightFalse;
                falseImpurity += gini ? probability * probability : -probability * Math.Log2(probability);
            }
        }

        if (gini)
        {
            trueImpurity = 1 - trueImpurity;
            falseImpurity = 1 - falseImpurity;
        }

        double probabilityTrue = weightTrue / (weightTrue + weightFalse);

        return probabilityTrue * trueImpurity + (1 - probabilityTrue) * falseImpurity;
    }

    private double Impurity(double[] counts, double total)
    {
        if (total <= 0)
            return 0;

        double impurity = 0;

        if (_options.Criterion == SplitCriterion.Gini)
        {
            foreach (double count in counts)
            {
                double probability = count / total;
                impurity += probability * probability;
            }

            return 1 - impurity;
        }

        foreach (double count in counts)
        {
            if (count <= 0)
                continue;

            double probability = count / total;
            impurity -= probability * Math.Log2(probability);
        }

        return impurity;
    }

    private DecisionLeaf MakeLeaf(double[] counts, double totalWeight, int documentCount) =>
        CreateLeaf(counts, totalWeight, documentCount, totalWeight);

    private DecisionLeaf CreateLeaf(double[] counts, double total, int documentCount, double weight)
    {
        int classCount = _categories.Count;
        var probabilities = new Dictionary<string, double>(classCount, StringComparer.Ordinal);
        string bestCategory = _categories[0];
        double bestProbability = double.NegativeInfinity;

        for (int c = 0; c < classCount; c++)
        {
            double probability = (counts[c] + _options.Alpha) / (total + _options.Alpha * classCount);
            string category = _categories[c];
            probabilities[category] = probability;

            bool better = probability > bestProbability + 1e-12
                || (Math.Abs(probability - bestProbability) <= 1e-12 && string.CompareOrdinal(category, bestCategory) < 0);

            if (!better)
                continue;

            bestCategory = category;
            bestProbability = probability;
        }

        return new DecisionLeaf(
            bestCategory,
            new ReadOnlyDictionary<string, double>(probabilities),
            documentCount,
            weight)
        {
            RawCounts = counts,
        };
    }

    private DecisionLeaf Walk(TItem item)
    {
        IFeatureSet features = _features.FeaturesOf(item);
        DecisionTreeNode node = _root!;

        while (node is DecisionBranch branch)
            node = branch.Question.Matches(features) ? branch.TrueBranch : branch.FalseBranch;

        return (DecisionLeaf)node;
    }

    private DecisionLeaf WalkWithPath(TItem item, out bool[] path)
    {
        IFeatureSet features = _features.FeaturesOf(item);
        DecisionTreeNode node = _root!;
        var answers = new List<bool>();

        while (node is DecisionBranch branch)
        {
            bool answer = branch.Question.Matches(features);
            answers.Add(answer);
            node = answer ? branch.TrueBranch : branch.FalseBranch;
        }

        path = answers.ToArray();
        return (DecisionLeaf)node;
    }

    private sealed record LearnedItem(
        DecisionLeaf Leaf,
        bool[] Path,
        string Category,
        double Weight);

    private sealed record PendingItem(string Category, IFeatureSet Features, double Weight);

    private sealed record TrainingItem(
        int CategoryIndex,
        int FeatureStart,
        int FeatureCount,
        double Weight);

    internal string FeatureSpaceHash => ModelFormat.Hash(_featuresById);

    internal IReadOnlyList<string> FeatureNames => _featuresById;

    internal static (double MinLeafProbability, double MinLeafSupport) ReadPredictionOptions(
        BinaryReader reader) =>
        (reader.ReadDouble(), reader.ReadDouble());

    internal void WriteState(BinaryWriter writer)
    {
        writer.Write(_options.MinLeafProbability);
        writer.Write(_options.MinLeafSupport);
        ModelFormat.WriteStrings(writer, _categories);
        ModelFormat.WriteStrings(writer, _featuresById);
        ModelFormat.WriteNode(writer, _root);

        var entries = new List<(bool[] Path, string Category, double Weight)>();
        var visited = new HashSet<DecisionLeaf>(ReferenceEqualityComparer.Instance);

        if (_root is not null && _reinforcement.Count > 0)
            CollectReinforcement(_root, new List<bool>(), visited, entries);

        writer.Write(entries.Count);

        foreach (var entry in entries)
        {
            ModelFormat.WritePath(writer, entry.Path);
            ModelFormat.WriteString(writer, entry.Category);
            writer.Write(entry.Weight);
        }

        writer.Write(_nextToken);
        writer.Write(_learned.Count);

        foreach (var pair in _learned.OrderBy(pair => pair.Key))
        {
            writer.Write(pair.Key);
            ModelFormat.WritePath(writer, pair.Value.Path);
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

        _featuresById = ModelFormat.ReadStrings(reader);
        _featureCosts = Array.Empty<double>();
        _root = ModelFormat.ReadNode(reader);
        _reinforcement.Clear();
        _learned.Clear();

        int entries = ModelFormat.ReadCount(reader);

        for (int i = 0; i < entries; i++)
        {
            bool[] path = ModelFormat.ReadPath(reader);
            string category = ModelFormat.ReadString(reader)
                ?? throw new InvalidDataException("A reinforcement entry has no category.");
            double weight = reader.ReadDouble();
            DecisionLeaf leaf = LeafAt(path)
                ?? throw new InvalidDataException("A reinforcement path does not reach a leaf.");

            if (!_reinforcement.TryGetValue(leaf, out var weights))
            {
                weights = new Dictionary<string, double>(StringComparer.Ordinal);
                _reinforcement[leaf] = weights;
            }

            weights[category] = weight;
        }

        _nextToken = reader.ReadInt64();
        int learnedCount = ModelFormat.ReadCount(reader);

        for (int i = 0; i < learnedCount; i++)
        {
            long token = reader.ReadInt64();
            bool[] path = ModelFormat.ReadPath(reader);
            string category = ModelFormat.ReadString(reader)
                ?? throw new InvalidDataException("A learned entry has no category.");
            double weight = reader.ReadDouble();
            DecisionLeaf leaf = LeafAt(path)
                ?? throw new InvalidDataException("A learned path does not reach a leaf.");

            _learned[token] = new LearnedItem(leaf, path, category, weight);
            _nextToken = Math.Max(_nextToken, token + 1);
        }
    }

    private void CollectReinforcement(
        DecisionTreeNode node,
        List<bool> path,
        HashSet<DecisionLeaf> visited,
        List<(bool[] Path, string Category, double Weight)> entries)
    {
        switch (node)
        {
            case DecisionBranch branch:
                path.Add(true);
                CollectReinforcement(branch.TrueBranch, path, visited, entries);
                path[^1] = false;
                CollectReinforcement(branch.FalseBranch, path, visited, entries);
                path.RemoveAt(path.Count - 1);
                break;

            case DecisionLeaf leaf when _reinforcement.TryGetValue(leaf, out var weights)
                                        && visited.Add(leaf):
                foreach (var pair in weights.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    entries.Add((path.ToArray(), pair.Key, pair.Value));
                break;
        }
    }

    private DecisionLeaf? LeafAt(IReadOnlyList<bool> path)
    {
        DecisionTreeNode? node = _root;

        foreach (bool answer in path)
        {
            if (node is not DecisionBranch branch)
                return null;

            node = answer ? branch.TrueBranch : branch.FalseBranch;
        }

        return node as DecisionLeaf;
    }

    /// <summary>
    /// The per-training scratch a depth-first build reuses across nodes. The slot maps are sized
    /// once for the corpus vocabulary and stamped by node version, so a node neither allocates a
    /// dictionary nor clears a vocabulary-sized array; the weight buffer is rented once and
    /// returned when training ends.
    /// </summary>
    private sealed class SplitScratch
    {
        public SplitScratch(int featureCount)
        {
            SlotByFeature = new int[featureCount];
            SlotVersion = new int[featureCount];
        }

        public int[] SlotByFeature { get; }

        public int[] SlotVersion { get; }

        public List<int> NodeFeatureIds { get; } = new();

        public List<int> DocumentCounts { get; } = new();

        public double[] Weights { get; private set; } = Array.Empty<double>();

        public int Version { get; private set; }

        public int NextVersion()
        {
            if (Version == int.MaxValue)
            {
                Array.Clear(SlotVersion, 0, SlotVersion.Length);
                Version = 0;
            }

            return ++Version;
        }

        public void ResetNode()
        {
            NodeFeatureIds.Clear();
            DocumentCounts.Clear();
        }

        public void EnsureCapacity(int required)
        {
            if (required <= Weights.Length)
                return;

            double[] larger = ArrayPool<double>.Shared.Rent(Math.Max(required, Weights.Length * 2));

            if (Weights.Length > 0)
            {
                Array.Copy(Weights, larger, Weights.Length);
                ArrayPool<double>.Shared.Return(Weights);
            }

            Weights = larger;
        }

        public void Release()
        {
            if (Weights.Length == 0)
                return;

            ArrayPool<double>.Shared.Return(Weights);
            Weights = Array.Empty<double>();
        }
    }

    private readonly record struct NumericPoint(double Value, int CategoryIndex, double Weight);

    private readonly record struct SplitChoice(
        int FeatureId,
        double Threshold,
        double Cost,
        double Gain,
        double GainRatio,
        double ImpurityBefore,
        double ImpurityAfter,
        int DocumentsTrue,
        int DocumentsFalse,
        double WeightTrue,
        double WeightFalse);
}

/// <summary>
/// The text convenience over <see cref="DecisionTreeClassifier{TItem}"/>: a
/// <see cref="SearchDocument"/> classifier that reads <see cref="SearchDocument.Text"/> and
/// implements <see cref="ITextClassifier"/>.
/// </summary>
/// <remarks>
/// This is the shape most callers want when the only dimension is text. For several dimensions —
/// text plus anything else — build a <see cref="DecisionTreeClassifier{TItem}"/> over your own
/// item type with a <see cref="CompositeFeatureSource{TItem}"/>, and keep the domain out of the
/// model.
/// </remarks>
public sealed class DecisionTreeClassifier : ITextClassifier
{
    private readonly DecisionTreeClassifier<SearchDocument> _inner;

    /// <summary>Creates a text classifier over <see cref="SearchDocument.Text"/>.</summary>
    /// <param name="tokenizer">Tokenizer used both at training and prediction time (default: <see cref="Tokenizer.Default"/>).</param>
    /// <param name="options">Tunable knobs; when null, <see cref="DecisionTreeOptions.Default"/> applies.</param>
    public DecisionTreeClassifier(ITokenizer? tokenizer = null, DecisionTreeOptions? options = null)
        : this(new TextFeatureSource(tokenizer), options)
    {
    }

    /// <summary>Creates a text classifier over a caller-supplied document feature space.</summary>
    /// <param name="features">The feature space to read from a document.</param>
    /// <param name="options">Tunable knobs; when null, <see cref="DecisionTreeOptions.Default"/> applies.</param>
    public DecisionTreeClassifier(IFeatureSource<SearchDocument> features, DecisionTreeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(features);

        _inner = new DecisionTreeClassifier<SearchDocument>(
            features,
            document => document.Category,
            weightOf: null,
            options);
    }

    /// <summary>The root of the trained tree, or null when nothing has been trained.</summary>
    public DecisionTreeNode? Root => _inner.Root;

    /// <summary>The categories seen during training, sorted ordinal.</summary>
    public IReadOnlyList<string> Categories => _inner.Categories;

    /// <inheritdoc />
    public void Train(IEnumerable<SearchDocument> documents) => _inner.Train(documents);

    /// <summary>Folds one labelled document into the leaf it reaches, without rebuilding the tree.</summary>
    /// <param name="document">The labelled document.</param>
    /// <returns>A token that removes exactly this contribution.</returns>
    public long Learn(SearchDocument document) => _inner.Learn(document);

    /// <summary>Folds one labelled text into the leaf it reaches, without rebuilding the tree.</summary>
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

    /// <summary>Records a correction at the leaf a text reaches, without retraining.</summary>
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

    /// <summary>Clears every correction; the tree returns to its training distribution.</summary>
    public void ForgetReinforcement() => _inner.ForgetReinforcement();

    /// <summary>Returns the questions a text answered on its way to a leaf, and the leaf it reached.</summary>
    /// <param name="text">Raw text; tokenized internally with this model's tokenizer.</param>
    /// <returns>The path and outcome, or null when the model is untrained.</returns>
    public DecisionExplanation? Explain(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return _inner.Explain(new SearchDocument(string.Empty, text));
    }

    /// <summary>Renders the trained tree as indented text, or <c>(untrained)</c> before training.</summary>
    /// <returns>A deterministic, human-readable rendering of the questions and leaves.</returns>
    public string Describe() => _inner.Describe();

    internal string FeatureSpaceHash => _inner.FeatureSpaceHash;

    internal void WriteState(BinaryWriter writer) => _inner.WriteState(writer);

    internal static DecisionTreeClassifier ReadState(
        BinaryReader reader,
        IFeatureSource<SearchDocument>? features)
    {
        var (minLeafProbability, minLeafSupport) =
            DecisionTreeClassifier<SearchDocument>.ReadPredictionOptions(reader);
        var model = new DecisionTreeClassifier(
            features ?? new TextFeatureSource(),
            new DecisionTreeOptions
            {
                MinLeafProbability = minLeafProbability,
                MinLeafSupport = minLeafSupport,
            });

        model._inner.ReadBody(reader);
        return model;
    }
}
