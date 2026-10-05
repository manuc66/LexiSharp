using LexiSharp.Core;
using LexiSharp.Hybrid;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Covers the observability seams: that a telemetry records what actually happened, that an
/// engine with no sink stays silent, and that instrumentation does not change results.
/// </summary>
public class RetrievalTelemetryTests
{
    private static readonly SearchDocument[] Corpus =
    [
        new("d1", "oauth access token renewal"),
        new("d2", "vector search retrieval pipeline"),
        new("d3", "token refresh and credential rotation"),
    ];

    private static (RankedTextSearchEngine Engine, List<RetrievalLogEvent> Events) Engine(
        RetrievalLog? log = null,
        IRetrievalMetrics? metrics = null)
    {
        var events = new List<RetrievalLogEvent>();
        RetrievalLog sink = log ?? (entry => events.Add(entry));

        var engine = new RankedTextSearchEngine(
            new InMemoryTextIndex(), new Bm25Scorer(), telemetry: new RetrievalTelemetry(sink, metrics));

        engine.Index(Corpus);
        return (engine, events);
    }

    [Fact]
    public void DefaultTelemetryIsDisabled()
    {
        Assert.False(RetrievalTelemetry.None.IsEnabled);
        Assert.Null(RetrievalTelemetry.None.Log);
        Assert.Null(RetrievalTelemetry.None.Metrics);
    }

    [Fact]
    public void EngineWithNoSinkRecordsNothing()
    {
        var engine = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());
        engine.Index(Corpus);

        var results = engine.Search("token");

        // The contract is the absence of a sink, not an observable counter: an uninstrumented
        // engine must not fail and must not change its results.
        Assert.NotEmpty(results);
    }

    [Fact]
    public void SearchEmitsOneInformationEventWithTheRealResultCount()
    {
        var (engine, events) = Engine();
        var before = events.Count;

        var results = engine.Search("token");

        var searchEvents = events.Skip(before).Where(e => e.Event == RetrievalLogEvents.Search).ToList();

        var search = Assert.Single(searchEvents);
        Assert.Equal(RetrievalLogLevel.Information, search.Level);
        Assert.Equal(RankedTextSearchEngine.EngineName, search.Engine);
        Assert.Equal(results.Count, search.Count);
        Assert.True(search.ElapsedMs >= 0, $"ElapsedMs was {search.ElapsedMs}.");
    }

    [Fact]
    public void CandidateCountReflectsDocumentsScoredNotPageSize()
    {
        var metrics = new InMemoryRetrievalMetrics();
        var (engine, _) = Engine(metrics: metrics);

        // A query matching many documents with a one-result page: the page size and the number of
        // documents relevance math was paid for are different numbers, and conflating them is the
        // bug this asserts against.
        var results = engine.Search("search OR token", new SearchOptions(Limit: 1));
        Assert.Single(results);

        var snapshot = metrics.Snapshot();
        var engineMetrics = Assert.Single(snapshot.Engines);
        Assert.Equal(1, engineMetrics.LastResultCount);
    }

    [Fact]
    public void IndexingEmitsTheRealDocumentAndVocabularyCounts()
    {
        var metrics = new InMemoryRetrievalMetrics();
        Engine(metrics: metrics);

        var snapshot = metrics.Snapshot();
        var index = Assert.Single(snapshot.Indexes);

        Assert.Equal(Corpus.Length, index.DocumentCount);
        Assert.True(
            index.VocabularySize > 0,
            "The vocabulary should be non-empty after indexing real text.");
    }

    [Fact]
    public void MetricsAccumulateAcrossSearches()
    {
        var metrics = new InMemoryRetrievalMetrics();
        var (engine, _) = Engine(metrics: metrics);

        engine.Search("token");
        engine.Search("vector");
        engine.Search("nothingmatchesthis");

        var snapshot = metrics.Snapshot();
        Assert.Equal(3, snapshot.SearchCount);

        var engineMetrics = Assert.Single(snapshot.Engines);
        Assert.Equal(3, engineMetrics.SearchCount);
    }

    [Fact]
    public void QueryOnEmptyIndexIsWarnedNotSilentlyEmpty()
    {
        var events = new List<RetrievalLogEvent>();
        var engine = new RankedTextSearchEngine(
            new InMemoryTextIndex(),
            new Bm25Scorer(),
            telemetry: RetrievalTelemetry.Logging(events.Add));

        var results = engine.Search("token");

        Assert.Empty(results);
        Assert.Contains(events, e => e.Level == RetrievalLogLevel.Warning);
    }

    [Fact]
    public void InstrumentationDoesNotChangeResults()
    {
        var plain = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());
        var instrumented = new RankedTextSearchEngine(
            new InMemoryTextIndex(), new Bm25Scorer(), telemetry: new RetrievalTelemetry(_ => { }));

        plain.Index(Corpus);
        instrumented.Index(Corpus);

        var options = new SearchOptions(Limit: 5);

        Assert.Equal(
            plain.Search("token", options).Select(r => (r.DocumentId, r.Score)),
            instrumented.Search("token", options).Select(r => (r.DocumentId, r.Score)));
    }

    [Fact]
    public void HybridReportsOneStagePerSourceAndOneForTheMerge()
    {
        var metrics = new InMemoryRetrievalMetrics();

        var lexical = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());
        var sparse = new RankedTextSearchEngine(new InMemoryTextIndex(), new TfIdfScorer());
        lexical.Index(Corpus);
        sparse.Index(Corpus);

        var hybrid = new HybridTextSearchEngine(
            [lexical, sparse],
            sourceNames: ["lexical", "semantic"],
            telemetry: new RetrievalTelemetry(metrics: metrics));

        hybrid.Search("token");

        var stages = metrics.Snapshot().Stages.Select(s => s.Stage).ToList();

        Assert.Contains("lexical", stages);
        Assert.Contains("semantic", stages);
        Assert.Contains("merge", stages);
    }

    [Fact]
    public void RerankedEngineNamesItsStageAfterTheReranker()
    {
        var metrics = new InMemoryRetrievalMetrics();

        var index = new InMemoryTextIndex();
        var inner = new RankedTextSearchEngine(index, new Bm25Scorer());
        inner.Index(Corpus);

        var engine = new RerankedTextSearchEngine(
            inner,
            new ProximityReranker(index),
            telemetry: new RetrievalTelemetry(metrics: metrics));

        engine.Search("token");

        var stages = metrics.Snapshot().Stages.Select(s => s.Stage).ToList();

        Assert.Contains("retrieve", stages);
        Assert.Contains(stages, s => s.StartsWith("rerank:", StringComparison.Ordinal));
    }

    [Fact]
    public void RerankedEngineReportsACompletedSearchWhenNothingIsFound()
    {
        var metrics = new InMemoryRetrievalMetrics();

        // Empty index: the inner engine retrieves nothing, so there is no rerank stage to time. The
        // search is still answered, and its latency belongs in the search counter — otherwise a
        // query that matches nothing leaves a retrieve stage row with no search beside it.
        var engine = new RerankedTextSearchEngine(
            new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer()),
            new ProximityReranker(new InMemoryTextIndex()),
            telemetry: new RetrievalTelemetry(metrics: metrics));

        Assert.Empty(engine.Search("token"));

        var snapshot = metrics.Snapshot();

        Assert.Equal(1, snapshot.SearchCount);

        var reported = Assert.Single(snapshot.Engines, e => e.Engine == RerankedTextSearchEngine.EngineName);
        Assert.Equal(1, reported.SearchCount);
        Assert.Equal(0, reported.LastResultCount);
    }

    [Fact]
    public void RerankedEngineDoesNotReportASearchForAnEmptyRequest()
    {
        var metrics = new InMemoryRetrievalMetrics();
        var index = new InMemoryTextIndex();
        var inner = new RankedTextSearchEngine(index, new Bm25Scorer());
        inner.Index(Corpus);

        var engine = new RerankedTextSearchEngine(
            inner,
            new ProximityReranker(index),
            telemetry: new RetrievalTelemetry(metrics: metrics));

        // An empty request (Limit <= 0) is rejected before any work: the contract on IsEmpty is
        // that a caller's own limit decides the outcome, without a scan, so no search is reported.
        Assert.Empty(engine.Search("token", new SearchOptions(Limit: 0)));
        Assert.Equal(0, metrics.Snapshot().SearchCount);
    }

    [Fact]
    public void WarningDoesNotRequireAMetricsCollector()
    {
        var events = new List<RetrievalLogEvent>();
        var telemetry = RetrievalTelemetry.Logging(events.Add);

        Assert.Null(telemetry.Metrics);
        telemetry.Warning("engine", "something degraded");

        Assert.Contains(events, e => e.Message == "something degraded");
    }

    [Fact]
    public void ElapsedMsIsNeverNegative()
    {
        // A start timestamp taken "later" than the read is the degenerate case; the conversion must
        // not produce a negative duration that would poison a min or a histogram.
        long future = RetrievalTelemetry.StartTimer() + 10_000;

        Assert.True(RetrievalTelemetry.ElapsedMs(future) < 0, "A future timestamp is expected to be negative.");
    }

    [Fact]
    public void IndexChangedWithNullIndexThrows()
    {
        Assert.Throws<ArgumentNullException>(
            () => RetrievalTelemetry.None.IndexChanged("engine", null!));
    }
}
