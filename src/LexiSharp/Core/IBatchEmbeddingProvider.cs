namespace LexiSharp.Core;

/// <summary>
/// An <see cref="IEmbeddingProvider"/> that can embed several texts in one call. Engines that
/// index a corpus check for this interface and use it instead of calling
/// <see cref="IEmbeddingProvider.GetTextEmbeddingAsync"/> once per document, which is what makes a
/// batched ONNX session or a batch endpoint pay off.
/// </summary>
/// <remarks>
/// The interface is additive: an existing <see cref="IEmbeddingProvider"/> keeps working, and a
/// consumer that does not implement it is embedded one text at a time. Implementations must return
/// one vector per input, in the same order, and all vectors must match <see cref="IEmbeddingProvider.Dimension"/>.
/// <para>
/// The batch is a hint, not a contract on size: an engine may pass as many texts as its input has,
/// so implementations should either accept large batches or split them internally.
/// </para>
/// </remarks>
public interface IBatchEmbeddingProvider : IEmbeddingProvider
{
    /// <summary>Embeds several texts for the given role, in input order.</summary>
    /// <param name="texts">Texts to embed; the list is not mutated.</param>
    /// <param name="use">Whether these are queries or indexed passages.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One vector per input, in the same order.</returns>
    Task<IReadOnlyList<ReadOnlyMemory<float>>> GetTextEmbeddingsAsync(
        IReadOnlyList<string> texts,
        EmbeddingUse use,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A batch provider that also knows which column the texts belong to — the pair of capabilities a
/// PostgreSQL engine with <c>EmbeddingColumns</c> needs to batch one column at a time instead of
/// per document.
/// </summary>
public interface IColumnAwareBatchEmbeddingProvider : IBatchEmbeddingProvider
{
    /// <summary>Embeds several texts for one column, in input order.</summary>
    /// <param name="texts">Texts to embed; the list is not mutated.</param>
    /// <param name="use">Whether these are queries or indexed passages.</param>
    /// <param name="column">The column label the engine is populating.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<ReadOnlyMemory<float>>> GetTextEmbeddingsAsync(
        IReadOnlyList<string> texts,
        EmbeddingUse use,
        string column,
        CancellationToken cancellationToken = default);
}
