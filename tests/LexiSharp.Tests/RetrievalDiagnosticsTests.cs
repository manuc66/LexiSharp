using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using LexiSharp.Core;
using LexiSharp.Diagnostics;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Covers the in-box adapter: the instruments a <see cref="MeterListener"/> sees, the spans an
/// <see cref="ActivityListener"/> sees, that both agree with <see cref="InMemoryRetrievalMetrics"/>
/// for the same search, and that an unobserved adapter is free and silent.
/// </summary>
public sealed class RetrievalDiagnosticsTests
{
    private static readonly SearchDocument[] Corpus =
    [
        new("d1", "oauth access token renewal"),
        new("d2", "vector search retrieval pipeline"),
        new("d3", "token refresh and credential rotation"),
    ];

    private static RankedTextSearchEngine Engine(IRetrievalMetrics? metrics)
    {
        var engine = new RankedTextSearchEngine(
            new InMemoryTextIndex(),
            new Bm25Scorer(),
            telemetry: metrics is null ? null : new RetrievalTelemetry(metrics: metrics));

        engine.Index(Corpus);
        return engine;
    }

    [Fact]
    public void MeterPublishesTheDocumentedInstrumentsAndUnits()
    {
        using var diagnostics = new RetrievalDiagnostics();
        using var capture = new MeterCapture(diagnostics.Meter);

        Assert.Equal(8, capture.Instruments.Count);

        AssertInstrument<Histogram<double>>(capture, RetrievalDiagnostics.SearchDurationInstrumentName, "s");
        AssertInstrument<Counter<long>>(capture, RetrievalDiagnostics.SearchCountInstrumentName, "{search}");
        AssertInstrument<Histogram<int>>(capture, RetrievalDiagnostics.SearchResultsInstrumentName, "{result}");
        AssertInstrument<Histogram<double>>(capture, RetrievalDiagnostics.StageDurationInstrumentName, "s");
        AssertInstrument<Counter<long>>(capture, RetrievalDiagnostics.StageCountInstrumentName, "{invocation}");
        AssertInstrument<Histogram<int>>(capture, RetrievalDiagnostics.StageItemsInstrumentName, "{item}");
        AssertInstrument<ObservableGauge<long>>(capture, RetrievalDiagnostics.IndexDocumentsInstrumentName, "{document}");
        AssertInstrument<ObservableGauge<long>>(capture, RetrievalDiagnostics.IndexVocabularyInstrumentName, "{term}");
    }

    [Fact]
    public void ASearchRecordsItsDurationCountAndPageSize_AndAgreesWithTheInMemoryCollector()
    {
        using var diagnostics = new RetrievalDiagnostics();
        using var capture = new MeterCapture(diagnostics.Meter);

        var memory = new InMemoryRetrievalMetrics();
        var engine = Engine(new FanOutRetrievalMetrics(memory, diagnostics));

        var results = engine.Search("token", new SearchOptions(Limit: 5));
        capture.CollectObservables();

        var snapshot = Assert.Single(memory.Snapshot().Engines);
        Assert.Equal(1, snapshot.SearchCount);
        Assert.Equal(results.Count, snapshot.LastResultCount);

        var duration = Assert.Single(capture.For(RetrievalDiagnostics.SearchDurationInstrumentName));
        Assert.Equal(RankedTextSearchEngine.EngineName, duration.Tag(RetrievalDiagnostics.EngineTagName));
        AssertSecondsAgreeWith(duration.Value, snapshot.TotalElapsedMs);

        var count = Assert.Single(capture.For(RetrievalDiagnostics.SearchCountInstrumentName));
        Assert.Equal(1d, count.Value);
        Assert.Equal(RankedTextSearchEngine.EngineName, count.Tag(RetrievalDiagnostics.EngineTagName));

        var results2 = Assert.Single(capture.For(RetrievalDiagnostics.SearchResultsInstrumentName));
        Assert.Equal(results.Count, (int)results2.Value);
        Assert.Equal(RankedTextSearchEngine.EngineName, results2.Tag(RetrievalDiagnostics.EngineTagName));

        // The only tag the adapter adds to a search measurement is the engine. Query text, result
        // ids and document ids have no tag to hide in. (The same search also reports a "candidates"
        // stage, whose measurements carry the stage tag too; they are asserted separately.)
        Assert.All(
            capture.Items.Where(item => item.Name.StartsWith("search.", StringComparison.Ordinal)
                                        && !item.Name.StartsWith("search.stage.", StringComparison.Ordinal)),
            item => Assert.Equal(
                new[] { RetrievalDiagnostics.EngineTagName },
                item.Tags.Select(tag => tag.Key).Distinct().ToArray()));
    }

    [Fact]
    public void StageInstrumentsCarryTheStageLabel_AndAgreeWithTheInMemoryCollector()
    {
        using var diagnostics = new RetrievalDiagnostics();
        using var capture = new MeterCapture(diagnostics.Meter);

        var memory = new InMemoryRetrievalMetrics();
        var metrics = new FanOutRetrievalMetrics(memory, diagnostics);

        var index = new InMemoryTextIndex();
        var inner = new RankedTextSearchEngine(index, new Bm25Scorer());
        inner.Index(Corpus);

        var engine = new RerankedTextSearchEngine(
            inner,
            new ProximityReranker(index),
            telemetry: new RetrievalTelemetry(metrics: metrics));

        Assert.NotEmpty(engine.Search("token", new SearchOptions(Limit: 5)));

        var reported = memory.Snapshot().Stages;
        Assert.Contains(reported, stage => stage.Stage == "retrieve");
        Assert.Contains(reported, stage => stage.Stage.StartsWith("rerank:", StringComparison.Ordinal));

        foreach (var stage in reported)
        {
            var duration = Assert.Single(
                capture.For(RetrievalDiagnostics.StageDurationInstrumentName),
                item => item.Tag(RetrievalDiagnostics.StageTagName) == stage.Stage);
            Assert.Equal(stage.Engine, duration.Tag(RetrievalDiagnostics.EngineTagName));
            AssertSecondsAgreeWith(duration.Value, stage.TotalElapsedMs);

            var count = Assert.Single(
                capture.For(RetrievalDiagnostics.StageCountInstrumentName),
                item => item.Tag(RetrievalDiagnostics.StageTagName) == stage.Stage);
            Assert.Equal(stage.InvocationCount, (long)count.Value);

            var items = Assert.Single(
                capture.For(RetrievalDiagnostics.StageItemsInstrumentName),
                item => item.Tag(RetrievalDiagnostics.StageTagName) == stage.Stage);
            Assert.Equal(stage.LastItemCount, (int)items.Value);
        }

        // The only tags on a stage measurement are the engine and the stage label.
        Assert.All(
            capture.Items.Where(item => item.Name.StartsWith("search.stage.", StringComparison.Ordinal)),
            item => Assert.Equal(
                new[] { RetrievalDiagnostics.EngineTagName, RetrievalDiagnostics.StageTagName },
                item.Tags.Select(tag => tag.Key).Distinct().Order(StringComparer.Ordinal).ToArray()));
    }

    [Fact]
    public void IndexGaugesReportTheLastSizeAndAgreeWithTheInMemoryCollector()
    {
        using var diagnostics = new RetrievalDiagnostics();
        using var capture = new MeterCapture(diagnostics.Meter);

        var memory = new InMemoryRetrievalMetrics();
        var metrics = new FanOutRetrievalMetrics(memory, diagnostics);
        Engine(metrics);

        capture.CollectObservables();

        var reported = Assert.Single(memory.Snapshot().Indexes);
        Assert.Equal(Corpus.Length, reported.DocumentCount);
        Assert.True(reported.VocabularySize > 0, "The vocabulary should be non-empty after indexing real text.");

        AssertGauge(capture, RetrievalDiagnostics.IndexDocumentsInstrumentName, reported.DocumentCount, reported.Engine);
        AssertGauge(capture, RetrievalDiagnostics.IndexVocabularyInstrumentName, reported.VocabularySize, reported.Engine);

        // A gauge reports the current size, not the history of changes: a second report replaces the
        // first rather than adding to it.
        capture.Clear();
        metrics.RecordIndex(reported.Engine, reported.DocumentCount + 1, reported.VocabularySize + 2);
        capture.CollectObservables();

        AssertGauge(capture, RetrievalDiagnostics.IndexDocumentsInstrumentName, reported.DocumentCount + 1, reported.Engine);
        AssertGauge(capture, RetrievalDiagnostics.IndexVocabularyInstrumentName, reported.VocabularySize + 2, reported.Engine);
    }

    [Fact]
    public void NothingIsAllocatedOrThrownWhenNoListenerIsAttached()
    {
        using var diagnostics = new RetrievalDiagnostics();

        // Nothing subscribes: the source returns null and the call sites become no-ops. Warm up the
        // instruments first so their one-time lazy setup is not counted as per-search allocation.
        diagnostics.RecordSearch("engine", 1, 1.0);
        diagnostics.RecordStage("engine", "stage", 1, 1.0);
        diagnostics.RecordIndex("engine", 1, 1);

        Assert.Null(diagnostics.StartSearch("engine"));
        Assert.Null(diagnostics.StartStage("engine", "stage"));

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 100; i++)
        {
            diagnostics.RecordSearch("engine", 1, 1.0);
            diagnostics.RecordStage("engine", "stage", 1, 1.0);
            diagnostics.RecordIndex("engine", 1, 1);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ASearchInsideTheScopesProducesAParentSpanAndAChildStageSpan()
    {
        using var spans = new ActivityCapture(RetrievalDiagnostics.ActivitySourceName);
        using var diagnostics = new RetrievalDiagnostics();
        var engine = Engine(diagnostics);

        using (var search = diagnostics.StartSearch(RankedTextSearchEngine.EngineName))
        {
            Assert.NotNull(search);
            Assert.Equal(RetrievalDiagnostics.SearchActivityName, search.OperationName);

            using (var stage = diagnostics.StartStage(RankedTextSearchEngine.EngineName, "score"))
            {
                Assert.NotNull(stage);
                Assert.Equal(RetrievalDiagnostics.StageActivityName, stage.OperationName);

                // A real search, on the engine the adapter is also collecting metrics from.
                Assert.NotEmpty(engine.Search("token"));
            }
        }

        Assert.Equal(2, spans.Items.Count);

        var recordedSearch = Assert.Single(spans.Items, span => span.Name == RetrievalDiagnostics.SearchActivityName);
        var recordedStage = Assert.Single(spans.Items, span => span.Name == RetrievalDiagnostics.StageActivityName);

        Assert.Equal(RankedTextSearchEngine.EngineName, recordedSearch.Tag(RetrievalDiagnostics.EngineTagName));
        Assert.Equal(RankedTextSearchEngine.EngineName, recordedStage.Tag(RetrievalDiagnostics.EngineTagName));
        Assert.Equal("score", recordedStage.Tag(RetrievalDiagnostics.StageTagName));

        Assert.Null(recordedSearch.ParentSpanId);
        Assert.Equal(recordedSearch.SpanId, recordedStage.ParentSpanId);

        Assert.True(recordedSearch.Duration > TimeSpan.Zero, $"The search span lasted {recordedSearch.Duration}.");
        Assert.True(recordedStage.Duration > TimeSpan.Zero, $"The stage span lasted {recordedStage.Duration}.");

        Assert.All(
            spans.Items,
            span => Assert.All(
                span.Tags.Keys,
                key => Assert.Contains(
                    key,
                    new[] { RetrievalDiagnostics.EngineTagName, RetrievalDiagnostics.StageTagName })));
    }

    [Fact]
    public void NonFiniteDurationsAndMissingLabelsDoNotThrow()
    {
        using var diagnostics = new RetrievalDiagnostics();
        using var capture = new MeterCapture(diagnostics.Meter);

        diagnostics.RecordSearch(null!, 1, double.NaN);
        diagnostics.RecordStage(null!, null!, 1, double.PositiveInfinity);
        diagnostics.RecordIndex(null!, -1, -1);
        capture.CollectObservables();

        Assert.All(
            capture.For(RetrievalDiagnostics.SearchDurationInstrumentName),
            item => Assert.Equal(0d, item.Value));
        Assert.All(
            capture.For(RetrievalDiagnostics.StageDurationInstrumentName),
            item => Assert.Equal(0d, item.Value));
        Assert.Equal("unknown", Assert.Single(capture.For(RetrievalDiagnostics.SearchCountInstrumentName)).Tag(RetrievalDiagnostics.EngineTagName));
        AssertGauge(capture, RetrievalDiagnostics.IndexDocumentsInstrumentName, 0, "unknown");
        AssertGauge(capture, RetrievalDiagnostics.IndexVocabularyInstrumentName, 0, "unknown");
    }

    [Fact]
    public void DisposeIsIdempotentAndStopsRecording()
    {
        var diagnostics = new RetrievalDiagnostics();
        using var capture = new MeterCapture(diagnostics.Meter);

        diagnostics.Dispose();
        diagnostics.Dispose();

        Assert.Null(diagnostics.StartSearch("engine"));
        Assert.Null(diagnostics.StartStage("engine", "stage"));

        diagnostics.RecordSearch("engine", 1, 1.0);
        diagnostics.RecordStage("engine", "stage", 1, 1.0);
        diagnostics.RecordIndex("engine", 1, 1);

        Assert.Empty(capture.Items);
    }

    private static void AssertInstrument<TInstrument>(MeterCapture capture, string name, string unit)
        where TInstrument : Instrument
    {
        var instrument = Assert.Single(capture.Instruments, candidate => candidate.Name == name);

        Assert.IsType<TInstrument>(instrument);
        Assert.Equal(unit, instrument.Unit);
        Assert.Equal(RetrievalDiagnostics.MeterName, instrument.Meter.Name);
    }

    private static void AssertGauge(MeterCapture capture, string name, long expected, string engine)
    {
        var measurement = Assert.Single(capture.For(name));
        Assert.Equal(expected, (long)measurement.Value);
        Assert.Equal(engine, measurement.Tag(RetrievalDiagnostics.EngineTagName));
    }

    /// <summary>
    /// Compares a duration recorded in seconds by an instrument with a total the in-memory collector
    /// reports in milliseconds. Both derive from the same <c>elapsedMs</c>, and the in-memory
    /// collector rounds to whole microseconds, so one microsecond of slack covers the rounding
    /// without hiding a unit error — seconds against milliseconds is a factor of 1,000.
    /// </summary>
    private static void AssertSecondsAgreeWith(double seconds, double milliseconds)
    {
        long fromInstrument = (long)Math.Round(seconds * 1_000_000);
        long fromMemory = (long)Math.Round(milliseconds * 1_000);

        Assert.InRange(Math.Abs(fromInstrument - fromMemory), 0, 1);
    }

    private sealed record Recorded(string Name, string? Unit, double Value, IReadOnlyList<KeyValuePair<string, object?>> Tags)
    {
        public string? Tag(string key) => Tags.FirstOrDefault(tag => tag.Key == key).Value as string;
    }

    /// <summary>A <see cref="MeterListener"/> narrowed to one meter, collecting synchronously.</summary>
    private sealed class MeterCapture : IDisposable
    {
        private readonly ConcurrentQueue<Recorded> _items = new();
        private readonly MeterListener _listener = new();

        public MeterCapture(Meter meter)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (!ReferenceEquals(instrument.Meter, meter))
                    return;

                Instruments.Add(instrument);
                listener.EnableMeasurementEvents(instrument);
            };

            _listener.SetMeasurementEventCallback<double>(
                (instrument, value, tags, _) => Capture(instrument, value, tags));
            _listener.SetMeasurementEventCallback<long>(
                (instrument, value, tags, _) => Capture(instrument, value, tags));
            _listener.SetMeasurementEventCallback<int>(
                (instrument, value, tags, _) => Capture(instrument, value, tags));

            _listener.Start();
        }

        public List<Instrument> Instruments { get; } = [];

        public IReadOnlyList<Recorded> Items => _items.ToArray();

        public IEnumerable<Recorded> For(string name) => Items.Where(item => item.Name == name);

        public void CollectObservables() => _listener.RecordObservableInstruments();

        public void Clear()
        {
            while (_items.TryDequeue(out _))
            {
            }
        }

        public void Dispose() => _listener.Dispose();

        private void Capture(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
            _items.Enqueue(new Recorded(instrument.Name, instrument.Unit, value, tags.ToArray()));
    }

    private sealed record SpanRecord(
        string Name,
        string SpanId,
        string? ParentSpanId,
        TimeSpan Duration,
        IReadOnlyDictionary<string, string?> Tags)
    {
        public string? Tag(string key) => Tags.TryGetValue(key, out string? value) ? value : null;
    }

    /// <summary>An <see cref="ActivityListener"/> narrowed to one source, copying stopped spans.</summary>
    private sealed class ActivityCapture : IDisposable
    {
        private readonly ConcurrentQueue<SpanRecord> _items = new();
        private readonly ActivityListener _listener;

        public ActivityCapture(string sourceName)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == sourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
                // Copied here: Activity instances are pooled and reset after they stop, so a
                // reference kept past this callback would later read somebody else's span.
                ActivityStopped = activity => _items.Enqueue(new SpanRecord(
                    activity.OperationName,
                    activity.SpanId.ToHexString(),
                    activity.ParentSpanId == default ? null : activity.ParentSpanId.ToHexString(),
                    activity.Duration,
                    activity.TagObjects.ToDictionary(tag => tag.Key, tag => tag.Value?.ToString()))),
            };

            ActivitySource.AddActivityListener(_listener);
        }

        public IReadOnlyList<SpanRecord> Items => _items.ToArray();

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>
    /// One telemetry sink fanning out to two collectors, so both see the same recorded values and
    /// the agreement asserted in the tests is a comparison of one call, not of two runs.
    /// </summary>
    private sealed class FanOutRetrievalMetrics(params IRetrievalMetrics[] sinks) : IRetrievalMetrics
    {
        public void RecordSearch(string engine, int resultCount, double elapsedMs)
        {
            foreach (var sink in sinks)
                sink.RecordSearch(engine, resultCount, elapsedMs);
        }

        public void RecordStage(string engine, string stage, int itemCount, double elapsedMs)
        {
            foreach (var sink in sinks)
                sink.RecordStage(engine, stage, itemCount, elapsedMs);
        }

        public void RecordIndex(string engine, int documentCount, int vocabularySize)
        {
            foreach (var sink in sinks)
                sink.RecordIndex(engine, documentCount, vocabularySize);
        }
    }
}
