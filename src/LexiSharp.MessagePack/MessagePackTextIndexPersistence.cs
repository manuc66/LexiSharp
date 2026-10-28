using System.Diagnostics.CodeAnalysis;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using MessagePack;

namespace LexiSharp.MessagePack;

/// <summary>
/// Saves and reloads an <see cref="InMemoryTextIndex"/> as MessagePack binary (LZ4-compressed):
/// the documents themselves — id, text, fields, category — plus the tokenizer configuration.
/// </summary>
/// <remarks>
/// The payload stores document <b>data</b>, not references, so reloading rebuilds a fully
/// functional index with identical statistics; nothing has to be re-indexed or re-derived.
/// <para>
/// Tokenizer handling:
/// <list type="bullet">
/// <item><description>A <see cref="Tokenizer"/> is saved with its <see cref="TokenizerOptions"/>
/// (stop words, n-grams, single-char terms) and reconstructed exactly by <see cref="Load(Stream, ITokenizer?)"/>.</description></item>
/// <item><description>A custom <see cref="ITokenizer"/> cannot be serialized; <see cref="Load(Stream, ITokenizer?)"/>
/// must be handed the same implementation, and refuses anything else.</description></item>
/// <item><description>A stemmer (<see cref="TokenizerOptions.Stemmer"/>) is not serializable:
/// <see cref="Load(Stream, ITokenizer?)"/> fails explicitly rather than silently changing the corpus.</description></item>
/// </list>
/// </para>
/// <para>
/// Passing an explicit tokenizer to <see cref="Load(Stream, ITokenizer?)"/> overrides reconstruction; the saved type
/// name must still match, which protects against rebuilding an index with the wrong pipeline.
/// </para>
/// </remarks>
public static class MessagePackTextIndexPersistence
{
    private const int FormatVersion = 2;

    /// <summary>
    /// Oldest payload this build still reads. Version 1 had no <c>TextFields</c>, so loading one
    /// yields an index whose documents have no text fields — which is what that file recorded.
    /// </summary>
    private const int OldestReadableVersion = 1;

    /// <summary>
    /// The serializer configuration both directions use, reading the payload as <b>untrusted
    /// input</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>MessagePackSerializerOptions.Standard</c> carries MessagePack's <c>TrustedData</c>
    /// settings: hash collision resistance off, and a decompression ceiling of
    /// <see cref="int.MaxValue"/> for an LZ4 payload. Both are wrong for this type. The payload
    /// holds <c>Dictionary&lt;string, string&gt;</c> fields whose keys are read straight out of the
    /// file, so colliding keys are a denial of service the reader performs on itself, and the
    /// compressed length is a number the file chooses — a small payload may declare two gigabytes
    /// of output.
    /// </para>
    /// <para>
    /// Nothing in the API says where a file came from, so the reading side pays for the safer
    /// configuration: SipHash over the keys it materializes, and a 64 MB ceiling on decompressed
    /// output. Writing is unaffected — the wire format does not change, and an index saved by an
    /// earlier build still loads.
    /// </para>
    /// <para>
    /// Internal so the tests can pin the settings rather than assert them through a side effect.
    /// </para>
    /// </remarks>
    internal static readonly MessagePackSerializerOptions SerializerOptions =
        MessagePackSerializerOptions.Standard
            .WithCompression(MessagePackCompression.Lz4BlockArray)
            .WithSecurity(MessagePackSecurity.UntrustedData);

    /// <summary>Serializes the index to the stream.</summary>
    public static void Save(InMemoryTextIndex index, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(stream);

        var stored = new StoredIndex(
            FormatVersion,
            DescribeTokenizer(index.Tokenizer),
            index.Documents.Select(ToStored).ToList());

        MessagePackSerializer.Serialize(stream, stored, SerializerOptions);
    }

    /// <summary>Serializes the index to a file (overwrites any existing file).</summary>
    public static void Save(InMemoryTextIndex index, string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        using FileStream stream = File.Create(filePath);
        Save(index, stream);
    }

    /// <summary>Deserializes an index previously saved with <see cref="Save(InMemoryTextIndex, Stream)"/>.</summary>
    /// <param name="stream">The binary payload.</param>
    /// <param name="tokenizer">
    /// Optional tokenizer override. When omitted, the saved <see cref="Tokenizer"/> configuration
    /// is reconstructed automatically.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// The payload is not a valid index, or the tokenizer configuration cannot be reconstructed
    /// (custom tokenizer, stemmer) and none was supplied.
    /// </exception>
    /// <exception cref="NotSupportedException">The payload was written by an incompatible format version.</exception>
    public static InMemoryTextIndex Load(Stream stream, ITokenizer? tokenizer = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        StoredIndex stored;

        try
        {
            stored = MessagePackSerializer.Deserialize<StoredIndex>(stream, SerializerOptions);
        }
        catch (MessagePackSerializationException exception)
        {
            throw new InvalidOperationException("The stream does not contain a valid LexiSharp index.", exception);
        }

        if (stored.Version is not (FormatVersion or OldestReadableVersion))
            throw new NotSupportedException(
                $"Unsupported index format version {stored.Version} " +
                $"(expected {FormatVersion}, or {OldestReadableVersion} for a file saved before " +
                "text fields were persisted).");

        var restoredTokenizer = RestoreTokenizer(stored.Tokenizer, tokenizer);
        var index = new InMemoryTextIndex(restoredTokenizer);
        index.Index(stored.Documents.Select(FromStored));

        return index;
    }

    /// <summary>Deserializes an index previously saved with <see cref="Save(InMemoryTextIndex, string)"/>.</summary>
    /// <param name="filePath">The file holding the binary payload.</param>
    /// <param name="tokenizer">Optional tokenizer override (see <see cref="Load(Stream, ITokenizer?)"/>).</param>
    public static InMemoryTextIndex Load(string filePath, ITokenizer? tokenizer = null)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        using FileStream stream = File.OpenRead(filePath);
        return Load(stream, tokenizer);
    }

    private static StoredTokenizer DescribeTokenizer(ITokenizer tokenizer)
    {
        string typeName = TokenizerTypeName(tokenizer);

        if (tokenizer is not Tokenizer lexishTokenizer)
            return new StoredTokenizer(typeName, false, 1, 1, false, null, false);

        TokenizerOptions options = lexishTokenizer.Options;

        return new StoredTokenizer(
            typeName,
            options.RemoveStopWords,
            options.NGramMin,
            options.NGramMax,
            options.KeepSingleCharTerms,
            options.StopWords is null
                ? null
                : options.StopWords.OrderBy(word => word, StringComparer.Ordinal).ToList(),
            options.Stemmer is not null);
    }

    /// <summary>The tokenizer's type name, as stored and matched on the way back in.</summary>
    /// <remarks>
    /// <see cref="Type.FullName"/> is null for a type the runtime cannot fully name — a generic type
    /// parameter, or a type built from one. A tokenizer is a concrete type, so this is about naming
    /// an unnameable type rather than tolerating one, and <c>Name</c> is never null for a runtime type. The short name keeps a null out of the format's non-nullable field: a payload
    /// written with a null in it restores as a mismatch, which is the honest answer for a type whose
    /// full name nobody could write down.
    /// </remarks>
    private static string TokenizerTypeName(ITokenizer tokenizer)
    {
        Type type = tokenizer.GetType();

        return type.FullName ?? type.Name;
    }

    private static ITokenizer RestoreTokenizer(StoredTokenizer stored, ITokenizer? provided)
    {
        if (provided is not null)
        {
            if (provided.GetType().FullName != stored.TypeName)
                throw new InvalidOperationException(
                    $"The index was built with tokenizer '{stored.TypeName}' but '{provided.GetType().FullName}' was supplied.");

            return provided;
        }

        if (stored.TypeName != typeof(Tokenizer).FullName)
            throw new InvalidOperationException(
                $"The index was built with a custom tokenizer ('{stored.TypeName}') that cannot be reconstructed; supply the same tokenizer to Load.");

        if (stored.HasStemmer)
            throw new InvalidOperationException(
                "The index was built with a stemmed tokenizer, and stemmers cannot be serialized; supply the same tokenizer to Load.");

        var options = new TokenizerOptions
        {
            RemoveStopWords = stored.RemoveStopWords,
            NGramMin = stored.NGramMin,
            NGramMax = stored.NGramMax,
            KeepSingleCharTerms = stored.KeepSingleCharTerms,
            StopWords = stored.CustomStopWords is null
                ? null
                : new HashSet<string>(stored.CustomStopWords, StringComparer.OrdinalIgnoreCase),
        };

        return new Tokenizer(options);
    }

    private static StoredDocument ToStored(SearchDocument document) => new(
        document.Id,
        document.Text,
        document.Fields is null ? null : new Dictionary<string, string>(document.Fields),
        document.Category,
        document.TextFields is null ? null : new Dictionary<string, string>(document.TextFields));

    private static SearchDocument FromStored(StoredDocument stored) => new(
        stored.Id,
        stored.Text,
        stored.Fields,
        stored.Category,
        stored.TextFields);
}