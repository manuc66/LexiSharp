using LexiSharp.Core;

namespace LexiSharp.Postgres;

/// <summary>
/// Optional capability of an <see cref="IEmbeddingProvider"/> used by a multi-column
/// <see cref="PostgresVectorSearchEngine"/>: the provider is told <b>which embedding column</b>
/// the text is being encoded for, on both sides (query and passage). This lets a provider
/// encode the same text differently per channel — e.g. center the query in each column's own
/// space (<c>GlobalCenteredPredictionStrategy</c>-style) — which is impossible when the engine
/// computes one query vector and reuses it across every column.
/// </summary>
/// <remarks>
/// The column provided is the logical label (a key of
/// <see cref="PostgresVectorOptions.EmbeddingColumns"/>), not the SQL column name. Providers
/// that do not care per channel simply do not implement this interface; the engine then falls
/// back to <see cref="IEmbeddingProvider.GetTextEmbeddingAsync(string, EmbeddingUse, CancellationToken)"/>
/// and computes the query vector once.
/// </remarks>
public interface IColumnAwareEmbeddingProvider : IEmbeddingProvider
{
    /// <summary>Embeds a single text for the given role <b>and embedding column</b>.</summary>
    /// <param name="text">Text to embed.</param>
    /// <param name="use">Whether the text is a query or an indexed passage.</param>
    /// <param name="column">Logical embedding column label (e.g. <c>title</c>, <c>description</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
        string text,
        EmbeddingUse use,
        string column,
        CancellationToken cancellationToken = default);
}