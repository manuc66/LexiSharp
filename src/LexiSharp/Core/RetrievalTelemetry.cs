using System.Diagnostics;
using System.Globalization;

namespace LexiSharp.Core;

/// <summary>
/// The opt-in bundle an engine holds to report what it did: a <see cref="RetrievalLog"/> sink, an
/// <see cref="IRetrievalMetrics"/> collector, or both. Pass it to an engine's
/// <c>telemetry</c> constructor parameter.
/// </summary>
/// <remarks>
/// <para>
/// <b>Zero cost when unused.</b> <see cref="IsEnabled"/> is false for the default
/// <see cref="None"/>, and an engine checks it once per search before touching a timer, so an
/// un-instrumented engine takes no timestamp, allocates nothing and calls nothing. The
/// measurements that do get taken are taken with <see cref="Stopwatch.GetTimestamp"/> and
/// converted with <see cref="Stopwatch.Frequency"/>, not with <c>DateTime.Now</c>.
/// </para>
/// <para>
/// <b>Thread-safe by delegation.</b> A telemetry is immutable after construction and holds no
/// per-query state; concurrent searches on one engine share it safely as long as the sinks are.
/// </para>
/// <para>
/// The methods are public so a custom <see cref="ITextSearchEngine"/> can report through the same
/// sinks as the built-in engines, rather than growing a second, parallel instrumentation path.
/// </para>
/// </remarks>
public sealed class RetrievalTelemetry
{
    /// <summary>The do-nothing instance: <see cref="IsEnabled"/> is false, nothing is recorded.</summary>
    public static readonly RetrievalTelemetry None = new(null, null);

    /// <summary>Creates a telemetry from an optional log sink and an optional metrics collector.</summary>
    /// <param name="log">Sink for events, or <c>null</c>.</param>
    /// <param name="metrics">Collector for measurements, or <c>null</c>.</param>
    public RetrievalTelemetry(RetrievalLog? log = null, IRetrievalMetrics? metrics = null)
    {
        Log = log;
        Metrics = metrics;
    }

    /// <summary>Creates a telemetry that only logs.</summary>
    public static RetrievalTelemetry Logging(RetrievalLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        return new RetrievalTelemetry(log, null);
    }

    /// <summary>Creates a telemetry that only collects metrics.</summary>
    public static RetrievalTelemetry Measuring(IRetrievalMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        return new RetrievalTelemetry(null, metrics);
    }

    /// <summary>The log sink, or <c>null</c>.</summary>
    public RetrievalLog? Log { get; }

    /// <summary>The metrics collector, or <c>null</c>.</summary>
    public IRetrievalMetrics? Metrics { get; }

    /// <summary>
    /// Whether either sink is present. Engines gate every measurement on this, so a search against
    /// an un-instrumented engine does not even read the clock.
    /// </summary>
    public bool IsEnabled => Log is not null || Metrics is not null;

    /// <summary>
    /// Reads the high-resolution timer. Call this right before the work to measure and pass the
    /// result to the matching <c>...Completed</c> method.
    /// </summary>
    public static long StartTimer() => Stopwatch.GetTimestamp();

    /// <summary>
    /// Converts the span since <paramref name="started"/> into milliseconds. Safe to call with a
    /// timestamp taken when telemetry was disabled; the value is simply unused then.
    /// </summary>
    public static double ElapsedMs(long started) =>
        (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

    /// <summary>Reports a completed search and its latency.</summary>
    /// <param name="engine">Name of the engine that served the search.</param>
    /// <param name="started">Timestamp from <see cref="StartTimer"/>, taken before the search began.</param>
    /// <param name="resultCount">Number of results returned.</param>
    /// <param name="candidateCount">
    /// Documents the engine actually scored, when it knows; <c>0</c> otherwise. Engine-specific:
    /// an engine may not be able to separate "candidates" from "documents scanned" cheaply.
    /// </param>
    /// <param name="logLevel">Severity for the log line; defaults to <see cref="RetrievalLogLevel.Information"/>.</param>
    public void SearchCompleted(
        string engine,
        long started,
        int resultCount,
        int candidateCount = 0,
        RetrievalLogLevel logLevel = RetrievalLogLevel.Information)
    {
        if (!IsEnabled)
            return;

        double elapsed = ElapsedMs(started);

        Metrics?.RecordSearch(engine, resultCount, elapsed);

        Log?.Invoke(new RetrievalLogEvent(
            logLevel,
            engine,
            RetrievalLogEvents.Search,
            string.Create(
                CultureInfo.InvariantCulture,
                $"query returned {resultCount} result(s) in {elapsed:0.###} ms"),
            elapsed,
            resultCount));

        if (candidateCount > 0)
        {
            // Candidate enumeration and scoring are one loop in the built-in engines, so the
            // "candidates" stage is reported over the whole search span rather than a separately
            // measured one. Its duration therefore equals the search duration; only the count is
            // new information, and that is what this stage exists to carry.
            StageCompleted(engine, "candidates", candidateCount, started);
        }
    }

    /// <summary>Reports one pipeline stage and its latency.</summary>
    /// <param name="engine">Name of the engine that ran the stage.</param>
    /// <param name="stage">Stage label (low-cardinality: <c>rerank</c>, <c>merge</c>, a source name, ...).</param>
    /// <param name="itemCount">Items the stage processed.</param>
    /// <param name="started">Timestamp from <see cref="StartTimer"/>, taken before the stage began.</param>
    /// <param name="logLevel">Severity for the log line; defaults to <see cref="RetrievalLogLevel.Debug"/>.</param>
    public void StageCompleted(
        string engine,
        string stage,
        int itemCount,
        long started,
        RetrievalLogLevel logLevel = RetrievalLogLevel.Debug)
    {
        if (!IsEnabled)
            return;

        double elapsed = ElapsedMs(started);

        Metrics?.RecordStage(engine, stage, itemCount, elapsed);

        Log?.Invoke(new RetrievalLogEvent(
            logLevel,
            engine,
            RetrievalLogEvents.Stage,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{stage} handled {itemCount} item(s) in {elapsed:0.###} ms"),
            elapsed,
            itemCount));
    }

    /// <summary>Reports a warning about a degraded but answered search.</summary>
    /// <param name="engine">Name of the engine reporting the condition.</param>
    /// <param name="message">Invariant-formatted description of the condition.</param>
    public void Warning(string engine, string message)
    {
        Log?.Invoke(new RetrievalLogEvent(
            RetrievalLogLevel.Warning, engine, RetrievalLogEvents.Search, message, 0, 0));
    }

    /// <summary>Reports the current size of an index after a mutation.</summary>
    /// <param name="engine">Name of the engine whose index changed.</param>
    /// <param name="index">The index, for its counts.</param>
    public void IndexChanged(string engine, ITextIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);

        if (!IsEnabled)
            return;

        int documents = index.Count;
        int vocabulary = index.VocabularySize;

        Metrics?.RecordIndex(engine, documents, vocabulary);

        Log?.Invoke(new RetrievalLogEvent(
            RetrievalLogLevel.Debug,
            engine,
            RetrievalLogEvents.Index,
            string.Create(
                CultureInfo.InvariantCulture,
                $"index holds {documents} document(s), {vocabulary} term(s)"),
            0,
            documents));
    }
}
