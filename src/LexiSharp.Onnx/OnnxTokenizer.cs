namespace LexiSharp.Onnx;

/// <summary>
/// A tokenized text ready to be bound to an ONNX graph: the token ids and the optional
/// segment ids, with no padding applied. Padding and batching are the caller's shapes, which is
/// why the encoder pads a batch to the longest member before it runs inference.
/// </summary>
public readonly struct OnnxTokenizedText
{
    private readonly int[]? _ids;
    private readonly int[]? _typeIds;

    /// <summary>Wraps token ids and, optionally, one segment id per token.</summary>
    /// <param name="ids">Token ids; cannot be <see langword="null"/>.</param>
    /// <param name="typeIds">
    /// Segment ids, or <see langword="null"/> when the model does not take a <c>token_type_ids</c>
    /// input. When present, the length must equal <paramref name="ids"/>'s.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="ids"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The two arrays differ in length.</exception>
    public OnnxTokenizedText(int[] ids, int[]? typeIds = null)
    {
        ArgumentNullException.ThrowIfNull(ids);

        if (typeIds is not null && typeIds.Length != ids.Length)
            throw new ArgumentException("Token ids and segment ids must have the same length.", nameof(typeIds));

        _ids = ids;
        _typeIds = typeIds;
    }

    /// <summary>Token ids, in order. Empty for a default value.</summary>
    public ReadOnlyMemory<int> Ids => _ids ?? ReadOnlyMemory<int>.Empty;

    /// <summary>Segment ids, in order. Empty when <see cref="HasTypeIds"/> is <see langword="false"/>.</summary>
    public ReadOnlyMemory<int> TypeIds => _typeIds ?? ReadOnlyMemory<int>.Empty;

    /// <summary>Number of tokens.</summary>
    public int Length => _ids?.Length ?? 0;

    /// <summary>Whether the tokenizer produced a segment id for every token.</summary>
    public bool HasTypeIds => _typeIds is not null;
}

/// <summary>
/// The tokenizer seam an ONNX encoder needs: text in, ids out. LexiSharp does not ship a model
/// vocabulary, so the implementation carries the checkpoint's own rules — WordPiece, SentencePiece
/// or anything else — and must be the same one at indexing and at query time.
/// </summary>
/// <remarks>
/// Implementations are called concurrently by the encoder and must be thread-safe, or the caller
/// must serialise them. <see cref="BertWordPieceTokenizer"/> is the shipped implementation; a
/// SentencePiece checkpoint needs its own, since the vocabulary and the normalization rules are
/// part of the checkpoint rather than of this package.
/// </remarks>
public interface IOnnxTokenizer
{
    /// <summary>
    /// Opaque identity of the tokenizer's vocabulary and rules, or <see langword="null"/> when the
    /// implementation cannot provide one. The encoder folds it into its embedding signature so a
    /// persisted index built with another vocabulary is refused.
    /// </summary>
    string? Fingerprint => null;

    /// <summary>Tokenizes one text, adding the model's special tokens and truncating to <paramref name="maxSequenceLength"/>.</summary>
    /// <param name="text">Text to tokenize.</param>
    /// <param name="maxSequenceLength">Hard cap on the returned token count, special tokens included.</param>
    OnnxTokenizedText Encode(string text, int maxSequenceLength);

    /// <summary>Tokenizes a (query, document) pair into one sequence, as a cross-encoder expects.</summary>
    /// <param name="query">The query side of the pair.</param>
    /// <param name="document">The document side of the pair.</param>
    /// <param name="maxSequenceLength">Hard cap on the merged token count.</param>
    /// <param name="maxQueryTokens">Cap on the query side, so a long query cannot crowd out the document.</param>
    OnnxTokenizedText EncodePair(string query, string document, int maxSequenceLength, int maxQueryTokens);
}
