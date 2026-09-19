using LexiSharp.Core;
using LexiSharp.Indexing;
using MessagePack;

namespace LexiSharp.MessagePack;

/// <summary>
/// Saves and reloads a <see cref="SparseTextSearchEngine"/> as MessagePack binary (LZ4-compressed):
/// the documents themselves — id, text, fields, category — plus the learned sparse weights of each.
/// </summary>
/// <remarks>
/// The payload stores the <b>results of embedding</b>, not the embedding model: reloading rebuilds a
/// fully functional engine whose stored vectors are byte-identical to the ones the provider
/// produced at index time, so documents are never re-embedded. Only the <i>query</i> still needs
/// the model afterwards, which is why <see cref="Load(Stream, ISparseEmbeddingProvider)"/> requires
/// an <see cref="ISparseEmbeddingProvider"/> (it embeds queries, not the corpus).
/// <para>
/// Round-trip equivalence: <c>restored.Search(q)</c> produces exactly the same scores as
/// <c>original.Search(q)</c> for any query (the provider is used identically on both sides).
/// </para>
/// </remarks>
public static class MessagePackSparseIndexPersistence
{
    private const int FormatVersion = 1;

    private static readonly MessagePackSerializerOptions SerializerOptions =
        MessagePackSerializerOptions.Standard
            .WithCompression(MessagePackCompression.Lz4BlockArray);

    /// <summary>Serializes the engine to the stream.</summary>
    public static void Save(SparseTextSearchEngine engine, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(stream);

        var stored = new StoredSparseIndex(
            FormatVersion,
            engine.Export()
                .Select(ToStored)
                .ToList());

        MessagePackSerializer.Serialize(stream, stored, SerializerOptions);
    }

    /// <summary>Serializes the engine to a file (overwrites any existing file).</summary>
    public static void Save(SparseTextSearchEngine engine, string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        using FileStream stream = File.Create(filePath);
        Save(engine, stream);
    }

    /// <summary>Deserializes an engine previously saved with <see cref="Save(SparseTextSearchEngine, Stream)"/>.</summary>
    /// <param name="stream">The binary payload.</param>
    /// <param name="embeddings">The sparse model: used to embed queries only.</param>
    /// <exception cref="InvalidOperationException">The payload is not a valid sparse index.</exception>
    /// <exception cref="NotSupportedException">The payload was written by an incompatible format version.</exception>
    public static SparseTextSearchEngine Load(Stream stream, ISparseEmbeddingProvider embeddings)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(embeddings);

        StoredSparseIndex stored;

        try
        {
            stored = MessagePackSerializer.Deserialize<StoredSparseIndex>(stream, SerializerOptions);
        }
        catch (MessagePackSerializationException exception)
        {
            throw new InvalidOperationException("The stream does not contain a valid LexiSharp sparse index.", exception);
        }

        if (stored.Version != FormatVersion)
            throw new NotSupportedException(
                $"Unsupported sparse index format version {stored.Version} (expected {FormatVersion}).");

        var engine = new SparseTextSearchEngine(embeddings);
        engine.Import(stored.Entries.Select(FromStored));

        return engine;
    }

    /// <summary>Deserializes an engine previously saved with <see cref="Save(SparseTextSearchEngine, string)"/>.</summary>
    /// <param name="filePath">The file holding the binary payload.</param>
    /// <param name="embeddings">The sparse model: used to embed queries only.</param>
    public static SparseTextSearchEngine Load(string filePath, ISparseEmbeddingProvider embeddings)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        using FileStream stream = File.OpenRead(filePath);
        return Load(stream, embeddings);
    }

    private static StoredSparseEntry ToStored(SparseIndexEntry entry) => new(
        new StoredDocument(
            entry.Document.Id,
            entry.Document.Text,
            entry.Document.Fields is null ? null : new Dictionary<string, string>(entry.Document.Fields),
            entry.Document.Category),
        new Dictionary<string, float>(entry.Weights, StringComparer.Ordinal));

    private static SparseIndexEntry FromStored(StoredSparseEntry stored) => new(
        new SearchDocument(
            stored.Document.Id,
            stored.Document.Text,
            stored.Document.Fields,
            stored.Document.Category),
        stored.Weights);
}