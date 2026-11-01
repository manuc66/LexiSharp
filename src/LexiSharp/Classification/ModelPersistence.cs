using System.Security.Cryptography;
using System.Text;
using LexiSharp.Core;

namespace LexiSharp.Classification;

/// <summary>
/// Saves and loads trained text classifiers in a versioned, dependency-free binary format.
/// </summary>
/// <remarks>
/// <para>
/// The format carries the trained state only: the tree structure and its questions, the forest's
/// trees, or the linear model's coefficients, together with the vocabulary, the prediction options
/// and both ledgers (corrections and learned tokens). It does not carry the
/// <see cref="IFeatureSource{TItem}"/> or the tokenizer, because a delegate is not data; load with
/// the same source configuration you trained with. The stored feature-space hash identifies the
/// vocabulary for your own bookkeeping and is verified on load, so a corrupted file is rejected
/// rather than silently misread.
/// </para>
/// <para>
/// The format is little-endian and starts with the magic <c>LXCL</c>, an <see cref="int"/> version,
/// a kind byte and the feature-space hash. <see cref="CurrentVersion"/> is the only version this
/// build reads; a mismatch throws <see cref="InvalidDataException"/> instead of guessing. Version 2
/// added the linear model's virtual categories and its correction options; a version-1 file is
/// refused rather than guessed, and the 0.x migration is to retrain and save again.
/// </para>
/// </remarks>
public static class ClassificationModelSerializer
{
    /// <summary>The only format version this build reads.</summary>
    public const int CurrentVersion = 2;

    private static ReadOnlySpan<byte> Magic => "LXCL"u8;

    private enum ModelKind : byte
    {
        DecisionTree = 1,
        RandomForest = 2,
        Linear = 3,
        Calibrated = 4,
        Centroid = 5,
    }

    /// <summary>Saves a trained model.</summary>
    /// <param name="model">The model to save.</param>
    /// <param name="destination">The stream to write to; it is left open.</param>
    public static void Save(ITextClassifier model, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(destination);

        using var writer = new BinaryWriter(destination, Encoding.UTF8, leaveOpen: true);
        WriteEnvelope(writer, model);
    }

    /// <summary>Saves a trained decision tree.</summary>
    /// <param name="model">The model to save.</param>
    /// <param name="destination">The stream to write to; it is left open.</param>
    public static void Save(DecisionTreeClassifier model, Stream destination) =>
        Save((ITextClassifier)model, destination);

    /// <summary>Saves a trained forest.</summary>
    /// <param name="model">The model to save.</param>
    /// <param name="destination">The stream to write to; it is left open.</param>
    public static void Save(RandomForestClassifier model, Stream destination) =>
        Save((ITextClassifier)model, destination);

    /// <summary>Saves a trained linear model.</summary>
    /// <param name="model">The model to save.</param>
    /// <param name="destination">The stream to write to; it is left open.</param>
    public static void Save(LinearClassifier model, Stream destination) =>
        Save((ITextClassifier)model, destination);

    /// <summary>Saves a calibrated model and the model it wraps.</summary>
    /// <param name="model">The model to save.</param>
    /// <param name="destination">The stream to write to; it is left open.</param>
    public static void Save(CalibratedClassifier model, Stream destination) =>
        Save((ITextClassifier)model, destination);

    /// <summary>Saves a centroid model.</summary>
    /// <param name="model">The model to save.</param>
    /// <param name="destination">The stream to write to; it is left open.</param>
    public static void Save(CentroidClassifier model, Stream destination) =>
        Save((ITextClassifier)model, destination);

    /// <summary>Loads a model, dispatching on the stored kind.</summary>
    /// <param name="source">The stream to read; it is left open.</param>
    /// <param name="features">The feature source to pair with the loaded state; null means a default <see cref="TextFeatureSource"/>.</param>
    /// <param name="embeddings">
    /// The embedding provider to pair with a stored centroid model. It is not stored in the file,
    /// exactly as the tokenizer is not: a provider is a delegate, not data.
    /// </param>
    /// <returns>The loaded model, typed as <see cref="ITextClassifier"/>.</returns>
    public static ITextClassifier Load(
        Stream source,
        IFeatureSource<SearchDocument>? features = null,
        IEmbeddingProvider? embeddings = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
        return ReadEnvelope(reader, features, embeddings);
    }

    /// <summary>Loads a centroid model, pairing its stored state with the caller's provider.</summary>
    /// <param name="source">The stream to read; it is left open.</param>
    /// <param name="embeddings">The embedding provider the loaded model will call.</param>
    /// <returns>The loaded centroid model.</returns>
    public static CentroidClassifier LoadCentroid(Stream source, IEmbeddingProvider embeddings)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(embeddings);

        using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
        ReadHeader(reader, ModelKind.Centroid, out _);
        return CentroidClassifier.ReadState(reader, embeddings);
    }

    /// <summary>Loads a decision tree.</summary>
    /// <param name="source">The stream to read; it is left open.</param>
    /// <param name="features">The feature source to pair with the loaded state; null means a default <see cref="TextFeatureSource"/>.</param>
    /// <returns>The loaded tree.</returns>
    public static DecisionTreeClassifier LoadDecisionTree(
        Stream source,
        IFeatureSource<SearchDocument>? features = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
        ReadHeader(reader, ModelKind.DecisionTree, out string hash);
        var model = DecisionTreeClassifier.ReadState(reader, features);
        VerifyHash(hash, model.FeatureSpaceHash);

        return model;
    }

    /// <summary>Loads a forest.</summary>
    /// <param name="source">The stream to read; it is left open.</param>
    /// <param name="features">The feature source to pair with the loaded state; null means a default <see cref="TextFeatureSource"/>.</param>
    /// <returns>The loaded forest.</returns>
    public static RandomForestClassifier LoadRandomForest(
        Stream source,
        IFeatureSource<SearchDocument>? features = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
        ReadHeader(reader, ModelKind.RandomForest, out string hash);
        var model = RandomForestClassifier.ReadState(reader, features);
        VerifyHash(hash, model.FeatureSpaceHash);

        return model;
    }

    /// <summary>Loads a linear model.</summary>
    /// <param name="source">The stream to read; it is left open.</param>
    /// <param name="features">The feature source to pair with the loaded state; null means a default <see cref="TextFeatureSource"/>.</param>
    /// <returns>The loaded linear model.</returns>
    public static LinearClassifier LoadLinear(
        Stream source,
        IFeatureSource<SearchDocument>? features = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
        ReadHeader(reader, ModelKind.Linear, out string hash);
        var model = LinearClassifier.ReadState(reader, features);
        VerifyHash(hash, model.FeatureSpaceHash);

        return model;
    }

    /// <summary>Loads a calibrated model and the model it wraps.</summary>
    /// <param name="source">The stream to read; it is left open.</param>
    /// <param name="features">The feature source to pair with the loaded state; null means a default <see cref="TextFeatureSource"/>.</param>
    /// <param name="embeddings">The embedding provider to pair with a stored centroid inner model, when there is one.</param>
    /// <returns>The loaded calibrated model.</returns>
    public static CalibratedClassifier LoadCalibrated(
        Stream source,
        IFeatureSource<SearchDocument>? features = null,
        IEmbeddingProvider? embeddings = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
        ReadHeader(reader, ModelKind.Calibrated, out _);
        double temperature = reader.ReadDouble();
        ITextClassifier inner = ReadEnvelope(reader, features, embeddings);

        return new CalibratedClassifier(inner, new TemperatureCalibrator(temperature));
    }

    private static void WriteEnvelope(BinaryWriter writer, ITextClassifier model)
    {
        switch (model)
        {
            case DecisionTreeClassifier tree:
                WriteHeader(writer, ModelKind.DecisionTree, tree.FeatureSpaceHash);
                tree.WriteState(writer);
                break;

            case RandomForestClassifier forest:
                WriteHeader(writer, ModelKind.RandomForest, forest.FeatureSpaceHash);
                forest.WriteState(writer);
                break;

            case LinearClassifier linear:
                WriteHeader(writer, ModelKind.Linear, linear.FeatureSpaceHash);
                linear.WriteState(writer);
                break;

            case CalibratedClassifier calibrated:
                WriteHeader(writer, ModelKind.Calibrated, string.Empty);
                writer.Write(calibrated.Temperature);
                WriteEnvelope(writer, calibrated.Inner);
                break;

            case CentroidClassifier centroid:
                WriteHeader(writer, ModelKind.Centroid, string.Empty);
                centroid.WriteState(writer);
                break;

            case FusedTextClassifier:
            case BlendTextClassifier:
                throw new NotSupportedException(
                    "A blended classifier is a graph of models and evidence sources; save the lanes individually.");

            case NearestNeighborClassifier:
                throw new NotSupportedException(
                    "A nearest-neighbour model stores its corpus; persistence is not implemented in v1.");

            default:
                throw new NotSupportedException(
                    $"No persistence format exists for {model.GetType().Name}.");
        }
    }

    private static ITextClassifier ReadEnvelope(
        BinaryReader reader,
        IFeatureSource<SearchDocument>? features,
        IEmbeddingProvider? embeddings)
    {
        ModelKind kind = ReadHeader(reader, expected: null, out string hash);

        ITextClassifier model;
        string actual;

        switch (kind)
        {
            case ModelKind.DecisionTree:
                model = DecisionTreeClassifier.ReadState(reader, features);
                actual = ((DecisionTreeClassifier)model).FeatureSpaceHash;
                break;

            case ModelKind.RandomForest:
                model = RandomForestClassifier.ReadState(reader, features);
                actual = ((RandomForestClassifier)model).FeatureSpaceHash;
                break;

            case ModelKind.Linear:
                model = LinearClassifier.ReadState(reader, features);
                actual = ((LinearClassifier)model).FeatureSpaceHash;
                break;

            case ModelKind.Calibrated:
                double temperature = reader.ReadDouble();
                ITextClassifier inner = ReadEnvelope(reader, features, embeddings);
                return new CalibratedClassifier(inner, new TemperatureCalibrator(temperature));

            case ModelKind.Centroid:
                if (embeddings is null)
                {
                    throw new InvalidOperationException(
                        "A centroid model needs an embedding provider to load; pass one to Load or use LoadCentroid.");
                }

                model = CentroidClassifier.ReadState(reader, embeddings);
                actual = string.Empty;
                break;

            default:
                throw new InvalidDataException($"Unknown model kind {(byte)kind}.");
        }

        VerifyHash(hash, actual);
        return model;
    }

    private static void WriteHeader(BinaryWriter writer, ModelKind kind, string featureSpaceHash)
    {
        writer.Write(Magic);
        writer.Write(CurrentVersion);
        writer.Write((byte)kind);
        ModelFormat.WriteString(writer, featureSpaceHash);
    }

    private static ModelKind ReadHeader(BinaryReader reader, ModelKind? expected, out string hash)
    {
        byte[] magic = reader.ReadBytes(Magic.Length);

        if (!magic.AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("The stream is not a LexiSharp classification model.");

        int version = reader.ReadInt32();

        if (version != CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported classification-model version {version}; this build reads {CurrentVersion}.");
        }

        var kind = (ModelKind)reader.ReadByte();

        if (expected is { } wanted && kind != wanted)
            throw new InvalidDataException($"Expected a {wanted} model, found {kind}.");

        hash = ModelFormat.ReadString(reader) ?? string.Empty;
        return kind;
    }

    private static void VerifyHash(string stored, string actual)
    {
        if (stored.Length > 0 && !string.Equals(stored, actual, StringComparison.Ordinal))
            throw new InvalidDataException("The stored feature-space hash does not match the model.");
    }
}

internal static class ModelFormat
{
    private const int MaximumCollectionSize = 10_000_000;
    private const int MaximumStringLength = 64 * 1024 * 1024;

    internal static string Hash(IReadOnlyList<string> features)
        {
            var builder = new StringBuilder();

            foreach (string feature in features)
            {
                builder.Append(feature);
                builder.Append('\n');
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
        }

        internal static void WriteString(BinaryWriter writer, string? value)
        {
            if (value is null)
            {
                writer.Write(-1);
                return;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(value);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        internal static string? ReadString(BinaryReader reader)
        {
            int length = reader.ReadInt32();

            if (length < 0)
                return null;

            if (length > MaximumStringLength)
                throw new InvalidDataException($"A string is too long ({length} bytes).");

            byte[] bytes = reader.ReadBytes(length);

            if (bytes.Length != length)
                throw new EndOfStreamException("The stream ended inside a string.");

            return Encoding.UTF8.GetString(bytes);
        }

        internal static void WriteStrings(BinaryWriter writer, IReadOnlyList<string> values)
        {
            writer.Write(values.Count);

            foreach (string value in values)
                WriteString(writer, value);
        }

        internal static string[] ReadStrings(BinaryReader reader)
        {
            int count = ReadCount(reader);
            var values = new string[count];

            for (int i = 0; i < count; i++)
                values[i] = ReadString(reader) ?? throw new InvalidDataException("A string is null.");

            return values;
        }

        internal static void WriteDoubles(BinaryWriter writer, IReadOnlyList<double> values)
        {
            writer.Write(values.Count);

            foreach (double value in values)
                writer.Write(value);
        }

        internal static double[] ReadDoubles(BinaryReader reader)
        {
            int count = ReadCount(reader);
            var values = new double[count];

            for (int i = 0; i < count; i++)
                values[i] = reader.ReadDouble();

            return values;
        }

        internal static int ReadCount(BinaryReader reader)
        {
            int count = reader.ReadInt32();

            if (count < 0 || count > MaximumCollectionSize)
                throw new InvalidDataException($"An invalid collection size was read ({count}).");

            return count;
        }

        internal static void WritePath(BinaryWriter writer, IReadOnlyList<bool> path)
        {
            writer.Write(path.Count);

            foreach (bool answer in path)
                writer.Write((byte)(answer ? 1 : 0));
        }

        internal static bool[] ReadPath(BinaryReader reader)
        {
            int length = ReadCount(reader);

            if (length > 4096)
                throw new InvalidDataException("A path is too long.");

            var path = new bool[length];

            for (int step = 0; step < length; step++)
            {
                byte answer = reader.ReadByte();

                if (answer > 1)
                    throw new InvalidDataException("A path is not a bit sequence.");

                path[step] = answer == 1;
            }

            return path;
        }

        internal static void WriteNode(BinaryWriter writer, DecisionTreeNode? node)
        {
            switch (node)
            {
                case null:
                    writer.Write((byte)0);
                    break;

                case DecisionLeaf leaf:
                    writer.Write((byte)1);
                    WriteString(writer, leaf.Category);
                    writer.Write(leaf.DocumentCount);
                    writer.Write(leaf.Weight);
                    WriteDoubles(writer, leaf.RawCounts);

                    var probabilities = leaf.Probabilities
                        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .ToArray();
                    writer.Write(probabilities.Length);

                    foreach (var pair in probabilities)
                    {
                        WriteString(writer, pair.Key);
                        writer.Write(pair.Value);
                    }

                    break;

                case DecisionBranch branch:
                    string feature = branch.Question switch
                    {
                        FeaturePresenceQuestion presence => presence.Feature,
                        NumericThresholdQuestion numeric => numeric.Feature,
                        _ => throw new NotSupportedException(
                            $"No persistence format exists for {branch.Question.GetType().Name}."),
                    };

                    writer.Write((byte)2);
                    WriteString(writer, feature);
                    writer.Write(branch.Question is NumericThresholdQuestion);
                    writer.Write(branch.Question is NumericThresholdQuestion threshold ? threshold.Threshold : 0);
                    WriteMetrics(writer, branch.Metrics);
                    WriteNode(writer, branch.TrueBranch);
                    WriteNode(writer, branch.FalseBranch);
                    break;

                default:
                    throw new NotSupportedException(
                        $"No persistence format exists for {node.GetType().Name}.");
            }
        }

        internal static DecisionTreeNode? ReadNode(BinaryReader reader)
        {
            byte tag = reader.ReadByte();

            switch (tag)
            {
                case 0:
                    return null;

                case 1:
                    string category = ReadString(reader) ?? throw new InvalidDataException("A leaf has no category.");
                    int documentCount = reader.ReadInt32();
                    double weight = reader.ReadDouble();
                    double[] rawCounts = ReadDoubles(reader);
                    int probabilityCount = ReadCount(reader);
                    var probabilities = new Dictionary<string, double>(probabilityCount, StringComparer.Ordinal);

                    for (int i = 0; i < probabilityCount; i++)
                    {
                        string name = ReadString(reader) ?? throw new InvalidDataException("A probability has no category.");
                        probabilities[name] = reader.ReadDouble();
                    }

                    return new DecisionLeaf(category, probabilities, documentCount, weight)
                    {
                        RawCounts = rawCounts,
                    };

                case 2:
                    string feature = ReadString(reader) ?? throw new InvalidDataException("A question has no feature.");
                    bool numeric = reader.ReadBoolean();
                    double threshold = reader.ReadDouble();
                    DecisionSplitMetrics metrics = ReadMetrics(reader);
                    DecisionTreeNode? trueBranch = ReadNode(reader);
                    DecisionTreeNode? falseBranch = ReadNode(reader);

                    if (trueBranch is null || falseBranch is null)
                        throw new InvalidDataException("A branch has a missing subtree.");

                    FeatureQuestion question = numeric
                        ? new NumericThresholdQuestion(feature, threshold)
                        : new FeaturePresenceQuestion(feature);

                    return new DecisionBranch(question, trueBranch, falseBranch, metrics);

                default:
                    throw new InvalidDataException($"Unknown tree-node tag {tag}.");
            }
        }

        private static void WriteMetrics(BinaryWriter writer, DecisionSplitMetrics metrics)
        {
            writer.Write(metrics.Gain);
            writer.Write(metrics.GainRatio);
            writer.Write(metrics.ImpurityBefore);
            writer.Write(metrics.ImpurityAfter);
            writer.Write(metrics.DocumentsTrue);
            writer.Write(metrics.DocumentsFalse);
            writer.Write(metrics.WeightTrue);
            writer.Write(metrics.WeightFalse);
            writer.Write(metrics.Cost);
        }

        private static DecisionSplitMetrics ReadMetrics(BinaryReader reader) =>
            new(
                reader.ReadDouble(),
                reader.ReadDouble(),
                reader.ReadDouble(),
                reader.ReadDouble(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadDouble(),
                reader.ReadDouble(),
                reader.ReadDouble());
    }
