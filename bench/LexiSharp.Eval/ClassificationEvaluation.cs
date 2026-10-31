using System.Diagnostics;
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

/// <summary>
/// Trains and scores the two classifiers on a labelled corpus. The measurement is deliberately
/// the same for both: accuracy and macro-F1 over the test split, with training and prediction time
/// kept apart. The tree also reports how many questions a test document answered on average.
/// </summary>
internal static class ClassificationEvaluation
{
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
