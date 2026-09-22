namespace LexiSharp.Core;

/// <summary>
/// Optional capability of a <see cref="ITextSearchEngine"/> that can enumerate the identifiers
/// of the documents it currently holds, without materializing the full documents.
/// </summary>
/// <remarks>
/// Useful for computing a "what is still indexed / what has disappeared" diff between the
/// engine's current state and an external ledger. Persistent engines yield pages of ids
/// (keyset pagination) so the caller can stream over corpora of any size with bounded memory.
/// Implementations are detected with pattern matching (<c>engine is IListableSearchEngine</c>).
/// </remarks>
public interface IListableSearchEngine
{
    /// <summary>Streams the ids of every stored document, in stable (id) order.</summary>
    /// <param name="batchSize">Number of ids fetched from the backing store per page.</param>
    /// <param name="cancellationToken">Cancellation for the enumeration.</param>
    IAsyncEnumerable<string> ListDocumentIdsAsync(
        int batchSize = 1000,
        CancellationToken cancellationToken = default);

    /// <summary>Gathers every stored document id into a single list.</summary>
    /// <param name="batchSize">Number of ids fetched from the backing store per page.</param>
    IReadOnlyList<string> ListDocumentIds(int batchSize = 1000);
}