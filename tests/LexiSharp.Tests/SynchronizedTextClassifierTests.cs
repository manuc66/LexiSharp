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
}
