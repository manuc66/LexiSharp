using System.Buffers;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
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

        var documents = new List<CorpusItem>(
            items.TryGetNonEnumeratedCount(out int capacity) ? capacity : 0);
        var ids = new HashSet<string>(capacity, StringComparer.Ordinal);
        var featureIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var featuresById = new List<string>();
        var documentFrequency = new List<int>();

        // The per-document id slices live in one growable buffer. A List<int> here would grow by
        // doubling and then hand over a second copy; a pooled array is grown and returned once.
        int[] flat = ArrayPool<int>.Shared.Rent(Math.Max(64, capacity * 8));
        int flatCount = 0;

        try
        {
            foreach (TItem item in items)
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

                IFeatureSet features = _features.FeaturesOf(item);
                int start = flatCount;

                // Resolve every feature to an integer once. The node walk, the profile, the
                // postings and the branch test then compare ints instead of hashing names; that
                // hashing was the cost of every one of those passes, on every document, at every
                // node.
                foreach (string feature in features.Features)
                {
                    if (!featureIds.TryGetValue(feature, out int featureId))
                    {
                        featureId = featuresById.Count;
                        featureIds[feature] = featureId;
                        featuresById.Add(feature);
                        documentFrequency.Add(0);
                    }

                    documentFrequency[featureId]++;

                    if (flatCount == flat.Length)
                        flat = Grow(flat, flatCount);

                    flat[flatCount++] = featureId;
                }

                documents.Add(new CorpusItem(id, start, flatCount - start, weight));
            }

            if (documents.Count == 0)
                return new CorpusDivision(null, Array.Empty<CorpusCluster>(), 0);

            // Ids are assigned in first-seen order, then reordered so id order is ordinal name
            // order. The split tie-break is "lowest feature name", which is then an int compare,
            // and the vocabulary array stays the only place a name is read.
            int vocabulary = featuresById.Count;
            var order = new int[vocabulary];

            for (int i = 0; i < vocabulary; i++)
                order[i] = i;

            Array.Sort(order, (left, right) => string.CompareOrdinal(featuresById[left], featuresById[right]));

            var sortedFeatures = new string[vocabulary];
            var remap = new int[vocabulary];

            for (int newId = 0; newId < vocabulary; newId++)
            {
                int oldId = order[newId];
                remap[oldId] = newId;
                sortedFeatures[newId] = featuresById[oldId];
            }

            for (int i = 0; i < flatCount; i++)
                flat[i] = remap[flat[i]];

            ReadOnlySpan<int> unsortedFrequency = CollectionsMarshal.AsSpan(documentFrequency);
            var frequency = new int[vocabulary];
            var idf = new double[vocabulary];

            for (int oldId = 0; oldId < vocabulary; oldId++)
                frequency[remap[oldId]] = unsortedFrequency[oldId];

            for (int id = 0; id < vocabulary; id++)
                idf[id] = Math.Log(1 + ((double)documents.Count / frequency[id]));

            var state = new BuildState(vocabulary, flat, sortedFeatures, idf);
            var clusters = new List<CorpusCluster>();
            DivisionNode? root = Build(documents, depth: 0, state, clusters);

            return new CorpusDivision(root, clusters, documents.Count);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(flat);
        }
    }

    private static int[] Grow(int[] array, int count)
    {
        int[] bigger = ArrayPool<int>.Shared.Rent(array.Length * 2);

        Array.Copy(array, bigger, count);
        ArrayPool<int>.Shared.Return(array);

        return bigger;
    }

    private DivisionNode Build(
        List<CorpusItem> documents,
        int depth,
        BuildState state,
        List<CorpusCluster> clusters)
    {
        int documentCount = documents.Count;

        if (depth >= _options.MaxDepth || documentCount < 2 * _options.MinDocumentsPerLeaf)
            return MakeLeaf(documents, depth, state, clusters);

        int[] frequency = state.NodeFrequency;
        List<int> touched = state.Touched;
        touched.Clear();

        foreach (CorpusItem document in documents)
        {
            foreach (int id in state.Ids.AsSpan(document.Start, document.Count))
            {
                if (frequency[id] == 0)
                    touched.Add(id);

                frequency[id]++;
            }
        }

        int profileCount = SelectProfile(state);

        if (profileCount < 2)
            return ReleaseAndLeaf(state, documents, depth, profileCount, clusters);

        EnsureCapacity(ref state.CandidateIds, Math.Min(profileCount, _options.MaxCandidateTerms));
        int candidateCount = SelectCandidates(state, profileCount, documentCount);

        if (candidateCount == 0)
            return ReleaseAndLeaf(state, documents, depth, profileCount, clusters);

        EnsureCapacity(ref state.TotalWeights, profileCount);
        EnsureCapacity(ref state.TrueWeights, profileCount);
        EnsureCapacity(ref state.PostingOffsets, candidateCount + 1);
        EnsureCapacity(ref state.PostingCursors, candidateCount);
        double nodeWeight = TotalWeights(documents, state, profileCount);

        BuildPostings(documents, state, frequency, candidateCount);

        var (best, bestSeparation) = FindBestSplit(
            documents,
            state,
            profileCount,
            candidateCount,
            nodeWeight);

        // A zero-separation split asks a question that changes nothing; MinSeparation = 0 must not
        // turn that into a split.
        if (best is null || bestSeparation < Math.Max(_options.MinSeparation, SplitFloor))
            return ReleaseAndLeaf(state, documents, depth, profileCount, clusters);

        SplitChoice choice = best.Value;
        var trueDocuments = new List<CorpusItem>(choice.DocumentsTrue);
        var falseDocuments = new List<CorpusItem>(choice.DocumentsFalse);

        foreach (CorpusItem document in documents)
        {
            (ContainsId(state.Ids.AsSpan(document.Start, document.Count), choice.FeatureId)
                ? trueDocuments
                : falseDocuments).Add(document);
        }

        // The children read the same node-frequency and profile-slot buffers, so they are clean
        // before the recursion instead of being cleared after it.
        ReleaseNode(state, profileCount);

        // Both children exclude the same path features, so one path is pushed and popped around
        // the two recursive builds instead of copying a set once per branch.
        state.UsedPath.Add(choice.FeatureId);

        try
        {
            DivisionNode trueBranch = Build(trueDocuments, depth + 1, state, clusters);
            DivisionNode falseBranch = Build(falseDocuments, depth + 1, state, clusters);

            var metrics = new DivisionSplitMetrics(
                choice.Separation,
                choice.Balance,
                choice.DocumentsTrue,
                choice.DocumentsFalse,
                choice.WeightTrue,
                choice.WeightFalse);

            return new DivisionBranch(
                new FeaturePresenceQuestion(choice.Feature),
                trueBranch,
                falseBranch,
                metrics);
        }
        finally
        {
            state.UsedPath.RemoveAt(state.UsedPath.Count - 1);
        }
    }

    private DivisionLeaf ReleaseAndLeaf(
        BuildState state,
        List<CorpusItem> documents,
        int depth,
        int profileCount,
        List<CorpusCluster> clusters)
    {
        ReleaseNode(state, profileCount);

        return MakeLeaf(documents, depth, state, clusters);
    }

    private int SelectProfile(BuildState state)
    {
        // The profile is local to the node. A global vocabulary would dilute a deep node with
        // features it does not contain, and would make its score depend on the cap rather than on
        // what is actually being separated here.
        int[] frequency = state.NodeFrequency;
        int[] profile = state.ProfileIds;
        int count = 0;

        foreach (int id in state.Touched)
        {
            if (frequency[id] >= _options.MinDocumentFrequency)
                profile[count++] = id;
        }

        if (count > 1)
            Array.Sort(profile, 0, count, state.ProfileOrder);

        int take = Math.Min(count, _options.MaxVocabularyTerms);

        // Profile order is count descending, then name ordinal, which is id order.
        for (int slot = 0; slot < take; slot++)
            state.SlotOfId[profile[slot]] = slot;

        return take;
    }

    private int SelectCandidates(BuildState state, int profileCount, int documentCount)
    {
        int[] profile = state.ProfileIds;
        int[] frequency = state.NodeFrequency;
        int[] candidates = state.CandidateIds;
        List<int> used = state.UsedPath;
        int count = 0;

        Array.Fill(state.CandidateOfSlot, -1, 0, profileCount);

        foreach (int id in profile.AsSpan(0, profileCount))
        {
            if (count == _options.MaxCandidateTerms)
                break;

            int present = frequency[id];

            if (used.Contains(id)
                || present < _options.MinDocumentsPerLeaf
                || documentCount - present < _options.MinDocumentsPerLeaf)
                continue;

            state.CandidateOfSlot[state.SlotOfId[id]] = count;
            candidates[count++] = id;
        }

        return count;
    }

    private static double TotalWeights(
        List<CorpusItem> documents,
        BuildState state,
        int profileCount)
    {
        double[] totalWeights = state.TotalWeights;
        Array.Clear(totalWeights, 0, profileCount);
        double nodeWeight = 0;

        foreach (CorpusItem document in documents)
        {
            nodeWeight += document.Weight;

            foreach (int id in state.Ids.AsSpan(document.Start, document.Count))
            {
                int slot = state.SlotOfId[id];

                if (slot >= 0)
                    totalWeights[slot] += document.Weight;
            }
        }

        return nodeWeight;
    }

    private static void BuildPostings(
        List<CorpusItem> documents,
        BuildState state,
        int[] frequency,
        int candidateCount)
    {
        int[] offsets = state.PostingOffsets;
        int[] cursors = state.PostingCursors;
        int[] candidates = state.CandidateIds;

        offsets[0] = 0;

        for (int candidate = 0; candidate < candidateCount; candidate++)
        {
            offsets[candidate + 1] = offsets[candidate] + frequency[candidates[candidate]];
            cursors[candidate] = offsets[candidate];
        }

        EnsureCapacity(ref state.PostingIndices, offsets[candidateCount]);
        int[] positions = state.PostingIndices;

        for (int i = 0; i < documents.Count; i++)
        {
            CorpusItem document = documents[i];

            foreach (int id in state.Ids.AsSpan(document.Start, document.Count))
            {
                int slot = state.SlotOfId[id];

                if (slot < 0)
                    continue;

                int candidate = state.CandidateOfSlot[slot];

                if (candidate >= 0)
                    positions[cursors[candidate]++] = i;
            }
        }
    }

    private (SplitChoice? Best, double Separation) FindBestSplit(
        List<CorpusItem> documents,
        BuildState state,
        int profileCount,
        int candidateCount,
        double nodeWeight)
    {
        SplitChoice? best = null;
        double bestSeparation = double.NegativeInfinity;
        double[] totalWeights = state.TotalWeights;
        double[] trueWeights = state.TrueWeights;
        int[] offsets = state.PostingOffsets;
        int[] positions = state.PostingIndices;

        for (int candidate = 0; candidate < candidateCount; candidate++)
        {
            int featureId = state.CandidateIds[candidate];
            int start = offsets[candidate];
            int end = offsets[candidate + 1];
            double weightTrue = 0;

            Array.Clear(trueWeights, 0, profileCount);

            for (int p = start; p < end; p++)
            {
                CorpusItem document = documents[positions[p]];
                weightTrue += document.Weight;

                foreach (int id in state.Ids.AsSpan(document.Start, document.Count))
                {
                    int slot = state.SlotOfId[id];

                    if (slot >= 0)
                        trueWeights[slot] += document.Weight;
                }
            }

            double weightFalse = nodeWeight - weightTrue;

            if (weightTrue <= 0 || weightFalse <= 0)
                continue;

            // The candidate's own profile is 1 against 0 on every split; counting it would add
            // the same constant to every score and make MinSeparation depend on it.
            int candidateIndex = state.SlotOfId[featureId];
            double sumSquared = 0;

            for (int i = 0; i < profileCount; i++)
            {
                if (i == candidateIndex)
                    continue;

                double probabilityTrue = trueWeights[i] / weightTrue;
                double probabilityFalse = (totalWeights[i] - trueWeights[i]) / weightFalse;
                double difference = probabilityTrue - probabilityFalse;
                sumSquared += difference * difference;
            }

            int compared = profileCount - 1;
            double probability = weightTrue / nodeWeight;
            double balance = probability * (1 - probability);
            double separation = compared == 0 ? 0 : balance * sumSquared / compared;

            bool better = best is null
                || separation > bestSeparation + SplitFloor
                || (Math.Abs(separation - bestSeparation) <= SplitFloor
                    && featureId < best.Value.FeatureId);

            if (!better)
                continue;

            int documentsTrue = end - start;
            best = new SplitChoice(
                featureId,
                state.FeaturesById[featureId],
                separation,
                balance,
                documentsTrue,
                documents.Count - documentsTrue,
                weightTrue,
                weightFalse);
            bestSeparation = separation;
        }

        return (best, bestSeparation);
    }

    private static void ReleaseNode(BuildState state, int profileCount)
    {
        int[] profile = state.ProfileIds;

        for (int slot = 0; slot < profileCount; slot++)
            state.SlotOfId[profile[slot]] = -1;

        List<int> touched = state.Touched;

        foreach (int id in touched)
            state.NodeFrequency[id] = 0;

        touched.Clear();
    }

    private static bool ContainsId(ReadOnlySpan<int> ids, int featureId)
    {
        foreach (int id in ids)
        {
            if (id == featureId)
                return true;
        }

        return false;
    }

    private static void EnsureCapacity<T>(ref T[] array, int length)
    {
        if (array.Length < length)
            array = new T[Math.Max(length, array.Length * 2)];
    }

    private DivisionLeaf MakeLeaf(
        List<CorpusItem> documents,
        int depth,
        BuildState state,
        List<CorpusCluster> clusters)
    {
        var ids = new string[documents.Count];
        double weight = 0;

        for (int i = 0; i < documents.Count; i++)
        {
            ids[i] = documents[i].Id;
            weight += documents[i].Weight;
        }

        Array.Sort(ids, StringComparer.Ordinal);
        string[] topFeatures = TopFeatures(documents, state);

        var cluster = new CorpusCluster(
            Array.AsReadOnly(ids),
            Array.AsReadOnly(topFeatures),
            depth,
            weight);
        clusters.Add(cluster);

        return new DivisionLeaf(cluster);
    }

    private string[] TopFeatures(List<CorpusItem> documents, BuildState state)
    {
        if (_options.TopFeaturesPerCluster == 0 || documents.Count == 0)
            return Array.Empty<string>();

        int[] frequency = state.NodeFrequency;
        List<int> touched = state.Touched;
        touched.Clear();

        foreach (CorpusItem document in documents)
        {
            foreach (int id in state.Ids.AsSpan(document.Start, document.Count))
            {
                if (frequency[id] == 0)
                    touched.Add(id);

                frequency[id]++;
            }
        }

        var scored = new List<(int FeatureId, double Score)>(touched.Count);

        foreach (int id in touched)
        {
            scored.Add((
                id,
                ((double)frequency[id] / documents.Count) * state.Idf[id]));
        }

        scored.Sort(static (left, right) =>
        {
            int byScore = right.Score.CompareTo(left.Score);

            return byScore != 0 ? byScore : left.FeatureId.CompareTo(right.FeatureId);
        });

        int take = Math.Min(_options.TopFeaturesPerCluster, scored.Count);
        var top = new string[take];

        for (int i = 0; i < take; i++)
            top[i] = state.FeaturesById[scored[i].FeatureId];

        foreach (int id in touched)
            frequency[id] = 0;

        touched.Clear();

        return top;
    }

    private sealed class BuildState
    {
        public BuildState(int vocabulary, int[] ids, string[] featuresById, double[] idf)
        {
            Ids = ids;
            FeaturesById = featuresById;
            Idf = idf;
            NodeFrequency = new int[vocabulary];
            SlotOfId = new int[vocabulary];
            Array.Fill(SlotOfId, -1);
            ProfileIds = new int[vocabulary];
            CandidateOfSlot = new int[vocabulary];
            ProfileOrder = new FrequencyComparer(NodeFrequency);
        }

        public int[] Ids { get; }

        public string[] FeaturesById { get; }

        public double[] Idf { get; }

        public int[] NodeFrequency { get; }

        public int[] SlotOfId { get; }

        public int[] ProfileIds { get; }

        public int[] CandidateOfSlot { get; }

        public int[] CandidateIds = Array.Empty<int>();

        public double[] TotalWeights = Array.Empty<double>();

        public double[] TrueWeights = Array.Empty<double>();

        public int[] PostingOffsets = Array.Empty<int>();

        public int[] PostingCursors = Array.Empty<int>();

        public int[] PostingIndices = Array.Empty<int>();

        public List<int> Touched { get; } = new();

        public List<int> UsedPath { get; } = new();

        public FrequencyComparer ProfileOrder { get; }
    }

    private sealed class FrequencyComparer(int[] frequency) : IComparer<int>
    {
        public int Compare(int left, int right)
        {
            int byFrequency = frequency[right].CompareTo(frequency[left]);

            return byFrequency != 0 ? byFrequency : left.CompareTo(right);
        }
    }

    private sealed record CorpusItem(string Id, int Start, int Count, double Weight);

    private readonly record struct SplitChoice(
        int FeatureId,
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
