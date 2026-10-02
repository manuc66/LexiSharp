using System.Collections.Concurrent;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The <see cref="AtomicEngineReference"/> publish point for the « snapshot swap » pattern: the
/// swap is atomic, readers observe only committed engines, and a reader that captures once per
/// search never sees a torn view.
/// </summary>
public class AtomicEngineReferenceTests
{
    [Fact]
    public void Read_ReturnsTheInitiallyPublishedEngine()
    {
        var engine = new StubEngine(7);
        var reference = new AtomicEngineReference(engine);

        Assert.Same(engine, reference.Current);
    }

    [Fact]
    public void Swap_PublishesTheNewEngineAndReturnsThePreviousOne()
    {
        var first = new StubEngine(7);
        var second = new StubEngine(8);
        var reference = new AtomicEngineReference(first);

        var displaced = reference.Swap(second);

        Assert.Same(first, displaced);
        Assert.Same(second, reference.Current);
    }

    [Fact]
    public void Constructor_NullEngine_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new AtomicEngineReference(null!));

    [Fact]
    public void Swap_NullEngine_Throws()
    {
        var reference = new AtomicEngineReference(new StubEngine(7));
        Assert.Throws<ArgumentNullException>(() => reference.Swap(null!));
    }

    /// <summary>
    /// The property that makes the holder a publish point rather than a field: over a writer
    /// swapping through 100 engines at full speed, every observed reference is one of the
    /// committed engines — never null, never a value in between — and once the writer settles, the
    /// holder reads the last engine committed.
    /// </summary>
    [Fact]
    public void ConcurrentReaders_ObserveOnlyCommittedEngines()
    {
        const int engineCount = 100;
        var engines = Enumerable.Range(0, engineCount).Select(tag => new StubEngine(tag)).ToArray();
        var reference = new AtomicEngineReference(engines[0]);

        var observed = new ConcurrentBag<int>();

        using var writerStop = new CancellationTokenSource();

        var writer = Task.Run(() =>
        {
            for (int i = 1; i < engineCount; i++)
                reference.Swap(engines[i]);

            writerStop.Cancel();
        });

        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            while (!writerStop.IsCancellationRequested)
            {
                var engine = reference.Current;

                // A torn reference would surface as null or as a stub outside [0, engineCount):
                // every committed engine is one of the hundred, and none is null.
                Assert.NotNull(engine);
                var stub = Assert.IsType<StubEngine>(engine);
                observed.Add(stub.Tag);

                // The captured engine is also searchable, returning its own page.
                Assert.Equal($"doc-{stub.Tag}", Assert.Single(engine.Search("query")).DocumentId);
            }
        })).ToArray();

        Task.WaitAll([writer, .. readers]);
        Assert.All(observed, tag => Assert.InRange(tag, 0, engineCount - 1));
        Assert.Equal(engineCount - 1, ((StubEngine)reference.Current).Tag);
    }

    /// <summary>
    /// The « capture once per search » rule the holder's remarks state: a reader that keeps the
    /// engine it captured is immune to the swaps happening underneath — its page is the captured
    /// engine's own, whatever the holder publishes in the meantime.
    /// </summary>
    [Fact]
    public void ASearchRunsAgainstTheEngineTheReaderCaptured()
    {
        var reference = new AtomicEngineReference(new StubEngine(7));

        var captured = reference.Current;
        reference.Swap(new StubEngine(99));

        // The captured instance still answers as itself, with its own results.
        var results = captured.Search("query");

        Assert.Equal("doc-7", Assert.Single(results).DocumentId);
    }

    private sealed class StubEngine(int tag) : ITextSearchEngine
    {
        public int Tag { get; } = tag;

        public void Index(IEnumerable<SearchDocument> documents) { }

        public void Add(SearchDocument document) { }

        public bool Remove(string documentId) => false;

        public void Clear() { }

        public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null) =>
            [new SearchResult($"doc-{Tag}", 1, new SearchDocument($"doc-{Tag}", query))];
    }
}