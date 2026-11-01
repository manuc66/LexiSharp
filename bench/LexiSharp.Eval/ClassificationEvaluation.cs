using System.Diagnostics;
using System.Globalization;
using LexiSharp.Classification;
using LexiSharp.Core;

namespace LexiSharp.Eval;

/// <summary>The outcome of one classifier on one labelled split.</summary>
internal sealed record ClassificationMetrics(
    string Method,
    int Documents,
    double Accuracy,
    double MacroF1,
    double AverageQuestions,
    TimeSpan TrainTime,
    TimeSpan PredictTime);

/// <summary>A stratified fit/validation split of a labelled corpus.</summary>
internal sealed record ClassificationSplit(
    IReadOnlyList<ClassificationDocument> Fit,
    IReadOnlyList<ClassificationDocument> Validation);

/// <summary>The calibration lane's figures for the linear model.</summary>
internal sealed record CalibrationMetrics(
    string Method,
    double Temperature,
    double LogLoss,
    double CalibratedLogLoss,
    double Brier,
    double CalibratedBrier);

/// <summary>One point of the forest's tuning grid.</summary>
internal sealed record ForestCandidate(int MaxDepth, double FeatureSubset, int MinDocumentsPerLeaf);

/// <summary>The grid point the validation split chose, and the score it won with.</summary>
internal sealed record TunedForest(
    ForestCandidate Candidate,
    double ValidationAccuracy,
    double ValidationMacroF1);

/// <summary>
/// Trains and scores the two classifiers on a labelled corpus. The measurement is deliberately
/// the same for both: accuracy and macro-F1 over the test split, with training and prediction time
/// kept apart. The tree also reports how many questions a test document answered on average.
/// </summary>
internal static class ClassificationEvaluation
{
    /// <summary>
    /// Splits a labelled corpus into fit and validation, stratified by category and deterministic
    /// for a seed. The test split is never an argument here: a tuner that can see it is not a
    /// held-out measurement.
    /// </summary>
    /// <param name="documents">The training corpus to split.</param>
    /// <param name="validationFraction">Share of each category kept for validation, in (0, 1).</param>
    /// <param name="seed">Fixed seed for the shuffle.</param>
    public static ClassificationSplit HoldOut(
        IReadOnlyList<ClassificationDocument> documents,
        double validationFraction,
        int seed = 7)
    {
        ArgumentNullException.ThrowIfNull(documents);

        if (validationFraction <= 0 || validationFraction >= 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(validationFraction),
                "The validation fraction must be in (0, 1).");
        }

        var fit = new List<ClassificationDocument>(documents.Count);
        var validation = new List<ClassificationDocument>();
        var random = new Random(seed); // NOSONAR:S2245 (fixed seed: the split is part of the reproduction)

        foreach (var group in documents
                     .GroupBy(document => document.Category, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var ordered = group.OrderBy(document => document.Id, StringComparer.Ordinal).ToList();

            for (int i = ordered.Count - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (ordered[i], ordered[j]) = (ordered[j], ordered[i]);
            }

            int validationCount = (int)Math.Round(
                ordered.Count * validationFraction,
                MidpointRounding.AwayFromZero);

            // A category with one document cannot be on both sides; it stays in fit rather than
            // making the validation split lie about a class it never saw.
            validationCount = ordered.Count >= 2
                ? Math.Clamp(validationCount, 1, ordered.Count - 1)
                : 0;

            for (int i = 0; i < ordered.Count; i++)
                (i < validationCount ? validation : fit).Add(ordered[i]);
        }

        return new ClassificationSplit(fit, validation);
    }

    /// <summary>
    /// Picks the candidate with the highest score; ties are broken by the ordinal key so the
    /// selection is deterministic. The score is the caller's validation metric, never a test one.
    /// </summary>
    public static T SelectBest<T>(
        IReadOnlyList<(T Candidate, double Score)> candidates,
        Func<T, string> key)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(key);

        if (candidates.Count == 0)
            throw new ArgumentException("At least one candidate is required.", nameof(candidates));

        var best = candidates[0];

        for (int i = 1; i < candidates.Count; i++)
        {
            var candidate = candidates[i];

            bool better = candidate.Score > best.Score + 1e-12
                || (Math.Abs(candidate.Score - best.Score) <= 1e-12
                    && string.CompareOrdinal(key(candidate.Candidate), key(best.Candidate)) < 0);

            if (better)
                best = candidate;
        }

        return best.Candidate;
    }

    /// <summary>
    /// Trains the forest on the fit split and scores every candidate on the validation split, then
    /// returns the best one. The test split is deliberately absent from the signature.
    /// </summary>
    public static TunedForest TuneForest(
        IReadOnlyList<ClassificationDocument> fit,
        IReadOnlyList<ClassificationDocument> validation,
        IReadOnlyList<ForestCandidate> candidates,
        RandomForestOptions baseOptions)
    {
        ArgumentNullException.ThrowIfNull(fit);
        ArgumentNullException.ThrowIfNull(validation);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(baseOptions);

        if (candidates.Count == 0)
            throw new ArgumentException("At least one candidate is required.", nameof(candidates));

        var scored = new List<(ForestCandidate Candidate, double Score)>(candidates.Count);
        var measured = new Dictionary<ForestCandidate, ClassificationMetrics>(candidates.Count);

        foreach (var candidate in candidates)
        {
            var options = baseOptions with
            {
                MaxDepth = candidate.MaxDepth,
                FeatureSubsetFraction = candidate.FeatureSubset,
                MinDocumentsPerLeaf = candidate.MinDocumentsPerLeaf,
            };

            var metrics = MeasureRandomForest(fit, validation, options);
            scored.Add((candidate, metrics.Accuracy));
            measured[candidate] = metrics;
        }

        ForestCandidate best = SelectBest(scored, CandidateKey);
        ClassificationMetrics bestMetrics = measured[best];

        return new TunedForest(best, bestMetrics.Accuracy, bestMetrics.MacroF1);
    }

    private static string CandidateKey(ForestCandidate candidate) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{candidate.MaxDepth:D2}|{candidate.FeatureSubset:F2}|{candidate.MinDocumentsPerLeaf:D2}");

    public static ClassificationMetrics MeasureNaiveBayes(
        IReadOnlyList<ClassificationDocument> train,
        IReadOnlyList<ClassificationDocument> test)
    {
        var classifier = new NaiveBayesClassifier();

        var trainTime = Stopwatch.StartNew();
        classifier.Train(train.Select(ToSearchDocument));
        trainTime.Stop();

        var predictTime = Stopwatch.StartNew();
        var predictions = new string?[test.Count];

        for (int i = 0; i < test.Count; i++)
            predictions[i] = classifier.PredictBest(test[i].Text);

        predictTime.Stop();

        return Score("Naive Bayes", test, predictions, double.NaN, trainTime.Elapsed, predictTime.Elapsed);
    }

    public static ClassificationMetrics MeasureDecisionTree(
        IReadOnlyList<ClassificationDocument> train,
        IReadOnlyList<ClassificationDocument> test,
        DecisionTreeOptions options)
    {
        var classifier = new DecisionTreeClassifier(options: options);

        var trainTime = Stopwatch.StartNew();
        classifier.Train(train.Select(ToSearchDocument));
        trainTime.Stop();

        var predictTime = Stopwatch.StartNew();
        var predictions = new string?[test.Count];
        long questions = 0;

        for (int i = 0; i < test.Count; i++)
        {
            var explanation = classifier.Explain(test[i].Text);
            predictions[i] = explanation?.Outcome.Category;
            questions += explanation?.Path.Count ?? 0;
        }

        predictTime.Stop();

        double averageQuestions = test.Count == 0 ? double.NaN : (double)questions / test.Count;

        return Score(
            "Decision tree",
            test,
            predictions,
            averageQuestions,
            trainTime.Elapsed,
            predictTime.Elapsed);
    }

    /// <summary>
    /// Fits a temperature on a validation slice of the train split, then measures the raw and
    /// calibrated log-loss and Brier of the full-train model on test. The test split is never used
    /// to fit the temperature.
    /// </summary>
    public static CalibrationMetrics MeasureLinearCalibration(
        IReadOnlyList<ClassificationDocument> train,
        IReadOnlyList<ClassificationDocument> test,
        LinearClassifierOptions options,
        double validationFraction)
    {
        var split = HoldOut(train, validationFraction);
        var validationModel = new LinearClassifier(options: options);
        validationModel.Train(split.Fit.Select(ToSearchDocument));

        var validationSamples = split.Validation
            .Select(document => new CalibrationSample(
                validationModel.Predict(document.Text, limit: int.MaxValue),
                document.Category))
            .ToList();
        var calibrator = TemperatureCalibrator.Fit(validationSamples);

        var testModel = new LinearClassifier(options: options);
        testModel.Train(train.Select(ToSearchDocument));

        var testSamples = test
            .Select(document => new CalibrationSample(
                testModel.Predict(document.Text, limit: int.MaxValue),
                document.Category))
            .ToList();

        var (logLoss, brier) = CalibrationScore(testSamples, calibrator: null);
        var (calibratedLogLoss, calibratedBrier) = CalibrationScore(testSamples, calibrator);

        return new CalibrationMetrics(
            "Linear",
            calibrator.Temperature,
            logLoss,
            calibratedLogLoss,
            brier,
            calibratedBrier);
    }

    internal static (double LogLoss, double Brier) CalibrationScore(
        IReadOnlyList<CalibrationSample> samples,
        TemperatureCalibrator? calibrator)
    {
        double logLoss = 0;
        double brier = 0;
        int count = 0;

        foreach (var sample in samples)
        {
            IReadOnlyList<ClassificationResult> distribution = calibrator is null
                ? sample.Distribution
                : calibrator.Calibrate(sample.Distribution);

            double actual = 0;
            double brierSum = 0;

            foreach (var result in distribution)
            {
                double probability = Math.Max(result.Probability, 1e-12);
                bool isActual = result.Category == sample.Actual;

                if (isActual)
                    actual = probability;

                double target = isActual ? 1 : 0;
                double difference = probability - target;
                brierSum += difference * difference;
            }

            if (actual <= 0)
                continue;

            logLoss -= Math.Log(actual);
            brier += brierSum;
            count++;
        }

        return count == 0 ? (0, 0) : (logLoss / count, brier / count);
    }

    public static ClassificationMetrics MeasureLinear(
        IReadOnlyList<ClassificationDocument> train,
        IReadOnlyList<ClassificationDocument> test,
        LinearClassifierOptions options)
    {
        var classifier = new LinearClassifier(options: options);

        var trainTime = Stopwatch.StartNew();
        classifier.Train(train.Select(ToSearchDocument));
        trainTime.Stop();

        var predictTime = Stopwatch.StartNew();
        var predictions = new string?[test.Count];

        for (int i = 0; i < test.Count; i++)
            predictions[i] = classifier.PredictBest(test[i].Text);

        predictTime.Stop();

        return Score("Linear", test, predictions, double.NaN, trainTime.Elapsed, predictTime.Elapsed);
    }

    public static ClassificationMetrics MeasureRandomForest(
        IReadOnlyList<ClassificationDocument> train,
        IReadOnlyList<ClassificationDocument> test,
        RandomForestOptions options)
    {
        var classifier = new RandomForestClassifier(options: options);

        var trainTime = Stopwatch.StartNew();
        classifier.Train(train.Select(ToSearchDocument));
        trainTime.Stop();

        var predictTime = Stopwatch.StartNew();
        var predictions = new string?[test.Count];

        for (int i = 0; i < test.Count; i++)
            predictions[i] = classifier.PredictBest(test[i].Text);

        predictTime.Stop();

        return Score("Random forest", test, predictions, double.NaN, trainTime.Elapsed, predictTime.Elapsed);
    }

    /// <summary>Scores predictions against labels, including the macro-F1 over the labels present.</summary>
    internal static ClassificationMetrics Score(
        string method,
        IReadOnlyList<ClassificationDocument> test,
        IReadOnlyList<string?> predictions,
        double averageQuestions,
        TimeSpan trainTime,
        TimeSpan predictTime)
    {
        if (test.Count != predictions.Count)
            throw new ArgumentException("One prediction is required per test document.", nameof(predictions));

        string[] categories = test
            .Select(document => document.Category)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(category => category, StringComparer.Ordinal)
            .ToArray();

        var truePositive = new Dictionary<string, int>(StringComparer.Ordinal);
        var falsePositive = new Dictionary<string, int>(StringComparer.Ordinal);
        var falseNegative = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (string category in categories)
        {
            truePositive[category] = 0;
            falsePositive[category] = 0;
            falseNegative[category] = 0;
        }

        int correct = 0;

        for (int i = 0; i < test.Count; i++)
        {
            string actual = test[i].Category;
            string? predicted = predictions[i];

            if (string.Equals(actual, predicted, StringComparison.Ordinal))
            {
                correct++;
                truePositive[actual]++;
                continue;
            }

            falseNegative[actual]++;

            if (predicted is not null && falsePositive.ContainsKey(predicted))
                falsePositive[predicted]++;
        }

        double macroF1 = 0;

        foreach (string category in categories)
        {
            int tp = truePositive[category];
            int fp = falsePositive[category];
            int fn = falseNegative[category];
            double precision = tp + fp == 0 ? 0 : (double)tp / (tp + fp);
            double recall = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
            double f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
            macroF1 += f1;
        }

        if (categories.Length > 0)
            macroF1 /= categories.Length;

        return new ClassificationMetrics(
            method,
            test.Count,
            test.Count == 0 ? 0 : (double)correct / test.Count,
            macroF1,
            averageQuestions,
            trainTime,
            predictTime);
    }

    private static SearchDocument ToSearchDocument(ClassificationDocument document) =>
        new(document.Id, document.Text, Category: document.Category);
}
