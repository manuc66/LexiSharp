using LexiSharp.Classification;
using LexiSharp.Core;
using LexiSharp.Eval;
using Xunit;

namespace LexiSharp.Tests;

public class OnlineClassificationEvaluationTests
{
    private static SearchDocument Doc(string id, string text, string category) =>
        new(id, text, Category: category);

    private static OnlineEvaluationOptions Options(
        int topK = 3,
        Func<int, bool>? retrainWhen = null,
        bool learn = true,
        double coverageTarget = 0.5) =>
        new()
        {
            TopK = topK,
            RetrainWhen = retrainWhen,
            BootstrapResamples = 200,
            Seed = 7,
            CoverageTarget = coverageTarget,
            Learn = learn,
            Method = "test",
        };

    [Fact]
    public void TheModelPredictsBeforeItLearns()
    {
        var model = new LearningClassifier();
        var stream = new[]
        {
            new OnlineEvaluationExample(Doc("1", "same text", "A"), 0),
            new OnlineEvaluationExample(Doc("2", "same text", "A"), 0),
        };

        var report = OnlineClassificationEvaluator.Evaluate(
            () => model, Array.Empty<SearchDocument>(), stream, Options());

        // First occurrence: the model has not seen "same text" and answers B. It is learned after
        // the prediction, so the second occurrence answers A.
        // The first call is the warmup Train, even when the warmup is empty: the evaluator hands
        // the factory a defined starting state.
        Assert.Equal("train", model.Calls[0]);
        Assert.Equal("predict:same text", model.Calls[1]);
        Assert.Equal("learn:same text", model.Calls[2]);
        Assert.Equal("predict:same text", model.Calls[3]);
        Assert.Equal(0.5, report.AccuracyAt1, precision: 10);

        OnlineBucketMetrics neverSeen = Assert.Single(
            report.TextBuckets, bucket => bucket.Bucket == "text:never-seen");
        OnlineBucketMetrics seen = Assert.Single(
            report.TextBuckets, bucket => bucket.Bucket == "text:seen");

        Assert.Equal(1, neverSeen.Documents);
        Assert.Equal(0, neverSeen.Accuracy);
        Assert.Equal(1, seen.Documents);
        Assert.Equal(1, seen.Accuracy);
    }

    [Fact]
    public void TheCategoryBucketFollowsWhatTheModelHasLearned()
    {
        var model = new CategoryClassifier("A");
        var stream = new[]
        {
            new OnlineEvaluationExample(Doc("1", "first", "B"), 0),
            new OnlineEvaluationExample(Doc("2", "second", "B"), 0),
        };
        var warmup = new[] { Doc("0", "warm", "A") };

        var report = OnlineClassificationEvaluator.Evaluate(() => model, warmup, stream, Options());

        OnlineBucketMetrics neverSeen = Assert.Single(
            report.CategoryBuckets, bucket => bucket.Bucket == "category:never-seen");
        OnlineBucketMetrics seen = Assert.Single(
            report.CategoryBuckets, bucket => bucket.Bucket == "category:seen");

        Assert.Equal(1, neverSeen.Documents);
        Assert.Equal(1, seen.Documents);
    }

    [Fact]
    public void TopKCountsARankTwoCategoryAtTwoButNotAtOne()
    {
        var model = new FixedClassifier(
            new ClassificationResult("B", 0.7),
            new ClassificationResult("A", 0.3));
        var stream = new[] { new OnlineEvaluationExample(Doc("1", "x", "A"), 0) };

        var report = OnlineClassificationEvaluator.Evaluate(
            () => model, Array.Empty<SearchDocument>(), stream, Options(topK: 2, learn: false));

        Assert.Equal(0, report.AccuracyAt1);
        Assert.Equal(1, report.AccuracyAtK);
    }

    [Fact]
    public void ANonIncrementalModelNeedsARetrainingSchedule()
    {
        var model = new CountingClassifier();
        var stream = new[] { new OnlineEvaluationExample(Doc("1", "x", "A"), 0) };

        var exception = Assert.Throws<ArgumentException>(() =>
            OnlineClassificationEvaluator.Evaluate(
                () => model, Array.Empty<SearchDocument>(), stream, Options()));

        Assert.Contains(nameof(CountingClassifier), exception.Message, StringComparison.Ordinal);
        Assert.Contains("retraining schedule", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRetrainingScheduleCountsTheExampleJustConsumed()
    {
        var model = new CountingClassifier();
        var stream = Enumerable.Range(0, 5)
            .Select(i => new OnlineEvaluationExample(Doc(i.ToString(), "text " + i, "A"), 0))
            .ToArray();

        var report = OnlineClassificationEvaluator.Evaluate(
            () => model,
            Array.Empty<SearchDocument>(),
            stream,
            Options(retrainWhen: count => count % 2 == 0));

        Assert.Equal(2, report.RetrainCalls);
        // One Train for the (empty) warmup, then one per scheduled retrain.
        Assert.Equal(3, model.TrainCalls);
    }

    [Fact]
    public void NoLearnLeavesTheModelAlone()
    {
        var model = new LearningClassifier();
        var stream = new[] { new OnlineEvaluationExample(Doc("1", "x", "A"), 0) };

        var report = OnlineClassificationEvaluator.Evaluate(
            () => model, Array.Empty<SearchDocument>(), stream, Options(learn: false));

        Assert.Equal(0, report.LearnCalls);
        Assert.Equal(0, model.LearnCalls);
        Assert.DoesNotContain(model.Calls, call => call.StartsWith("learn:", StringComparison.Ordinal));
    }

    [Fact]
    public void CoverageRecommendsTheHighestPrecisionThatStillCoversTheTarget()
    {
        var model = new ProbabilityClassifier(
            ("A", 0.9, true),
            ("A", 0.8, false),
            ("A", 0.4, true),
            ("A", 0.3, false));
        var stream = new[]
        {
            new OnlineEvaluationExample(Doc("1", "p9", "A"), 0),
            new OnlineEvaluationExample(Doc("2", "p8", "A"), 0),
            new OnlineEvaluationExample(Doc("3", "p4", "A"), 0),
            new OnlineEvaluationExample(Doc("4", "p3", "A"), 0),
        };

        var report = OnlineClassificationEvaluator.Evaluate(
            () => model, Array.Empty<SearchDocument>(), stream, Options(coverageTarget: 0.5, learn: false));

        Assert.Equal(0.4, report.RecommendedCoverage.Threshold, precision: 10);
        Assert.Equal(0.75, report.RecommendedCoverage.Coverage, precision: 10);
        Assert.Equal(2.0 / 3.0, report.RecommendedCoverage.Precision, precision: 10);
    }

    [Fact]
    public void AnEmptyStreamReportsZeroRatherThanDividingByIt()
    {
        var model = new LearningClassifier();
        var report = OnlineClassificationEvaluator.Evaluate(
            () => model,
            Array.Empty<SearchDocument>(),
            Array.Empty<OnlineEvaluationExample>(),
            Options());

        Assert.Equal(0, report.Documents);
        Assert.Equal(0, report.AccuracyAt1);
        Assert.Equal(0, report.AccuracyAtK);
        Assert.DoesNotContain(report.Coverage, point => point.Total > 0);
    }

    [Fact]
    public void TheOrderHashIsStableAndCoversBlocks()
    {
        var model = new LearningClassifier();
        var stream = new[]
        {
            new OnlineEvaluationExample(Doc("1", "a", "A"), 0),
            new OnlineEvaluationExample(Doc("2", "b", "A"), 1),
        };

        var first = OnlineClassificationEvaluator.Evaluate(
            () => model, Array.Empty<SearchDocument>(), stream, Options());
        var second = OnlineClassificationEvaluator.Evaluate(
            () => model, Array.Empty<SearchDocument>(), stream, Options());

        Assert.Equal(first.OrderHash, second.OrderHash);
        Assert.Equal(64, first.OrderHash.Length);
    }

    [Fact]
    public void InvalidOptionsAndStreamRowsAreRejected()
    {
        var model = new LearningClassifier();
        var stream = new[] { new OnlineEvaluationExample(Doc("1", "x", "A"), 0) };

        Assert.Throws<ArgumentNullException>(() =>
            OnlineClassificationEvaluator.Evaluate(null!, Array.Empty<SearchDocument>(), stream));
        Assert.Throws<ArgumentNullException>(() =>
            OnlineClassificationEvaluator.Evaluate(() => model, null!, stream));
        Assert.Throws<ArgumentNullException>(() =>
            OnlineClassificationEvaluator.Evaluate(() => model, Array.Empty<SearchDocument>(), null!));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OnlineClassificationEvaluator.Evaluate(
                () => model, Array.Empty<SearchDocument>(), stream, Options(topK: 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OnlineClassificationEvaluator.Evaluate(
                () => model,
                Array.Empty<SearchDocument>(),
                stream,
                new OnlineEvaluationOptions { CoverageTarget = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OnlineClassificationEvaluator.Evaluate(
                () => model,
                Array.Empty<SearchDocument>(),
                stream,
                new OnlineEvaluationOptions { BootstrapResamples = 0 }));
        Assert.Throws<ArgumentException>(() =>
            OnlineClassificationEvaluator.Evaluate(
                () => model,
                Array.Empty<SearchDocument>(),
                new[] { new OnlineEvaluationExample(Doc("1", "x", string.Empty), 0) }));
        Assert.Throws<ArgumentNullException>(() =>
            OnlineClassificationEvaluator.Evaluate(
                () => model,
                Array.Empty<SearchDocument>(),
                new OnlineEvaluationExample[] { null! }));
        Assert.Throws<InvalidOperationException>(() =>
            OnlineClassificationEvaluator.Evaluate(
                () => null!,
                Array.Empty<SearchDocument>(),
                stream));
    }

    [Fact]
    public void TheReportExposesEveryFigure()
    {
        var model = new LearningClassifier();
        var report = OnlineClassificationEvaluator.Evaluate(
            () => model,
            Array.Empty<SearchDocument>(),
            new[] { new OnlineEvaluationExample(Doc("1", "x", "A"), 0) },
            Options(learn: false));

        _ = report.Method;
        _ = report.Documents;
        _ = report.TopK;
        _ = report.AccuracyAt1;
        _ = report.AccuracyAtK;
        _ = report.MacroF1;
        _ = report.AccuracyAt195;
        _ = report.AccuracyAtK95;
        _ = report.MacroF195;
        _ = report.TextBuckets;
        _ = report.CategoryBuckets;
        _ = report.Coverage;
        _ = report.RecommendedCoverage;
        _ = report.LearnCalls;
        _ = report.RetrainCalls;
        _ = report.WarmupTrainTime;
        _ = report.PredictTime;
        _ = report.LearnTime;
        _ = report.RetrainTime;
        _ = report.OrderHash;
    }

    [Fact]
    public void MacroF1CountsAFalsePositiveAndAFalseNegative()
    {
        var model = new FixedClassifier(new ClassificationResult("B", 1));
        var report = OnlineClassificationEvaluator.Evaluate(
            () => model,
            Array.Empty<SearchDocument>(),
            new[] { new OnlineEvaluationExample(Doc("1", "x", "A"), 0) },
            Options(learn: false));

        Assert.Equal(0, report.MacroF1);
    }

    [Fact]
    public void TheBlockBootstrapValidatesItsInputsAndHandlesEmptyInputs()
    {
        Assert.Throws<ArgumentException>(() =>
            OnlineEvaluationStatistics.BootstrapBlocks(
                new[] { 0 }, new[] { "A" }, new string?[] { "A", "B" }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OnlineEvaluationStatistics.BootstrapBlocks(
                new[] { 0 }, new[] { "A" }, new string?[] { "A" }, resamples: 0));
        Assert.Throws<ArgumentException>(() =>
            OnlineEvaluationStatistics.BootstrapAccuracyBlocks(
                new[] { 0 }, new[] { true, false }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OnlineEvaluationStatistics.BootstrapAccuracyBlocks(
                new[] { 0 }, new[] { true }, resamples: 0));

        EvaluationIntervals empty = OnlineEvaluationStatistics.BootstrapBlocks(
            Array.Empty<int>(), Array.Empty<string>(), Array.Empty<string?>());
        Assert.Equal(0, empty.Accuracy.Lower);
        Assert.Equal(0, empty.MacroF1.Upper);
        Assert.Equal(0, OnlineEvaluationStatistics.BootstrapAccuracyBlocks(
            Array.Empty<int>(), Array.Empty<bool>()).Lower);
    }

    [Fact]
    public void TheBlockBootstrapHandlesTwoCategories()
    {
        EvaluationIntervals intervals = OnlineEvaluationStatistics.BootstrapBlocks(
            new[] { 0, 0, 1, 1 },
            new[] { "A", "B", "A", "B" },
            new[] { "B", "A", "A", "B" },
            200,
            7);

        Assert.InRange(intervals.Accuracy.Lower, 0, 1);
        Assert.InRange(intervals.Accuracy.Upper, intervals.Accuracy.Lower, 1);
    }

    [Fact]
    public void ASynchronizedPlainModelIgnoresAContextInsteadOfThrowing()
    {
        using var model = new SynchronizedTextClassifier(new LearningClassifier());
        var stream = new[]
        {
            new OnlineEvaluationExample(
                Doc("1", "x", "A"),
                0,
                new BlendContext { Candidates = new[] { new TextEvidence("X", 1) } }),
        };

        var report = OnlineClassificationEvaluator.Evaluate(
            () => model, Array.Empty<SearchDocument>(), stream, Options(learn: false));

        Assert.Equal(0, report.AccuracyAt1);
    }

    [Fact]
    public void ASynchronizedContextualModelReceivesTheContext()
    {
        var blend = new BlendTextClassifier(new[]
        {
            new TextClassifierLane(new FixedClassifier(
                new ClassificationResult("A", 0.5), new ClassificationResult("B", 0.5)), 1),
        });
        using var model = new SynchronizedTextClassifier(blend);
        var stream = new[]
        {
            new OnlineEvaluationExample(
                Doc("1", "x", "X"),
                0,
                new BlendContext { Candidates = new[] { new TextEvidence("X", 10) } }),
        };

        var report = OnlineClassificationEvaluator.Evaluate(
            () => model, Array.Empty<SearchDocument>(), stream, Options(learn: false));

        Assert.Equal(1, report.AccuracyAt1);
    }

    [Fact]
    public void ASynchronizedNonIncrementalModelNeedsASchedule()
    {
        using var model = new SynchronizedTextClassifier(new FixedClassifier(
            new ClassificationResult("A", 1)));
        var stream = new[] { new OnlineEvaluationExample(Doc("1", "x", "A"), 0) };

        var exception = Assert.Throws<ArgumentException>(() =>
            OnlineClassificationEvaluator.Evaluate(
                () => model, Array.Empty<SearchDocument>(), stream, Options()));

        Assert.Contains("retraining schedule", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASynchronizedWrappedNonIncrementalFusedNeedsASchedule()
    {
        var fused = new FusedTextClassifier(
            new FixedClassifier(new ClassificationResult("A", 0.5)),
            new FixedClassifier(new ClassificationResult("B", 0.5)));
        using var model = new SynchronizedTextClassifier(fused);

        Assert.False(model.SupportsIncremental);

        var stream = new[] { new OnlineEvaluationExample(Doc("1", "x", "A"), 0) };
        Assert.Throws<ArgumentException>(() =>
            OnlineClassificationEvaluator.Evaluate(
                () => model, Array.Empty<SearchDocument>(), stream, Options()));
    }

    [Fact]
    public void AFusedWithANonIncrementalLaneNeedsASchedule()
    {
        var model = new FusedTextClassifier(
            new FixedClassifier(new ClassificationResult("A", 0.5)),
            new FixedClassifier(new ClassificationResult("B", 0.5)));
        var stream = new[] { new OnlineEvaluationExample(Doc("1", "x", "A"), 0) };

        Assert.Throws<ArgumentException>(() =>
            OnlineClassificationEvaluator.Evaluate(
                () => model, Array.Empty<SearchDocument>(), stream, Options()));
    }

    [Fact]
    public void APerExampleContextReachesAContextualClassifier()
    {
        var blend = new BlendTextClassifier(new[]
        {
            new TextClassifierLane(new FixedClassifier(
                new ClassificationResult("A", 0.5), new ClassificationResult("B", 0.5)), 1),
        });
        var context = new BlendContext
        {
            Candidates = new[] { new TextEvidence("X", 10) },
        };
        var withContext = new[]
        {
            new OnlineEvaluationExample(Doc("1", "x", "X"), 0, context),
        };
        var withoutContext = new[]
        {
            new OnlineEvaluationExample(Doc("2", "x", "X"), 0),
        };

        var contextual = OnlineClassificationEvaluator.Evaluate(
            () => blend, Array.Empty<SearchDocument>(), withContext, Options(learn: false));
        var plain = OnlineClassificationEvaluator.Evaluate(
            () => blend, Array.Empty<SearchDocument>(), withoutContext, Options(learn: false));

        Assert.IsAssignableFrom<IContextualTextClassifier>(blend);
        Assert.Equal(1.0, contextual.AccuracyAt1);
        Assert.Equal(0.0, plain.AccuracyAt1);
    }

    [Fact]
    public void TheOrderHashIncludesTheRequestContext()
    {
        var model = new FixedClassifier(new ClassificationResult("A", 1));
        var first = new[]
        {
            new OnlineEvaluationExample(
                Doc("1", "x", "A"),
                0,
                new BlendContext { Candidates = new[] { new TextEvidence("X", 1) } }),
        };
        var second = new[]
        {
            new OnlineEvaluationExample(
                Doc("1", "x", "A"),
                0,
                new BlendContext { Candidates = new[] { new TextEvidence("Y", 1) } }),
        };

        var firstReport = OnlineClassificationEvaluator.Evaluate(
            () => model, Array.Empty<SearchDocument>(), first, Options(learn: false));
        var secondReport = OnlineClassificationEvaluator.Evaluate(
            () => model, Array.Empty<SearchDocument>(), second, Options(learn: false));

        Assert.NotEqual(firstReport.OrderHash, secondReport.OrderHash);
    }

    [Fact]
    public void TheBlockBootstrapResamplesBlocksNotRows()
    {
        // Two large blocks with opposite outcomes: a document bootstrap is tight around 0.5, a
        // block bootstrap draws whole blocks and therefore ranges from 0 to 1.
        const int rowsPerBlock = 1000;
        var blocks = Enumerable.Repeat(0, rowsPerBlock)
            .Concat(Enumerable.Repeat(1, rowsPerBlock))
            .ToArray();
        var actual = Enumerable.Repeat("A", rowsPerBlock * 2).ToArray();
        var predicted = Enumerable.Repeat("A", rowsPerBlock)
            .Concat(Enumerable.Repeat("B", rowsPerBlock))
            .ToArray();

        EvaluationIntervals blocksInterval =
            OnlineEvaluationStatistics.BootstrapBlocks(blocks, actual, predicted, 500, 7);
        BootstrapIntervals rowsInterval = ClassificationStatistics.Bootstrap(
            actual.Select((category, index) => new ClassificationDocument(index.ToString(), "t", category)).ToArray(),
            predicted,
            500,
            7);

        Assert.True(
            blocksInterval.Accuracy.Upper - blocksInterval.Accuracy.Lower >
            rowsInterval.Accuracy.Upper - rowsInterval.Accuracy.Lower,
            $"block [{blocksInterval.Accuracy.Lower:F3}, {blocksInterval.Accuracy.Upper:F3}] " +
            $"row [{rowsInterval.Accuracy.Lower:F3}, {rowsInterval.Accuracy.Upper:F3}]");
    }

    private sealed class FixedClassifier(params ClassificationResult[] results) : ITextClassifier
    {
        public void Train(IEnumerable<SearchDocument> documents) { }

        public IReadOnlyList<ClassificationResult> Predict(
            string text, int limit = 3, IReadOnlySet<string>? excludedCategories = null) =>
            results.Take(limit).ToArray();

        public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null) =>
            results.Length > 0 ? results[0].Category : null;
    }

    private sealed class LearningClassifier : ITextClassifier, IIncrementalTextClassifier
    {
        private readonly HashSet<string> _learned = new(StringComparer.Ordinal);

        public List<string> Calls { get; } = new();

        public int LearnCalls { get; private set; }

        public void Train(IEnumerable<SearchDocument> documents)
        {
            Calls.Add("train");

            foreach (SearchDocument document in documents)
                _learned.Add(document.Text);
        }

        public void Learn(SearchDocument document)
        {
            Calls.Add("learn:" + document.Text);
            LearnCalls++;
            _learned.Add(document.Text);
        }

        public void Unlearn(SearchDocument document) => _learned.Remove(document.Text);

        public IReadOnlyList<ClassificationResult> Predict(
            string text, int limit = 3, IReadOnlySet<string>? excludedCategories = null)
        {
            Calls.Add("predict:" + text);

            return _learned.Contains(text)
                ? new[] { new ClassificationResult("A", 0.9), new ClassificationResult("B", 0.1) }
                    .Take(limit).ToArray()
                : new[] { new ClassificationResult("B", 0.9), new ClassificationResult("A", 0.1) }
                    .Take(limit).ToArray();
        }

        public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null)
        {
            IReadOnlyList<ClassificationResult> results = Predict(text, limit: 1);
            return results.Count > 0 ? results[0].Category : null;
        }
    }

    private sealed class CategoryClassifier(string initial) : ITextClassifier, IIncrementalTextClassifier
    {
        private readonly SortedSet<string> _categories = new(StringComparer.Ordinal) { initial };

        public void Train(IEnumerable<SearchDocument> documents) { }

        public void Learn(SearchDocument document)
        {
            if (!string.IsNullOrEmpty(document.Category))
                _categories.Add(document.Category);
        }

        public void Unlearn(SearchDocument document) => _categories.Remove(document.Category ?? string.Empty);

        public IReadOnlyList<ClassificationResult> Predict(
            string text, int limit = 3, IReadOnlySet<string>? excludedCategories = null) =>
            _categories.Select((category, index) => new ClassificationResult(category, 1.0 / (index + 1)))
                .Take(limit).ToArray();

        public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null) =>
            _categories.Count > 0 ? _categories.Min : null;
    }

    private sealed class CountingClassifier : ITextClassifier
    {
        public int TrainCalls { get; private set; }

        public void Train(IEnumerable<SearchDocument> documents) => TrainCalls++;

        public IReadOnlyList<ClassificationResult> Predict(
            string text, int limit = 3, IReadOnlySet<string>? excludedCategories = null) =>
            new[] { new ClassificationResult("A", 1.0) };

        public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null) => "A";
    }

    private sealed class ProbabilityClassifier(
        params (string Category, double Probability, bool Correct)[] outcomes) : ITextClassifier
    {
        private int _index;

        public void Train(IEnumerable<SearchDocument> documents) { }

        public IReadOnlyList<ClassificationResult> Predict(
            string text, int limit = 3, IReadOnlySet<string>? excludedCategories = null)
        {
            (string category, double probability, bool correct) = outcomes[_index++ % outcomes.Length];
            string winner = correct ? "A" : "B";
            return new[]
            {
                new ClassificationResult(winner, probability),
                new ClassificationResult(winner == "A" ? "B" : "A", 1 - probability),
            }.Take(limit).ToArray();
        }

        public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null)
        {
            IReadOnlyList<ClassificationResult> results = Predict(text, limit: 1);
            return results.Count > 0 ? results[0].Category : null;
        }
    }
}
