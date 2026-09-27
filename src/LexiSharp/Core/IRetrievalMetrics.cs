namespace LexiSharp.Core;

/// <summary>
/// Receives the measurements a <see cref="RetrievalTelemetry"/> takes. Implement it to ship
/// numbers to a metrics backend (OpenTelemetry, Prometheus, StatsD, ...).
/// </summary>
/// <remarks>
/// <para>
/// The three callbacks are the whole contract. They are called a few times per query at most
/// and never inside a per-candidate loop, so an implementation does not need to be allocation-free
/// — but it does need to be <b>thread-safe</b>: concurrent searches call it concurrently.
/// </para>
/// <para>
/// Implementations should not throw. A metrics backend being down must never fail a search; if
/// an implementation can fail, catch inside it.
/// </para>
/// </remarks>
public interface IRetrievalMetrics
{
    /// <summary>A search completed and returned a page.</summary>
    /// <param name="engine">Name of the engine that served the search.</param>
    /// <param name="resultCount">Number of results on the returned page.</param>
    /// <param name="elapsedMs">Wall-clock duration of the search, in milliseconds.</param>
    void RecordSearch(string engine, int resultCount, double elapsedMs);

    /// <summary>One pipeline stage of a search completed.</summary>
    /// <param name="engine">Name of the engine that ran the stage.</param>
    /// <param name="stage">
    /// Stage label. Built-in engines use <see cref="RetrievalLogEvents.Stage"/> with a detail
    /// such as <c>rerank</c>, <c>merge</c> or a source engine's name; treat it as an opaque,
    /// low-cardinality string.
    /// </param>
    /// <param name="itemCount">Number of items the stage processed (candidates in, results out, ...).</param>
    /// <param name="elapsedMs">Wall-clock duration of the stage, in milliseconds.</param>
    void RecordStage(string engine, string stage, int itemCount, double elapsedMs);

    /// <summary>The index changed size.</summary>
    /// <param name="engine">Name of the engine whose index changed.</param>
    /// <param name="documentCount">Number of documents in the index after the change.</param>
    /// <param name="vocabularySize">Number of distinct terms in the index after the change.</param>
    void RecordIndex(string engine, int documentCount, int vocabularySize);
}
