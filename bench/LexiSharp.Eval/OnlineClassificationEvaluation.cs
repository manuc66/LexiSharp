using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LexiSharp.Classification;
using LexiSharp.Core;

namespace LexiSharp.Eval;

/// <summary>One labelled document in the stream, with the block (day, period, ...) it belongs to.</summary>
internal sealed record OnlineExample(SearchDocument Document, int Block);

/// <summary>
/// The inputs of a predict-then-learn run. The order of <see cref="Warmup"/> then
/// <see cref="Stream"/> is the order the model sees; the evaluator never reorders it.
/// </summary>
internal sealed record OnlineEvaluationOptions(
    Func<ITextClassifier> Factory,
    IReadOnlyList<SearchDocument> Warmup,
    IReadOnlyList<OnlineExample> Stream,
    int TopK = 3,
    Func<int, bool>? RetrainWhen = null,
    int BootstrapResamples = 1000,
    int Seed = 7,
    double CoverageTarget = 0.5,
    bool Learn = true,
    string? Method = null);

/// <summary>Accuracy and macro-F1 for one slice of the stream.</summary>
internal sealed record OnlineBucketMetrics(
    string Bucket,
    int Documents,
    double Accuracy,
    double MacroF1,
    ConfidenceInterval Accuracy95,
    ConfidenceInterval MacroF195);

/// <summary>The outcome of one predict-then-learn run.</summary>
internal sealed record OnlineReport(
    string Method,
    int Documents,
    int TopK,
    double AccuracyAt1,
    double AccuracyAtK,
    double MacroF1,
    ConfidenceInterval AccuracyAt195,
    ConfidenceInterval AccuracyAtK95,
    ConfidenceInterval MacroF195,
    IReadOnlyList<OnlineBucketMetrics> TextBuckets,
    IReadOnlyList<OnlineBucketMetrics> CategoryBuckets,
    IReadOnlyList<CoveragePoint> Coverage,
    CoveragePoint RecommendedCoverage,
    int LearnCalls,
    int RetrainCalls,
    TimeSpan WarmupTrainTime,
    TimeSpan PredictTime,
    TimeSpan LearnTime,
    TimeSpan RetrainTime,
    string OrderHash);

/// <summary>
/// Replays a labelled stream through any <see cref="ITextClassifier"/>: predict first, then learn.
/// </summary>
/// <remarks>
/// <para>
/// The order is the contract. For every example the model is asked for a distribution
/// <b>before</b> the example is learned, so a document never helps classify itself. The evaluator
/// then reports the rank of the true category in that distribution, splits the stream by what the
/// model had seen at that moment (the text, and the category), and only afterwards mutates the
/// model: <see cref="IIncrementalTextClassifier.Learn"/> when the model supports it, or a full
/// <see cref="ITextClassifier.Train"/> on the accumulated corpus when <see cref="OnlineEvaluationOptions.RetrainWhen"/>
/// says so.
/// </para>
/// <para>
/// A non-incremental model without a retraining schedule is rejected: its predictions would be
/// constant after the warmup, and reporting that as an online measurement would be a lie.
/// </para>
/// <para>
/// Confidence intervals are a percentile bootstrap over <b>blocks</b>, not documents. A stream is
/// ordered in time and two rows from the same block are not independent; resampling whole blocks
/// keeps that correlation. The seed is fixed, so the interval is reproducible.
/// </para>
/// </remarks>
internal static class OnlineClassificationEvaluation
{
    public static OnlineReport Run(OnlineEvaluationOptions options)
    {
        Validate(options);
        ITextClassifier model = CreateModel(options);

        (List<SearchDocument> accumulated, HashSet<string> seenTexts, HashSet<string> seenCategories,
            TimeSpan warmupTime) = Warmup(model, options);

        StreamResult stream = RunStream(model, options, accumulated, seenTexts, seenCategories);

        return BuildReport(
            options,
            model,
            stream.Outcomes,
            stream.LearnCalls,
            stream.RetrainCalls,
            warmupTime,
            stream.PredictTime,
            stream.LearnTime,
            stream.RetrainTime);
    }

    private static void Validate(OnlineEvaluationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Factory);
        ArgumentNullException.ThrowIfNull(options.Warmup);
        ArgumentNullException.ThrowIfNull(options.Stream);

        if (options.TopK < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "TopK must be at least 1.");

        if (!double.IsFinite(options.CoverageTarget) || options.CoverageTarget <= 0 || options.CoverageTarget > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The coverage target must be greater than 0 and at most 1.");
        }

        if (options.BootstrapResamples < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "At least one bootstrap resample is required.");

        foreach (OnlineExample example in options.Stream)
        {
            SearchDocument document = example.Document
                ?? throw new ArgumentException("The stream contains a null document.", nameof(options));

            if (string.IsNullOrEmpty(document.Category))
                throw new ArgumentException($"Stream document '{document.Id}' has no category.", nameof(options));
        }
    }

    private static ITextClassifier CreateModel(OnlineEvaluationOptions options)
    {
        ITextClassifier model = options.Factory()
            ?? throw new InvalidOperationException("The classifier factory returned null.");

        if (options.Learn && options.RetrainWhen is null && model is not IIncrementalTextClassifier)
        {
            throw new ArgumentException(
                $"{model.GetType().Name} is not incremental and no retraining schedule was supplied; " +
                "the stream would never update the model.",
                nameof(options));
        }

        return model;
    }

    private static (
        List<SearchDocument> Accumulated,
        HashSet<string> SeenTexts,
        HashSet<string> SeenCategories,
        TimeSpan Elapsed) Warmup(ITextClassifier model, OnlineEvaluationOptions options)
    {
        var watch = Stopwatch.StartNew();
        model.Train(options.Warmup);
        watch.Stop();

        var seenTexts = new HashSet<string>(StringComparer.Ordinal);
        var seenCategories = new HashSet<string>(StringComparer.Ordinal);

        foreach (SearchDocument document in options.Warmup)
        {
            if (document is null)
                continue;

            seenTexts.Add(document.Text);

            if (!string.IsNullOrEmpty(document.Category))
                seenCategories.Add(document.Category);
        }

        var accumulated = new List<SearchDocument>(options.Warmup.Count + options.Stream.Count);
        accumulated.AddRange(options.Warmup);

        return (accumulated, seenTexts, seenCategories, watch.Elapsed);
    }

    private static StreamResult RunStream(
        ITextClassifier model,
        OnlineEvaluationOptions options,
        List<SearchDocument> accumulated,
        HashSet<string> seenTexts,
        HashSet<string> seenCategories)
    {
        var outcomes = new List<Outcome>(options.Stream.Count);
        var predictWatch = Stopwatch.StartNew();
        var learnWatch = new Stopwatch();
        var retrainWatch = new Stopwatch();
        int learnCalls = 0;
        int retrainCalls = 0;

        for (int i = 0; i < options.Stream.Count; i++)
        {
            OnlineExample example = options.Stream[i];
            outcomes.Add(Observe(model, example, options.TopK, seenTexts, seenCategories, predictWatch));
            seenTexts.Add(example.Document.Text);

            if (!options.Learn)
                continue;

            LearnOne(
                model,
                example.Document,
                i + 1,
                options,
                accumulated,
                seenCategories,
                learnWatch,
                retrainWatch,
                ref learnCalls,
                ref retrainCalls);
        }

        return new StreamResult(
            outcomes,
            learnCalls,
            retrainCalls,
            predictWatch.Elapsed,
            learnWatch.Elapsed,
            retrainWatch.Elapsed);
    }

    private static Outcome Observe(
        ITextClassifier model,
        OnlineExample example,
        int topK,
        HashSet<string> seenTexts,
        HashSet<string> seenCategories,
        Stopwatch predictWatch)
    {
        SearchDocument document = example.Document;
        string actual = document.Category!;

        predictWatch.Start();
        IReadOnlyList<ClassificationResult> results = model.Predict(document.Text, limit: int.MaxValue);
        predictWatch.Stop();

        string? predicted = results.Count > 0 ? results[0].Category : null;
        int rank = RankOf(results, actual);

        return new Outcome(
            example.Block,
            actual,
            predicted,
            results.Count > 0 ? results[0].Probability : 0,
            rank == 0,
            rank >= 0 && rank < topK,
            seenTexts.Contains(document.Text),
            seenCategories.Contains(actual));
    }

    private static void LearnOne(
        ITextClassifier model,
        SearchDocument document,
        int consumed,
        OnlineEvaluationOptions options,
        List<SearchDocument> accumulated,
        HashSet<string> seenCategories,
        Stopwatch learnWatch,
        Stopwatch retrainWatch,
        ref int learnCalls,
        ref int retrainCalls)
    {
        // The schedule counts the examples already consumed, including this one. That makes
        // `--retrain-every 50` read as "retrain after every 50th document", with no off-by-one
        // hidden in the caller.
        bool retrainDue = options.RetrainWhen is not null && options.RetrainWhen(consumed);

        if (model is IIncrementalTextClassifier incremental && !retrainDue)
        {
            learnWatch.Start();
            incremental.Learn(document);
            learnWatch.Stop();
            learnCalls++;

            if (!string.IsNullOrEmpty(document.Category))
                seenCategories.Add(document.Category);

            return;
        }

        accumulated.Add(document);

        if (!retrainDue)
            return;

        retrainWatch.Start();
        model.Train(accumulated);
        retrainWatch.Stop();
        retrainCalls++;
        RebuildSeenCategories(accumulated, seenCategories);
    }

    private static void RebuildSeenCategories(
        List<SearchDocument> accumulated,
        HashSet<string> seenCategories)
    {
        seenCategories.Clear();

        foreach (SearchDocument learned in accumulated)
        {
            if (!string.IsNullOrEmpty(learned.Category))
                seenCategories.Add(learned.Category);
        }
    }

    private sealed record StreamResult(
        List<Outcome> Outcomes,
        int LearnCalls,
        int RetrainCalls,
        TimeSpan PredictTime,
        TimeSpan LearnTime,
        TimeSpan RetrainTime);

    private static OnlineReport BuildReport(
        OnlineEvaluationOptions options,
        ITextClassifier model,
        List<Outcome> outcomes,
        int learnCalls,
        int retrainCalls,
        TimeSpan warmupTrainTime,
        TimeSpan predictTime,
        TimeSpan learnTime,
        TimeSpan retrainTime)
    {
        int topK = options.TopK;
        int total = outcomes.Count;

        double accuracyAt1 = total == 0 ? 0 : outcomes.Count(outcome => outcome.CorrectAt1) / (double)total;
        double accuracyAtK = total == 0 ? 0 : outcomes.Count(outcome => outcome.CorrectAtK) / (double)total;

        int[] blocks = outcomes.Select(outcome => outcome.Block).ToArray();
        string[] actual = outcomes.Select(outcome => outcome.Actual).ToArray();
        string?[] predicted = outcomes.Select(outcome => outcome.Predicted).ToArray();
        bool[] at1 = outcomes.Select(outcome => outcome.CorrectAt1).ToArray();
        bool[] atK = outcomes.Select(outcome => outcome.CorrectAtK).ToArray();

        BootstrapIntervals intervals = ClassificationStatistics.BootstrapBlocks(
            blocks, actual, predicted, options.BootstrapResamples, options.Seed);

        ConfidenceInterval atKInterval = ClassificationStatistics.BootstrapAccuracyBlocks(
            blocks, atK, options.BootstrapResamples, options.Seed);

        var textBuckets = new[]
        {
            BuildBucket("text:seen", outcomes.Where(outcome => outcome.SeenText).ToList(), options),
            BuildBucket("text:never-seen", outcomes.Where(outcome => !outcome.SeenText).ToList(), options),
        };

        var categoryBuckets = new[]
        {
            BuildBucket("category:seen", outcomes.Where(outcome => outcome.SeenCategory).ToList(), options),
            BuildBucket("category:never-seen", outcomes.Where(outcome => !outcome.SeenCategory).ToList(), options),
        };

        (IReadOnlyList<CoveragePoint> coverage, CoveragePoint recommended) =
            BuildCoverage(outcomes, options.CoverageTarget);

        return new OnlineReport(
            options.Method ?? model.GetType().Name,
            total,
            topK,
            accuracyAt1,
            accuracyAtK,
            PointMacroF1(outcomes),
            intervals.Accuracy,
            atKInterval,
            intervals.MacroF1,
            textBuckets,
            categoryBuckets,
            coverage,
            recommended,
            learnCalls,
            retrainCalls,
            warmupTrainTime,
            predictTime,
            learnTime,
            retrainTime,
            HashOrder(options));
    }

    private static OnlineBucketMetrics BuildBucket(
        string name,
        List<Outcome> outcomes,
        OnlineEvaluationOptions options)
    {
        if (outcomes.Count == 0)
        {
            return new OnlineBucketMetrics(name, 0, 0, 0, new(0, 0), new(0, 0));
        }

        int[] blocks = outcomes.Select(outcome => outcome.Block).ToArray();
        string[] actual = outcomes.Select(outcome => outcome.Actual).ToArray();
        string?[] predicted = outcomes.Select(outcome => outcome.Predicted).ToArray();

        BootstrapIntervals intervals = ClassificationStatistics.BootstrapBlocks(
            blocks, actual, predicted, options.BootstrapResamples, options.Seed);

        return new OnlineBucketMetrics(
            name,
            outcomes.Count,
            outcomes.Count(outcome => outcome.CorrectAt1) / (double)outcomes.Count,
            PointMacroF1(outcomes),
            intervals.Accuracy,
            intervals.MacroF1);
    }

    private static double PointMacroF1(List<Outcome> outcomes)
    {
        if (outcomes.Count == 0)
            return 0;

        string[] categories = outcomes
            .Select(outcome => outcome.Actual)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(category => category, StringComparer.Ordinal)
            .ToArray();

        double sum = 0;
        int present = 0;

        foreach (string category in categories)
        {
            double? f1 = CategoryF1(outcomes, category);

            if (f1 is null)
                continue;

            present++;
            sum += f1.Value;
        }

        return present == 0 ? 0 : sum / present;
    }

    private static double? CategoryF1(List<Outcome> outcomes, string category)
    {
        int truePositive = 0;
        int falsePositive = 0;
        int falseNegative = 0;

        foreach (Outcome outcome in outcomes)
        {
            bool isActual = string.Equals(outcome.Actual, category, StringComparison.Ordinal);
            bool isPredicted = string.Equals(outcome.Predicted, category, StringComparison.Ordinal);

            if (isActual && isPredicted)
                truePositive++;
            else if (!isActual && isPredicted)
                falsePositive++;
            else if (isActual && !isPredicted)
                falseNegative++;
        }

        if (truePositive + falsePositive + falseNegative == 0)
            return null;

        double precision = truePositive + falsePositive == 0
            ? 0
            : (double)truePositive / (truePositive + falsePositive);
        double recall = truePositive + falseNegative == 0
            ? 0
            : (double)truePositive / (truePositive + falseNegative);

        return precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
    }

    private static (IReadOnlyList<CoveragePoint> Points, CoveragePoint Recommended) BuildCoverage(
        List<Outcome> outcomes,
        double target)
    {
        int total = outcomes.Count;

        if (total == 0)
        {
            var empty = new CoveragePoint(1, 0, 0, 0, 0);
            return (new[] { empty }, empty);
        }

        var thresholds = outcomes
            .Select(outcome => outcome.TopProbability)
            .Distinct()
            .OrderByDescending(probability => probability)
            .ToArray();

        var points = new List<CoveragePoint>(thresholds.Length);

        foreach (double threshold in thresholds)
        {
            int covered = 0;
            int correct = 0;

            foreach (Outcome outcome in outcomes)
            {
                if (outcome.TopProbability < threshold)
                    continue;

                covered++;

                if (outcome.CorrectAt1)
                    correct++;
            }

            points.Add(new CoveragePoint(
                threshold,
                covered,
                total,
                (double)covered / total,
                covered == 0 ? 0 : (double)correct / covered));
        }

        CoveragePoint? recommended = null;

        foreach (CoveragePoint point in points)
        {
            if (point.Coverage < target)
                continue;

            if (recommended is null
                || point.Precision > recommended.Precision
                || (point.Precision == recommended.Precision && point.Coverage > recommended.Coverage))
            {
                recommended = point;
            }
        }

        // No threshold reaches the target: recommend the widest coverage rather than the sharpest
        // one, because "cover as much as possible" is the only useful answer left.
        recommended ??= points[^1];

        return (points, recommended);
    }

    private static int RankOf(IReadOnlyList<ClassificationResult> results, string actual)
    {
        for (int i = 0; i < results.Count; i++)
        {
            if (string.Equals(results[i].Category, actual, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    private static string HashOrder(OnlineEvaluationOptions options)
    {
        var builder = new StringBuilder();

        foreach (SearchDocument document in options.Warmup)
        {
            builder.Append(document.Id);
            builder.Append('\n');
        }

        foreach (OnlineExample example in options.Stream)
        {
            builder.Append(example.Document.Id);
            builder.Append('\t');
            builder.Append(example.Block.ToString(CultureInfo.InvariantCulture));
            builder.Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private readonly record struct Outcome(
        int Block,
        string Actual,
        string? Predicted,
        double TopProbability,
        bool CorrectAt1,
        bool CorrectAtK,
        bool SeenText,
        bool SeenCategory);
}
