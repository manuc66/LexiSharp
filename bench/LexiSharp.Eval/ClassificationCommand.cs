using System.Globalization;
using System.Text.Json;
using LexiSharp.Classification;
using LexiSharp.Core;

namespace LexiSharp.Eval;

/// <summary>One classifier's pinned figures.</summary>
internal sealed record ClassificationPinRow(
    string Method,
    double Accuracy,
    double MacroF1,
    double AccuracyLower,
    double AccuracyUpper,
    double MacroF1Lower,
    double MacroF1Upper,
    double? AverageQuestions,
    string PredictionHash,
    IReadOnlyDictionary<string, double> PerClassF1);

/// <summary>One model's pinned calibration figures.</summary>
internal sealed record CalibrationPinRow(
    string Method,
    double Temperature,
    double LogLoss,
    double CalibratedLogLoss,
    double Brier,
    double CalibratedBrier);

/// <summary>
/// The pinned outcome of a classification run: the dataset identity, the split sizes, the tree
/// options, and the figures. A pin that does not name its configuration cannot be replayed.
/// </summary>
internal sealed record ClassificationPin(
    string Dataset,
    string SourceMd5,
    int TrainDocuments,
    int TestDocuments,
    int Categories,
    int MaxDepth,
    int MinDocumentsPerLeaf,
    int MinDocumentFrequency,
    string Criterion,
    int ForestTrees,
    double ForestFeatureSubset,
    int ForestSeed,
    double ValidationFraction,
    int LinearEpochs,
    double LinearLearningRate,
    double LinearL2,
    bool LinearIdf,
    bool LinearTfIdf,
    IReadOnlyList<CalibrationPinRow> Calibration,
    IReadOnlyList<ClassificationPinRow> Rows);

/// <summary>
/// The <c>classification</c> verb: downloads 20 Newsgroups, trains Naive Bayes and the decision
/// tree on the train split, scores both on the test split, and can write or verify a pin.
/// </summary>
internal static class ClassificationCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        string dataBaseDir = Path.Combine(AppContext.BaseDirectory, "../../../data");
        int? limit = null;
        int maxDepth = 12;
        int minDocumentsPerLeaf = 5;
        int minDocumentFrequency = 2;
        var criterion = SplitCriterion.InformationGain;
        int forestTrees = 40;
        double forestSubset = 0.3;
        int forestSeed = 7;
        bool tune = false;
        bool linearIdf = false;
        bool linearTfIdf = false;
        bool crossValidate = false;
        var textMode = ClassificationTextMode.SubjectAndBody;
        double validationFraction = 0.15;
        string? verifyPath = null;
        string? writePath = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data" when i + 1 < args.Length:
                    dataBaseDir = args[++i];
                    break;
                case "--limit" when i + 1 < args.Length:
                    if (!TryReadPositive(args[++i], out int parsedLimit))
                    {
                        Console.Error.WriteLine("classification: --limit expects a positive integer.");
                        return 2;
                    }

                    limit = parsedLimit;
                    break;
                case "--depth" when i + 1 < args.Length:
                    if (!TryReadNonNegative(args[++i], out maxDepth))
                    {
                        Console.Error.WriteLine("classification: --depth expects a non-negative integer.");
                        return 2;
                    }

                    break;
                case "--min-leaf" when i + 1 < args.Length:
                    if (!TryReadPositive(args[++i], out minDocumentsPerLeaf))
                    {
                        Console.Error.WriteLine("classification: --min-leaf expects a positive integer.");
                        return 2;
                    }

                    break;
                case "--min-df" when i + 1 < args.Length:
                    if (!TryReadPositive(args[++i], out minDocumentFrequency))
                    {
                        Console.Error.WriteLine("classification: --min-df expects a positive integer.");
                        return 2;
                    }

                    break;
                case "--criterion" when i + 1 < args.Length:
                    if (!Enum.TryParse(args[++i], ignoreCase: true, out criterion))
                    {
                        Console.Error.WriteLine("classification: --criterion expects InformationGain, GainRatio or Gini.");
                        return 2;
                    }

                    break;
                case "--trees" when i + 1 < args.Length:
                    if (!TryReadPositive(args[++i], out forestTrees))
                    {
                        Console.Error.WriteLine("classification: --trees expects a positive integer.");
                        return 2;
                    }

                    break;
                case "--subset" when i + 1 < args.Length:
                    if (!TryReadFraction(args[++i], out forestSubset))
                    {
                        Console.Error.WriteLine("classification: --subset expects a number in (0, 1].");
                        return 2;
                    }

                    break;
                case "--seed" when i + 1 < args.Length:
                    if (!TryReadNonNegative(args[++i], out forestSeed))
                    {
                        Console.Error.WriteLine("classification: --seed expects a non-negative integer.");
                        return 2;
                    }

                    break;
                case "--tune":
                    tune = true;
                    break;
                case "--linear-idf":
                    linearIdf = true;
                    break;
                case "--linear-tfidf":
                    linearTfIdf = true;
                    break;
                case "--cv":
                    crossValidate = true;
                    break;
                case "--text" when i + 1 < args.Length:
                    textMode = args[++i] switch
                    {
                        "full" => ClassificationTextMode.SubjectAndBody,
                        "subject" => ClassificationTextMode.SubjectOnly,
                        _ => (ClassificationTextMode)(-1),
                    };

                    if (!Enum.IsDefined(textMode))
                    {
                        Console.Error.WriteLine("classification: --text expects 'full' or 'subject'.");
                        return 2;
                    }

                    break;
                case "--validation" when i + 1 < args.Length:
                    if (!TryReadFraction(args[++i], out validationFraction))
                    {
                        Console.Error.WriteLine("classification: --validation expects a number in (0, 1).");
                        return 2;
                    }

                    break;
                case "--verify" when i + 1 < args.Length:
                    verifyPath = args[++i];
                    break;
                case "--write" when i + 1 < args.Length:
                    writePath = args[++i];
                    break;
                default:
                    Console.Error.WriteLine($"classification: unknown argument '{args[i]}'.");
                    return 2;
            }
        }

        var corpus = await TwentyNewsgroups.LoadOrDownloadAsync(dataBaseDir, limit, textMode);

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"20 Newsgroups ({corpus.Name}): {corpus.Train.Count} train, {corpus.Test.Count} test, " +
            $"{corpus.Categories.Count} categories, md5 {corpus.SourceMd5}"));

        var forestOptions = new RandomForestOptions
        {
            Trees = forestTrees,
            MaxDepth = maxDepth,
            MinDocumentsPerLeaf = minDocumentsPerLeaf,
            MinDocumentFrequency = minDocumentFrequency,
            FeatureSubsetFraction = forestSubset,
            Criterion = criterion,
            Seed = forestSeed,
        };

        if (tune)
        {
            var split = ClassificationEvaluation.HoldOut(corpus.Train, validationFraction);
            var candidates = new[]
            {
                new ForestCandidate(12, 0.3, 2),
                new ForestCandidate(20, 0.5, 2),
                new ForestCandidate(30, 0.5, 2),
                new ForestCandidate(30, 0.3, 2),
                new ForestCandidate(20, 0.5, 5),
            };

            var tuned = ClassificationEvaluation.TuneForest(split.Fit, split.Validation, candidates, forestOptions);

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Tuning on validation ({split.Fit.Count} fit / {split.Validation.Count} validation): " +
                $"depth={tuned.Candidate.MaxDepth}, subset={tuned.Candidate.FeatureSubset:F2}, " +
                $"min leaf={tuned.Candidate.MinDocumentsPerLeaf} " +
                $"(accuracy={tuned.ValidationAccuracy:F4}, macro-F1={tuned.ValidationMacroF1:F4})"));

            maxDepth = tuned.Candidate.MaxDepth;
            minDocumentsPerLeaf = tuned.Candidate.MinDocumentsPerLeaf;
            forestSubset = tuned.Candidate.FeatureSubset;
            forestOptions = forestOptions with
            {
                MaxDepth = maxDepth,
                MinDocumentsPerLeaf = minDocumentsPerLeaf,
                FeatureSubsetFraction = forestSubset,
            };
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Decision tree: depth={maxDepth}, min leaf={minDocumentsPerLeaf}, " +
            $"min df={minDocumentFrequency}, criterion={criterion}"));
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Random forest: trees={forestTrees}, subset={forestSubset:F2}, seed={forestSeed}"));

        var options = new DecisionTreeOptions
        {
            MaxDepth = maxDepth,
            MinDocumentsPerLeaf = minDocumentsPerLeaf,
            MinDocumentFrequency = minDocumentFrequency,
            Criterion = criterion,
        };

        var linearOptions = new LinearClassifierOptions
        {
            MinDocumentFrequency = minDocumentFrequency,
            IdfWeighting = linearIdf || linearTfIdf,
        };

        Func<ITextClassifier> linearFactory = linearTfIdf
            ? () => new LinearClassifier(new TextFeatureSource(emitTermFrequency: true), linearOptions)
            : () => new LinearClassifier(options: linearOptions);

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Linear: epochs={linearOptions.Epochs}, rate={linearOptions.LearningRate:F2}, " +
            $"l2={linearOptions.L2}, min df={linearOptions.MinDocumentFrequency}, " +
            $"idf={linearOptions.IdfWeighting}, tfidf={linearTfIdf}"));

        var naiveBayes = ClassificationEvaluation.MeasureNaiveBayes(corpus.Train, corpus.Test);
        var tree = ClassificationEvaluation.MeasureDecisionTree(corpus.Train, corpus.Test, options);
        var forest = ClassificationEvaluation.MeasureRandomForest(corpus.Train, corpus.Test, forestOptions);
        var linear = ClassificationEvaluation.MeasureClassifier(
            "Linear", corpus.Train, corpus.Test, linearFactory);

        Print(naiveBayes);
        Print(tree);
        Print(forest);
        Print(linear);

        foreach (var result in new[]
        {
            ClassificationEvaluation.McNemar(
                corpus.Test, naiveBayes.Predictions, tree.Predictions, "Naive Bayes", "Decision tree"),
            ClassificationEvaluation.McNemar(
                corpus.Test, naiveBayes.Predictions, forest.Predictions, "Naive Bayes", "Random forest"),
            ClassificationEvaluation.McNemar(
                corpus.Test, naiveBayes.Predictions, linear.Predictions, "Naive Bayes", "Linear"),
        })
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"McNemar {result.Candidate} vs {result.Baseline}: " +
                $"baseline-only={result.BaselineOnly}, candidate-only={result.CandidateOnly}, " +
                $"p={result.PValue:G4}"));
        }

        if (crossValidate)
        {
            foreach (var cv in new[]
            {
                ClassificationEvaluation.CrossValidate(
                    "Naive Bayes", corpus.Train, 5, () => new NaiveBayesClassifier()),
                ClassificationEvaluation.CrossValidate(
                    "Linear", corpus.Train, 5, linearFactory),
            })
            {
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"5-fold CV {cv.Method}: accuracy {cv.AccuracyMean:F4} ± {cv.AccuracyStd:F4}, " +
                    $"macro-F1 {cv.MacroF1Mean:F4} ± {cv.MacroF1Std:F4}"));
            }
        }

        var calibrations = new[]
        {
            ClassificationEvaluation.MeasureCalibration(
                "Decision tree",
                corpus.Train,
                corpus.Test,
                validationFraction,
                () => new DecisionTreeClassifier(options: options)),
            ClassificationEvaluation.MeasureCalibration(
                "Random forest",
                corpus.Train,
                corpus.Test,
                validationFraction,
                () => new RandomForestClassifier(options: forestOptions)),
            ClassificationEvaluation.MeasureCalibration(
                "Linear",
                corpus.Train,
                corpus.Test,
                validationFraction,
                linearFactory),
        };

        foreach (var calibration in calibrations)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Calibration {calibration.Method}: T={calibration.Temperature:F2}, " +
                $"log-loss {calibration.LogLoss:F4} -> {calibration.CalibratedLogLoss:F4}, " +
                $"Brier {calibration.Brier:F4} -> {calibration.CalibratedBrier:F4}"));
        }

        var pin = new ClassificationPin(
            corpus.Name,
            corpus.SourceMd5,
            corpus.Train.Count,
            corpus.Test.Count,
            corpus.Categories.Count,
            maxDepth,
            minDocumentsPerLeaf,
            minDocumentFrequency,
            criterion.ToString(),
            forestTrees,
            forestSubset,
            forestSeed,
            validationFraction,
            linearOptions.Epochs,
            linearOptions.LearningRate,
            linearOptions.L2,
            linearIdf,
            linearTfIdf,
            calibrations
                .Select(calibration => new CalibrationPinRow(
                    calibration.Method,
                    calibration.Temperature,
                    calibration.LogLoss,
                    calibration.CalibratedLogLoss,
                    calibration.Brier,
                    calibration.CalibratedBrier))
                .ToArray(),
            [ToRow(naiveBayes), ToRow(tree), ToRow(forest), ToRow(linear)]);

        if (writePath is not null)
        {
            File.WriteAllText(writePath, JsonSerializer.Serialize(pin, JsonOptions) + Environment.NewLine);
            Console.WriteLine($"Wrote {writePath}");
        }

        return verifyPath is null ? 0 : Verify(pin, verifyPath);
    }

    private static void Print(ClassificationMetrics metrics)
    {
        string questions = double.IsNaN(metrics.AverageQuestions)
            ? "-"
            : metrics.AverageQuestions.ToString("F2", CultureInfo.InvariantCulture);
        string predict = metrics.Documents == 0
            ? "-"
            : (metrics.PredictTime.TotalMilliseconds / metrics.Documents)
                .ToString("F3", CultureInfo.InvariantCulture);

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{metrics.Method,-16} accuracy={metrics.Accuracy:F4} " +
            $"[{metrics.Accuracy95.Lower:F4},{metrics.Accuracy95.Upper:F4}] " +
            $"macro-F1={metrics.MacroF1:F4} [{metrics.MacroF195.Lower:F4},{metrics.MacroF195.Upper:F4}] " +
            $"questions={questions} train={metrics.TrainTime.TotalSeconds:F2}s predict={predict} ms/doc"));
    }

    private static ClassificationPinRow ToRow(ClassificationMetrics metrics) =>
        new(
            metrics.Method,
            metrics.Accuracy,
            metrics.MacroF1,
            metrics.Accuracy95.Lower,
            metrics.Accuracy95.Upper,
            metrics.MacroF195.Lower,
            metrics.MacroF195.Upper,
            double.IsNaN(metrics.AverageQuestions) ? null : metrics.AverageQuestions,
            metrics.PredictionHash,
            metrics.PerClassF1);

    private static int Verify(ClassificationPin actual, string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"classification: pin not found: {path}");
            return 2;
        }

        ClassificationPin? expected = JsonSerializer.Deserialize<ClassificationPin>(File.ReadAllText(path), JsonOptions);

        if (expected is null)
        {
            Console.Error.WriteLine("classification: pin is empty.");
            return 2;
        }

        if (!string.Equals(expected.Dataset, actual.Dataset, StringComparison.Ordinal)
            || !string.Equals(expected.SourceMd5, actual.SourceMd5, StringComparison.Ordinal)
            || expected.TrainDocuments != actual.TrainDocuments
            || expected.TestDocuments != actual.TestDocuments
            || expected.Categories != actual.Categories
            || expected.MaxDepth != actual.MaxDepth
            || expected.MinDocumentsPerLeaf != actual.MinDocumentsPerLeaf
            || expected.MinDocumentFrequency != actual.MinDocumentFrequency
            || !string.Equals(expected.Criterion, actual.Criterion, StringComparison.Ordinal)
            || expected.ForestTrees != actual.ForestTrees
            || Math.Abs(expected.ForestFeatureSubset - actual.ForestFeatureSubset) > 1e-12
            || expected.ForestSeed != actual.ForestSeed
            || Math.Abs(expected.ValidationFraction - actual.ValidationFraction) > 1e-12
            || expected.LinearEpochs != actual.LinearEpochs
            || Math.Abs(expected.LinearLearningRate - actual.LinearLearningRate) > 1e-12
            || Math.Abs(expected.LinearL2 - actual.LinearL2) > 1e-12
            || expected.LinearIdf != actual.LinearIdf
            || expected.LinearTfIdf != actual.LinearTfIdf)
        {
            Console.Error.WriteLine("classification: the pin describes a different dataset or configuration.");
            return 1;
        }

        bool matches = true;

        foreach (var row in actual.Calibration)
        {
            CalibrationPinRow? pinned = expected.Calibration?.FirstOrDefault(
                candidate => candidate.Method == row.Method);

            if (pinned is null)
            {
                Console.Error.WriteLine($"classification: no pinned calibration row for {row.Method}.");
                matches = false;
                continue;
            }

            if (Math.Abs(pinned.Temperature - row.Temperature) > 1e-9
                || Math.Abs(pinned.LogLoss - row.LogLoss) > 1e-9
                || Math.Abs(pinned.CalibratedLogLoss - row.CalibratedLogLoss) > 1e-9
                || Math.Abs(pinned.Brier - row.Brier) > 1e-9
                || Math.Abs(pinned.CalibratedBrier - row.CalibratedBrier) > 1e-9)
            {
                Console.Error.WriteLine($"classification: calibration drifted for {row.Method}.");
                matches = false;
            }
        }

        foreach (var row in actual.Rows)
        {
            ClassificationPinRow? pinned = expected.Rows.FirstOrDefault(candidate => candidate.Method == row.Method);

            if (pinned is null)
            {
                Console.Error.WriteLine($"classification: no pinned row for {row.Method}.");
                matches = false;
                continue;
            }

            if (Math.Abs(pinned.Accuracy - row.Accuracy) > 1e-6
                || Math.Abs(pinned.MacroF1 - row.MacroF1) > 1e-6
                || Math.Abs(pinned.AccuracyLower - row.AccuracyLower) > 1e-9
                || Math.Abs(pinned.AccuracyUpper - row.AccuracyUpper) > 1e-9
                || Math.Abs(pinned.MacroF1Lower - row.MacroF1Lower) > 1e-9
                || Math.Abs(pinned.MacroF1Upper - row.MacroF1Upper) > 1e-9)
            {
                Console.Error.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"classification: {row.Method} drifted: accuracy {pinned.Accuracy:F4} -> {row.Accuracy:F4}, " +
                    $"macro-F1 {pinned.MacroF1:F4} -> {row.MacroF1:F4}, " +
                    $"or its bootstrap interval moved."));
                matches = false;
            }

            if (!string.Equals(pinned.PredictionHash, row.PredictionHash, StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"classification: {row.Method} predictions drifted.");
                matches = false;
            }

            foreach (var (category, f1) in row.PerClassF1)
            {
                if (!pinned.PerClassF1.TryGetValue(category, out double pinnedF1)
                    || Math.Abs(pinnedF1 - f1) > 1e-9)
                {
                    Console.Error.WriteLine($"classification: {row.Method} per-class F1 drifted for {category}.");
                    matches = false;
                }
            }
        }

        foreach (var pinned in expected.Rows)
        {
            if (actual.Rows.Any(row => row.Method == pinned.Method))
                continue;

            Console.Error.WriteLine($"classification: pinned row {pinned.Method} is missing from the run.");
            matches = false;
        }

        Console.WriteLine(matches ? "classification: pin matches." : "classification: pin drifted.");
        return matches ? 0 : 1;
    }

    private static bool TryReadPositive(string value, out int result) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) && result > 0;

    private static bool TryReadNonNegative(string value, out int result) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) && result >= 0;

    private static bool TryReadFraction(string value, out double result) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result)
        && result > 0
        && result <= 1;
}
