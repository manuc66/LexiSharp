using MessagePack;

namespace LexiSharp.MessagePack;

/// <summary>Wire format of a persisted index (versioned).</summary>
[MessagePackObject(AllowPrivate = true)]
internal sealed record StoredIndex(
    [property: Key(0)] int Version,
    [property: Key(1)] StoredTokenizer Tokenizer,
    [property: Key(2)] List<StoredDocument> Documents);

/// <summary>Wire format of the tokenizer configuration used to build an index.</summary>
[MessagePackObject(AllowPrivate = true)]
internal sealed record StoredTokenizer(
    [property: Key(0)] string TypeName,
    [property: Key(1)] bool RemoveStopWords,
    [property: Key(2)] int NGramMin,
    [property: Key(3)] int NGramMax,
    [property: Key(4)] bool KeepSingleCharTerms,
    [property: Key(5)] List<string>? CustomStopWords,
    [property: Key(6)] bool HasStemmer);

/// <summary>Wire format of a single document.</summary>
[MessagePackObject(AllowPrivate = true)]
internal sealed record StoredDocument(
    [property: Key(0)] string Id,
    [property: Key(1)] string Text,
    [property: Key(2)] Dictionary<string, string>? Fields,
    [property: Key(3)] string? Category);