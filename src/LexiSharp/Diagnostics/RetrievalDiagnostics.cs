using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using LexiSharp.Core;

namespace LexiSharp.Diagnostics;

/// <summary>
/// Turns the <see cref="IRetrievalMetrics"/> seam into <see cref="System.Diagnostics.Metrics.Meter"/>
/// instruments and <see cref="ActivitySource"/> spans, using only types that ship with .NET.
/// </summary>
/// <remarks>
/// <para>
/// <b>In-box, no exporter.</b> A .NET host that already runs the OpenTelemetry SDK — or any other
/// collector that subscribes to <see cref="Meter"/> and <see cref="ActivitySource"/> — gets the
/// measurements without this library referencing a package: attach the exporter in the host, not
/// here. The meter is named <see cref="MeterName"/> and the source <see cref="ActivitySourceName"/>,
/// so the whole surface is wired by those two names.
/// </para>
/// <para>
/// <b>Instruments.</b> A duration is recorded in <b>seconds</b>, the unit the OpenTelemetry
/// semantic conventions use for duration instruments (for example
/// <c>http.server.request.duration</c>). No semantic convention defines a search instrument, so the
/// names below are this library's own; the nearest conventions describe an HTTP request or a
/// database call, and reusing one would misdescribe a search. An application exposing search over
/// HTTP should keep the HTTP server's own <c>http.server.request.duration</c> for the request and
/// read these for the retrieval work inside it.
/// </para>
/// <list type="table">
/// <listheader><term>instrument</term><description>kind, unit, recorded from</description></listheader>
/// <item>
/// <term><see cref="SearchDurationInstrumentName"/></term>
/// <description><see cref="Histogram{T}"/> of seconds, from <see cref="RecordSearch"/>.</description>
/// </item>
/// <item>
/// <term><see cref="SearchCountInstrumentName"/></term>
/// <description><see cref="Counter{T}"/> of completed searches, from <see cref="RecordSearch"/>.</description>
/// </item>
/// <item>
/// <term><see cref="SearchResultsInstrumentName"/></term>
/// <description><see cref="Histogram{T}"/> of page sizes, from <see cref="RecordSearch"/>.</description>
/// </item>
/// <item>
/// <term><see cref="StageDurationInstrumentName"/></term>
/// <description><see cref="Histogram{T}"/> of seconds, from <see cref="RecordStage"/>.</description>
/// </item>
/// <item>
/// <term><see cref="StageCountInstrumentName"/></term>
/// <description><see cref="Counter{T}"/> of stage runs, from <see cref="RecordStage"/>.</description>
/// </item>
/// <item>
/// <term><see cref="StageItemsInstrumentName"/></term>
/// <description><see cref="Histogram{T}"/> of items a stage processed, from <see cref="RecordStage"/>.</description>
/// </item>
/// <item>
/// <term><see cref="IndexDocumentsInstrumentName"/></term>
/// <description><see cref="ObservableGauge{T}"/> of documents, last value reported by <see cref="RecordIndex"/>.</description>
/// </item>
/// <item>
/// <term><see cref="IndexVocabularyInstrumentName"/></term>
/// <description><see cref="ObservableGauge{T}"/> of distinct terms, last value reported by <see cref="RecordIndex"/>.</description>
/// </item>
/// </list>
/// <para>
/// <b>Cardinality is part of the contract.</b> The only tags this adapter adds are
/// <see cref="EngineTagName"/> and <see cref="StageTagName"/>. Both values are caller-supplied, so
/// they must be <b>low-cardinality bounded sets</b>: an engine name is one per engine type you
/// construct, and a stage label is one per pipeline position (<c>retrieve</c>, <c>merge</c>,
/// <c>rerank:&lt;name&gt;</c>, or one of your <c>sourceNames</c>). They must not be built from
/// query text, result ids, document ids, user ids or any other per-request value — every distinct
/// value becomes a series in the backend, and an unbounded tag turns a metrics pipeline into a
/// memory leak. Query text and document ids belong in a trace or a log line, never here.
/// </para>
/// <para>
/// <b>Spans.</b> <see cref="StartSearch"/> and <see cref="StartStage"/> open the spans; the metrics
/// callbacks cannot, because <see cref="IRetrievalMetrics"/> reports <i>after</i> the work and
/// carries no start timestamp or correlation id. Nest a stage scope inside a search scope and the
/// stage span is its child through <see cref="Activity.Current"/>, so a traced pipeline lines up
/// with the stages <c>SearchTrace</c> records. Both return <c>null</c> when no listener is attached,
/// which makes <c>using var span = diagnostics.StartSearch(...)</c> free in an uninstrumented host;
/// neither throws.
/// </para>
/// <para>
/// <b>Lifetime.</b> One instance per process (or per meter scope), constructed once and passed to
/// every engine's <see cref="RetrievalTelemetry"/>. Do not construct one per search: a meter that
/// short-lived loses its instruments, and the gauges would start over. <see cref="Dispose"/> releases
/// the meter and the source; record nothing afterwards.
/// </para>
/// </remarks>
public sealed class RetrievalDiagnostics : IRetrievalMetrics, IDisposable
{
    /// <summary>Name of the <see cref="Meter"/>: subscribe to this to receive the instruments.</summary>
    public const string MeterName = "LexiSharp";

    /// <summary>Name of the <see cref="ActivitySource"/>: subscribe to this to receive the spans.</summary>
    public const string ActivitySourceName = "LexiSharp.Search";

    /// <summary>Name of the span a <see cref="StartSearch"/> scope opens.</summary>
    public const string SearchActivityName = "search";

    /// <summary>Name of the span a <see cref="StartStage"/> scope opens.</summary>
    public const string StageActivityName = "search.stage";

    /// <summary>Tag holding the engine name on every instrument and span.</summary>
    public const string EngineTagName = "search.engine";

    /// <summary>Tag holding the stage label on a stage instrument or span.</summary>
    public const string StageTagName = "search.stage";

    /// <summary>Histogram of completed-search durations, in seconds.</summary>
    public const string SearchDurationInstrumentName = "search.duration";

    /// <summary>Counter of completed searches.</summary>
    public const string SearchCountInstrumentName = "search.count";

    /// <summary>Histogram of results returned by a completed search.</summary>
    public const string SearchResultsInstrumentName = "search.results";

    /// <summary>Histogram of completed pipeline-stage durations, in seconds.</summary>
    public const string StageDurationInstrumentName = "search.stage.duration";

    /// <summary>Counter of completed pipeline-stage runs.</summary>
    public const string StageCountInstrumentName = "search.stage.count";

    /// <summary>Histogram of items a completed pipeline stage processed.</summary>
    public const string StageItemsInstrumentName = "search.stage.items";

    /// <summary>Gauge of documents in an engine's index, as last reported.</summary>
    public const string IndexDocumentsInstrumentName = "index.documents";

    /// <summary>Gauge of distinct terms in an engine's index, as last reported.</summary>
    public const string IndexVocabularyInstrumentName = "index.vocabulary";

    private const string UnitSeconds = "s";
    private const string UnitSearches = "{search}";
    private const string UnitResults = "{result}";
    private const string UnitInvocations = "{invocation}";
    private const string UnitItems = "{item}";
    private const string UnitDocuments = "{document}";
    private const string UnitTerms = "{term}";
    private const string UnknownLabel = "unknown";

    private readonly ConcurrentDictionary<string, IndexSize> _indexSizes = new(StringComparer.Ordinal);

    private readonly Histogram<double> _searchDuration;
    private readonly Counter<long> _searchCount;
    private readonly Histogram<int> _searchResults;
    private readonly Histogram<double> _stageDuration;
    private readonly Counter<long> _stageCount;
    private readonly Histogram<int> _stageItems;
    private readonly ObservableGauge<long> _documentsGauge;
    private readonly ObservableGauge<long> _vocabularyGauge;

    private int _disposed;

    /// <summary>Creates the meter, its instruments and the activity source.</summary>
    public RetrievalDiagnostics()
    {
        string? version = typeof(RetrievalDiagnostics).Assembly.GetName().Version?.ToString();

        Meter = new Meter(MeterName, version);
        ActivitySource = new ActivitySource(ActivitySourceName, version);

        _searchDuration = Meter.CreateHistogram<double>(
            SearchDurationInstrumentName,
            UnitSeconds,
            "Wall-clock duration of one completed search.");

        _searchCount = Meter.CreateCounter<long>(
            SearchCountInstrumentName,
            UnitSearches,
            "Number of completed searches.");

        _searchResults = Meter.CreateHistogram<int>(
            SearchResultsInstrumentName,
            UnitResults,
            "Number of results on the page returned by a completed search.");

        _stageDuration = Meter.CreateHistogram<double>(
            StageDurationInstrumentName,
            UnitSeconds,
            "Wall-clock duration of one completed pipeline stage.");

        _stageCount = Meter.CreateCounter<long>(
            StageCountInstrumentName,
            UnitInvocations,
            "Number of completed pipeline-stage runs.");

        _stageItems = Meter.CreateHistogram<int>(
            StageItemsInstrumentName,
            UnitItems,
            "Number of items a completed pipeline stage processed.");

        _documentsGauge = Meter.CreateObservableGauge(
            IndexDocumentsInstrumentName,
            ObserveDocuments,
            UnitDocuments,
            "Documents in an engine's index, as last reported.");

        _vocabularyGauge = Meter.CreateObservableGauge(
            IndexVocabularyInstrumentName,
            ObserveVocabulary,
            UnitTerms,
            "Distinct terms in an engine's index, as last reported.");
    }

    /// <summary>
    /// The meter that owns the instruments. Subscribe with a
    /// <see cref="MeterListener"/> filtered on <see cref="MeterName"/>; the adapter owns its lifetime.
    /// </summary>
    public Meter Meter { get; }

    /// <summary>
    /// The source that opens the search and stage spans. Subscribe with an
    /// <see cref="ActivityListener"/> filtered on <see cref="ActivitySourceName"/>; the adapter owns
    /// its lifetime.
    /// </summary>
    public ActivitySource ActivitySource { get; }

    /// <inheritdoc />
    /// <remarks>
    /// Records <see cref="SearchDurationInstrumentName"/> in seconds, increments
    /// <see cref="SearchCountInstrumentName"/> and records <see cref="SearchResultsInstrumentName"/>,
    /// all tagged with <see cref="EngineTagName"/>. A non-finite or negative duration is recorded as
    /// <c>0</c> rather than poisoning a histogram, and a null or empty engine label is recorded as
    /// <c>unknown</c> rather than throwing out of a search.
    /// </remarks>
    public void RecordSearch(string engine, int resultCount, double elapsedMs)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var tag = new KeyValuePair<string, object?>(EngineTagName, Normalize(engine));

        _searchDuration.Record(ToSeconds(elapsedMs), tag);
        _searchCount.Add(1, tag);
        _searchResults.Record(Math.Max(resultCount, 0), tag);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Records <see cref="StageDurationInstrumentName"/> in seconds, increments
    /// <see cref="StageCountInstrumentName"/> and records <see cref="StageItemsInstrumentName"/>,
    /// tagged with <see cref="EngineTagName"/> and <see cref="StageTagName"/>.
    /// </remarks>
    public void RecordStage(string engine, string stage, int itemCount, double elapsedMs)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        var engineTag = new KeyValuePair<string, object?>(EngineTagName, Normalize(engine));
        var stageTag = new KeyValuePair<string, object?>(StageTagName, Normalize(stage));

        _stageDuration.Record(ToSeconds(elapsedMs), engineTag, stageTag);
        _stageCount.Add(1, engineTag, stageTag);
        _stageItems.Record(Math.Max(itemCount, 0), engineTag, stageTag);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Stores the last observed size per engine; <see cref="IndexDocumentsInstrumentName"/> and
    /// <see cref="IndexVocabularyInstrumentName"/> are gauges read when a listener collects, not
    /// counters of every change. A later report for the same engine replaces the earlier one.
    /// <para>
    /// Nothing is stored, and nothing is allocated, while no listener has enabled a gauge: a gauge
    /// cannot replay a size it was never told to keep, so a listener that attaches after the last
    /// index change sees that engine at the next report rather than at collection time. With a
    /// listener attached, the first report for an engine allocates one holder; later reports update
    /// it in place.
    /// </para>
    /// </remarks>
    public void RecordIndex(string engine, int documentCount, int vocabularySize)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        if (!_documentsGauge.Enabled && !_vocabularyGauge.Enabled)
            return;

        string label = Normalize(engine);

        if (!_indexSizes.TryGetValue(label, out IndexSize? size))
            size = _indexSizes.GetOrAdd(label, static _ => new IndexSize());

        Volatile.Write(ref size.Packed, Pack(documentCount, vocabularySize));
    }

    /// <summary>
    /// Opens a span for one search, tagged with <see cref="EngineTagName"/>.
    /// </summary>
    /// <param name="engine">Engine name, a low-cardinality label.</param>
    /// <returns>
    /// The span to dispose when the search ends, or <c>null</c> when no listener is attached. Use it
    /// as <c>using var span = diagnostics.StartSearch(engine);</c>.
    /// </returns>
    /// <remarks>
    /// The span is a child of <see cref="Activity.Current"/> if one exists. It never throws and
    /// allocates nothing when nobody listens.
    /// </remarks>
    public Activity? StartSearch(string engine)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return null;

        Activity? activity = ActivitySource.StartActivity(SearchActivityName, ActivityKind.Internal);
        activity?.SetTag(EngineTagName, Normalize(engine));

        return activity;
    }

    /// <summary>
    /// Opens a child span for one pipeline stage, tagged with <see cref="EngineTagName"/> and
    /// <see cref="StageTagName"/>.
    /// </summary>
    /// <param name="engine">Engine name, a low-cardinality label.</param>
    /// <param name="stage">
    /// Stage label, a low-cardinality bounded set — the same labels <see cref="RecordStage"/> receives.
    /// </param>
    /// <returns>
    /// The span to dispose when the stage ends, or <c>null</c> when no listener is attached.
    /// </returns>
    /// <remarks>
    /// Call it inside a <see cref="StartSearch"/> scope to make the stage span its child, so a trace
    /// shows the pipeline the way <c>SearchTrace</c> lists its stages. It never throws and allocates
    /// nothing when nobody listens.
    /// </remarks>
    public Activity? StartStage(string engine, string stage)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return null;

        Activity? activity = ActivitySource.StartActivity(StageActivityName, ActivityKind.Internal);

        if (activity is null)
            return null;

        activity.SetTag(EngineTagName, Normalize(engine));
        activity.SetTag(StageTagName, Normalize(stage));

        return activity;
    }

    /// <summary>Disposes the meter and the activity source. Idempotent.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Meter.Dispose();
        ActivitySource.Dispose();
        GC.SuppressFinalize(this);
    }

    private IEnumerable<Measurement<long>> ObserveDocuments()
    {
        foreach (var pair in _indexSizes)
        {
            long packed = Volatile.Read(ref pair.Value.Packed);
            yield return new Measurement<long>(
                (uint)(packed >> 32),
                new KeyValuePair<string, object?>(EngineTagName, pair.Key));
        }
    }

    private IEnumerable<Measurement<long>> ObserveVocabulary()
    {
        foreach (var pair in _indexSizes)
        {
            long packed = Volatile.Read(ref pair.Value.Packed);
            yield return new Measurement<long>(
                (uint)packed,
                new KeyValuePair<string, object?>(EngineTagName, pair.Key));
        }
    }

    private static string Normalize(string? label) =>
        string.IsNullOrEmpty(label) ? UnknownLabel : label;

    private static double ToSeconds(double elapsedMs) =>
        // NaN and the infinities survive an `elapsedMs > 0` test and would put a value a backend
        // cannot aggregate into a histogram; a negative duration is a clock anomaly, not a
        // measurement, so both are recorded as the 0 floor the in-memory collector uses too.
        double.IsFinite(elapsedMs) && elapsedMs > 0 ? elapsedMs / 1000.0 : 0.0;

    private static long Pack(int documentCount, int vocabularySize) =>
        // One 64-bit write, so the two gauges never read a document count from one report beside a
        // vocabulary size from another. Both counts are non-negative ints by contract.
        ((long)(uint)Math.Max(documentCount, 0) << 32) | (uint)Math.Max(vocabularySize, 0);

    /// <summary>
    /// Mutable holder so a repeated report for an existing engine updates in place. A value type in
    /// the dictionary would allocate a replacement entry on every write, which the no-listener path
    /// must not do.
    /// </summary>
    private sealed class IndexSize
    {
        public long Packed;
    }
}
