namespace LexiSharp.Onnx;

/// <summary>
/// Normalization rules for <see cref="BertWordPieceTokenizer"/>. The BERT family does not agree on
/// them: <c>bert-base-uncased</c> lowercases and strips accents, <c>bert-base-cased</c> does
/// neither, and some fine-tunes fold only one of the two.
/// </summary>
public sealed class BertWordPieceOptions
{
    /// <summary>Whether to lowercase before WordPiece. Defaults to <see langword="true"/>.</summary>
    public bool LowerCase { get; init; } = true;

    /// <summary>Whether to strip diacritics (NFD then drop non-spacing marks). Defaults to <see langword="true"/>.</summary>
    public bool FoldAccents { get; init; } = true;
}
