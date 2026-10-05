using System.Collections.Concurrent;

namespace LexiSharp.Core;

/// <summary>Aggregated measurements for one engine.</summary>
/// <param name="Engine">Engine name.</param>
/// <param name="SearchCount">Number of completed searches.</param>
/// <param name="TotalElapsedMs">Sum of the search durations.</param>
/// <param name="MinElapsedMs">Shortest search observed; <c>0</c> when no search ran.</param>
/// <param name="MaxElapsedMs">Longest search observed; <c>0</c> when no search ran.</param>
/// <param name="LastResultCount">
/// Results on the page of the most recently recorded search — the last write to land, which under
/// concurrency is not necessarily the last search to finish.
/// </param>
public sealed record EngineRetrievalMetrics(
    string Engine,
    long SearchCount,
    double TotalElapsedMs,
    double MinElapsedMs,
    double MaxElapsedMs,
    int LastResultCount);

/// <summary>Aggregated measurements for one pipeline stage of one engine.</summary>
/// <param name="Engine">Engine name.</param>
/// <param name="Stage">Stage label.</param>
/// <param name="InvocationCount">Number of times the stage ran.</param>
/// <param name="TotalElapsedMs">Sum of the stage durations.</param>
/// <param name="MaxElapsedMs">Longest stage run observed.</param>
/// <param name="LastItemCount">
/// Items handled by the most recently recorded run — the last write to land, which under
/// concurrency is not necessarily the last run to finish.
/// </param>
public sealed record StageRetrievalMetrics(
    string Engine,
    string Stage,
    long InvocationCount,
    double TotalElapsedMs,
    double MaxElapsedMs,
    int LastItemCount);

/// <summary>The latest observed size of one engine's index.</summary>
/// <param name="Engine">Engine name.</param>
/// <param name="DocumentCount">Number of indexed documents.</param>
/// <param name="VocabularySize">Number of distinct terms in the vocabulary.</param>
public sealed record IndexRetrievalMetrics(string Engine, int DocumentCount, int VocabularySize);

/// <summary>A copy of every counter an <see cref="InMemoryRetrievalMetrics"/> holds.</summary>
/// <param name="SearchCount">Total searches across all engines.</param>
/// <param name="TotalElapsedMs">Total time spent searching across all engines.</param>
/// <param name="Engines">Per-engine breakdown, ordered by engine name.</param>
/// <param name="Stages">Per-stage breakdown, ordered by engine then stage name.</param>
/// <param name="Indexes">Last observed index sizes, ordered by engine name.</param>
public sealed record RetrievalMetricsSnapshot(
    long SearchCount,
    double TotalElapsedMs,
    IReadOnlyList<EngineRetrievalMetrics> Engines,
    IReadOnlyList<StageRetrievalMetrics> Stages,
    IReadOnlyList<IndexRetrievalMetrics> Indexes)
{
    /// <summary>An empty snapshot, for a collector that has not been called yet.</summary>
    public static readonly RetrievalMetricsSnapshot Empty = new(
        0, 0,
        Array.Empty<EngineRetrievalMetrics>(),
        Array.Empty<StageRetrievalMetrics>(),
        Array.Empty<IndexRetrievalMetrics>());
}

/// <summary>
/// The default <see cref="IRetrievalMetrics"/>: in-memory counters grouped by engine and stage,
/// with no external dependency. Enough to assert on in tests and to back a diagnostics endpoint in a small
/// application; for a real metrics pipeline, implement <see cref="IRetrievalMetrics"/> over your
/// backend of choice.
/// </summary>
/// <remarks>
/// <para>
/// <b>Thread-safe.</b> Concurrent searches update the counters through <see cref="Interlocked"/>
/// and per-engine lookups through a <see cref="ConcurrentDictionary{TKey, TValue}"/>, so several
/// searches can record at once. This is the one place the library claims thread-safety on
/// purpose, and <c>ConcurrentDictionary</c> + <see cref="Interlocked"/> is what backs it.
/// </para>
/// <para>
/// <b>Latency resolution.</b> Durations accumulate in whole microseconds. A search faster than
/// one microsecond therefore reads as <c>0</c> ms, which is the floor of what the timer can
/// resolve here, not a measurement error worth correcting.
/// </para>
/// </remarks>
public sealed class InMemoryRetrievalMetrics : IRetrievalMetrics
{
    private const double MicrosecondsPerMillisecond = 1000.0;

    private readonly ConcurrentDictionary<string, EngineCounters> _engines = new(StringComparer.Ordinal);
    // The explicit comparer is redundant (the tuple default is already ordinal per component) and
    // kept only so the three dictionaries read alike.
    private readonly ConcurrentDictionary<(string Engine, string Stage), StageCounters> _stages =
        new(EqualityComparer<(string Engine, string Stage)>.Default);
    private readonly ConcurrentDictionary<string, IndexCounters> _indexes = new(StringComparer.Ordinal);

    private long _searchCount;
    private long _totalElapsedMicros;
    private int _lastResultCount;

    /// <inheritdoc />
    public void RecordSearch(string engine, int resultCount, double elapsedMs)
    {
        ArgumentNullException.ThrowIfNull(engine);

        long micros = ToMicros(elapsedMs);

        Interlocked.Increment(ref _searchCount);
        Interlocked.Add(ref _totalElapsedMicros, micros);
        Interlocked.Exchange(ref _lastResultCount, resultCount);

        _engines.GetOrAdd(engine, static _ => new EngineCounters()).Record(micros, resultCount);
    }

    /// <inheritdoc />
    public void RecordStage(string engine, string stage, int itemCount, double elapsedMs)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(stage);

        long micros = ToMicros(elapsedMs);

        _stages
            .GetOrAdd((engine, stage), static _ => new StageCounters())
            .Record(micros, itemCount);
    }

    /// <inheritdoc />
    public void RecordIndex(string engine, int documentCount, int vocabularySize)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _indexes[engine] = new IndexCounters(documentCount, vocabularySize);
    }

    /// <summary>Total searches recorded across every engine.</summary>
    public long SearchCount => Interlocked.Read(ref _searchCount);

    /// <summary>Total time spent searching, in milliseconds.</summary>
    public double TotalElapsedMs => Interlocked.Read(ref _totalElapsedMicros) / MicrosecondsPerMillisecond;

    /// <summary>Results on the page of the most recent search.</summary>
    public int LastResultCount => Volatile.Read(ref _lastResultCount);

    /// <summary>
    /// A copy of every counter. Each counter is read independently, so a snapshot taken while
    /// searches are running is self-consistent per counter but not a single point in time across
    /// all of them.
    /// </summary>
    public RetrievalMetricsSnapshot Snapshot() => new(
        SearchCount,
        TotalElapsedMs,
        _engines
            .Select(pair => pair.Value.Snapshot(pair.Key))
            .OrderBy(x => x.Engine, StringComparer.Ordinal)
            .ToList(),
        _stages
            .Select(pair => pair.Value.Snapshot(pair.Key.Engine, pair.Key.Stage))
            .OrderBy(x => x.Engine, StringComparer.Ordinal)
            .ThenBy(x => x.Stage, StringComparer.Ordinal)
            .ToList(),
        _indexes
            .Select(pair => pair.Value.Snapshot(pair.Key))
            .OrderBy(x => x.Engine, StringComparer.Ordinal)
            .ToList());

    /// <summary>
    /// Drops every counter, as if nothing had been recorded yet.
    /// </summary>
    /// <remarks>
    /// Best-effort under concurrency: the dictionaries are cleared and the totals zeroed one field
    /// at a time, so a search that records while <see cref="Reset"/> runs may survive in a counter
    /// cleared just before it, and a <see cref="Snapshot"/> taken mid-reset can see a half-cleared
    /// collector. Call it from a quiescent point — a test teardown, an admin endpoint between
    /// searches — not as a way to cancel in-flight measurements.
    /// </remarks>
    public void Reset()
    {
        _engines.Clear();
        _stages.Clear();
        _indexes.Clear();

        Interlocked.Exchange(ref _searchCount, 0);
        Interlocked.Exchange(ref _totalElapsedMicros, 0);
        Interlocked.Exchange(ref _lastResultCount, 0);
    }

    private static long ToMicros(double elapsedMs) =>
        // NaN and the infinities survive an `elapsedMs <= 0` test, and casting one of them to long
        // is undefined-to-garbage rather than an exception -- so they are excluded explicitly.
        // Rounded rather than truncated: the cast would drop every fraction of a microsecond, and
        // accumulated over a long run those dropped halves bias the total low.
        double.IsFinite(elapsedMs) && elapsedMs > 0
            ? (long)Math.Round(elapsedMs * MicrosecondsPerMillisecond)
            : 0;

    private sealed class EngineCounters
    {
        private long _searchCount;
        private long _totalMicros;
        private long _minMicros = -1;
        private long _maxMicros;
        private int _lastResultCount;

        public void Record(long micros, int resultCount)
        {
            Interlocked.Increment(ref _searchCount);
            Interlocked.Add(ref _totalMicros, micros);
            Interlocked.Exchange(ref _lastResultCount, resultCount);

            // One call each: installing the -1 sentinel and converging on it in two steps left a
            // window in which a concurrent Snapshot() read the sentinel back as a zero minimum --
            // indistinguishable from a search that really did take under a microsecond.
            UpdateMin(ref _minMicros, micros);
            UpdateMax(ref _maxMicros, micros);
        }

        public EngineRetrievalMetrics Snapshot(string engine) => new(
            engine,
            Interlocked.Read(ref _searchCount),
            Interlocked.Read(ref _totalMicros) / MicrosecondsPerMillisecond,
            MinMs(),
            Interlocked.Read(ref _maxMicros) / MicrosecondsPerMillisecond,
            Volatile.Read(ref _lastResultCount));

        private double MinMs()
        {
            long min = Interlocked.Read(ref _minMicros);
            return min < 0 ? 0 : min / MicrosecondsPerMillisecond;
        }
    }

    private sealed class StageCounters
    {
        private long _invocations;
        private long _totalMicros;
        private long _maxMicros;
        private int _lastItemCount;

        public void Record(long micros, int itemCount)
        {
            Interlocked.Increment(ref _invocations);
            Interlocked.Add(ref _totalMicros, micros);
            Interlocked.Exchange(ref _lastItemCount, itemCount);
            UpdateMax(ref _maxMicros, micros);
        }

        public StageRetrievalMetrics Snapshot(string engine, string stage) => new(
            engine,
            stage,
            Interlocked.Read(ref _invocations),
            Interlocked.Read(ref _totalMicros) / MicrosecondsPerMillisecond,
            Interlocked.Read(ref _maxMicros) / MicrosecondsPerMillisecond,
            Volatile.Read(ref _lastItemCount));
    }

    private sealed record IndexCounters(int DocumentCount, int VocabularySize)
    {
        public IndexRetrievalMetrics Snapshot(string engine) => new(engine, DocumentCount, VocabularySize);
    }

    /// <summary>
    /// Lock-free minimum on a <c>long</c> holding a microsecond count, where <c>-1</c> is the
    /// "nothing seen yet" sentinel rather than a value. The compare-exchange is optimistic: the
    /// loop re-reads the current value and only wins if nobody else changed it in between, and a
    /// losing writer simply retries with the fresh value. Seeding and converging in one loop keeps
    /// the sentinel from ever being observable through <see cref="EngineCounters.MinMs"/>.
    /// </summary>
    private static void UpdateMin(ref long target, long value)
    {
        long current = Volatile.Read(ref target);

        while (current < 0 || value < current)
        {
            long observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
                return;

            current = observed;
        }
    }

    /// <summary>
    /// Lock-free maximum on the same representation, seeded by <c>0</c> since a duration is never
    /// negative. Optimistic compare-exchange, same shape as <see cref="UpdateMin"/>.
    /// </summary>
    private static void UpdateMax(ref long target, long value)
    {
        long current = Volatile.Read(ref target);

        while (value > current)
        {
            long observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current)
                return;

            current = observed;
        }
    }
}
