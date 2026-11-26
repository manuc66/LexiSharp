using System.Security.Cryptography;
using System.Text;

namespace LexiSharp.Adaptive;

/// <summary>Reads and writes the versioned <c>LXPR</c> preference snapshot.</summary>
/// <remarks>
/// The layout is little-endian: <c>LXPR</c>, the version, the options, the vocabulary (empty in
/// hashed mode), the category weight vectors, the snapshot's canonical signature, and a trailing
/// SHA-256 of every preceding byte. A bad magic, an unknown version, a negative count, a
/// non-finite weight or a signature mismatch throws <see cref="InvalidDataException"/>.
/// </remarks>
public static class PreferenceSnapshotSerializer
{
    private const int MaxFeaturesHard = 1_000_000;
    private const int MaxCategoriesHard = 100_000;
    private const int MaxStringBytes = 1_048_576;

    private static ReadOnlySpan<byte> Magic => "LXPR"u8;

    /// <summary>Writes a preference snapshot and its trailing integrity hash.</summary>
    /// <param name="snapshot">The snapshot to write.</param>
    /// <param name="destination">The stream to write to; it is left open.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static void Save(PreferenceSnapshot snapshot, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(destination);

        using var body = new MemoryStream();
        WriteBody(snapshot, body);

        byte[] bytes = body.ToArray();
        destination.Write(bytes);
        destination.Write(SHA256.HashData(bytes));
    }

    /// <summary>Reads a preference snapshot and verifies its trailing hash.</summary>
    /// <param name="source">The stream to read; it is left open.</param>
    /// <returns>The snapshot.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="InvalidDataException">The magic, version, body or hash is invalid.</exception>
    public static PreferenceSnapshot Load(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var buffer = new MemoryStream();
        source.CopyTo(buffer);

        byte[] bytes = buffer.ToArray();

        if (bytes.Length < Magic.Length + sizeof(int) + SHA256.HashSizeInBytes)
        {
            throw new InvalidDataException("The preference snapshot is truncated.");
        }

        byte[] body = bytes.AsSpan(0, bytes.Length - SHA256.HashSizeInBytes).ToArray();
        byte[] signature = bytes.AsSpan(bytes.Length - SHA256.HashSizeInBytes).ToArray();

        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(body), signature))
        {
            throw new InvalidDataException("The preference snapshot hash does not match its body.");
        }

        using var reader = new BinaryReader(new MemoryStream(body), Encoding.UTF8, leaveOpen: false);

        return ReadBody(reader);
    }

    private static void WriteBody(PreferenceSnapshot snapshot, Stream destination)
    {
        using var writer = new BinaryWriter(destination, Encoding.UTF8, leaveOpen: true);

        writer.Write(Magic);
        writer.Write(PreferenceSnapshot.CurrentVersion);
        writer.Write((int)snapshot.Options.Epochs);
        writer.Write(snapshot.Options.LearningRate);
        writer.Write(snapshot.Options.L2);
        writer.Write(snapshot.Options.Seed);
        writer.Write(snapshot.Options.MaxFeatures);
        writer.Write(snapshot.Options.MaxEvents);
        writer.Write(snapshot.Options.HashFeatures);
        writer.Write(snapshot.Options.HashDimension);
        writer.Write(snapshot.Hashed);
        writer.Write(snapshot.Features.Count);

        foreach (string feature in snapshot.Features)
        {
            writer.Write(feature);
        }

        writer.Write(snapshot.Categories.Count);

        foreach (PreferenceCategorySnapshot category in snapshot.Categories)
        {
            writer.Write(category.Category);
            writer.Write(category.Weights.Count);

            foreach (PreferenceWeightSnapshot weight in category.Weights)
            {
                writer.Write(weight.Feature is not null);

                if (weight.Feature is not null)
                {
                    writer.Write(weight.Feature);
                }

                writer.Write(weight.Index);
                writer.Write(weight.Weight);
            }
        }

        writer.Write(snapshot.Signature);
    }

    private static PreferenceSnapshot ReadBody(BinaryReader reader)
    {
        Span<byte> magic = stackalloc byte[Magic.Length];
        int read = reader.Read(magic);

        if (read != Magic.Length || !magic.SequenceEqual(Magic))
        {
            throw new InvalidDataException("The preference snapshot magic is invalid.");
        }

        int version = reader.ReadInt32();

        if (version != PreferenceSnapshot.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported preference snapshot version {version}; " +
                $"this build reads {PreferenceSnapshot.CurrentVersion}.");
        }

        var options = new PreferenceOptions
        {
            Epochs = reader.ReadInt32(),
            LearningRate = reader.ReadDouble(),
            L2 = reader.ReadDouble(),
            Seed = reader.ReadInt32(),
            MaxFeatures = reader.ReadInt32(),
            MaxEvents = reader.ReadInt32(),
            HashFeatures = reader.ReadBoolean(),
            HashDimension = reader.ReadInt32(),
        };

        try
        {
            PreferenceLearner.ValidateOptions(options);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException("The preference snapshot carries invalid options.", exception);
        }

        bool hashed = reader.ReadBoolean();
        int featureCount = ReadCount(reader, hashed ? 0 : Math.Min(options.MaxFeatures, MaxFeaturesHard));
        var features = new List<string>(Math.Min(featureCount, 1024));

        for (int i = 0; i < featureCount; i++)
        {
            features.Add(ReadString(reader));
        }

        int categoryCount = ReadCount(reader, MaxCategoriesHard);
        var categories = new List<PreferenceCategorySnapshot>(Math.Min(categoryCount, 1024));

        for (int i = 0; i < categoryCount; i++)
        {
            string category = ReadString(reader);
            int weightCount = ReadCount(reader, Math.Min(options.MaxFeatures, MaxFeaturesHard));
            var weights = new List<PreferenceWeightSnapshot>(Math.Min(weightCount, 1024));

            for (int w = 0; w < weightCount; w++)
            {
                bool hasFeature = reader.ReadBoolean();
                string? feature = hasFeature ? ReadString(reader) : null;
                int index = reader.ReadInt32();
                double weight = reader.ReadDouble();

                if (!double.IsFinite(weight))
                {
                    throw new InvalidDataException("The preference snapshot carries a non-finite weight.");
                }

                weights.Add(new PreferenceWeightSnapshot(feature, index, weight));
            }

            categories.Add(new PreferenceCategorySnapshot(category, weights));
        }

        string signature = ReadString(reader);

        return new PreferenceSnapshot(
            version,
            options,
            hashed,
            features,
            categories,
            signature);
    }

    private static int ReadCount(BinaryReader reader, int maximum)
    {
        int count = reader.ReadInt32();

        if (count < 0 || count > maximum)
        {
            throw new InvalidDataException($"The preference snapshot carries an invalid count: {count}.");
        }

        return count;
    }

    private static string ReadString(BinaryReader reader)
    {
        int length = 0;
        int shift = 0;

        while (true)
        {
            if (shift > 28)
            {
                throw new InvalidDataException("The preference snapshot carries an oversized string.");
            }

            byte current = reader.ReadByte();
            length |= (current & 0x7F) << shift;
            shift += 7;

            if ((current & 0x80) == 0)
            {
                break;
            }
        }

        if (length < 0 || length > MaxStringBytes)
        {
            throw new InvalidDataException($"The preference snapshot carries an oversized string: {length} bytes.");
        }

        byte[] bytes = reader.ReadBytes(length);

        if (bytes.Length != length)
        {
            throw new EndOfStreamException("The preference snapshot is truncated.");
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
