namespace LexiSharp.Core;

/// <summary>
/// Asynchronous counterpart of <see cref="IReranker"/>: re-orders a candidate list without
/// blocking the caller. See <see cref="IAsyncCrossEncoderScorer"/> for the rationale and the
/// limits of an async seam in a synchronous search core.
/// </summary>
public interface IAsyncReranker
{
    /// <summary>Human readable name of the strategy.</summary>
    string Name { get; }

    /// <summary>Re-ranks the candidate list for a query, best-first, without mutating the input.</summary>
    Task<IReadOnlyList<SearchResult>> RerankAsync(
        string query,
        IReadOnlyList<SearchResult> candidates,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Adapts a synchronous <see cref="IReranker"/> to <see cref="IAsyncReranker"/> by running it
/// on the calling thread inside a completed task.
/// </summary>
public sealed class SyncRerankerAdapter : IAsyncReranker
{
    private readonly IReranker _inner;

    /// <param name="inner">The synchronous reranker to adapt.</param>
    public SyncRerankerAdapter(IReranker inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <inheritdoc />
    public string Name => _inner.Name;

    /// <inheritdoc />
    public Task<IReadOnlyList<SearchResult>> RerankAsync(
        string query,
        IReadOnlyList<SearchResult> candidates,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(_inner.Rerank(query, candidates));
}
