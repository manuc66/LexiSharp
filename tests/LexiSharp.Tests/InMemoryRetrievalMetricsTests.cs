using System.Collections.Concurrent;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Covers <see cref="InMemoryRetrievalMetrics"/>: that counters aggregate correctly, that the
/// snapshot is a faithful copy, and that concurrent recording neither loses nor corrupts counts.
/// </summary>
public class InMemoryRetrievalMetricsTests
{
    [Fact]
    public void FreshCollectorIsEmpty()
    {
        var metrics = new InMemoryRetrievalMetrics();

        var snapshot = metrics.Snapshot();

        Assert.Equal(0, snapshot.SearchCount);
        Assert.Equal(0, snapshot.TotalElapsedMs);
        Assert.Empty(snapshot.Engines);
        Assert.Empty(snapshot.Stages);
        Assert.Empty(snapshot.Indexes);
        Assert.Equal(RetrievalMetricsSnapshot.Empty.SearchCount, snapshot.SearchCount);
    }

    [Fact]
    public void SearchesAccumulatePerEngineAndInTotal()
    {
        var metrics = new InMemoryRetrievalMetrics();

        metrics.RecordSearch("a", 10, 5.0);
        metrics.RecordSearch("a", 20, 3.0);
        metrics.RecordSearch("b", 5, 2.0);

        var snapshot = metrics.Snapshot();

        Assert.Equal(3, snapshot.SearchCount);
        Assert.Equal(10.0, snapshot.TotalElapsedMs, 3);

        var a = Assert.Single(snapshot.Engines, e => e.Engine == "a");
        Assert.Equal(2, a.SearchCount);
        Assert.Equal(8.0, a.TotalElapsedMs, 3);
        Assert.Equal(20, a.LastResultCount);

        var b = Assert.Single(snapshot.Engines, e => e.Engine == "b");
        Assert.Equal(1, b.SearchCount);
    }

    [Fact]
    public void MinAndMaxTrackTheExtremes()
    {
        var metrics = new InMemoryRetrievalMetrics();

        metrics.RecordSearch("a", 1, 10.0);
        metrics.RecordSearch("a", 1, 2.0);
        metrics.RecordSearch("a", 1, 25.0);
        metrics.RecordSearch("a", 1, 7.0);

        var a = Assert.Single(metrics.Snapshot().Engines);

        Assert.Equal(2.0, a.MinElapsedMs, 3);
        Assert.Equal(25.0, a.MaxElapsedMs, 3);
    }

    [Fact]
    public void StageRecordDoesNotCreateAnEngineEntry()
    {
        var metrics = new InMemoryRetrievalMetrics();

        metrics.RecordStage("a", "merge", 3, 1.0);

        // A stage is not a search: no engine breakdown is invented from one, so the min's "nothing
        // seen yet" sentinel can never surface as a bogus engine entry.
        Assert.Empty(metrics.Snapshot().Engines);
        Assert.Equal(1, Assert.Single(metrics.Snapshot().Stages).InvocationCount);
    }

    [Fact]
    public void StageNamesContainingSeparatorsDoNotCollide()
    {
        var metrics = new InMemoryRetrievalMetrics();

        // Engine/stage names are caller-supplied; a composite string key would let these two
        // recordings land in the same bucket.
        metrics.RecordStage("a:b", "c", 1, 1.0);
        metrics.RecordStage("a", "b:c", 1, 1.0);

        var stages = metrics.Snapshot().Stages;

        Assert.Equal(2, stages.Count);
        Assert.Contains(stages, s => s.Engine == "a:b" && s.Stage == "c");
        Assert.Contains(stages, s => s.Engine == "a" && s.Stage == "b:c");
    }

    [Fact]
    public void StagesAreKeyedByEngineAndStage()
    {
        var metrics = new InMemoryRetrievalMetrics();

        metrics.RecordStage("a", "merge", 5, 1.0);
        metrics.RecordStage("a", "merge", 7, 2.0);
        metrics.RecordStage("a", "rerank", 3, 4.0);

        var stages = metrics.Snapshot().Stages;

        Assert.Equal(2, stages.Count);

        var merge = Assert.Single(stages, s => s.Stage == "merge");
        Assert.Equal(2, merge.InvocationCount);
        Assert.Equal(3.0, merge.TotalElapsedMs, 3);
        Assert.Equal(2.0, merge.MaxElapsedMs, 3);
        Assert.Equal(7, merge.LastItemCount);
    }

    [Fact]
    public void IndexSizeKeepsOnlyTheLatestValue()
    {
        var metrics = new InMemoryRetrievalMetrics();

        metrics.RecordIndex("a", 10, 100);
        metrics.RecordIndex("a", 25, 260);

        var index = Assert.Single(metrics.Snapshot().Indexes);

        Assert.Equal(25, index.DocumentCount);
        Assert.Equal(260, index.VocabularySize);
    }

    [Fact]
    public void ResetDropsEveryCounter()
    {
        var metrics = new InMemoryRetrievalMetrics();
        metrics.RecordSearch("a", 1, 1.0);
        metrics.RecordStage("a", "merge", 1, 1.0);
        metrics.RecordIndex("a", 1, 1);

        metrics.Reset();

        var snapshot = metrics.Snapshot();
        Assert.Equal(0, snapshot.SearchCount);
        Assert.Empty(snapshot.Engines);
        Assert.Empty(snapshot.Stages);
        Assert.Empty(snapshot.Indexes);
    }

    [Fact]
    public void NullEngineOrStageIsRejected()
    {
        var metrics = new InMemoryRetrievalMetrics();

        Assert.Throws<ArgumentNullException>(() => metrics.RecordSearch(null!, 1, 1.0));
        Assert.Throws<ArgumentNullException>(() => metrics.RecordStage("a", null!, 1, 1.0));
        Assert.Throws<ArgumentNullException>(() => metrics.RecordIndex(null!, 1, 1));
    }

    [Fact]
    public void NegativeOrNonFiniteElapsedIsTreatedAsZero()
    {
        var metrics = new InMemoryRetrievalMetrics();

        metrics.RecordSearch("a", 1, -5.0);
        metrics.RecordSearch("a", 1, double.NaN);
        metrics.RecordSearch("a", 1, double.PositiveInfinity);

        Assert.Equal(0, metrics.TotalElapsedMs);
    }

    [Fact]
    public void ConcurrentRecordingLosesNoSearches()
    {
        var metrics = new InMemoryRetrievalMetrics();
        const int Workers = 8;
        const int PerWorker = 500;

        Parallel.For(0, Workers, worker =>
        {
            for (int i = 0; i < PerWorker; i++)
            {
                metrics.RecordSearch("a", i, 0.5);
                metrics.RecordStage("a", "merge", i, 0.25);
            }
        });

        var snapshot = metrics.Snapshot();

        Assert.Equal(Workers * PerWorker, snapshot.SearchCount);

        var a = Assert.Single(snapshot.Engines);
        Assert.Equal(Workers * PerWorker, a.SearchCount);

        var merge = Assert.Single(snapshot.Stages);
        Assert.Equal(Workers * PerWorker, merge.InvocationCount);

        // Every sample was the same duration, so the extremes are exactly that duration.
        Assert.Equal(0.5, a.MinElapsedMs, 3);
        Assert.Equal(0.5, a.MaxElapsedMs, 3);
    }

    [Fact]
    public void ConcurrentRecordingWithMixedDurationsKeepsAValidExtremes()
    {
        var metrics = new InMemoryRetrievalMetrics();
        const int Workers = 8;
        const int PerWorker = 500;

        // Racing writers on the min/max fields: the assertion is that the surviving extremes are
        // still real observed values, not that a specific one won.
        Parallel.For(0, Workers, worker =>
        {
            for (int i = 0; i < PerWorker; i++)
            {
                double elapsed = worker + 1;
                metrics.RecordSearch("a", 1, elapsed);
            }
        });

        var a = Assert.Single(metrics.Snapshot().Engines);

        Assert.Equal(Workers * PerWorker, a.SearchCount);
        Assert.InRange(a.MinElapsedMs, 1.0, Workers);
        Assert.InRange(a.MaxElapsedMs, 1.0, Workers);
        Assert.True(a.MinElapsedMs <= a.MaxElapsedMs);
    }

    [Fact]
    public void SnapshotIsUnaffectedByLaterRecordings()
    {
        var metrics = new InMemoryRetrievalMetrics();
        metrics.RecordSearch("a", 1, 1.0);

        var snapshot = metrics.Snapshot();
        metrics.RecordSearch("a", 1, 100.0);

        Assert.Equal(1, snapshot.SearchCount);
        Assert.Equal(1.0, snapshot.TotalElapsedMs, 3);
        Assert.Equal(2, metrics.SearchCount);
    }

    [Fact]
    public void ImplementsTheMetricsContract()
    {
        // The point of the interface: a caller holding only IRetrievalMetrics can still record.
        IRetrievalMetrics metrics = new InMemoryRetrievalMetrics();

        metrics.RecordSearch("a", 1, 1.0);
        metrics.RecordStage("a", "merge", 1, 1.0);
        metrics.RecordIndex("a", 1, 1);

        Assert.IsType<InMemoryRetrievalMetrics>(metrics);
    }

    [Fact]
    public async Task ConcurrentSnapshotReadsDoNotThrow()
    {
        var metrics = new InMemoryRetrievalMetrics();
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var errors = new ConcurrentQueue<Exception>();

        var writer = Task.Run(() =>
        {
            for (int i = 0; i < 20_000 && !stop.IsCancellationRequested; i++)
                metrics.RecordSearch("a", i, 0.1);
        });

        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    _ = metrics.Snapshot();
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            }
        });

        await Task.WhenAll(writer, reader);

        Assert.Empty(errors);
    }
}
