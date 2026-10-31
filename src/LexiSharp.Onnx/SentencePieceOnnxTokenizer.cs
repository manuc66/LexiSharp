using System.Security.Cryptography;
using Microsoft.ML.Tokenizers;

namespace LexiSharp.Onnx;

/// <summary>
/// Options for <see cref="SentencePieceOnnxTokenizer"/>: the model decides the pieces, the caller
/// decides the special tokens and the id remapping.
/// </summary>
public sealed class SentencePieceOnnxTokenizerOptions
{
    /// <summary>Whether the tokenizer emits its beginning-of-sentence token. Defaults to <see langword="false"/>.</summary>
    public bool AddBeginningOfSentence { get; init; }

    /// <summary>Whether the tokenizer emits its end-of-sentence token. Defaults to <see langword="false"/>.</summary>
    public bool AddEndOfSentence { get; init; }

    /// <summary>
    /// Optional id mapping applied after tokenization — for exports whose graph vocabulary is
    /// offset from the SentencePiece model (some Xenova E5 exports shift every id by one and map
    /// the unknown token to a fixed id). Receives the SentencePiece id, returns the graph id.
    /// </summary>
    public Func<int, int>? IdMap { get; init; }

    /// <summary>
    /// Id prepended after tokenization, or <see langword="null"/> for none. Covers exports whose
    /// graph expects a fixed start token (<c>[CLS]</c> or <c>&lt;s&gt;</c>) that the SentencePiece
    /// model itself does not emit.
    /// </summary>
    public int? PrefixId { get; init; }

    /// <summary>
    /// Id appended after tokenization, or <see langword="null"/> for none. Covers exports whose
    /// graph expects a fixed end token (<c>[SEP]</c> or <c>&lt;/s&gt;</c>).
    /// </summary>
    public int? SuffixId { get; init; }

    /// <summary>
    /// Size of the graph's vocabulary, when it is known. When set, <see cref="SentencePieceOnnxTokenizer.Encode"/>
    /// rejects a mapped id outside <c>[0, GraphVocabularySize)</c> instead of handing ONNX Runtime
    /// an index its Gather may resolve to nothing. Leave <see langword="null"/> when the export's
    /// vocabulary size is not known.
    /// </summary>
    public int? GraphVocabularySize { get; init; }

    /// <summary>
    /// Id inserted between the query and the document by <see cref="SentencePieceOnnxTokenizer.EncodePair"/>,
    /// or <see langword="null"/> when the checkpoint has no pair convention. Pair encoding throws
    /// <see cref="NotSupportedException"/> while this is null, rather than concatenating silently.
    /// </summary>
    public int? PairSeparatorId { get; init; }
}

/// <summary>
/// A SentencePiece tokenizer over a checkpoint's <c>sentencepiece.bpe.model</c> (or
/// <c>spiece.model</c>): the tokenizer E5, multilingual MiniLM and most non-BERT encoders publish.
/// </summary>
/// <remarks>
/// <para>
/// The model file is the checkpoint's own; nothing is downloaded or assumed. The BOS/EOS flags and
/// the optional <see cref="SentencePieceOnnxTokenizerOptions.IdMap"/> cover the two deviations seen
/// in real exports: whether the graph expects the sentence markers, and whether its vocabulary is
/// offset from the SentencePiece ids. Pair encoding is opt-in because most SentencePiece
/// cross-encoders define their own separator; without one, the method says so.
/// </para>
/// <para>
/// The instance is immutable and safe for concurrent use.
/// </para>
/// </remarks>
public sealed class SentencePieceOnnxTokenizer : IOnnxTokenizer
{
    private readonly SentencePieceTokenizer _tokenizer;
    private readonly SentencePieceOnnxTokenizerOptions _options;
    private readonly string _fingerprint;

    /// <summary>Loads a SentencePiece model from disk.</summary>
    /// <param name="modelPath">Path to the checkpoint's SentencePiece model.</param>
    /// <param name="options">Special-token and id-mapping rules; defaults apply when omitted.</param>
    /// <exception cref="ArgumentException"><paramref name="modelPath"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">The file is not a SentencePiece model.</exception>
    public SentencePieceOnnxTokenizer(string modelPath, SentencePieceOnnxTokenizerOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            throw new ArgumentException("modelPath is required.", nameof(modelPath));

        _options = options ?? new SentencePieceOnnxTokenizerOptions();

        byte[] model = File.ReadAllBytes(modelPath);
        using var stream = new MemoryStream(model, writable: false);
        _tokenizer = SentencePieceTokenizer.Create(
            stream,
            _options.AddBeginningOfSentence,
            _options.AddEndOfSentence);
        _fingerprint = Convert.ToHexString(SHA256.HashData(model));
    }

    /// <inheritdoc />
    public string? Fingerprint => _fingerprint;

    /// <summary>The SentencePiece model's unknown-token id, before <see cref="SentencePieceOnnxTokenizerOptions.IdMap"/> is applied.</summary>
    public int UnknownId => _tokenizer.UnknownId;

    /// <summary>Tokenizes one text, applies the id map and truncates to the cap.</summary>
    /// <param name="text">Text to tokenize.</param>
    /// <param name="maxSequenceLength">Hard cap on the returned token count; must be at least 1.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxSequenceLength"/> is below 1.</exception>
    public OnnxTokenizedText Encode(string text, int maxSequenceLength)
    {
        ArgumentNullException.ThrowIfNull(text);

        int reserved = (_options.PrefixId.HasValue ? 1 : 0)
            + (_options.SuffixId.HasValue ? 1 : 0)
            + (_options.AddBeginningOfSentence ? 1 : 0)
            + (_options.AddEndOfSentence ? 1 : 0);

        if (maxSequenceLength < Math.Max(1, reserved))
            throw new ArgumentOutOfRangeException(
                nameof(maxSequenceLength),
                maxSequenceLength,
                $"The tokenizer's special tokens need at least {reserved} token(s).");

        IReadOnlyList<int> ids = _tokenizer.EncodeToIds(
            text,
            _options.AddBeginningOfSentence,
            _options.AddEndOfSentence,
            considerPreTokenization: true,
            considerNormalization: true);

        List<int> mapped = new(ids.Count + 2);

        if (_options.PrefixId is int prefix)
            mapped.Add(prefix);

        foreach (int id in ids)
            mapped.Add(Map(id));

        if (_options.SuffixId is int suffix)
            mapped.Add(suffix);

        return new OnnxTokenizedText(Truncate(mapped, maxSequenceLength));
    }

    /// <summary>
    /// Merges a query and a document into one sequence using
    /// <see cref="SentencePieceOnnxTokenizerOptions.PairSeparatorId"/>.
    /// </summary>
    /// <param name="query">The query side.</param>
    /// <param name="document">The document side.</param>
    /// <param name="maxSequenceLength">Hard cap on the merged sequence; must be at least 2.</param>
    /// <param name="maxQueryTokens">Cap on the query side; must be at least 1.</param>
    /// <exception cref="NotSupportedException">No <see cref="SentencePieceOnnxTokenizerOptions.PairSeparatorId"/> is configured.</exception>
    public OnnxTokenizedText EncodePair(string query, string document, int maxSequenceLength, int maxQueryTokens)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(document);

        if (_options.PairSeparatorId is not int separator)
            throw new NotSupportedException(
                "This SentencePiece tokenizer has no pair convention. Set SentencePieceOnnxTokenizerOptions.PairSeparatorId, " +
                "or encode the concatenated text as a single sequence.");

        if (maxSequenceLength < 2)
            throw new ArgumentOutOfRangeException(nameof(maxSequenceLength), maxSequenceLength, "A pair needs room for at least the separator and one token.");

        if (maxQueryTokens < 1)
            throw new ArgumentOutOfRangeException(nameof(maxQueryTokens), maxQueryTokens, "The query needs at least one token.");

        IReadOnlyList<int> queryIds = _tokenizer.EncodeToIds(query, false, false, true, true);
        IReadOnlyList<int> documentIds = _tokenizer.EncodeToIds(document, false, false, true, true);

        int queryKeep = Math.Min(queryIds.Count, Math.Min(maxQueryTokens, maxSequenceLength - 1));
        int documentKeep = Math.Min(documentIds.Count, maxSequenceLength - queryKeep - 1);

        int[] ids = new int[queryKeep + 1 + documentKeep];
        int[] types = new int[ids.Length];

        for (int i = 0; i < queryKeep; i++)
            ids[i] = Map(queryIds[i]);

        ids[queryKeep] = separator;

        for (int i = 0; i < documentKeep; i++)
        {
            ids[queryKeep + 1 + i] = Map(documentIds[i]);
            types[queryKeep + 1 + i] = 1;
        }

        return new OnnxTokenizedText(ids, types);
    }

    private int[] Truncate(List<int> ids, int maxSequenceLength)
    {
        if (ids.Count <= maxSequenceLength)
            return ids.ToArray();

        int tail = (_options.SuffixId.HasValue ? 1 : 0) + (_options.AddEndOfSentence ? 1 : 0);
        int head = Math.Min(Math.Max(0, maxSequenceLength - tail), ids.Count - Math.Min(tail, ids.Count));
        int[] result = new int[head + Math.Min(tail, ids.Count - head)];

        for (int i = 0; i < head; i++)
            result[i] = ids[i];

        int tailStart = ids.Count - (result.Length - head);

        for (int i = head; i < result.Length; i++)
            result[i] = ids[tailStart + (i - head)];

        return result;
    }

    private int Map(int id)
    {
        int mapped = _options.IdMap is null ? id : _options.IdMap(id);

        if (mapped < 0 || (_options.GraphVocabularySize is int size && mapped >= size))
            throw new InvalidOperationException(
                $"The id map produced {mapped} from {id}, outside the graph vocabulary [0, {_options.GraphVocabularySize}).");

        return mapped;
    }
}
