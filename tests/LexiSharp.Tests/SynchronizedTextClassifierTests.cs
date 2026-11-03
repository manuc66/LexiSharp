using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class SynchronizedTextClassifierTests
{
    private static readonly SearchDocument[] Corpus =
    {
        new("1", "internet connection problem", Category: "Support"),
        new("2", "monthly invoice", Category: "Billing"),
    };

    private static NaiveBayesClassifier TrainedInner()
    {
        var inner = new NaiveBayesClassifier();
        inner.Train(Corpus);
        return inner;
    }

    [Fact]
    public void TheWrapper_CarriesTheCapabilitiesItForwards()
    {
        using var classifier = new SynchronizedTextClassifier(TrainedInner());

        Assert.IsAssignableFrom<IIncrementalTextClassifier>(classifier);
        Assert.IsAssignableFrom<IReinforceableTextClassifier>(classifier);
        Assert.IsAssignableFrom<IWeightedPredictor>(classifier);
        Assert.IsAssignableFrom<IDisposable>(classifier);
    }

    [Fact]
    public void Learn_And_Unlearn_ReachTheWrappedModel()
    {
        using var classifier = new SynchronizedTextClassifier(TrainedInner());
        var addition = new SearchDocument("3", "package delivery tracking", Category: "Shipping");

        Assert.Equal("Billing", classifier.PredictBest("invoice"));

        classifier.Learn(addition);
        Assert.Equal("Support", classifier.PredictBest("internet connection problem"));

        classifier.Unlearn(addition);
        Assert.Equal(2, classifier.Predict("anything", limit: 10).Count);
    }

    // Through the wrapper, learning a corpus one document at a time must still be identical to
    // training on it: the lock must not change the model, only when it may be read.
    [Fact]
    public void Learn_ThroughTheWrapper_MatchesTrainingTheCorpusAtOnce()
    {
        using var classifier = new SynchronizedTextClassifier(new NaiveBayesClassifier());
        foreach (var document in Corpus)
            classifier.Learn(document);

        var trained = new NaiveBayesClassifier();
        trained.Train(Corpus);

        foreach (string query in new[] { "internet connection", "invoice", "delivery" })
        {
            var expected = trained.Predict(query, limit: 10).Select(r => (r.Category, r.Probability));
            var actual = classifier.Predict(query, limit: 10).Select(r => (r.Category, r.Probability));
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Ctor_RejectsANullClassifier()
    {
        Assert.Throws<ArgumentNullException>(() => new SynchronizedTextClassifier(null!));
    }

    [Fact]
    public void Predict_ReachesTheWrappedModel()
    {
        using var classifier = new SynchronizedTextClassifier(TrainedInner());

        Assert.Equal("Support", classifier.PredictBest("internet connection"));
        Assert.Equal("Billing", classifier.Predict("invoice", limit: 1)[0].Category);
    }

    [Fact]
    public void Train_ReachesTheWrappedModel()
    {
        using var classifier = new SynchronizedTextClassifier(new NaiveBayesClassifier());

        classifier.Train(new[] { new SearchDocument("1", "alpha", Category: "Only") });

        Assert.Equal("Only", classifier.PredictBest("alpha"));
    }

    [Fact]
    public void Reinforce_And_Unreinforce_ReachTheWrappedModel()
    {
        using var classifier = new SynchronizedTextClassifier(TrainedInner());
        const string Query = "monthly invoice";

        Assert.Equal("Billing", classifier.PredictBest(Query));

        classifier.Reinforce(Query, "Support", weight: 4.0);
        Assert.Equal("Support", classifier.PredictBest(Query));

        classifier.Unreinforce(Query, "Support", weight: 4.0);
        Assert.Equal("Billing", classifier.PredictBest(Query));
    }

    [Fact]
    public void ForgetReinforcement_ReachesTheWrappedModel()
    {
        using var classifier = new SynchronizedTextClassifier(TrainedInner());
        const string Query = "monthly invoice";

        classifier.Reinforce(Query, "Support", weight: 4.0);
        classifier.ForgetReinforcement();

        Assert.Equal("Billing", classifier.PredictBest(Query));
    }

    [Fact]
    public void Predict_WeightedTokens_ReachesTheWrappedModel()
    {
        using var classifier = new SynchronizedTextClassifier(TrainedInner());

        var results = classifier.Predict(
            new[] { new WeightedToken("internet", 0.0), WeightedToken.Full("invoice") }, limit: 1);

        Assert.Equal("Billing", results[0].Category);
    }

    [Fact]
    public void Predict_ExcludedCategories_ReachTheWrappedModel()
    {
        using var classifier = new SynchronizedTextClassifier(TrainedInner());

        var results = classifier.Predict("invoice", limit: 5, excludedCategories: new HashSet<string> { "Billing" });

        Assert.Single(results);
        Assert.Equal("Support", results[0].Category);
    }

    /// <summary>A classifier that is reinforcable but not weighted, to exercise the fallback.</summary>
    private sealed class PlainReinforceableClassifier : IReinforceableTextClassifier
    {
        public void Train(IEnumerable<SearchDocument> documents) { }

        public void Reinforce(string text, string category, double weight = 1.0) { }

        public void Unreinforce(string text, string category, double weight = 1.0) { }

        public void ForgetReinforcement() { }

        public IReadOnlyList<ClassificationResult> Predict(
            string text, int limit = 3, IReadOnlySet<string>? excludedCategories = null) =>
            Array.Empty<ClassificationResult>();

        public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null) => null;
    }

    [Fact]
    public void ContextualPredict_ReachesTheWrappedBlend()
    {
        var inner = new NaiveBayesClassifier();
        inner.Train(Corpus);
        var blend = new BlendTextClassifier(new[]
        {
            new TextClassifierLane(inner, 1),
        });
        using var classifier = new SynchronizedTextClassifier(blend);
        var context = new BlendContext
        {
            Candidates = new[] { new TextEvidence("X", 10) },
        };

        Assert.IsAssignableFrom<IContextualTextClassifier>(classifier);
        Assert.Equal("X", classifier.Predict("invoice", context, limit: 1)[0].Category);
    }

    [Fact]
    public void ContextualPredict_ThrowsWhenTheWrappedModelCannotDoIt()
    {
        using var classifier = new SynchronizedTextClassifier(TrainedInner());
        var context = BlendContext.Empty;

        var failure = Assert.Throws<NotSupportedException>(
            () => classifier.Predict("invoice", context));

        Assert.Contains(nameof(NaiveBayesClassifier), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Predict_WeightedTokens_ThrowsWhenTheWrappedModelCannotDoIt()
    {
        using var classifier = new SynchronizedTextClassifier(new PlainReinforceableClassifier());

        var failure = Assert.Throws<NotSupportedException>(
            () => classifier.Predict(new[] { WeightedToken.Full("anything") }));

        // The message has to name what is actually wrapped, or the caller has no way to tell which
        // layer refused.
        Assert.Contains(nameof(PlainReinforceableClassifier), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Predict_WeightedTokens_SaysNothingAboutTheWrapperWhenItWouldWork()
    {
        using var classifier = new SynchronizedTextClassifier(TrainedInner());

        var results = classifier.Predict(new[] { WeightedToken.Full("invoice") }, limit: 1);

        Assert.Equal("Billing", results[0].Category);
    }

    [Fact]
    public void Dispose_LeavesTheWrappedModelAlone()
    {
        var inner = TrainedInner();
        var classifier = new SynchronizedTextClassifier(inner);
        classifier.Dispose();

        // Disposing the wrapper releases the lock, not the thing it protects: the caller still owns
        // the model they passed in.
        Assert.Equal("Support", inner.PredictBest("internet connection"));
    }

    [Fact]
    public void UsingADisposedWrapper_Throws()
    {
        var classifier = new SynchronizedTextClassifier(TrainedInner());
        classifier.Dispose();

        Assert.Throws<ObjectDisposedException>(() => classifier.PredictBest("invoice"));
        Assert.Throws<ObjectDisposedException>(() => classifier.Predict("invoice"));
        Assert.Throws<ObjectDisposedException>(() => classifier.Train(Corpus));
        Assert.Throws<ObjectDisposedException>(() => classifier.Reinforce("invoice", "Support"));
        Assert.Throws<ObjectDisposedException>(() => classifier.Unreinforce("invoice", "Support"));
        Assert.Throws<ObjectDisposedException>(classifier.ForgetReinforcement);
    }

    /// <summary>
    /// The smallest contract: prediction and training only. A wrapper that required reinforcement
    /// to be present could not carry a nearest-neighbour or a centroid model, and those are exactly
    /// the models whose mutations need the same lock.
    /// </summary>
    private sealed class PlainTextClassifier : ITextClassifier
    {
        private bool _trained;

        public void Train(IEnumerable<SearchDocument> documents) => _trained = true;

        public IReadOnlyList<ClassificationResult> Predict(
            string text, int limit = 3, IReadOnlySet<string>? excludedCategories = null) =>
            _trained && limit > 0
                ? new[] { new ClassificationResult("Only", 1.0) }
                : Array.Empty<ClassificationResult>();

        public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null) =>
            Predict(text, limit: 1).Count > 0 ? "Only" : null;
    }

    [Fact]
    public void TheWrapper_CarriesAPlainClassifier()
    {
        using var classifier = new SynchronizedTextClassifier(new PlainTextClassifier());

        classifier.Train(Corpus);

        Assert.Equal("Only", classifier.PredictBest("anything"));
    }

    [Fact]
    public void Reinforce_ThrowsWhenTheWrappedModelCannotDoIt()
    {
        using var classifier = new SynchronizedTextClassifier(new PlainTextClassifier());

        var reinforce = Assert.Throws<NotSupportedException>(
            () => classifier.Reinforce("anything", "Only"));
        var unreinforce = Assert.Throws<NotSupportedException>(
            () => classifier.Unreinforce("anything", "Only"));
        var forget = Assert.Throws<NotSupportedException>(classifier.ForgetReinforcement);

        Assert.Contains(nameof(PlainTextClassifier), reinforce.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(PlainTextClassifier), unreinforce.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(PlainTextClassifier), forget.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reinforceable but not incremental: the wrapper must refuse the incremental members by name
    /// instead of quietly retraining, which is what a silent fallback here would look like.
    /// </summary>
    private sealed class ReinforceableOnlyClassifier : IReinforceableTextClassifier
    {
        public void Train(IEnumerable<SearchDocument> documents) { }

        public void Reinforce(string text, string category, double weight = 1.0) { }

        public void Unreinforce(string text, string category, double weight = 1.0) { }

        public void ForgetReinforcement() { }

        public IReadOnlyList<ClassificationResult> Predict(
            string text, int limit = 3, IReadOnlySet<string>? excludedCategories = null) =>
            Array.Empty<ClassificationResult>();

        public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null) => null;
    }

    [Fact]
    public void Learn_And_Unlearn_ThrowWhenTheWrappedModelIsNotIncremental()
    {
        using var classifier = new SynchronizedTextClassifier(new ReinforceableOnlyClassifier());
        var document = new SearchDocument("1", "some text", Category: "A");

        var learn = Assert.Throws<NotSupportedException>(() => classifier.Learn(document));
        var unlearn = Assert.Throws<NotSupportedException>(() => classifier.Unlearn(document));

        Assert.Contains(nameof(ReinforceableOnlyClassifier), learn.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ReinforceableOnlyClassifier), unlearn.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UsingADisposedWrapper_Throws_ForTheIncrementalMembersToo()
    {
        var classifier = new SynchronizedTextClassifier(TrainedInner());
        classifier.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => classifier.Learn(new SearchDocument("9", "package delivery", Category: "Shipping")));
        Assert.Throws<ObjectDisposedException>(
            () => classifier.Unlearn(new SearchDocument("1", "internet connection problem", Category: "Support")));
    }

    // Same shape as InMemoryTextIndexTests.GetCandidateDocuments_MultiTerm_IsConsistentUnderConcurrentQueries:
    // hammer Predict from many threads while a writer continuously reinforces and un-reinforces the
    // same text, and require no exception and a stable answer throughout. Without the lock the
    // reader threads see a half-updated ledger (a Dictionary mutated while being enumerated by
    // another thread), which is what makes the lock load-bearing rather than decorative.
    [Fact]
    public async Task Predict_IsConsistentUnderConcurrentReinforcement()
    {
        var inner = TrainedInner();

        using var classifier = new SynchronizedTextClassifier(inner);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        int readFailures = 0;
        int writeFailures = 0;
        long reads = 0;

        // Two writers, deliberately touching different state: one churns the reinforcement ledger,
        // the other the corpus counts. Both are dictionaries being mutated under a reader, which is
        // the whole hazard, and they are independent so neither can mask the other.
        var writer = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    // Net-zero, repeated: a category that keeps being added to the ledger and
                    // taken out of it, which is exactly the churn that makes an unsynchronized
                    // read observe a torn state.
                    classifier.Reinforce("invoice", "Support", weight: 4.0);
                    classifier.Unreinforce("invoice", "Support", weight: 4.0);
                }
                catch
                {
                    Interlocked.Increment(ref writeFailures);
                }
            }
        });

        var learner = Task.Run(() =>
        {
            var churn = new SearchDocument("churn", "package delivery tracking", Category: "Shipping");
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    // The corpus counts churn too: the per-class term dictionary, the corpus-wide
                    // counts and the document frequencies are all rewritten on every call.
                    classifier.Learn(churn);
                    classifier.Unlearn(churn);
                }
                catch
                {
                    Interlocked.Increment(ref writeFailures);
                }
            }
        });

        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    Interlocked.Increment(ref reads);

                    // The writers only ever churn the ledger for "invoice" and a Shipping document
                    // built from "package delivery tracking", so "internet" — which neither
                    // mentions — has to keep answering Support no matter what state they are in.
                    if (classifier.PredictBest("internet") != "Support")
                        Interlocked.Increment(ref readFailures);
                }
                catch
                {
                    Interlocked.Increment(ref readFailures);
                }
            }
        })).ToArray();

        await Task.WhenAll(readers.Append(writer).Append(learner));

        Assert.Equal(0, readFailures);
        Assert.Equal(0, writeFailures);

        // A test that only ever sees an idle scheduler would pass without the lock too, so require
        // that it actually did the work it claims to have.
        Assert.True(reads > 1000, $"expected a meaningful number of concurrent reads, saw {reads}.");
    }

    [Fact]
    public async Task AsyncMembers_ReachTheWrappedModel()
    {
        var inner = new NearestNeighborClassifier();
        using var classifier = new SynchronizedTextClassifier(inner);

        Assert.IsAssignableFrom<IAsyncTextClassifier>(classifier);
        Assert.IsAssignableFrom<IIncrementalAsyncTextClassifier>(classifier);
        Assert.True(classifier.SupportsAsync);

        await classifier.TrainAsync(Corpus);

        var results = await classifier.PredictAsync("invoice", limit: 1);
        Assert.Equal("Billing", results[0].Category);

        var addition = new SearchDocument("3", "package delivery tracking", Category: "Shipping");
        await classifier.LearnAsync(addition);
        await classifier.UnlearnAsync(addition);
    }

    [Fact]
    public async Task SupportsIncrementalAsync_IsReportedSeparately()
    {
        using var classifier = new SynchronizedTextClassifier(new AsyncNonIncrementalClassifier());
        var document = new SearchDocument("9", "package delivery", Category: "Shipping");

        Assert.True(classifier.SupportsAsync);
        Assert.False(classifier.SupportsIncrementalAsync);

        var failure = await Assert.ThrowsAsync<NotSupportedException>(() => classifier.LearnAsync(document));

        Assert.Contains(nameof(AsyncNonIncrementalClassifier), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsyncMembers_ThrowWhenTheWrappedModelIsSynchronous()
    {
        using var classifier = new SynchronizedTextClassifier(TrainedInner());
        var document = new SearchDocument("9", "package delivery", Category: "Shipping");

        Assert.False(classifier.SupportsAsync);

        var train = await Assert.ThrowsAsync<NotSupportedException>(() => classifier.TrainAsync(Corpus));
        var predict = await Assert.ThrowsAsync<NotSupportedException>(() => classifier.PredictAsync("invoice"));
        var learn = await Assert.ThrowsAsync<NotSupportedException>(() => classifier.LearnAsync(document));
        var unlearn = await Assert.ThrowsAsync<NotSupportedException>(() => classifier.UnlearnAsync(document));

        Assert.Contains(nameof(NaiveBayesClassifier), train.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(NaiveBayesClassifier), predict.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(NaiveBayesClassifier), learn.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(NaiveBayesClassifier), unlearn.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APredictionWaitsForAnAsyncWriter()
    {
        var inner = new BlockingAsyncClassifier();
        using var classifier = new SynchronizedTextClassifier(inner);
        var document = new SearchDocument("9", "package delivery", Category: "Shipping");

        Task learn = classifier.LearnAsync(document);
        await inner.LearnEntered.Task;

        Task<IReadOnlyList<ClassificationResult>> predict = classifier.PredictAsync("invoice");
        Assert.False(predict.IsCompleted);
        Assert.False(inner.PredictEntered.Task.IsCompleted);

        inner.LearnRelease.SetResult(true);
        await learn;

        await inner.PredictEntered.Task;
        inner.PredictRelease.SetResult(true);

        Assert.Equal("A", (await predict)[0].Category);
    }

    [Fact]
    public async Task ACancelledPredictionLeavesTheLockQueue()
    {
        var inner = new BlockingAsyncClassifier();
        using var classifier = new SynchronizedTextClassifier(inner);
        var document = new SearchDocument("9", "package delivery", Category: "Shipping");

        Task learn = classifier.LearnAsync(document);
        await inner.LearnEntered.Task;

        using var cancellation = new CancellationTokenSource();
        Task<IReadOnlyList<ClassificationResult>> predict =
            classifier.PredictAsync("invoice", cancellationToken: cancellation.Token);

        Assert.False(predict.IsCompleted);
        Assert.False(inner.PredictEntered.Task.IsCompleted);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => predict);

        inner.LearnRelease.SetResult(true);
        await learn;

        // The cancelled prediction never entered the model.
        Assert.False(inner.PredictEntered.Task.IsCompleted);
    }

    [Fact]
    public async Task AnAsyncWriterWaitsForAPredictionInFlight()
    {
        var inner = new BlockingAsyncClassifier();
        using var classifier = new SynchronizedTextClassifier(inner);
        var document = new SearchDocument("9", "package delivery", Category: "Shipping");

        Task<IReadOnlyList<ClassificationResult>> predict = classifier.PredictAsync("invoice");
        await inner.PredictEntered.Task;

        Task learn = classifier.LearnAsync(document);
        Assert.False(learn.IsCompleted);
        Assert.False(inner.LearnEntered.Task.IsCompleted);

        inner.PredictRelease.SetResult(true);
        await predict;

        await inner.LearnEntered.Task;
        inner.LearnRelease.SetResult(true);

        await learn;
    }

    [Fact]
    public async Task AsyncPredictionsOverlap()
    {
        var inner = new BlockingAsyncClassifier();
        using var classifier = new SynchronizedTextClassifier(inner);

        Task<IReadOnlyList<ClassificationResult>> first = classifier.PredictAsync("first");
        Task<IReadOnlyList<ClassificationResult>> second = classifier.PredictAsync("second");

        await inner.TwoPredictionsEntered.Task;

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        inner.PredictRelease.SetResult(true);

        await Task.WhenAll(first, second);
    }

    // The two blocking tests above can only show that the wrapper's task is not finished; they
    // cannot show that the inner classifier was not entered, because the fake blocks either way.
    // This probe records actual overlap inside the model, which is the property the lock exists for.
    [Fact]
    public async Task AsyncReadersAndWriters_AreExclusive()
    {
        var inner = new ExclusionProbeClassifier();
        using var classifier = new SynchronizedTextClassifier(inner);
        var document = new SearchDocument("9", "package delivery", Category: "Shipping");

        Task[] readers = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () =>
            {
                for (int i = 0; i < 2000; i++)
                    await classifier.PredictAsync("invoice");
            }))
            .ToArray();

        Task[] writers = Enumerable.Range(0, 2)
            .Select(_ => Task.Run(async () =>
            {
                for (int i = 0; i < 500; i++)
                {
                    await classifier.LearnAsync(document);
                    await classifier.UnlearnAsync(document);
                }
            }))
            .ToArray();

        await Task.WhenAll([.. readers, .. writers]);

        Assert.Equal(0, inner.Violations);
        Assert.True(inner.MaxConcurrentReaders > 1, "the read lock did not admit overlapping readers");
    }

    /// <summary>
    /// Async prediction and synchronous incremental learning, but no async incremental capability:
    /// the wrapper must report that distinction rather than claim <c>LearnAsync</c> works.
    /// </summary>
    private sealed class AsyncNonIncrementalClassifier : IAsyncTextClassifier, IIncrementalTextClassifier
    {
        public void Train(IEnumerable<SearchDocument> documents) { }

        public IReadOnlyList<ClassificationResult> Predict(
            string text, int limit = 3, IReadOnlySet<string>? excludedCategories = null) =>
            [new ClassificationResult("A", 1)];

        public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null) => "A";

        public Task TrainAsync(
            IEnumerable<SearchDocument> documents,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ClassificationResult>> PredictAsync(
            string text,
            int limit = 3,
            IReadOnlySet<string>? excludedCategories = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ClassificationResult>>([new ClassificationResult("A", 1)]);

        public void Learn(SearchDocument document) { }

        public void Unlearn(SearchDocument document) { }
    }

    /// <summary>
    /// Records whether a reader and a writer, or two writers, were ever inside the model at once.
    /// The counters are the test's own oracle; the wrapper cannot make them lie.
    /// </summary>
    private sealed class ExclusionProbeClassifier : IIncrementalAsyncTextClassifier
    {
        private int _readersInside;
        private int _writerInside;
        private int _violations;
        private int _maxConcurrentReaders;

        public int Violations => Volatile.Read(ref _violations);

        public int MaxConcurrentReaders => Volatile.Read(ref _maxConcurrentReaders);

        public void Train(IEnumerable<SearchDocument> documents) { }

        public IReadOnlyList<ClassificationResult> Predict(
            string text, int limit = 3, IReadOnlySet<string>? excludedCategories = null) =>
            [new ClassificationResult("A", 1)];

        public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null) => "A";

        public Task TrainAsync(
            IEnumerable<SearchDocument> documents,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<IReadOnlyList<ClassificationResult>> PredictAsync(
            string text,
            int limit = 3,
            IReadOnlySet<string>? excludedCategories = null,
            CancellationToken cancellationToken = default)
        {
            int readers = Interlocked.Increment(ref _readersInside);

            if (Volatile.Read(ref _writerInside) != 0)
                Interlocked.Increment(ref _violations);

            RecordReaderCount(readers);

            try
            {
                await Task.Yield();
                return [new ClassificationResult("A", 1)];
            }
            finally
            {
                Interlocked.Decrement(ref _readersInside);
            }
        }

        public Task LearnAsync(
            SearchDocument document,
            CancellationToken cancellationToken = default) => WriteAsync();

        public Task UnlearnAsync(
            SearchDocument document,
            CancellationToken cancellationToken = default) => WriteAsync();

        private async Task WriteAsync()
        {
            bool ownsWriter = Interlocked.CompareExchange(ref _writerInside, 1, 0) == 0;

            if (!ownsWriter)
                Interlocked.Increment(ref _violations);

            if (Volatile.Read(ref _readersInside) != 0)
                Interlocked.Increment(ref _violations);

            try
            {
                await Task.Yield();
            }
            finally
            {
                if (ownsWriter)
                    Volatile.Write(ref _writerInside, 0);
            }
        }

        private void RecordReaderCount(int readers)
        {
            int current = Volatile.Read(ref _maxConcurrentReaders);

            while (readers > current)
            {
                int observed = Interlocked.CompareExchange(ref _maxConcurrentReaders, readers, current);

                if (observed == current)
                    return;

                current = observed;
            }
        }
    }

    /// <summary>
    /// A classifier whose async operations block until the test releases them, so the wrapper's
    /// lock ordering can be observed rather than timed.
    /// </summary>
    private sealed class BlockingAsyncClassifier : IIncrementalAsyncTextClassifier
    {
        public TaskCompletionSource<bool> LearnEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> LearnRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> PredictEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> PredictRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> TwoPredictionsEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _predictions;

        public void Train(IEnumerable<SearchDocument> documents) { }

        public IReadOnlyList<ClassificationResult> Predict(
            string text, int limit = 3, IReadOnlySet<string>? excludedCategories = null) =>
            [new ClassificationResult("A", 1)];

        public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null) => "A";

        public Task TrainAsync(
            IEnumerable<SearchDocument> documents,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<IReadOnlyList<ClassificationResult>> PredictAsync(
            string text,
            int limit = 3,
            IReadOnlySet<string>? excludedCategories = null,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _predictions) == 2)
                TwoPredictionsEntered.TrySetResult(true);

            PredictEntered.TrySetResult(true);
            await PredictRelease.Task.ConfigureAwait(false);

            return [new ClassificationResult("A", 1)];
        }

        public async Task LearnAsync(
            SearchDocument document,
            CancellationToken cancellationToken = default)
        {
            LearnEntered.TrySetResult(true);
            await LearnRelease.Task.ConfigureAwait(false);
        }

        public Task UnlearnAsync(
            SearchDocument document,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
