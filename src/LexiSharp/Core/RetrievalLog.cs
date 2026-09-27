namespace LexiSharp.Core;

/// <summary>Severity of a <see cref="RetrievalLogEvent"/>.</summary>
public enum RetrievalLogLevel
{
    /// <summary>Per-stage detail; off by default in most sinks.</summary>
    Debug = 0,

    /// <summary>One line per completed search: the normal operational signal.</summary>
    Information = 1,

    /// <summary>Something degraded but still answered: empty expansion, truncated trace, slow rerank.</summary>
    Warning = 2,
}

/// <summary>Stable event names carried by <see cref="RetrievalLogEvent.Event"/>.</summary>
public static class RetrievalLogEvents
{
    /// <summary>A search completed.</summary>
    public const string Search = "search";

    /// <summary>One pipeline stage of a search completed.</summary>
    public const string Stage = "stage";

    /// <summary>The index was mutated (documents added, removed, replaced, cleared).</summary>
    public const string Index = "index";

    /// <summary>Engine-level configuration, reported once when the engine is constructed.</summary>
    public const string Configure = "configure";
}

/// <summary>
/// One observability event emitted by a search engine. Immutable and allocation-free to build,
/// so a sink can consume it without the engine paying more than the call itself.
/// </summary>
/// <param name="Level">Severity, see <see cref="RetrievalLogLevel"/>.</param>
/// <param name="Engine">Name of the engine that emitted the event (e.g. <c>RankedTextSearchEngine</c>).</param>
/// <param name="Event">What happened, one of the <see cref="RetrievalLogEvents"/> constants.</param>
/// <param name="Message">Invariantly-formatted human-readable detail.</param>
/// <param name="ElapsedMs">Wall-clock duration of the operation, in milliseconds; <c>0</c> for events that are not timed.</param>
/// <param name="Count">
/// Event-dependent count: results returned for <see cref="RetrievalLogEvents.Search"/>, items
/// handled for <see cref="RetrievalLogEvents.Stage"/>, documents in the index for
/// <see cref="RetrievalLogEvents.Index"/>.
/// </param>
public readonly record struct RetrievalLogEvent(
    RetrievalLogLevel Level,
    string Engine,
    string Event,
    string Message,
    double ElapsedMs,
    int Count);

/// <summary>
/// Sink for the events a <see cref="RetrievalTelemetry"/> produces.
/// </summary>
/// <remarks>
/// <para>
/// The core takes a delegate rather than <c>Microsoft.Extensions.Logging.ILogger</c> to stay
/// dependency-free; <c>LexiSharp.AspNetCore</c> ships the adapter. The parameter is passed by
/// value on purpose: the delegate is invoked a handful of times per <i>query</i>, never inside
/// the per-candidate scoring loop, so <c>in</c>/<c>ref</c> would only make lambda adaptation
/// worse (a lambda over an <c>in</c> parameter must declare it explicitly) without measurable gain.
/// </para>
/// <para>
/// A sink must be safe to call from concurrent searches: one engine serves many callers at once.
/// </para>
/// </remarks>
public delegate void RetrievalLog(RetrievalLogEvent entry);
