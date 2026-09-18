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
    private const int FormatVersion = 1;

    private static readonly MessagePackSerializerOptions SerializerOptions =
        MessagePackSerializerOptions.Standard
            .WithCompression(MessagePackCompression.Lz4BlockArray);

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

        if (stored.Version != FormatVersion)
            throw new NotSupportedException(
                $"Unsupported index format version {stored.Version} (expected {FormatVersion}).");

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
        if (tokenizer is not Tokenizer lexishTokenizer)
            return new StoredTokenizer(tokenizer.GetType().FullName!, false, 1, 1, false, null, false);

        TokenizerOptions options = lexishTokenizer.Options;

        return new StoredTokenizer(
            tokenizer.GetType().FullName!,
            options.RemoveStopWords,
            options.NGramMin,
            options.NGramMax,
            options.KeepSingleCharTerms,
            options.StopWords is null
                ? null
                : options.StopWords.OrderBy(word => word, StringComparer.Ordinal).ToList(),
            options.Stemmer is not null);
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
        document.Category);

    private static SearchDocument FromStored(StoredDocument stored) => new(
        stored.Id,
        stored.Text,
        stored.Fields,
        stored.Category);
}