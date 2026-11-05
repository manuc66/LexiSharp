namespace LexiSharp.Core;

/// <summary>
/// Asynchronous counterpart of <see cref="ICrossEncoderScorer"/>: scores a (query, document) pair
/// without blocking the caller. Use this when the model is remote or otherwise async, so a
/// pipeline can await it instead of blocking a thread on sync-over-async.
/// </summary>
/// <remarks>
/// <para>
/// LexiSharp's core search path (<see cref="ITextSearchEngine.Search(string, SearchOptions?, CancellationToken)"/>)
/// is synchronous, so an async scorer still has to be awaited at the engine boundary by the caller
/// — this seam does not make the whole pipeline async. It exists so a client holding an async
/// model (an HTTP endpoint, a gRPC service) can score pairs and rerank candidate lists without
/// blocking its own async code, and so an <see cref="IAsyncReranker"/> can be built on top of it.
/// </para>
/// <para>
/// The same score conventions apply: higher is more relevant, <c>0</c> means "not a match",
/// NaN/infinite scores are rejected by consuming rerankers.
/// </para>
/// </remarks>
public interface IAsyncCrossEncoderScorer
{
    /// <summary>Human readable name of the underlying model, e.g. <c>"cross-encoder/ms-marco-MiniLM"</c>.</summary>
    string Name { get; }

    /// <summary>Judges how relevant <paramref name="document"/> is to <paramref name="query"/>.</summary>
    Task<double> ScoreAsync(
        string query,
        SearchDocument document,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Adapts a synchronous <see cref="ICrossEncoderScorer"/> to <see cref="IAsyncCrossEncoderScorer"/>
/// by running it on the calling thread inside a completed task. Useful for reusing an in-process
/// model (ONNX) behind an async consumer.
/// </summary>
public sealed class SyncCrossEncoderScorerAdapter : IAsyncCrossEncoderScorer
{
    private readonly ICrossEncoderScorer _inner;

    /// <param name="inner">The synchronous scorer to adapt.</param>
    public SyncCrossEncoderScorerAdapter(ICrossEncoderScorer inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <inheritdoc />
    public string Name => _inner.Name;

    /// <inheritdoc />
    public Task<double> ScoreAsync(string query, SearchDocument document, CancellationToken cancellationToken = default) =>
        Task.FromResult(_inner.Score(query, document));
}
