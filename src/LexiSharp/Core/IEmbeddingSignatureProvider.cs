namespace LexiSharp.Core;

/// <summary>
/// An <see cref="IEmbeddingProvider"/> that can identify the model and preprocessing that produce
/// its vectors. The signature is what lets a persisted index refuse to compare vectors from a
/// different checkpoint, a different pooling rule or a different prefix convention — a mismatch
/// that would otherwise surface as silently worse search results.
/// </summary>
/// <remarks>
/// The value is opaque to LexiSharp: consumers compare it for equality and store it beside their
/// vectors. Implementations should derive it from the model content, the tokenizer and every
/// option that changes a vector (pooling, normalization, prefixes, truncation), and return
/// <see langword="null"/> when no stable identity is available.
/// </remarks>
public interface IEmbeddingSignatureProvider
{
    /// <summary>Opaque identity of the model and its preprocessing, or <see langword="null"/> when unknown.</summary>
    string? EmbeddingSignature { get; }
}
