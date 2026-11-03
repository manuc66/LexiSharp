using System.Collections.Concurrent;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The <see cref="AtomicClassifierReference"/> publish point for the « snapshot swap » pattern on a
/// classifier: the swap is atomic, readers observe only committed models, and a reader that captures
/// once per prediction never sees a torn view.
/// </summary>
public class AtomicClassifierReferenceTests
{
    [Fact]
    public void Read_ReturnsTheInitiallyPublishedClassifier()
    {
        var classifier = new StubClassifier(7);
        var reference = new AtomicClassifierReference(classifier);

        Assert.Same(classifier, reference.Current);
    }

    [Fact]
    public void Swap_PublishesTheNewClassifierAndReturnsThePreviousOne()
    {
        var first = new StubClassifier(7);
        var second = new StubClassifier(8);
        var reference = new AtomicClassifierReference(first);

        var displaced = reference.Swap(second);

        Assert.Same(first, displaced);
        Assert.Same(second, reference.Current);
    }

    [Fact]
    public void Constructor_NullClassifier_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new AtomicClassifierReference(null!));

    [Fact]
    public void Swap_NullClassifier_Throws()
    {
        var reference = new AtomicClassifierReference(new StubClassifier(7));
        Assert.Throws<ArgumentNullException>(() => reference.Swap(null!));
    }

    [Fact]
    public async Task ConcurrentReaders_ObserveOnlyCommittedClassifiers()
    {
        const int classifierCount = 100;
        var classifiers = Enumerable.Range(0, classifierCount).Select(tag => new StubClassifier(tag)).ToArray();
        var reference = new AtomicClassifierReference(classifiers[0]);

        var observed = new ConcurrentBag<int>();

        // The deadline is a watchdog: a starved writer must not leave the readers looping forever.
        using var writerStop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var writer = Task.Run(() =>
        {
            for (int i = 1; i < classifierCount; i++)
                reference.Swap(classifiers[i]);

            writerStop.Cancel();
        });

        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            while (!writerStop.IsCancellationRequested)
            {
                var classifier = reference.Current;

                // A torn reference would surface as null or as a stub outside [0, classifierCount).
                Assert.NotNull(classifier);
                var stub = Assert.IsType<StubClassifier>(classifier);
                observed.Add(stub.Tag);

                Assert.Equal($"cat-{stub.Tag}", classifier.PredictBest("query"));

                await Task.Yield();
            }
        })).ToArray();

        await Task.WhenAll([writer, .. readers]);

        Assert.All(observed, tag => Assert.InRange(tag, 0, classifierCount - 1));
        Assert.Equal(classifierCount - 1, ((StubClassifier)reference.Current).Tag);
    }

    [Fact]
    public void APredictionRunsAgainstTheClassifierTheReaderCaptured()
    {
        var reference = new AtomicClassifierReference(new StubClassifier(7));

        var captured = reference.Current;
        reference.Swap(new StubClassifier(99));

        Assert.Equal("cat-7", captured.PredictBest("query"));
    }

    private sealed class StubClassifier(int tag) : ITextClassifier
    {
        public int Tag { get; } = tag;

        public void Train(IEnumerable<SearchDocument> documents) { }

        public IReadOnlyList<ClassificationResult> Predict(
            string text,
            int limit = 3,
            IReadOnlySet<string>? excludedCategories = null) =>
            [new ClassificationResult($"cat-{Tag}", 1)];

        public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null) =>
            $"cat-{Tag}";
    }
}
