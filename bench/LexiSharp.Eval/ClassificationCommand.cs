using System.Diagnostics;
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

    private static readonly LinearCandidate[] LinearCandidates =
    [
        new(20, 0.05, 1e-4),
        new(20, 0.10, 1e-4),
        new(20, 0.20, 1e-4),
        new(40, 0.05, 1e-4),
        new(40, 0.10, 1e-4),
        new(40, 0.20, 1e-4),
        new(60, 0.10, 1e-4),
        new(80, 0.10, 1e-4),
        new(40, 0.10, 1e-5),
        new(40, 0.10, 1e-3),
    ];

    public static async Task<int> RunAsync(string[] args)
    {
        var options = new CommandOptions();

        if (!TryParse(args, options))
            return 2;

        return await RunAsync(options);
    }

    private static bool TryParse(string[] args, CommandOptions options)
    {
        for (int i = 0; i < args.Length; i++)
        {
            bool? parsed = ParseDatasetFlag(args, ref i, options);

            if (parsed is null)
                parsed = ParseTreeFlag(args, ref i, options);

            if (parsed is null)
                parsed = ParseForestFlag(args, ref i, options);

            if (parsed is null)
                parsed = ParseEvaluationFlag(args, ref i, options);

            if (parsed is null)
                parsed = ParseOutputFlag(args, ref i, options);

            if (parsed is null)
            {
                Console.Error.WriteLine($"classification: unknown argument '{args[i]}'.");
                return false;
            }

            if (parsed == false)
                return false;
        }

        return true;
    }

    private static bool? ParseDatasetFlag(string[] args, ref int i, CommandOptions options)
    {
        switch (args[i])
        {
            case "--data" when i + 1 < args.Length:
                options.DataBaseDir = args[++i];
                return true;
            case "--limit" when i + 1 < args.Length:
                if (!TryReadPositive(args[++i], out int limit))
                {
                    Console.Error.WriteLine("classification: --limit expects a positive integer.");
                    return false;
                }

                options.Limit = limit;
                return true;
            default:
                return null;
        }
    }

    private static bool? ParseTreeFlag(string[] args, ref int i, CommandOptions options)
    {
        switch (args[i])
        {
            case "--depth" when i + 1 < args.Length:
                if (!TryReadNonNegative(args[++i], out int depth))
                {
                    Console.Error.WriteLine("classification: --depth expects a non-negative integer.");
                    return false;
                }

                options.MaxDepth = depth;
                return true;
            case "--min-leaf" when i + 1 < args.Length:
                if (!TryReadPositive(args[++i], out int minLeaf))
                {
                    Console.Error.WriteLine("classification: --min-leaf expects a positive integer.");
                    return false;
                }

                options.MinDocumentsPerLeaf = minLeaf;
                return true;
            case "--min-df" when i + 1 < args.Length:
                if (!TryReadPositive(args[++i], out int minDf))
                {
                    Console.Error.WriteLine("classification: --min-df expects a positive integer.");
                    return false;
                }

                options.MinDocumentFrequency = minDf;
                return true;
            case "--criterion" when i + 1 < args.Length:
                if (!Enum.TryParse(args[++i], ignoreCase: true, out SplitCriterion criterion))
                {
                    Console.Error.WriteLine("classification: --criterion expects InformationGain, GainRatio or Gini.");
                    return false;
                }

                options.Criterion = criterion;
                return true;
            default:
                return null;
        }
    }

    private static bool? ParseForestFlag(string[] args, ref int i, CommandOptions options)
    {
        switch (args[i])
        {
            case "--trees" when i + 1 < args.Length:
                if (!TryReadPositive(args[++i], out int trees))
                {
                    Console.Error.WriteLine("classification: --trees expects a positive integer.");
                    return false;
                }

                options.ForestTrees = trees;
                return true;
            case "--subset" when i + 1 < args.Length:
                if (!TryReadFraction(args[++i], out double subset))
                {
                    Console.Error.WriteLine("classification: --subset expects a number in (0, 1].");
                    return false;
                }

                options.ForestSubset = subset;
                return true;
            case "--seed" when i + 1 < args.Length:
                if (!TryReadNonNegative(args[++i], out int seed))
                {
                    Console.Error.WriteLine("classification: --seed expects a non-negative integer.");
                    return false;
                }

                options.ForestSeed = seed;
                return true;
            case "--tune":
                options.Tune = true;
                return true;
            case "--tune-linear":
                options.TuneLinear = true;
                return true;
            default:
                return null;
        }
    }

    private static bool? ParseEvaluationFlag(string[] args, ref int i, CommandOptions options)
    {
        switch (args[i])
        {
            case "--cv":
                options.CrossValidate = true;
                return true;
            case "--online":
                options.Online = true;
                return true;
            case "--coverage":
                options.Coverage = true;
                return true;
            case "--coverage-target" when i + 1 < args.Length:
                if (!double.TryParse(
                        args[++i],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double coverageTarget)
                    || !double.IsFinite(coverageTarget)
                    || coverageTarget <= 0
                    || coverageTarget > 1)
                {
                    Console.Error.WriteLine("classification: --coverage-target expects a number in (0, 1].");
                    return false;
                }

                options.CoverageTarget = coverageTarget;
                return true;
            case "--validation" when i + 1 < args.Length:
                if (!TryReadFraction(args[++i], out double validation))
                {
                    Console.Error.WriteLine("classification: --validation expects a number in (0, 1).");
                    return false;
                }

                options.ValidationFraction = validation;
                return true;
            default:
                return null;
        }
    }

    private static bool? ParseOutputFlag(string[] args, ref int i, CommandOptions options)
    {
        switch (args[i])
        {
            case "--linear-idf":
                options.LinearIdf = true;
                return true;
            case "--linear-tfidf":
                options.LinearTfIdf = true;
                return true;
            case "--text" when i + 1 < args.Length:
                options.TextMode = args[++i] switch
                {
                    "full" => ClassificationTextMode.SubjectAndBody,
                    "subject" => ClassificationTextMode.SubjectOnly,
                    _ => (ClassificationTextMode)(-1),
                };

                if (!Enum.IsDefined(options.TextMode))
                {
                    Console.Error.WriteLine("classification: --text expects 'full' or 'subject'.");
                    return false;
                }

                return true;
            case "--verify" when i + 1 < args.Length:
                options.VerifyPath = args[++i];
                return true;
            case "--write" when i + 1 < args.Length:
                options.WritePath = args[++i];
                return true;
            default:
                return null;
        }
    }

    private static async Task<int> RunAsync(CommandOptions options)
    {
        var corpus = await TwentyNewsgroups.LoadOrDownloadAsync(
            options.DataBaseDir,
            options.Limit,
            options.TextMode);

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"20 Newsgroups ({corpus.Name}): {corpus.Train.Count} train, {corpus.Test.Count} test, " +
            $"{corpus.Categories.Count} categories, md5 {corpus.SourceMd5}"));

        var forestOptions = new RandomForestOptions
        {
            Trees = options.ForestTrees,
            MaxDepth = options.MaxDepth,
            MinDocumentsPerLeaf = options.MinDocumentsPerLeaf,
            MinDocumentFrequency = options.MinDocumentFrequency,
            FeatureSubsetFraction = options.ForestSubset,
            Criterion = options.Criterion,
            Seed = options.ForestSeed,
        };

        if (options.Tune)
            forestOptions = Tune(corpus, options, forestOptions);

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Decision tree: depth={options.MaxDepth}, min leaf={options.MinDocumentsPerLeaf}, " +
            $"min df={options.MinDocumentFrequency}, criterion={options.Criterion}"));
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Random forest: trees={options.ForestTrees}, subset={options.ForestSubset:F2}, " +
            $"seed={options.ForestSeed}"));

        var treeOptions = new DecisionTreeOptions
        {
            MaxDepth = options.MaxDepth,
            MinDocumentsPerLeaf = options.MinDocumentsPerLeaf,
            MinDocumentFrequency = options.MinDocumentFrequency,
            Criterion = options.Criterion,
        };

        var linearOptions = new LinearClassifierOptions
        {
            MinDocumentFrequency = options.MinDocumentFrequency,
            IdfWeighting = options.LinearIdf || options.LinearTfIdf,
        };

        Func<LinearClassifierOptions, ITextClassifier> linearFactoryFor = options.LinearTfIdf
            ? candidate => new LinearClassifier(new TextFeatureSource(emitTermFrequency: true), candidate)
            : candidate => new LinearClassifier(options: candidate);

        if (options.TuneLinear)
        {
            var split = ClassificationEvaluation.HoldOut(corpus.Train, options.ValidationFraction);
            TunedLinear tuned = ClassificationEvaluation.TuneLinear(
                split.Fit, split.Validation, LinearCandidates, linearOptions, linearFactoryFor);

            linearOptions = linearOptions with
            {
                Epochs = tuned.Candidate.Epochs,
                LearningRate = tuned.Candidate.LearningRate,
                L2 = tuned.Candidate.L2,
            };

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Linear tuning on validation ({split.Fit.Count} fit / {split.Validation.Count} validation): " +
                $"epochs={tuned.Candidate.Epochs}, rate={tuned.Candidate.LearningRate:F2}, " +
                $"l2={tuned.Candidate.L2:G3} (accuracy={tuned.ValidationAccuracy:F4}, " +
                $"macro-F1={tuned.ValidationMacroF1:F4})"));
        }

        Func<ITextClassifier> linearFactory = () => linearFactoryFor(linearOptions);

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Linear: epochs={linearOptions.Epochs}, rate={linearOptions.LearningRate:F2}, " +
            $"l2={linearOptions.L2}, min df={linearOptions.MinDocumentFrequency}, " +
            $"idf={linearOptions.IdfWeighting}, tfidf={options.LinearTfIdf}"));

        var naiveBayes = ClassificationEvaluation.MeasureNaiveBayes(corpus.Train, corpus.Test);
        var tree = ClassificationEvaluation.MeasureDecisionTree(corpus.Train, corpus.Test, treeOptions);
        var forest = ClassificationEvaluation.MeasureRandomForest(corpus.Train, corpus.Test, forestOptions);
        var linear = ClassificationEvaluation.MeasureClassifier(
            "Linear", corpus.Train, corpus.Test, linearFactory);

        Print(naiveBayes);
        Print(tree);
        Print(forest);
        Print(linear);

        PrintMcNemar(corpus, naiveBayes, tree, forest, linear);

        if (options.Coverage)
        {
            RunCoverage(corpus, options, treeOptions, forestOptions, linearFactory);
        }

        if (options.CrossValidate)
            RunCrossValidation(corpus, linearFactory);

        if (options.Online)
            RunOnline(corpus, treeOptions, linearFactory);

        CalibrationPinRow[] calibrations = RunCalibration(
            corpus,
            options,
            treeOptions,
            forestOptions,
            linearFactory);

        var pin = BuildPin(
            corpus,
            options,
            treeOptions,
            forestOptions,
            linearOptions,
            calibrations,
            naiveBayes,
            tree,
            forest,
            linear);

        if (options.WritePath is not null)
        {
            File.WriteAllText(
                options.WritePath,
                JsonSerializer.Serialize(pin, JsonOptions) + Environment.NewLine);
            Console.WriteLine($"Wrote {options.WritePath}");
        }

        return options.VerifyPath is null ? 0 : Verify(pin, options.VerifyPath);
    }

    private static RandomForestOptions Tune(
        ClassificationCorpus corpus,
        CommandOptions options,
        RandomForestOptions forestOptions)
    {
        var split = ClassificationEvaluation.HoldOut(corpus.Train, options.ValidationFraction);
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

        options.MaxDepth = tuned.Candidate.MaxDepth;
        options.MinDocumentsPerLeaf = tuned.Candidate.MinDocumentsPerLeaf;
        options.ForestSubset = tuned.Candidate.FeatureSubset;

        return forestOptions with
        {
            MaxDepth = options.MaxDepth,
            MinDocumentsPerLeaf = options.MinDocumentsPerLeaf,
            FeatureSubsetFraction = options.ForestSubset,
        };
    }

    private static void PrintMcNemar(
        ClassificationCorpus corpus,
        ClassificationMetrics naiveBayes,
        ClassificationMetrics tree,
        ClassificationMetrics forest,
        ClassificationMetrics linear)
    {
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
    }

    private static void RunCoverage(
        ClassificationCorpus corpus,
        CommandOptions options,
        DecisionTreeOptions treeOptions,
        RandomForestOptions forestOptions,
        Func<ITextClassifier> linearFactory)
    {
        foreach (var report in new[]
        {
            ClassificationEvaluation.MeasureCoverage(
                "Naive Bayes", corpus.Train, corpus.Test, () => new NaiveBayesClassifier(), options.CoverageTarget),
            ClassificationEvaluation.MeasureCoverage(
                "Decision tree", corpus.Train, corpus.Test, () => new DecisionTreeClassifier(options: treeOptions), options.CoverageTarget),
            ClassificationEvaluation.MeasureCoverage(
                "Random forest", corpus.Train, corpus.Test, () => new RandomForestClassifier(options: forestOptions), options.CoverageTarget),
            ClassificationEvaluation.MeasureCoverage(
                "Linear", corpus.Train, corpus.Test, linearFactory, options.CoverageTarget),
        })
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"Coverage {report.Method}: target={options.CoverageTarget:F2} " +
                $"threshold={report.Recommended.Threshold:F2} " +
                $"coverage={report.Recommended.Coverage:F3} " +
                $"precision={report.Recommended.Precision:F3}"));

            foreach (var point in report.Points)
            {
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  threshold={point.Threshold:F1} coverage={point.Coverage:F3} " +
                    $"precision={point.Precision:F3} covered={point.Covered}/{point.Total}"));
            }
        }
    }

    private static void RunCrossValidation(
        ClassificationCorpus corpus,
        Func<ITextClassifier> linearFactory)
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

    private static void RunOnline(
        ClassificationCorpus corpus,
        DecisionTreeOptions treeOptions,
        Func<ITextClassifier> linearFactory)
    {
        var shuffled = Shuffle(corpus.Train);
        int split = shuffled.Count * 4 / 5;
        var prefix = shuffled.Take(split).ToList();
        var rest = shuffled.Skip(split).ToList();

        MeasureOnline(
            "Decision tree",
            () => new DecisionTreeClassifier(options: treeOptions),
            prefix,
            rest,
            corpus.Train);
        MeasureOnline("Linear", linearFactory, prefix, rest, corpus.Train);
    }

    private static CalibrationPinRow[] RunCalibration(
        ClassificationCorpus corpus,
        CommandOptions options,
        DecisionTreeOptions treeOptions,
        RandomForestOptions forestOptions,
        Func<ITextClassifier> linearFactory)
    {
        var calibrations = new[]
        {
            ClassificationEvaluation.MeasureCalibration(
                "Decision tree",
                corpus.Train,
                corpus.Test,
                options.ValidationFraction,
                () => new DecisionTreeClassifier(options: treeOptions)),
            ClassificationEvaluation.MeasureCalibration(
                "Random forest",
                corpus.Train,
                corpus.Test,
                options.ValidationFraction,
                () => new RandomForestClassifier(options: forestOptions)),
            ClassificationEvaluation.MeasureCalibration(
                "Linear",
                corpus.Train,
                corpus.Test,
                options.ValidationFraction,
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

        return calibrations
            .Select(calibration => new CalibrationPinRow(
                calibration.Method,
                calibration.Temperature,
                calibration.LogLoss,
                calibration.CalibratedLogLoss,
                calibration.Brier,
                calibration.CalibratedBrier))
            .ToArray();
    }

    private static ClassificationPin BuildPin(
        ClassificationCorpus corpus,
        CommandOptions options,
        DecisionTreeOptions treeOptions,
        RandomForestOptions forestOptions,
        LinearClassifierOptions linearOptions,
        IReadOnlyList<CalibrationPinRow> calibrations,
        ClassificationMetrics naiveBayes,
        ClassificationMetrics tree,
        ClassificationMetrics forest,
        ClassificationMetrics linear) =>
        new(
            corpus.Name,
            corpus.SourceMd5,
            corpus.Train.Count,
            corpus.Test.Count,
            corpus.Categories.Count,
            treeOptions.MaxDepth,
            treeOptions.MinDocumentsPerLeaf,
            treeOptions.MinDocumentFrequency,
            treeOptions.Criterion.ToString(),
            forestOptions.Trees,
            forestOptions.FeatureSubsetFraction,
            forestOptions.Seed,
            options.ValidationFraction,
            linearOptions.Epochs,
            linearOptions.LearningRate,
            linearOptions.L2,
            options.LinearIdf,
            options.LinearTfIdf,
            calibrations,
            [ToRow(naiveBayes), ToRow(tree), ToRow(forest), ToRow(linear)]);

    private sealed class CommandOptions
    {
        public string DataBaseDir { get; set; } =
            Path.Combine(AppContext.BaseDirectory, "../../../data");

        public int? Limit { get; set; }

        public int MaxDepth { get; set; } = 12;

        public int MinDocumentsPerLeaf { get; set; } = 5;

        public int MinDocumentFrequency { get; set; } = 2;

        public SplitCriterion Criterion { get; set; } = SplitCriterion.InformationGain;

        public int ForestTrees { get; set; } = 40;

        public double ForestSubset { get; set; } = 0.3;

        public int ForestSeed { get; set; } = 7;

        public bool Tune { get; set; }

        public bool TuneLinear { get; set; }

        public bool LinearIdf { get; set; }

        public bool LinearTfIdf { get; set; }

        public bool CrossValidate { get; set; }

        public bool Online { get; set; }

        public bool Coverage { get; set; }

        public double CoverageTarget { get; set; } = 0.5;

        public ClassificationTextMode TextMode { get; set; } = ClassificationTextMode.SubjectAndBody;

        public double ValidationFraction { get; set; } = 0.15;

        public string? VerifyPath { get; set; }

        public string? WritePath { get; set; }
    }

    private static void MeasureOnline(
        string name,
        Func<ITextClassifier> factory,
        IReadOnlyList<ClassificationDocument> prefix,
        IReadOnlyList<ClassificationDocument> rest,
        IReadOnlyList<ClassificationDocument> full)
    {
        var model = factory();
        model.Train(prefix.Select(ToDocument));

        var tokens = new List<long>(rest.Count);
        var learn = Stopwatch.StartNew();

        foreach (var document in rest)
        {
            tokens.Add(model switch
            {
                DecisionTreeClassifier tree => tree.Learn(document.Text, document.Category),
                LinearClassifier linear => linear.Learn(document.Text, document.Category),
                _ => throw new NotSupportedException($"No Learn for {model.GetType().Name}."),
            });
        }

        learn.Stop();

        var unlearn = Stopwatch.StartNew();

        foreach (long token in tokens)
        {
            _ = model switch
            {
                DecisionTreeClassifier tree => tree.Unlearn(token),
                LinearClassifier linear => linear.Unlearn(token),
                _ => false,
            };
        }

        unlearn.Stop();

        var retrain = Stopwatch.StartNew();
        factory().Train(full.Select(ToDocument));
        retrain.Stop();

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Online {name}: learn {rest.Count} docs={learn.Elapsed.TotalMilliseconds:F1} ms, " +
            $"unlearn={unlearn.Elapsed.TotalMilliseconds:F1} ms, " +
            $"retrain {full.Count}={retrain.Elapsed.TotalMilliseconds:F1} ms (correction, not a retrain)"));
    }

    private static SearchDocument ToDocument(ClassificationDocument document) =>
        new(document.Id, document.Text, Category: document.Category);

    private static List<ClassificationDocument> Shuffle(IReadOnlyList<ClassificationDocument> documents)
    {
        var shuffled = new List<ClassificationDocument>(documents);
        var random = new Random(7); // NOSONAR:S2245 (fixed seed: the split is reproducible)

        for (int i = shuffled.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        return shuffled;
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
        if (!TryLoadPin(path, out ClassificationPin expected))
            return 2;

        if (!HeaderMatches(expected, actual))
            return 1;

        bool matches = VerifyCalibration(expected, actual);
        matches &= VerifyRows(expected, actual);
        matches &= VerifyNoMissingRows(expected, actual);

        Console.WriteLine(matches ? "classification: pin matches." : "classification: pin drifted.");
        return matches ? 0 : 1;
    }

    private static bool TryLoadPin(string path, out ClassificationPin pin)
    {
        pin = null!;

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"classification: pin not found: {path}");
            return false;
        }

        ClassificationPin? expected = JsonSerializer.Deserialize<ClassificationPin>(
            File.ReadAllText(path),
            JsonOptions);

        if (expected is null)
        {
            Console.Error.WriteLine("classification: pin is empty.");
            return false;
        }

        pin = expected;
        return true;
    }

    private static bool HeaderMatches(ClassificationPin expected, ClassificationPin actual)
    {
        var mismatches = new List<string>();

        void Check(bool matches, string field)
        {
            if (!matches)
                mismatches.Add(field);
        }

        Check(string.Equals(expected.Dataset, actual.Dataset, StringComparison.Ordinal), "dataset");
        Check(string.Equals(expected.SourceMd5, actual.SourceMd5, StringComparison.Ordinal), "source md5");
        Check(expected.TrainDocuments == actual.TrainDocuments, "train documents");
        Check(expected.TestDocuments == actual.TestDocuments, "test documents");
        Check(expected.Categories == actual.Categories, "categories");
        Check(expected.MaxDepth == actual.MaxDepth, "max depth");
        Check(expected.MinDocumentsPerLeaf == actual.MinDocumentsPerLeaf, "min leaf");
        Check(expected.MinDocumentFrequency == actual.MinDocumentFrequency, "min df");
        Check(string.Equals(expected.Criterion, actual.Criterion, StringComparison.Ordinal), "criterion");
        Check(expected.ForestTrees == actual.ForestTrees, "forest trees");
        Check(Math.Abs(expected.ForestFeatureSubset - actual.ForestFeatureSubset) <= 1e-12, "forest subset");
        Check(expected.ForestSeed == actual.ForestSeed, "forest seed");
        Check(Math.Abs(expected.ValidationFraction - actual.ValidationFraction) <= 1e-12, "validation");
        Check(expected.LinearEpochs == actual.LinearEpochs, "linear epochs");
        Check(Math.Abs(expected.LinearLearningRate - actual.LinearLearningRate) <= 1e-12, "linear rate");
        Check(Math.Abs(expected.LinearL2 - actual.LinearL2) <= 1e-12, "linear L2");
        Check(expected.LinearIdf == actual.LinearIdf, "linear idf");
        Check(expected.LinearTfIdf == actual.LinearTfIdf, "linear tfidf");

        if (mismatches.Count == 0)
            return true;

        Console.Error.WriteLine(
            $"classification: the pin describes a different dataset or configuration ({string.Join(", ", mismatches)}).");
        return false;
    }

    private static bool VerifyCalibration(ClassificationPin expected, ClassificationPin actual)
    {
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

        return matches;
    }

    private static bool VerifyRows(ClassificationPin expected, ClassificationPin actual)
    {
        bool matches = true;

        foreach (var row in actual.Rows)
        {
            ClassificationPinRow? pinned = expected.Rows.FirstOrDefault(
                candidate => candidate.Method == row.Method);

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

            if (!PerClassMatches(pinned, row))
            {
                matches = false;
            }
        }

        return matches;
    }

    private static bool PerClassMatches(ClassificationPinRow pinned, ClassificationPinRow row)
    {
        bool matches = true;

        foreach (var (category, f1) in row.PerClassF1)
        {
            if (!pinned.PerClassF1.TryGetValue(category, out double pinnedF1)
                || Math.Abs(pinnedF1 - f1) > 1e-9)
            {
                Console.Error.WriteLine(
                    $"classification: {row.Method} per-class F1 drifted for {category}.");
                matches = false;
            }
        }

        return matches;
    }

    private static bool VerifyNoMissingRows(ClassificationPin expected, ClassificationPin actual)
    {
        bool matches = true;

        foreach (var pinned in expected.Rows)
        {
            if (actual.Rows.Any(row => row.Method == pinned.Method))
                continue;

            Console.Error.WriteLine($"classification: pinned row {pinned.Method} is missing from the run.");
            matches = false;
        }

        return matches;
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
