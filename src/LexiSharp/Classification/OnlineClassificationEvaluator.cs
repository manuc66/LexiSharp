using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LexiSharp.Core;

namespace LexiSharp.Classification;

/// <summary>
/// One labelled document in the stream, with the block (day, period, ...) it belongs to and an
/// optional request context for a contextual classifier.
/// </summary>
/// <param name="Document">The labelled document; its category is the ground truth.</param>
/// <param name="Block">The day or period the row belongs to; whole blocks are resampled.</param>
/// <param name="Context">Optional request evidence passed to an <see cref="IContextualTextClassifier"/>.</param>
public sealed record OnlineEvaluationExample(
    SearchDocument Document,
    int Block,
    BlendContext? Context = null);

/// <summary>Options for <see cref="OnlineClassificationEvaluator.Evaluate"/>.</summary>
public sealed record OnlineEvaluationOptions
{
    /// <summary>Maximum rank counted as correct. Default: <c>3</c>.</summary>
    public int TopK { get; init; } = 3;

    /// <summary>
    /// Retrain when the predicate returns true for the number of stream examples already consumed.
    /// Null means never; a non-incremental model then requires a predicate.
    /// </summary>
    public Func<int, bool>? RetrainWhen { get; init; }

    /// <summary>Number of block-bootstrap resamples. Default: <c>1000</c>.</summary>
    public int BootstrapResamples { get; init; } = 1000;

    /// <summary>Fixed bootstrap seed. Default: <c>7</c>.</summary>
    public int Seed { get; init; } = 7;

    /// <summary>Coverage target for the recommended precision point. Default: <c>0.5</c>.</summary>
    public double CoverageTarget { get; init; } = 0.5;

    /// <summary>Whether the stream mutates the model after each prediction. Default: <c>true</c>.</summary>
    public bool Learn { get; init; } = true;

    /// <summary>Name reported for the model; null uses the model type name.</summary>
    public string? Method { get; init; }
}

/// <summary>Accuracy and macro-F1 for one slice of the stream.</summary>
/// <param name="Bucket">The bucket name, such as <c>text:never-seen</c>.</param>
/// <param name="Documents">Number of rows in the bucket.</param>
/// <param name="Accuracy">Top-1 accuracy in the bucket.</param>
/// <param name="MacroF1">Macro-F1 over the categories present in the bucket.</param>
/// <param name="Accuracy95">Block-bootstrap accuracy interval.</param>
/// <param name="MacroF195">Block-bootstrap macro-F1 interval.</param>
public sealed record OnlineBucketMetrics(
    string Bucket,
    int Documents,
    double Accuracy,
    double MacroF1,
    ClassificationInterval Accuracy95,
    ClassificationInterval MacroF195);

/// <summary>The outcome of one predict-then-learn run.</summary>
/// <param name="Method">The model name.</param>
/// <param name="Documents">Number of stream rows.</param>
/// <param name="TopK">The requested top-k.</param>
/// <param name="AccuracyAt1">Top-1 accuracy.</param>
/// <param name="AccuracyAtK">Accuracy within the requested top-k.</param>
/// <param name="MacroF1">Macro-F1 over the stream.</param>
/// <param name="AccuracyAt195">Block-bootstrap top-1 interval.</param>
/// <param name="AccuracyAtK95">Block-bootstrap top-k interval.</param>
/// <param name="MacroF195">Block-bootstrap macro-F1 interval.</param>
/// <param name="TextBuckets">Seen/never-seen text buckets.</param>
/// <param name="CategoryBuckets">Seen/never-seen category buckets.</param>
/// <param name="Coverage">The precision/coverage curve.</param>
/// <param name="RecommendedCoverage">The recommended point for the target.</param>
/// <param name="LearnCalls">Number of incremental Learn calls.</param>
/// <param name="RetrainCalls">Number of full Train calls.</param>
/// <param name="WarmupTrainTime">Time spent on the warmup training.</param>
/// <param name="PredictTime">Time spent predicting.</param>
/// <param name="LearnTime">Time spent learning.</param>
/// <param name="RetrainTime">Time spent retraining.</param>
/// <param name="OrderHash">SHA-256 of the warmup/stream order, for a pin.</param>
public sealed record OnlineReport(
    string Method,
    int Documents,
    int TopK,
    double AccuracyAt1,
    double AccuracyAtK,
    double MacroF1,
    ClassificationInterval AccuracyAt195,
    ClassificationInterval AccuracyAtK95,
    ClassificationInterval MacroF195,
    IReadOnlyList<OnlineBucketMetrics> TextBuckets,
    IReadOnlyList<OnlineBucketMetrics> CategoryBuckets,
    IReadOnlyList<OnlineCoveragePoint> Coverage,
    OnlineCoveragePoint RecommendedCoverage,
    int LearnCalls,
    int RetrainCalls,
    TimeSpan WarmupTrainTime,
    TimeSpan PredictTime,
    TimeSpan LearnTime,
    TimeSpan RetrainTime,
    string OrderHash);

/// <summary>One point of the precision/coverage curve.</summary>
/// <param name="Threshold">Minimum top probability to answer.</param>
/// <param name="Covered">Documents at or above the threshold.</param>
/// <param name="Total">Documents in the stream.</param>
/// <param name="Coverage">Covered / total.</param>
/// <param name="Precision">Correct / covered.</param>
public sealed record OnlineCoveragePoint(
    double Threshold,
    int Covered,
    int Total,
    double Coverage,
    double Precision);

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
public static class OnlineClassificationEvaluator
{
    /// <summary>
    /// Replays a labelled stream through a classifier: predict first, score the rank, then learn.
    /// </summary>
    /// <param name="factory">Creates the model; called once.</param>
    /// <param name="warmup">Documents the model is trained on before the stream starts.</param>
    /// <param name="stream">The ordered stream; each row carries its block and optional request context.</param>
    /// <param name="options">Top-k, retraining schedule, bootstrap and coverage settings; null uses the defaults.</param>
    /// <returns>The accuracy, buckets, coverage and block-bootstrap intervals.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">
    /// A stream row has no category, the model is not incremental without a schedule, or an option
    /// is out of range.
    /// </exception>
    public static OnlineReport Evaluate(
        Func<ITextClassifier> factory,
        IReadOnlyList<SearchDocument> warmup,
        IReadOnlyList<OnlineEvaluationExample> stream,
        OnlineEvaluationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(warmup);
        ArgumentNullException.ThrowIfNull(stream);

        options ??= new OnlineEvaluationOptions();
        Validate(options, stream);
        ITextClassifier model = CreateModel(factory, options);

        (List<SearchDocument> accumulated, HashSet<string> seenTexts, HashSet<string> seenCategories,
            TimeSpan warmupTime) = Warmup(model, warmup, stream.Count);

        StreamResult result = RunStream(model, stream, options, accumulated, seenTexts, seenCategories);
        string orderHash = HashOrder(warmup, stream);

        return BuildReport(
            options,
            model,
            result.Outcomes,
            result.LearnCalls,
            result.RetrainCalls,
            warmupTime,
            result.PredictTime,
            result.LearnTime,
            result.RetrainTime,
            orderHash);
    }

    private static void Validate(
        OnlineEvaluationOptions options,
        IReadOnlyList<OnlineEvaluationExample> stream)
    {
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

        foreach (OnlineEvaluationExample example in stream)
        {
            ArgumentNullException.ThrowIfNull(example, nameof(stream));
            SearchDocument document = example.Document
                ?? throw new ArgumentException("The stream contains a null document.", nameof(stream));

            if (string.IsNullOrEmpty(document.Category))
                throw new ArgumentException($"Stream document '{document.Id}' has no category.", nameof(stream));
        }
    }

    private static ITextClassifier CreateModel(
        Func<ITextClassifier> factory,
        OnlineEvaluationOptions options)
    {
        ITextClassifier model = factory()
            ?? throw new InvalidOperationException("The classifier factory returned null.");

        if (options.Learn && options.RetrainWhen is null && model is not IIncrementalTextClassifier)
        {
            throw new ArgumentException(
                $"{model.GetType().Name} is not incremental and no retraining schedule was supplied; " +
                "the stream would never update the model.",
                nameof(factory));
        }

        return model;
    }

    private static (
        List<SearchDocument> Accumulated,
        HashSet<string> SeenTexts,
        HashSet<string> SeenCategories,
        TimeSpan Elapsed) Warmup(
        ITextClassifier model,
        IReadOnlyList<SearchDocument> warmup,
        int streamCount)
    {
        var watch = Stopwatch.StartNew();
        model.Train(warmup);
        watch.Stop();

        var seenTexts = new HashSet<string>(StringComparer.Ordinal);
        var seenCategories = new HashSet<string>(StringComparer.Ordinal);

        foreach (SearchDocument document in warmup)
        {
            if (document is null)
                continue;

            seenTexts.Add(document.Text);

            if (!string.IsNullOrEmpty(document.Category))
                seenCategories.Add(document.Category);
        }

        var accumulated = new List<SearchDocument>(warmup.Count + streamCount);
        accumulated.AddRange(warmup);

        return (accumulated, seenTexts, seenCategories, watch.Elapsed);
    }

    private static StreamResult RunStream(
        ITextClassifier model,
        IReadOnlyList<OnlineEvaluationExample> stream,
        OnlineEvaluationOptions options,
        List<SearchDocument> accumulated,
        HashSet<string> seenTexts,
        HashSet<string> seenCategories)
    {
        var outcomes = new List<Outcome>(stream.Count);
        var predictWatch = Stopwatch.StartNew();
        var learnWatch = new Stopwatch();
        var retrainWatch = new Stopwatch();
        int learnCalls = 0;
        int retrainCalls = 0;

        for (int i = 0; i < stream.Count; i++)
        {
            OnlineEvaluationExample example = stream[i];
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

    private static IReadOnlyList<ClassificationResult> Predict(
        ITextClassifier model,
        OnlineEvaluationExample example)
    {
        if (example.Context is { } context && model is IContextualTextClassifier contextual)
            return contextual.Predict(example.Document.Text, context, limit: int.MaxValue);

        return model.Predict(example.Document.Text, limit: int.MaxValue);
    }

    private static Outcome Observe(
        ITextClassifier model,
        OnlineEvaluationExample example,
        int topK,
        HashSet<string> seenTexts,
        HashSet<string> seenCategories,
        Stopwatch predictWatch)
    {
        SearchDocument document = example.Document;
        string actual = document.Category!;

        predictWatch.Start();
        IReadOnlyList<ClassificationResult> results = Predict(model, example);
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
        TimeSpan retrainTime,
        string orderHash)
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

        EvaluationIntervals intervals = OnlineEvaluationStatistics.BootstrapBlocks(
            blocks, actual, predicted, options.BootstrapResamples, options.Seed);

        ClassificationInterval atKInterval = OnlineEvaluationStatistics.BootstrapAccuracyBlocks(
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

        (IReadOnlyList<OnlineCoveragePoint> coverage, OnlineCoveragePoint recommended) =
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
            orderHash);
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

        EvaluationIntervals intervals = OnlineEvaluationStatistics.BootstrapBlocks(
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

    private static (IReadOnlyList<OnlineCoveragePoint> Points, OnlineCoveragePoint Recommended) BuildCoverage(
        List<Outcome> outcomes,
        double target)
    {
        int total = outcomes.Count;

        if (total == 0)
        {
            var empty = new OnlineCoveragePoint(1, 0, 0, 0, 0);
            return (new[] { empty }, empty);
        }

        Outcome[] ordered = outcomes
            .OrderByDescending(outcome => outcome.TopProbability)
            .ToArray();

        var points = new List<OnlineCoveragePoint>();
        int covered = 0;
        int correct = 0;
        int index = 0;

        while (index < ordered.Length)
        {
            double threshold = ordered[index].TopProbability;

            while (index < ordered.Length && ordered[index].TopProbability == threshold)
            {
                covered++;

                if (ordered[index].CorrectAt1)
                    correct++;

                index++;
            }

            points.Add(new OnlineCoveragePoint(
                threshold,
                covered,
                total,
                (double)covered / total,
                covered == 0 ? 0 : (double)correct / covered));
        }

        OnlineCoveragePoint? recommended = null;

        foreach (OnlineCoveragePoint point in points)
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

    private static string HashOrder(
        IReadOnlyList<SearchDocument> warmup,
        IReadOnlyList<OnlineEvaluationExample> stream)
    {
        var builder = new StringBuilder();

        foreach (SearchDocument document in warmup)
        {
            builder.Append(document.Id);
            builder.Append('\n');
        }

        foreach (OnlineEvaluationExample example in stream)
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
