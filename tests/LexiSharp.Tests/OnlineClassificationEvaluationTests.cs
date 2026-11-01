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
        ITextClassifier model,
        IReadOnlyList<SearchDocument> warmup,
        IReadOnlyList<OnlineExample> stream,
        int topK = 3,
        Func<int, bool>? retrainWhen = null,
        bool learn = true,
        double coverageTarget = 0.5) =>
        new(() => model, warmup, stream, topK, retrainWhen, BootstrapResamples: 200, Seed: 7,
            CoverageTarget: coverageTarget, Learn: learn, Method: "test");

    [Fact]
    public void TheModelPredictsBeforeItLearns()
    {
        var model = new LearningClassifier();
        var stream = new[]
        {
            new OnlineExample(Doc("1", "same text", "A"), 0),
            new OnlineExample(Doc("2", "same text", "A"), 0),
        };

        var report = OnlineClassificationEvaluation.Run(Options(model, Array.Empty<SearchDocument>(), stream));

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
            new OnlineExample(Doc("1", "first", "B"), 0),
            new OnlineExample(Doc("2", "second", "B"), 0),
        };
        var warmup = new[] { Doc("0", "warm", "A") };

        var report = OnlineClassificationEvaluation.Run(Options(model, warmup, stream));

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
        var stream = new[] { new OnlineExample(Doc("1", "x", "A"), 0) };

        var report = OnlineClassificationEvaluation.Run(
            Options(model, Array.Empty<SearchDocument>(), stream, topK: 2, learn: false));

        Assert.Equal(0, report.AccuracyAt1);
        Assert.Equal(1, report.AccuracyAtK);
    }

    [Fact]
    public void ANonIncrementalModelNeedsARetrainingSchedule()
    {
        var model = new CountingClassifier();
        var stream = new[] { new OnlineExample(Doc("1", "x", "A"), 0) };

        var exception = Assert.Throws<ArgumentException>(() =>
            OnlineClassificationEvaluation.Run(Options(model, Array.Empty<SearchDocument>(), stream)));

        Assert.Contains(nameof(CountingClassifier), exception.Message, StringComparison.Ordinal);
        Assert.Contains("retraining schedule", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRetrainingScheduleCountsTheExampleJustConsumed()
    {
        var model = new CountingClassifier();
        var stream = Enumerable.Range(0, 5)
            .Select(i => new OnlineExample(Doc(i.ToString(), "text " + i, "A"), 0))
            .ToArray();

        var report = OnlineClassificationEvaluation.Run(
            Options(model, Array.Empty<SearchDocument>(), stream, retrainWhen: count => count % 2 == 0));

        Assert.Equal(2, report.RetrainCalls);
        // One Train for the (empty) warmup, then one per scheduled retrain.
        Assert.Equal(3, model.TrainCalls);
    }

    [Fact]
    public void NoLearnLeavesTheModelAlone()
    {
        var model = new LearningClassifier();
        var stream = new[] { new OnlineExample(Doc("1", "x", "A"), 0) };

        var report = OnlineClassificationEvaluation.Run(
            Options(model, Array.Empty<SearchDocument>(), stream, learn: false));

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
            new OnlineExample(Doc("1", "p9", "A"), 0),
            new OnlineExample(Doc("2", "p8", "A"), 0),
            new OnlineExample(Doc("3", "p4", "A"), 0),
            new OnlineExample(Doc("4", "p3", "A"), 0),
        };

        var report = OnlineClassificationEvaluation.Run(
            Options(model, Array.Empty<SearchDocument>(), stream, coverageTarget: 0.5, learn: false));

        Assert.Equal(0.4, report.RecommendedCoverage.Threshold, precision: 10);
        Assert.Equal(0.75, report.RecommendedCoverage.Coverage, precision: 10);
        Assert.Equal(2.0 / 3.0, report.RecommendedCoverage.Precision, precision: 10);
    }

    [Fact]
    public void AnEmptyStreamReportsZeroRatherThanDividingByIt()
    {
        var report = OnlineClassificationEvaluation.Run(Options(
            new LearningClassifier(),
            Array.Empty<SearchDocument>(),
            Array.Empty<OnlineExample>()));

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
            new OnlineExample(Doc("1", "a", "A"), 0),
            new OnlineExample(Doc("2", "b", "A"), 1),
        };

        var first = OnlineClassificationEvaluation.Run(Options(model, Array.Empty<SearchDocument>(), stream));
        var second = OnlineClassificationEvaluation.Run(Options(model, Array.Empty<SearchDocument>(), stream));

        Assert.Equal(first.OrderHash, second.OrderHash);
        Assert.Equal(64, first.OrderHash.Length);
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

        BootstrapIntervals blocksInterval =
            ClassificationStatistics.BootstrapBlocks(blocks, actual, predicted, 500, 7);
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
