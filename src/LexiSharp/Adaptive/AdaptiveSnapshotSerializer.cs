using System.Security.Cryptography;
using System.Text;

namespace LexiSharp.Adaptive;

/// <summary>Reads and writes the versioned <c>LXFA</c> adaptive snapshot.</summary>
/// <remarks>
/// The layout is little-endian: the four bytes <c>LXFA</c>, an <see cref="int"/> version, the model
/// signature, the adaptive options, the feedback memory, an optional conformal calibration, and a
/// trailing SHA-256 of every preceding byte. A bad magic, an unknown version, a negative count or a
/// signature mismatch throws <see cref="InvalidDataException"/> rather than guessing.
/// </remarks>
public static class AdaptiveSnapshotSerializer
{
    private const int MaxContextsHard = 100_000;
    private const int MaxCategoriesHard = 100_000;
    private const int MaxSnapshotItems = 1_000_000;
    private const int MaxStringBytes = 1_048_576;

    private static ReadOnlySpan<byte> Magic => "LXFA"u8;

    /// <summary>Builds a snapshot and computes its signature.</summary>
    /// <param name="modelSignature">The caller's signature of the text model.</param>
    /// <param name="options">The adaptive options.</param>
    /// <param name="memory">The memory snapshot.</param>
    /// <param name="calibration">The calibration snapshot, or null.</param>
    /// <returns>The snapshot with its <see cref="AdaptiveSnapshot.Signature"/> filled.</returns>
    public static AdaptiveSnapshot Create(
        string modelSignature,
        AdaptiveOptions options,
        FeedbackSnapshot memory,
        WeightedConformalSnapshot? calibration)
    {
        ArgumentNullException.ThrowIfNull(modelSignature);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(memory);

        var unsigned = new AdaptiveSnapshot(
            AdaptiveSnapshot.CurrentVersion,
            modelSignature,
            options,
            memory,
            calibration,
            string.Empty);

        using var body = new MemoryStream();
        WriteBody(unsigned, body);

        return unsigned with
        {
            Signature = Convert.ToHexStringLower(SHA256.HashData(body.ToArray())),
        };
    }

    /// <summary>Writes a snapshot and its trailing signature.</summary>
    /// <param name="snapshot">The snapshot; its stored signature is not trusted.</param>
    /// <param name="destination">The stream to write to; it is left open.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static void Save(AdaptiveSnapshot snapshot, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(destination);

        using var body = new MemoryStream();
        WriteBody(snapshot, body);

        byte[] bytes = body.ToArray();
        destination.Write(bytes);
        destination.Write(SHA256.HashData(bytes));
    }

    /// <summary>Reads a snapshot and verifies its trailing signature.</summary>
    /// <param name="source">The stream to read; it is left open.</param>
    /// <returns>The snapshot with the verified signature.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
    /// <exception cref="InvalidDataException">The magic, version, body or signature is invalid.</exception>
    public static AdaptiveSnapshot Load(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var buffer = new MemoryStream();
        source.CopyTo(buffer);

        byte[] bytes = buffer.ToArray();

        if (bytes.Length < Magic.Length + sizeof(int) + SHA256.HashSizeInBytes)
        {
            throw new InvalidDataException("The adaptive snapshot is truncated.");
        }

        byte[] body = bytes.AsSpan(0, bytes.Length - SHA256.HashSizeInBytes).ToArray();
        byte[] signature = bytes.AsSpan(bytes.Length - SHA256.HashSizeInBytes).ToArray();

        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(body), signature))
        {
            throw new InvalidDataException("The adaptive snapshot signature does not match its body.");
        }

        using var reader = new BinaryReader(new MemoryStream(body), Encoding.UTF8, leaveOpen: false);

        return ReadBody(reader) with
        {
            Signature = Convert.ToHexStringLower(signature),
        };
    }

    private static void WriteBody(AdaptiveSnapshot snapshot, Stream destination)
    {
        using var writer = new BinaryWriter(destination, Encoding.UTF8, leaveOpen: true);

        writer.Write(Magic);
        writer.Write(AdaptiveSnapshot.CurrentVersion);
        writer.Write(snapshot.ModelSignature);
        WriteOptions(writer, snapshot.Options);
        WriteFeedback(writer, snapshot.Memory);
        writer.Write(snapshot.Calibration is not null);

        if (snapshot.Calibration is { } calibration)
        {
            WriteCalibration(writer, calibration);
        }
    }

    private static AdaptiveSnapshot ReadBody(BinaryReader reader)
    {
        Span<byte> magic = stackalloc byte[Magic.Length];
        int read = reader.Read(magic);

        if (read != Magic.Length || !magic.SequenceEqual(Magic))
        {
            throw new InvalidDataException("The adaptive snapshot magic is invalid.");
        }

        int version = reader.ReadInt32();

        if (version != AdaptiveSnapshot.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported adaptive snapshot version {version}; " +
                $"this build reads {AdaptiveSnapshot.CurrentVersion}.");
        }

        string modelSignature = ReadString(reader);
        AdaptiveOptions options = ReadOptions(reader);
        FeedbackSnapshot memory = ReadFeedback(reader);
        bool hasCalibration = reader.ReadBoolean();
        WeightedConformalSnapshot? calibration = hasCalibration
            ? ReadCalibration(reader)
            : null;

        return new AdaptiveSnapshot(
            version,
            modelSignature,
            options,
            memory,
            calibration,
            string.Empty);
    }

    private static void WriteFeedback(BinaryWriter writer, FeedbackSnapshot snapshot)
    {
        writer.Write(snapshot.SchemaVersion);
        writer.Write(snapshot.CodecId);
        WritePolicy(writer, snapshot.Policy);
        WriteMemoryOptions(writer, snapshot.Options);
        writer.Write(snapshot.ContextsHashed);
        writer.Write(snapshot.SavedAt.UtcTicks);
        writer.Write(snapshot.Global.LastUpdated.UtcTicks);
        writer.Write(snapshot.Global.Categories.Count);

        foreach (CategoryFeedbackSnapshot category in snapshot.Global.Categories)
        {
            WriteCategory(writer, category);
        }

        writer.Write(snapshot.Contexts.Count);

        foreach (ContextFeedbackSnapshot context in snapshot.Contexts)
        {
            writer.Write(context.Key);
            writer.Write(context.LastUpdated.UtcTicks);
            writer.Write(context.Exposures);
            writer.Write(context.Responses);
            writer.Write(context.Observations);
            writer.Write(context.Categories.Count);

            foreach (CategoryFeedbackSnapshot category in context.Categories)
            {
                WriteCategory(writer, category);
            }
        }
    }

    private static FeedbackSnapshot ReadFeedback(BinaryReader reader)
    {
        int schema = reader.ReadInt32();

        if (schema != FeedbackSnapshot.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported feedback snapshot version {schema}; " +
                $"this build reads {FeedbackSnapshot.CurrentVersion}.");
        }

        string codecId = ReadString(reader);
        RecencyPolicy policy = ReadPolicy(reader);
        FeedbackMemoryOptions options = ReadMemoryOptions(reader);
        bool contextsHashed = reader.ReadBoolean();
        DateTimeOffset savedAt = ReadInstant(reader);
        DateTimeOffset globalUpdated = ReadInstant(reader);
        int globalCount = ReadCount(reader, MaxCategoriesHard);
        var globalCategories = new List<CategoryFeedbackSnapshot>(Math.Min(globalCount, 1024));

        for (int i = 0; i < globalCount; i++)
        {
            globalCategories.Add(ReadCategory(reader));
        }

        int contextCount = ReadCount(reader, MaxContextsHard);
        var contexts = new List<ContextFeedbackSnapshot>(Math.Min(contextCount, 1024));

        for (int i = 0; i < contextCount; i++)
        {
            string key = ReadString(reader);
            DateTimeOffset lastUpdated = ReadInstant(reader);
            long exposures = reader.ReadInt64();
            long responses = reader.ReadInt64();
            long observations = reader.ReadInt64();
            int categoryCount = ReadCount(reader, MaxCategoriesHard);
            var categories = new List<CategoryFeedbackSnapshot>(Math.Min(categoryCount, 1024));

            for (int c = 0; c < categoryCount; c++)
            {
                categories.Add(ReadCategory(reader));
            }

            contexts.Add(new ContextFeedbackSnapshot(
                key,
                lastUpdated,
                exposures,
                responses,
                observations,
                categories));
        }

        return new FeedbackSnapshot(
            schema,
            codecId,
            policy,
            options,
            contextsHashed,
            savedAt,
            new GlobalFeedbackSnapshot(globalUpdated, globalCategories),
            contexts);
    }

    private static void WriteCalibration(BinaryWriter writer, WeightedConformalSnapshot snapshot)
    {
        writer.Write(snapshot.SchemaVersion);
        writer.Write(snapshot.SavedAt.UtcTicks);
        writer.Write(snapshot.Alpha);
        WritePolicy(writer, snapshot.Policy);
        writer.Write(snapshot.MinSamples);
        writer.Write(snapshot.MaxSamples);
        writer.Write(snapshot.RefreshEvery);
        writer.Write(snapshot.MaxThresholdAge.Ticks);
        writer.Write(snapshot.Entries.Count);

        foreach (CalibrationEntry entry in snapshot.Entries)
        {
            writer.Write(entry.Score);
            writer.Write(entry.At.UtcTicks);
        }
    }

    private static WeightedConformalSnapshot ReadCalibration(BinaryReader reader)
    {
        int schema = reader.ReadInt32();

        if (schema != WeightedConformalSnapshot.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported conformal snapshot version {schema}; " +
                $"this build reads {WeightedConformalSnapshot.CurrentVersion}.");
        }

        DateTimeOffset savedAt = ReadInstant(reader);
        double alpha = reader.ReadDouble();
        RecencyPolicy policy = ReadPolicy(reader);
        int minSamples = reader.ReadInt32();
        int maxSamples = reader.ReadInt32();
        int refreshEvery = reader.ReadInt32();
        long ageTicks = reader.ReadInt64();
        int count = ReadCount(reader, MaxSnapshotItems);
        var entries = new List<CalibrationEntry>(Math.Min(count, 1024));

        for (int i = 0; i < count; i++)
        {
            double score = reader.ReadDouble();
            entries.Add(new CalibrationEntry(score, ReadInstant(reader)));
        }

        return new WeightedConformalSnapshot(
            schema,
            savedAt,
            alpha,
            policy,
            minSamples,
            maxSamples,
            refreshEvery,
            TimeSpan.FromTicks(ageTicks),
            entries);
    }

    private static void WriteOptions(BinaryWriter writer, AdaptiveOptions options)
    {
        writer.Write(options.AcceptanceWeight);
        writer.Write(options.RefusalWeight);
        writer.Write(options.MinScore);
        writer.Write(options.AutoTuneHalfLife);
    }

    private static AdaptiveOptions ReadOptions(BinaryReader reader) =>
        new()
        {
            AcceptanceWeight = reader.ReadDouble(),
            RefusalWeight = reader.ReadDouble(),
            MinScore = reader.ReadDouble(),
            AutoTuneHalfLife = reader.ReadBoolean(),
        };

    private static void WritePolicy(BinaryWriter writer, RecencyPolicy policy)
    {
        writer.Write(policy.HalfLife.Ticks);
        writer.Write(policy.MaxAge.Ticks);
        writer.Write(policy.FrequencySaturation);
        writer.Write((int)policy.Mode);
        writer.Write(policy.MaxCopies);
    }

    private static RecencyPolicy ReadPolicy(BinaryReader reader)
    {
        var halfLife = TimeSpan.FromTicks(reader.ReadInt64());
        var maxAge = TimeSpan.FromTicks(reader.ReadInt64());
        double saturation = reader.ReadDouble();
        int mode = reader.ReadInt32();
        int maxCopies = reader.ReadInt32();

        return new RecencyPolicy(
            halfLife,
            maxAge,
            saturation,
            (SampleWeightMode)mode,
            maxCopies);
    }

    private static void WriteMemoryOptions(BinaryWriter writer, FeedbackMemoryOptions options)
    {
        writer.Write(options.VetoAfter);
        writer.Write(options.MaxContexts);
        writer.Write(options.MaxCategoriesPerContext);
        writer.Write(options.MaxPairs);
        writer.Write(options.MinMass);
        writer.Write(options.Shrinkage);
        writer.Write(options.GlobalSmoothing);
    }

    private static FeedbackMemoryOptions ReadMemoryOptions(BinaryReader reader) =>
        new()
        {
            VetoAfter = reader.ReadInt32(),
            MaxContexts = reader.ReadInt32(),
            MaxCategoriesPerContext = reader.ReadInt32(),
            MaxPairs = reader.ReadInt64(),
            MinMass = reader.ReadDouble(),
            Shrinkage = reader.ReadDouble(),
            GlobalSmoothing = reader.ReadDouble(),
        };

    private static void WriteCategory(BinaryWriter writer, CategoryFeedbackSnapshot category)
    {
        writer.Write(category.Category);
        writer.Write(category.AcceptanceMass);
        writer.Write(category.RefusalMass);
    }

    private static CategoryFeedbackSnapshot ReadCategory(BinaryReader reader) =>
        new(ReadString(reader), reader.ReadDouble(), reader.ReadDouble());

    private static DateTimeOffset ReadInstant(BinaryReader reader)
    {
        long ticks = reader.ReadInt64();

        try
        {
            return new DateTimeOffset(ticks, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException("The adaptive snapshot carries an invalid instant.", exception);
        }
    }

    private static int ReadCount(BinaryReader reader, int maximum)
    {
        int count = reader.ReadInt32();

        if (count < 0 || count > maximum)
        {
            throw new InvalidDataException($"The adaptive snapshot carries an invalid count: {count}.");
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
                throw new InvalidDataException("The adaptive snapshot carries an oversized string.");
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
            throw new InvalidDataException($"The adaptive snapshot carries an oversized string: {length} bytes.");
        }

        byte[] bytes = reader.ReadBytes(length);

        if (bytes.Length != length)
        {
            throw new EndOfStreamException("The adaptive snapshot is truncated.");
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
