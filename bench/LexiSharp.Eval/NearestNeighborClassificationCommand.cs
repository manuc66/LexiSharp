using System.Globalization;
using System.Text.Json;
using LexiSharp.Classification;
using LexiSharp.Core;
using LexiSharp.Ranking;

namespace LexiSharp.Eval;

/// <summary>The pinned outcome of a BM25 nearest-neighbour run.</summary>
internal sealed record NearestNeighborPin(
    string Dataset,
    string SourceMd5,
    int TrainDocuments,
    int TestDocuments,
    int Categories,
    string Text,
    double ValidationFraction,
    int Seed,
    int K,
    string Vote,
    double Temperature,
    double Bm25K1,
    double Bm25B,
    ClassificationPinRow Row);

/// <summary>
/// The <c>classification-knn</c> verb: tune K on a validation slice, score once on test, and
/// report the paired comparison against Naive Bayes, coverage, and a replayable pin.
/// </summary>
internal static class NearestNeighborClassificationCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        CommandOptions options = new();

        if (!TryParse(args, options))
            return 2;

        var corpus = await TwentyNewsgroups.LoadOrDownloadAsync(
            options.DataBaseDir,
            options.Limit,
            options.TextMode);

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"20 Newsgroups ({corpus.Name}): {corpus.Train.Count} train, {corpus.Test.Count} test, " +
            $"{corpus.Categories.Count} categories, md5 {corpus.SourceMd5}"));

        var split = ClassificationEvaluation.HoldOut(corpus.Train, options.ValidationFraction, options.Seed);

        var candidates = new List<(int K, double Accuracy, double MacroF1)>();

        foreach (int k in options.KValues)
        {
            var metrics = ClassificationEvaluation.MeasureClassifier(
                $"KNN k={k}",
                split.Fit,
                split.Validation,
                Factory(k, options),
                confidence: false);

            candidates.Add((k, metrics.Accuracy, metrics.MacroF1));
        }

        (int bestK, double validationAccuracy, double validationMacroF1) = candidates
            .OrderByDescending(candidate => candidate.MacroF1)
            .ThenBy(candidate => candidate.K)
            .First();

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Validation ({split.Validation.Count} docs): {string.Join(", ", candidates.Select(candidate => $"k={candidate.K} {candidate.MacroF1:F4}"))}"));

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Selected K={bestK}: validation accuracy {validationAccuracy:F4}, macro-F1 {validationMacroF1:F4}"));

        var row = ClassificationEvaluation.MeasureClassifier(
            "KNN (BM25)", corpus.Train, corpus.Test, Factory(bestK, options));

        Print(row);

        var naiveBayes = ClassificationEvaluation.MeasureNaiveBayes(corpus.Train, corpus.Test);
        McNemarResult mcnemar = ClassificationEvaluation.McNemar(
            corpus.Test,
            naiveBayes.Predictions,
            row.Predictions,
            "Naive Bayes",
            "KNN (BM25)");

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"McNemar KNN vs Naive Bayes: p={mcnemar.PValue:G3} " +
            $"(KNN-only {mcnemar.CandidateOnly}, NB-only {mcnemar.BaselineOnly})"));

        var coverage = ClassificationEvaluation.MeasureCoverage(
            "KNN (BM25)",
            corpus.Train,
            corpus.Test,
            Factory(bestK, options),
            options.CoverageTarget);

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Coverage target {options.CoverageTarget:F2}: threshold {coverage.Recommended.Threshold:F2}, " +
            $"coverage {coverage.Recommended.Coverage:F3}, precision {coverage.Recommended.Precision:F3}"));

        var pin = new NearestNeighborPin(
            corpus.Name,
            corpus.SourceMd5,
            corpus.Train.Count,
            corpus.Test.Count,
            corpus.Categories.Count,
            options.TextMode == ClassificationTextMode.SubjectOnly ? "subject" : "full",
            options.ValidationFraction,
            options.Seed,
            bestK,
            options.Vote.ToString(),
            options.Temperature,
            options.Bm25.K1,
            options.Bm25.B,
            ToRow(row));

        if (options.WritePath is not null)
        {
            File.WriteAllText(options.WritePath, JsonSerializer.Serialize(pin, JsonOptions) + Environment.NewLine);
            Console.WriteLine($"Wrote {options.WritePath}");
        }

        return options.VerifyPath is null ? 0 : Verify(pin, options.VerifyPath);
    }

    private static Func<ITextClassifier> Factory(int k, CommandOptions options) =>
        () => new NearestNeighborClassifier(options: new NearestNeighborOptions
        {
            K = k,
            Vote = options.Vote,
            Temperature = options.Temperature,
            Bm25 = options.Bm25,
            MinProbability = options.MinProbability,
        });

    private static void Print(ClassificationMetrics metrics)
    {
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{metrics.Method}: accuracy {metrics.Accuracy:F4} " +
            $"[{metrics.Accuracy95.Lower:F4}, {metrics.Accuracy95.Upper:F4}], " +
            $"macro-F1 {metrics.MacroF1:F4} [{metrics.MacroF195.Lower:F4}, {metrics.MacroF195.Upper:F4}], " +
            $"train {metrics.TrainTime.TotalMilliseconds:F0} ms, " +
            $"predict {metrics.PredictTime.TotalMilliseconds:F0} ms"));
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
            null,
            metrics.PredictionHash,
            metrics.PerClassF1);

    private static bool TryParse(string[] args, CommandOptions options)
    {
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data" when i + 1 < args.Length:
                    options.DataBaseDir = args[++i];
                    break;
                case "--limit" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int limit)
                        || limit <= 0)
                    {
                        Console.Error.WriteLine("classification-knn: --limit expects a positive integer.");
                        return false;
                    }

                    options.Limit = limit;
                    break;
                case "--text" when i + 1 < args.Length:
                    string text = args[++i];

                    if (text == "full")
                        options.TextMode = ClassificationTextMode.SubjectAndBody;
                    else if (text == "subject")
                        options.TextMode = ClassificationTextMode.SubjectOnly;
                    else
                    {
                        Console.Error.WriteLine("classification-knn: --text expects 'full' or 'subject'.");
                        return false;
                    }

                    break;
                case "--validation" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out double validation)
                        || !double.IsFinite(validation) || validation <= 0 || validation >= 1)
                    {
                        Console.Error.WriteLine("classification-knn: --validation expects a number in (0, 1).");
                        return false;
                    }

                    options.ValidationFraction = validation;
                    break;
                case "--seed" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed)
                        || seed < 0)
                    {
                        Console.Error.WriteLine("classification-knn: --seed expects a non-negative integer.");
                        return false;
                    }

                    options.Seed = seed;
                    break;
                case "--k" when i + 1 < args.Length:
                    if (!TryParseKValues(args[++i], out int[] values))
                    {
                        Console.Error.WriteLine("classification-knn: --k expects a comma-separated list of positive integers.");
                        return false;
                    }

                    options.KValues = values;
                    break;
                case "--vote" when i + 1 < args.Length:
                    if (!Enum.TryParse(args[++i], ignoreCase: true, out NeighborVote vote)
                        || !Enum.IsDefined(vote))
                    {
                        Console.Error.WriteLine("classification-knn: --vote expects Score, Rank or Temperature.");
                        return false;
                    }

                    options.Vote = vote;
                    break;
                case "--temperature" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out double temperature)
                        || !double.IsFinite(temperature) || temperature <= 0)
                    {
                        Console.Error.WriteLine("classification-knn: --temperature expects a finite number greater than 0.");
                        return false;
                    }

                    options.Temperature = temperature;
                    break;
                case "--bm25-k1" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out double k1)
                        || !double.IsFinite(k1) || k1 < 0)
                    {
                        Console.Error.WriteLine("classification-knn: --bm25-k1 expects a non-negative number.");
                        return false;
                    }

                    options.Bm25 = options.Bm25 with { K1 = k1 };
                    break;
                case "--bm25-b" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out double b)
                        || !double.IsFinite(b) || b < 0 || b > 1)
                    {
                        Console.Error.WriteLine("classification-knn: --bm25-b expects a number in [0, 1].");
                        return false;
                    }

                    options.Bm25 = options.Bm25 with { B = b };
                    break;
                case "--min-probability" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out double minimum)
                        || !double.IsFinite(minimum) || minimum < 0 || minimum > 1)
                    {
                        Console.Error.WriteLine("classification-knn: --min-probability expects a number in [0, 1].");
                        return false;
                    }

                    options.MinProbability = minimum;
                    break;
                case "--coverage-target" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out double target)
                        || !double.IsFinite(target) || target <= 0 || target > 1)
                    {
                        Console.Error.WriteLine("classification-knn: --coverage-target expects a number in (0, 1].");
                        return false;
                    }

                    options.CoverageTarget = target;
                    break;
                case "--write" when i + 1 < args.Length:
                    options.WritePath = args[++i];
                    break;
                case "--verify" when i + 1 < args.Length:
                    options.VerifyPath = args[++i];
                    break;
                default:
                    Console.Error.WriteLine($"classification-knn: unknown argument '{args[i]}'.");
                    return false;
            }
        }

        return true;
    }

    private static bool TryParseKValues(string value, out int[] values)
    {
        values = Array.Empty<int>();
        string[] parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
            return false;

        values = new int[parts.Length];

        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i])
                || values[i] <= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static int Verify(NearestNeighborPin actual, string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"classification-knn: pin not found: {path}");
            return 1;
        }

        var expected = JsonSerializer.Deserialize<NearestNeighborPin>(File.ReadAllText(path), JsonOptions);

        if (expected is null)
        {
            Console.Error.WriteLine("classification-knn: pin is empty.");
            return 1;
        }

        var mismatches = new List<string>();

        void Check(bool matches, string field)
        {
            if (!matches)
                mismatches.Add(field);
        }

        Check(expected.Dataset == actual.Dataset, "dataset");
        Check(expected.SourceMd5 == actual.SourceMd5, "sourceMd5");
        Check(expected.TrainDocuments == actual.TrainDocuments, "trainDocuments");
        Check(expected.TestDocuments == actual.TestDocuments, "testDocuments");
        Check(expected.Categories == actual.Categories, "categories");
        Check(expected.Text == actual.Text, "text");
        Check(expected.ValidationFraction == actual.ValidationFraction, "validationFraction");
        Check(expected.Seed == actual.Seed, "seed");
        Check(expected.K == actual.K, "k");
        Check(expected.Vote == actual.Vote, "vote");
        Check(expected.Temperature == actual.Temperature, "temperature");
        Check(expected.Bm25K1 == actual.Bm25K1, "bm25K1");
        Check(expected.Bm25B == actual.Bm25B, "bm25B");

        CheckClose(expected.Row.Accuracy, actual.Row.Accuracy, "accuracy", mismatches);
        CheckClose(expected.Row.MacroF1, actual.Row.MacroF1, "macroF1", mismatches);
        CheckClose(expected.Row.AccuracyLower, actual.Row.AccuracyLower, "accuracyLower", mismatches);
        CheckClose(expected.Row.AccuracyUpper, actual.Row.AccuracyUpper, "accuracyUpper", mismatches);
        CheckClose(expected.Row.MacroF1Lower, actual.Row.MacroF1Lower, "macroF1Lower", mismatches);
        CheckClose(expected.Row.MacroF1Upper, actual.Row.MacroF1Upper, "macroF1Upper", mismatches);
        Check(expected.Row.PredictionHash == actual.Row.PredictionHash, "predictionHash");

        if (expected.Row.PerClassF1.Count != actual.Row.PerClassF1.Count)
        {
            mismatches.Add("perClassF1 count");
        }
        else
        {
            foreach (var (category, value) in expected.Row.PerClassF1)
            {
                if (!actual.Row.PerClassF1.TryGetValue(category, out double current))
                    mismatches.Add($"perClassF1:{category}");
                else
                    CheckClose(value, current, $"perClassF1:{category}", mismatches);
            }
        }

        if (mismatches.Count > 0)
        {
            Console.Error.WriteLine($"classification-knn: pin drifted ({string.Join(", ", mismatches)}).");
            return 1;
        }

        Console.WriteLine("classification-knn: pin matches.");
        return 0;
    }

    private static void CheckClose(double expected, double actual, string field, List<string> mismatches)
    {
        if (Math.Abs(expected - actual) > 1e-9)
            mismatches.Add(field);
    }

    private sealed class CommandOptions
    {
        public string DataBaseDir { get; set; } =
            Path.Combine(AppContext.BaseDirectory, "../../../data");

        public int? Limit { get; set; }
        public ClassificationTextMode TextMode { get; set; } = ClassificationTextMode.SubjectAndBody;
        public double ValidationFraction { get; set; } = 0.15;
        public int Seed { get; set; } = 7;
        public int[] KValues { get; set; } = { 1, 3, 5, 10, 25, 50 };
        public NeighborVote Vote { get; set; } = NeighborVote.Score;
        public double Temperature { get; set; } = 0.1;
        public Bm25Parameters Bm25 { get; set; } = Bm25Parameters.Balanced;
        public double MinProbability { get; set; }
        public double CoverageTarget { get; set; } = 0.5;
        public string? WritePath { get; set; }
        public string? VerifyPath { get; set; }
    }
}
