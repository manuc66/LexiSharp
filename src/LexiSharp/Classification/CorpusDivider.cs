using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Classification;

/// <summary>A node of a corpus division: either a question or a cluster.</summary>
public abstract record DivisionNode;

/// <summary>
/// An internal node of a corpus division: the question that split this region of the corpus and
/// the two regions it produced.
/// </summary>
/// <param name="Question">The yes/no question asked at this node.</param>
/// <param name="TrueBranch">The region whose items answer yes.</param>
/// <param name="FalseBranch">The region whose items answer no.</param>
/// <param name="Metrics">The separation measured when this split was chosen.</param>
public sealed record DivisionBranch(
    FeatureQuestion Question,
    DivisionNode TrueBranch,
    DivisionNode FalseBranch,
    DivisionSplitMetrics Metrics) : DivisionNode;

/// <summary>A terminal region of the corpus.</summary>
/// <param name="Cluster">The items in the region and the features that represent them.</param>
public sealed record DivisionLeaf(CorpusCluster Cluster) : DivisionNode;

/// <summary>The measurements behind one chosen corpus split.</summary>
/// <param name="Separation">The balance-weighted mean squared difference of the child feature profiles.</param>
/// <param name="Balance">How evenly the split divided the region by weight, in <c>[0, 0.25]</c>.</param>
/// <param name="DocumentsTrue">How many items answered yes.</param>
/// <param name="DocumentsFalse">How many items answered no.</param>
/// <param name="WeightTrue">The summed sample weight of the items that answered yes.</param>
/// <param name="WeightFalse">The summed sample weight of the items that answered no.</param>
public sealed record DivisionSplitMetrics(
    double Separation,
    double Balance,
    int DocumentsTrue,
    int DocumentsFalse,
    double WeightTrue,
    double WeightFalse);

/// <summary>
/// One terminal region of a corpus division: its document ids, the features that best represent
/// it, and the depth at which it was found.
/// </summary>
/// <param name="DocumentIds">The items in the region, sorted ordinal.</param>
/// <param name="TopFeatures">The region's most representative features, by document frequency × idf.</param>
/// <param name="Depth">How many questions were needed to reach this region.</param>
/// <param name="Weight">The summed sample weight of the region.</param>
public sealed record CorpusCluster(
    IReadOnlyList<string> DocumentIds,
    IReadOnlyList<string> TopFeatures,
    int Depth,
    double Weight);

/// <summary>The tree and terminal clusters produced by <see cref="CorpusDivider{TItem}.Divide"/>.</summary>
public sealed class CorpusDivision
{
    internal CorpusDivision(DivisionNode? root, IReadOnlyList<CorpusCluster> clusters, int documentCount)
    {
        Root = root;
        Clusters = clusters;
        DocumentCount = documentCount;
    }

    /// <summary>The root of the division, or null for an empty corpus.</summary>
    public DivisionNode? Root { get; }

    /// <summary>The terminal regions, in tree order (yes branch before no branch).</summary>
    public IReadOnlyList<CorpusCluster> Clusters { get; }

    /// <summary>How many items were divided.</summary>
    public int DocumentCount { get; }

    /// <summary>Renders the division as indented text, or <c>(empty corpus)</c> when there is nothing to show.</summary>
    /// <returns>A deterministic, human-readable rendering of the questions and clusters.</returns>
    public string Describe()
    {
        if (Root is null)
            return "(empty corpus)";

        var builder = new StringBuilder();
        DescribeNode(Root, builder, indent: string.Empty, prefix: string.Empty);

        return builder.ToString();
    }

    private static void DescribeNode(DivisionNode node, StringBuilder builder, string indent, string prefix)
    {
        builder.Append(indent).Append(prefix);

        if (node is DivisionLeaf leaf)
        {
            builder.Append(leaf.Cluster.DocumentIds.Count.ToString(CultureInfo.InvariantCulture))
                .Append(" document(s)");

            if (leaf.Cluster.TopFeatures.Count > 0)
                builder.Append(": ").Append(string.Join(", ", leaf.Cluster.TopFeatures));

            builder.AppendLine();
            return;
        }

        var branch = (DivisionBranch)node;

        builder.Append(branch.Question.Describe())
            .Append(" (")
            .Append(branch.Metrics.DocumentsTrue.ToString(CultureInfo.InvariantCulture))
            .Append(" | ")
            .Append(branch.Metrics.DocumentsFalse.ToString(CultureInfo.InvariantCulture))
            .AppendLine(")");

        DescribeNode(branch.TrueBranch, builder, indent + "  ", "yes: ");
        DescribeNode(branch.FalseBranch, builder, indent + "  ", "no:  ");
    }
}

/// <summary>
/// Divides an unlabelled corpus over any feature space into two recursively: at each node it asks
/// the feature whose presence splits the items into the two most different feature profiles, so
/// the corpus is dichotomized by its most discriminative question first.
/// </summary>
/// <remarks>
/// <para>
/// This is the unsupervised half of the question tree. There are no labels: a split is scored by
/// how differently the two sides are described. For a candidate feature <c>f</c>, the items that
/// carry it and the items that do not become two weighted presence profiles, and the score is the
/// balance-weighted mean squared difference between those profiles — the between-group variance of
/// binary features. A rare feature that isolates one item scores low because the balance factor is
/// low, and a feature every item carries scores zero because the profiles are identical.
/// </para>
/// <para>
/// The score is a heuristic, not a statistical test and not a probability. It is deterministic:
/// ties are broken by ordinal feature order, so the same corpus and options always produce the
/// same tree. Candidate features and the compared vocabulary are capped by
/// <see cref="CorpusDividerOptions.MaxCandidateTerms"/> and
/// <see cref="CorpusDividerOptions.MaxVocabularyTerms"/>; a cluster's top features are computed
/// over the full cluster, not over that cap.
/// </para>
/// <para>
/// <b>Weights.</b> Every item carries a non-negative sample weight, by default <c>1</c>; the
/// profiles and the balance are weighted. A half-life is the caller's policy, not an option here:
/// the divider sees a number, not a clock.
/// </para>
/// </remarks>
/// <typeparam name="TItem">The item type being divided.</typeparam>
public sealed class CorpusDivider<TItem>
{
    /// <summary>
    /// Below this, a separation is floating-point dust rather than a split worth asking. It is the
    /// same threshold the ordinal tie-break uses, so "equal" and "useful" cannot disagree.
    /// </summary>
    private const double SplitFloor = 1e-12;

    private readonly IFeatureSource<TItem> _features;
    private readonly Func<TItem, string> _idOf;
    private readonly Func<TItem, double> _weightOf;
    private readonly CorpusDividerOptions _options;

    /// <summary>Creates a divider over a feature space.</summary>
    /// <param name="features">The feature space the divider reads.</param>
    /// <param name="idOf">Returns an item's stable id.</param>
    /// <param name="weightOf">Returns an item's non-negative sample weight; null means <c>1</c> for every item.</param>
    /// <param name="options">Tunable knobs; when null, <see cref="CorpusDividerOptions.Default"/> applies.</param>
    public CorpusDivider(
        IFeatureSource<TItem> features,
        Func<TItem, string> idOf,
        Func<TItem, double>? weightOf = null,
        CorpusDividerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(idOf);

        _features = features;
        _idOf = idOf;
        _weightOf = weightOf ?? (_ => 1.0);
        _options = options ?? CorpusDividerOptions.Default;

        if (!_options.IsValid)
            throw new ArgumentException("The corpus-divider options are not valid.", nameof(options));
    }

    /// <summary>Divides a corpus into recursively split regions. Categories, if present, are ignored.</summary>
    /// <param name="items">The corpus to divide.</param>
    /// <returns>The division tree and its terminal clusters.</returns>
    /// <exception cref="ArgumentException">An item id is null or empty, or a sample weight is negative or not finite.</exception>
    public CorpusDivision Divide(IEnumerable<TItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var documents = new List<CorpusItem>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in items)
        {
            string id = _idOf(item);

            if (string.IsNullOrEmpty(id))
                throw new ArgumentException("Item ids must be non-empty strings.", nameof(items));

            if (!ids.Add(id))
                throw new ArgumentException("Item ids must be unique.", nameof(items));

            double weight = _weightOf(item);

            if (!double.IsFinite(weight) || weight < 0)
                throw new ArgumentException("Sample weights must be finite and non-negative.", nameof(items));

            if (weight == 0)
                continue;

            documents.Add(new CorpusItem(id, _features.FeaturesOf(item), weight));
        }

        if (documents.Count == 0)
            return new CorpusDivision(null, Array.Empty<CorpusCluster>(), 0);

        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var document in documents)
        {
            foreach (string feature in document.Features.Features)
                documentFrequency[feature] = documentFrequency.TryGetValue(feature, out int count) ? count + 1 : 1;
        }

        var idf = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var (feature, frequency) in documentFrequency)
            idf[feature] = Math.Log(1 + ((double)documents.Count / frequency));

        var clusters = new List<CorpusCluster>();
        DivisionNode? root = Build(
            documents,
            depth: 0,
            new HashSet<string>(StringComparer.Ordinal),
            idf,
            clusters);

        return new CorpusDivision(root, clusters, documents.Count);
    }

    private DivisionNode Build(
        List<CorpusItem> documents,
        int depth,
        HashSet<string> used,
        Dictionary<string, double> idf,
        List<CorpusCluster> clusters)
    {
        int documentCount = documents.Count;

        if (depth >= _options.MaxDepth || documentCount < 2 * _options.MinDocumentsPerLeaf)
            return MakeLeaf(documents, depth, idf, clusters);

        var nodeFrequency = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var document in documents)
        {
            foreach (string feature in document.Features.Features)
                nodeFrequency[feature] = nodeFrequency.TryGetValue(feature, out int count) ? count + 1 : 1;
        }

        string[] profileFeatures = ProfileFeatures(nodeFrequency);

        if (profileFeatures.Length < 2)
            return MakeLeaf(documents, depth, idf, clusters);

        var profileIndex = new Dictionary<string, int>(profileFeatures.Length, StringComparer.Ordinal);

        for (int i = 0; i < profileFeatures.Length; i++)
            profileIndex[profileFeatures[i]] = i;

        List<string> candidates = CandidateFeatures(profileFeatures, nodeFrequency, used, documentCount);

        if (candidates.Count == 0)
            return MakeLeaf(documents, depth, idf, clusters);

        double[] totalWeights = TotalWeights(documents, profileIndex, out double nodeWeight);
        Dictionary<string, List<int>> postings = BuildPostings(documents, candidates);
        var (best, bestSeparation) = FindBestSplit(
            candidates,
            postings,
            documents,
            profileFeatures,
            profileIndex,
            totalWeights,
            nodeWeight);

        // A zero-separation split asks a question that changes nothing; MinSeparation = 0 must not
        // turn that into a split.
        if (best is null || bestSeparation < Math.Max(_options.MinSeparation, SplitFloor))
            return MakeLeaf(documents, depth, idf, clusters);

        SplitChoice choice = best.Value;
        var trueDocuments = new List<CorpusItem>(choice.DocumentsTrue);
        var falseDocuments = new List<CorpusItem>(choice.DocumentsFalse);

        foreach (var document in documents)
            (document.Features.Contains(choice.Feature) ? trueDocuments : falseDocuments).Add(document);

        var usedTrue = new HashSet<string>(used, StringComparer.Ordinal) { choice.Feature };
        var usedFalse = new HashSet<string>(used, StringComparer.Ordinal) { choice.Feature };

        var metrics = new DivisionSplitMetrics(
            choice.Separation,
            choice.Balance,
            choice.DocumentsTrue,
            choice.DocumentsFalse,
            choice.WeightTrue,
            choice.WeightFalse);

        return new DivisionBranch(
            new FeaturePresenceQuestion(choice.Feature),
            Build(trueDocuments, depth + 1, usedTrue, idf, clusters),
            Build(falseDocuments, depth + 1, usedFalse, idf, clusters),
            metrics);
    }

    private string[] ProfileFeatures(Dictionary<string, int> nodeFrequency) =>
        // The profile is local to the node. A global vocabulary would dilute a deep node with
        // features it does not contain, and would make its score depend on the cap rather than on
        // what is actually being separated here.
        nodeFrequency
            .Where(pair => pair.Value >= _options.MinDocumentFrequency)
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(_options.MaxVocabularyTerms)
            .Select(pair => pair.Key)
            .ToArray();

    private List<string> CandidateFeatures(
        string[] profileFeatures,
        Dictionary<string, int> nodeFrequency,
        HashSet<string> used,
        int documentCount) =>
        profileFeatures
            .Where(feature => !used.Contains(feature)
                && nodeFrequency[feature] >= _options.MinDocumentsPerLeaf
                && documentCount - nodeFrequency[feature] >= _options.MinDocumentsPerLeaf)
            .Take(_options.MaxCandidateTerms)
            .ToList();

    private static double[] TotalWeights(
        List<CorpusItem> documents,
        Dictionary<string, int> profileIndex,
        out double nodeWeight)
    {
        var totalWeights = new double[profileIndex.Count];
        nodeWeight = 0;

        foreach (var document in documents)
        {
            nodeWeight += document.Weight;

            foreach (string feature in document.Features.Features)
            {
                if (profileIndex.TryGetValue(feature, out int index))
                    totalWeights[index] += document.Weight;
            }
        }

        return totalWeights;
    }

    private static Dictionary<string, List<int>> BuildPostings(
        List<CorpusItem> documents,
        List<string> candidates)
    {
        var candidateSet = candidates.ToHashSet(StringComparer.Ordinal);
        var postings = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        for (int i = 0; i < documents.Count; i++)
        {
            foreach (string feature in documents[i].Features.Features)
            {
                if (!candidateSet.Contains(feature))
                    continue;

                if (!postings.TryGetValue(feature, out List<int>? indices))
                {
                    indices = new List<int>();
                    postings[feature] = indices;
                }

                indices.Add(i);
            }
        }

        return postings;
    }

    private (SplitChoice? Best, double Separation) FindBestSplit(
        List<string> candidates,
        Dictionary<string, List<int>> postings,
        List<CorpusItem> documents,
        string[] profileFeatures,
        Dictionary<string, int> profileIndex,
        double[] totalWeights,
        double nodeWeight)
    {
        SplitChoice? best = null;
        double bestSeparation = double.NegativeInfinity;

        foreach (string feature in candidates)
        {
            var scored = ScoreCandidate(
                feature,
                postings[feature],
                documents,
                profileFeatures,
                profileIndex,
                totalWeights,
                nodeWeight);

            if (scored is not { } candidate)
                continue;

            bool better = best is null
                || candidate.Separation > bestSeparation + SplitFloor
                || (Math.Abs(candidate.Separation - bestSeparation) <= SplitFloor
                    && string.CompareOrdinal(feature, best.Value.Feature) < 0);

            if (!better)
                continue;

            best = new SplitChoice(
                feature,
                candidate.Separation,
                candidate.Balance,
                candidate.DocumentsTrue,
                candidate.DocumentsFalse,
                candidate.WeightTrue,
                candidate.WeightFalse);
            bestSeparation = candidate.Separation;
        }

        return (best, bestSeparation);
    }

    private static CandidateScore? ScoreCandidate(
        string feature,
        List<int> indices,
        List<CorpusItem> documents,
        string[] profileFeatures,
        Dictionary<string, int> profileIndex,
        double[] totalWeights,
        double nodeWeight)
    {
        var trueWeights = new double[profileFeatures.Length];
        double weightTrue = 0;

        foreach (int index in indices)
        {
            var document = documents[index];
            weightTrue += document.Weight;

            foreach (string present in document.Features.Features)
            {
                if (profileIndex.TryGetValue(present, out int profile))
                    trueWeights[profile] += document.Weight;
            }
        }

        double weightFalse = nodeWeight - weightTrue;

        if (weightTrue <= 0 || weightFalse <= 0)
            return null;

        // The candidate's own profile is 1 against 0 on every split; counting it would add
        // the same constant to every score and make MinSeparation depend on it.
        int candidateIndex = profileIndex[feature];
        double sumSquared = 0;

        for (int i = 0; i < profileFeatures.Length; i++)
        {
            if (i == candidateIndex)
                continue;

            double probabilityTrue = trueWeights[i] / weightTrue;
            double probabilityFalse = (totalWeights[i] - trueWeights[i]) / weightFalse;
            double difference = probabilityTrue - probabilityFalse;
            sumSquared += difference * difference;
        }

        int compared = profileFeatures.Length - 1;
        double probability = weightTrue / nodeWeight;
        double balance = probability * (1 - probability);
        double separation = compared == 0 ? 0 : balance * sumSquared / compared;

        return new CandidateScore(
            separation,
            balance,
            indices.Count,
            documents.Count - indices.Count,
            weightTrue,
            weightFalse);
    }

    private readonly record struct CandidateScore(
        double Separation,
        double Balance,
        int DocumentsTrue,
        int DocumentsFalse,
        double WeightTrue,
        double WeightFalse);

    private DivisionLeaf MakeLeaf(
        List<CorpusItem> documents,
        int depth,
        Dictionary<string, double> idf,
        List<CorpusCluster> clusters)
    {
        string[] ids = documents
            .Select(document => document.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        string[] topFeatures = TopFeatures(documents, idf);
        double weight = 0;

        foreach (var document in documents)
            weight += document.Weight;

        var cluster = new CorpusCluster(
            Array.AsReadOnly(ids),
            Array.AsReadOnly(topFeatures),
            depth,
            weight);
        clusters.Add(cluster);

        return new DivisionLeaf(cluster);
    }

    private string[] TopFeatures(List<CorpusItem> documents, Dictionary<string, double> idf)
    {
        if (_options.TopFeaturesPerCluster == 0 || documents.Count == 0)
            return Array.Empty<string>();

        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var document in documents)
        {
            foreach (string feature in document.Features.Features)
                documentFrequency[feature] = documentFrequency.TryGetValue(feature, out int count) ? count + 1 : 1;
        }

        return documentFrequency
            .Select(pair => (
                Feature: pair.Key,
                Score: (double)pair.Value / documents.Count * idf.GetValueOrDefault(pair.Key)))
            .OrderByDescending(pair => pair.Score)
            .ThenBy(pair => pair.Feature, StringComparer.Ordinal)
            .Take(_options.TopFeaturesPerCluster)
            .Select(pair => pair.Feature)
            .ToArray();
    }

    private sealed record CorpusItem(string Id, IFeatureSet Features, double Weight);

    private readonly record struct SplitChoice(
        string Feature,
        double Separation,
        double Balance,
        int DocumentsTrue,
        int DocumentsFalse,
        double WeightTrue,
        double WeightFalse);
}

/// <summary>
/// The text convenience over <see cref="CorpusDivider{TItem}"/>: divides a
/// <see cref="SearchDocument"/> corpus by the terms of <see cref="SearchDocument.Text"/>.
/// </summary>
/// <remarks>
/// This is the shape most callers want when the only dimension is text. For several dimensions,
/// build a <see cref="CorpusDivider{TItem}"/> over your own item type with a
/// <see cref="CompositeFeatureSource{TItem}"/>.
/// </remarks>
public sealed class CorpusDivider
{
    private readonly CorpusDivider<SearchDocument> _inner;

    /// <summary>Creates a divider over the terms of <see cref="SearchDocument.Text"/>.</summary>
    /// <param name="tokenizer">Tokenizer used to reduce documents to terms (default: <see cref="Tokenizer.Default"/>).</param>
    /// <param name="options">Tunable knobs; when null, <see cref="CorpusDividerOptions.Default"/> applies.</param>
    public CorpusDivider(ITokenizer? tokenizer = null, CorpusDividerOptions? options = null)
        : this(new TextFeatureSource(tokenizer), options)
    {
    }

    /// <summary>Creates a divider over a caller-supplied document feature space.</summary>
    /// <param name="features">The feature space to read from a document.</param>
    /// <param name="options">Tunable knobs; when null, <see cref="CorpusDividerOptions.Default"/> applies.</param>
    public CorpusDivider(IFeatureSource<SearchDocument> features, CorpusDividerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(features);

        _inner = new CorpusDivider<SearchDocument>(
            features,
            document => document.Id,
            weightOf: null,
            options);
    }

    /// <summary>Divides a corpus into recursively split regions. Categories, if present, are ignored.</summary>
    /// <param name="documents">The corpus to divide.</param>
    /// <returns>The division tree and its terminal clusters.</returns>
    public CorpusDivision Divide(IEnumerable<SearchDocument> documents) => _inner.Divide(documents);
}
