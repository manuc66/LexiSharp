using System.Globalization;
using System.Text.Json;
using LexiSharp.Classification;
using LexiSharp.Core;

namespace LexiSharp.Eval;

/// <summary>One model's figures in a pinned online run.</summary>
internal sealed record OnlineBucketPin(
    string Bucket,
    int Documents,
    double Accuracy,
    double MacroF1,
    double AccuracyLower,
    double AccuracyUpper,
    double MacroF1Lower,
    double MacroF1Upper);

/// <summary>
/// The pinned outcome of an online run. The order hash is part of the identity: a pin that does
/// not name the sequence it replayed cannot be reproduced, even with the same documents.
/// </summary>
internal sealed record OnlineClassificationPin(
    string Dataset,
    string SourceMd5,
    int WarmupDocuments,
    int StreamDocuments,
    double WarmupFraction,
    int Blocks,
    int TopK,
    int Seed,
    string Model,
    int RetrainEvery,
    bool Learn,
    double CoverageTarget,
    string OrderHash,
    double AccuracyAt1,
    double AccuracyAtK,
    double MacroF1,
    double AccuracyAt1Lower,
    double AccuracyAt1Upper,
    double AccuracyAtKLower,
    double AccuracyAtKUpper,
    double MacroF1Lower,
    double MacroF1Upper,
    int LearnCalls,
    int RetrainCalls,
    IReadOnlyList<OnlineBucketPin> TextBuckets,
    IReadOnlyList<OnlineBucketPin> CategoryBuckets);

/// <summary>
/// The <c>classification-online</c> verb: replay a labelled stream, predict first and learn
/// afterwards, and report accuracy, buckets, coverage and block-bootstrap intervals.
/// </summary>
internal static class OnlineClassificationCommand
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

        PrintCorpus(corpus);

        if (!TrySplit(corpus, options, out SearchDocument[] warmup, out OnlineExample[] stream))
            return 2;

        Func<int, bool>? retrainWhen = options.Learn && options.RetrainEvery > 0
            ? count => count % options.RetrainEvery == 0
            : null;

        Func<ITextClassifier>? factory = CreateFactory(options.Model);

        if (factory is null)
        {
            Console.Error.WriteLine($"classification-online: unknown model '{options.Model}'.");
            return 2;
        }

        if (!ValidateLearnMode(options, retrainWhen))
            return 2;

        var evaluation = new OnlineEvaluationOptions(
            factory,
            warmup,
            stream,
            options.TopK,
            retrainWhen,
            BootstrapResamples: 1000,
            Seed: options.Seed,
            CoverageTarget: options.CoverageTarget,
            Learn: options.Learn,
            Method: options.Model);

        OnlineReport report = OnlineClassificationEvaluation.Run(evaluation);
        PrintReport(options, report, warmup.Length, stream.Length);

        var pin = ToPin(corpus, options, warmup.Length, stream.Length, options.RetrainEvery, report);
        return WriteOrVerify(options, pin);
    }

    private static void PrintCorpus(ClassificationCorpus corpus) =>
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"20 Newsgroups ({corpus.Name}): {corpus.Train.Count} train, {corpus.Categories.Count} categories, " +
            $"md5 {corpus.SourceMd5}"));

    private static bool TrySplit(
        ClassificationCorpus corpus,
        CommandOptions options,
        out SearchDocument[] warmup,
        out OnlineExample[] stream)
    {
        var train = Shuffle(corpus.Train, options.Seed);
        int warmupCount = (int)Math.Round(train.Count * options.WarmupFraction, MidpointRounding.AwayFromZero);

        if (warmupCount < 0 || warmupCount > train.Count)
        {
            Console.Error.WriteLine("classification-online: the warmup fraction leaves no stream.");
            warmup = Array.Empty<SearchDocument>();
            stream = Array.Empty<OnlineExample>();
            return false;
        }

        int streamCount = train.Count - warmupCount;
        warmup = train.Take(warmupCount).Select(ToDocument).ToArray();
        stream = train.Skip(warmupCount)
            .Select((document, index) => new OnlineExample(
                ToDocument(document),
                options.Blocks <= 1 ? 0 : index * options.Blocks / Math.Max(1, streamCount)))
            .ToArray();

        if (stream.Length > 0)
            return true;

        Console.Error.WriteLine("classification-online: the stream is empty.");
        return false;
    }

    private static Func<ITextClassifier>? CreateFactory(string model) =>
        model switch
        {
            "nb" => () => new NaiveBayesClassifier(),
            "linear" => () => new LinearClassifier(),
            "tree" => () => new DecisionTreeClassifier(options: new DecisionTreeOptions
            {
                MaxDepth = 12,
                MinDocumentsPerLeaf = 5,
                MinDocumentFrequency = 2,
            }),
            "forest" => () => new RandomForestClassifier(options: new RandomForestOptions
            {
                Trees = 25,
                FeatureSubsetFraction = 0.3,
                Seed = 7,
                MaxDepth = 12,
                MinDocumentsPerLeaf = 5,
                MinDocumentFrequency = 2,
            }),
            _ => null,
        };

    private static bool ValidateLearnMode(CommandOptions options, Func<int, bool>? retrainWhen)
    {
        if (!options.Learn || retrainWhen is not null || options.Model == "nb")
            return true;

        Console.Error.WriteLine(
            $"classification-online: '{options.Model}' is not incremental; pass --retrain-every N or --no-learn.");
        return false;
    }

    private static void PrintReport(
        CommandOptions options,
        OnlineReport report,
        int warmupCount,
        int streamCount)
    {
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Online {options.Model}: warmup {warmupCount}, stream {streamCount}, blocks {options.Blocks}, " +
            $"top-k {options.TopK}, learn={options.Learn}, retrain-every={(options.RetrainEvery == 0 ? "never" : options.RetrainEvery.ToString(CultureInfo.InvariantCulture))}"));

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Accuracy@1 {report.AccuracyAt1:F4} [{report.AccuracyAt195.Lower:F4}, {report.AccuracyAt195.Upper:F4}], " +
            $"accuracy@{report.TopK} {report.AccuracyAtK:F4} [{report.AccuracyAtK95.Lower:F4}, {report.AccuracyAtK95.Upper:F4}], " +
            $"macro-F1 {report.MacroF1:F4} [{report.MacroF195.Lower:F4}, {report.MacroF195.Upper:F4}]"));

        PrintBuckets("text", report.TextBuckets);
        PrintBuckets("category", report.CategoryBuckets);

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Coverage target {options.CoverageTarget:F2}: threshold {report.RecommendedCoverage.Threshold:F2}, " +
            $"coverage {report.RecommendedCoverage.Coverage:F3}, precision {report.RecommendedCoverage.Precision:F3}"));

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Calls: learn {report.LearnCalls}, retrain {report.RetrainCalls}; " +
            $"time: warmup {report.WarmupTrainTime.TotalMilliseconds:F1} ms, " +
            $"predict {report.PredictTime.TotalMilliseconds:F1} ms, " +
            $"learn {report.LearnTime.TotalMilliseconds:F1} ms, " +
            $"retrain {report.RetrainTime.TotalMilliseconds:F1} ms; order {report.OrderHash[..12]}…"));
    }

    private static int WriteOrVerify(CommandOptions options, OnlineClassificationPin pin)
    {
        if (options.WritePath is not null)
        {
            File.WriteAllText(options.WritePath, JsonSerializer.Serialize(pin, JsonOptions) + Environment.NewLine);
            Console.WriteLine($"Wrote {options.WritePath}");
        }

        return options.VerifyPath is null ? 0 : Verify(pin, options.VerifyPath);
    }

    private static void PrintBuckets(string dimension, IReadOnlyList<OnlineBucketMetrics> buckets)
    {
        foreach (OnlineBucketMetrics bucket in buckets)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"  {dimension}:{bucket.Bucket[(bucket.Bucket.IndexOf(':') + 1)..],-14} " +
                $"n={bucket.Documents,6} acc={bucket.Accuracy:F4} [{bucket.Accuracy95.Lower:F4}, {bucket.Accuracy95.Upper:F4}] " +
                $"macro-F1={bucket.MacroF1:F4}"));
        }
    }

    private static bool TryParse(string[] args, CommandOptions options)
    {
        for (int i = 0; i < args.Length; i++)
        {
            bool? handled = TryParseData(args, ref i, options)
                ?? TryParseModel(args, ref i, options)
                ?? TryParseEvaluation(args, ref i, options)
                ?? TryParseOutput(args, ref i, options);

            if (handled is null)
            {
                Console.Error.WriteLine($"classification-online: unknown argument '{args[i]}'.");
                return false;
            }

            if (handled == false)
                return false;
        }

        return true;
    }

    private static bool? TryParseData(string[] args, ref int i, CommandOptions options)
    {
        switch (args[i])
        {
            case "--data" when i + 1 < args.Length:
                options.DataBaseDir = args[++i];
                return true;
            case "--limit" when i + 1 < args.Length:
                if (!CommandParsing.TryPositiveInt(args[++i], out int limit))
                {
                    Console.Error.WriteLine("classification-online: --limit expects a positive integer.");
                    return false;
                }

                options.Limit = limit;
                return true;
            case "--text" when i + 1 < args.Length:
                if (!CommandParsing.TryTextMode(args[++i], out ClassificationTextMode mode))
                {
                    Console.Error.WriteLine("classification-online: --text expects 'full' or 'subject'.");
                    return false;
                }

                options.TextMode = mode;
                return true;
            default:
                return null;
        }
    }

    private static bool? TryParseModel(string[] args, ref int i, CommandOptions options)
    {
        switch (args[i])
        {
            case "--model" when i + 1 < args.Length:
                options.Model = args[++i];

                if (options.Model is not ("nb" or "linear" or "tree" or "forest"))
                {
                    Console.Error.WriteLine("classification-online: --model expects nb, linear, tree or forest.");
                    return false;
                }

                return true;
            case "--warmup" when i + 1 < args.Length:
                if (!CommandParsing.TryFraction(args[++i], out double warmup))
                {
                    Console.Error.WriteLine("classification-online: --warmup expects a number in (0, 1).");
                    return false;
                }

                options.WarmupFraction = warmup;
                return true;
            case "--retrain-every" when i + 1 < args.Length:
                if (!CommandParsing.TryNonNegativeInt(args[++i], out int every))
                {
                    Console.Error.WriteLine("classification-online: --retrain-every expects a non-negative integer.");
                    return false;
                }

                options.RetrainEvery = every;
                return true;
            default:
                return null;
        }
    }

    private static bool? TryParseEvaluation(string[] args, ref int i, CommandOptions options)
    {
        switch (args[i])
        {
            case "--top-k" when i + 1 < args.Length:
                if (!CommandParsing.TryPositiveInt(args[++i], out int topK))
                {
                    Console.Error.WriteLine("classification-online: --top-k expects a positive integer.");
                    return false;
                }

                options.TopK = topK;
                return true;
            case "--blocks" when i + 1 < args.Length:
                if (!CommandParsing.TryPositiveInt(args[++i], out int blocks))
                {
                    Console.Error.WriteLine("classification-online: --blocks expects a positive integer.");
                    return false;
                }

                options.Blocks = blocks;
                return true;
            case "--seed" when i + 1 < args.Length:
                if (!CommandParsing.TryNonNegativeInt(args[++i], out int seed))
                {
                    Console.Error.WriteLine("classification-online: --seed expects a non-negative integer.");
                    return false;
                }

                options.Seed = seed;
                return true;
            case "--coverage-target" when i + 1 < args.Length:
                if (!CommandParsing.TryUnitInterval(args[++i], out double target) || target <= 0)
                {
                    Console.Error.WriteLine("classification-online: --coverage-target expects a number in (0, 1].");
                    return false;
                }

                options.CoverageTarget = target;
                return true;
            default:
                return null;
        }
    }

    private static bool? TryParseOutput(string[] args, ref int i, CommandOptions options)
    {
        switch (args[i])
        {
            case "--no-learn":
                options.Learn = false;
                return true;
            case "--write" when i + 1 < args.Length:
                options.WritePath = args[++i];
                return true;
            case "--verify" when i + 1 < args.Length:
                options.VerifyPath = args[++i];
                return true;
            default:
                return null;
        }
    }

    private static OnlineClassificationPin ToPin(
        ClassificationCorpus corpus,
        CommandOptions options,
        int warmupDocuments,
        int streamDocuments,
        int retrainEvery,
        OnlineReport report) =>
        new(
            corpus.Name,
            corpus.SourceMd5,
            warmupDocuments,
            streamDocuments,
            options.WarmupFraction,
            options.Blocks,
            options.TopK,
            options.Seed,
            options.Model,
            retrainEvery,
            options.Learn,
            options.CoverageTarget,
            report.OrderHash,
            report.AccuracyAt1,
            report.AccuracyAtK,
            report.MacroF1,
            report.AccuracyAt195.Lower,
            report.AccuracyAt195.Upper,
            report.AccuracyAtK95.Lower,
            report.AccuracyAtK95.Upper,
            report.MacroF195.Lower,
            report.MacroF195.Upper,
            report.LearnCalls,
            report.RetrainCalls,
            report.TextBuckets.Select(ToPin).ToArray(),
            report.CategoryBuckets.Select(ToPin).ToArray());

    private static OnlineBucketPin ToPin(OnlineBucketMetrics bucket) =>
        new(
            bucket.Bucket,
            bucket.Documents,
            bucket.Accuracy,
            bucket.MacroF1,
            bucket.Accuracy95.Lower,
            bucket.Accuracy95.Upper,
            bucket.MacroF195.Lower,
            bucket.MacroF195.Upper);

    private static int Verify(OnlineClassificationPin actual, string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"classification-online: pin not found: {path}");
            return 1;
        }

        var expected = JsonSerializer.Deserialize<OnlineClassificationPin>(
            File.ReadAllText(path), JsonOptions);

        if (expected is null)
        {
            Console.Error.WriteLine("classification-online: pin is empty.");
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
        Check(expected.WarmupDocuments == actual.WarmupDocuments, "warmupDocuments");
        Check(expected.StreamDocuments == actual.StreamDocuments, "streamDocuments");
        Check(expected.WarmupFraction == actual.WarmupFraction, "warmupFraction");
        Check(expected.Blocks == actual.Blocks, "blocks");
        Check(expected.TopK == actual.TopK, "topK");
        Check(expected.Seed == actual.Seed, "seed");
        Check(expected.Model == actual.Model, "model");
        Check(expected.RetrainEvery == actual.RetrainEvery, "retrainEvery");
        Check(expected.Learn == actual.Learn, "learn");
        Check(expected.CoverageTarget == actual.CoverageTarget, "coverageTarget");
        Check(expected.OrderHash == actual.OrderHash, "orderHash");

        CheckClose(expected.AccuracyAt1, actual.AccuracyAt1, "accuracyAt1", mismatches);
        CheckClose(expected.AccuracyAtK, actual.AccuracyAtK, "accuracyAtK", mismatches);
        CheckClose(expected.MacroF1, actual.MacroF1, "macroF1", mismatches);
        CheckClose(expected.AccuracyAt1Lower, actual.AccuracyAt1Lower, "accuracyAt1Lower", mismatches);
        CheckClose(expected.AccuracyAt1Upper, actual.AccuracyAt1Upper, "accuracyAt1Upper", mismatches);
        CheckClose(expected.AccuracyAtKLower, actual.AccuracyAtKLower, "accuracyAtKLower", mismatches);
        CheckClose(expected.AccuracyAtKUpper, actual.AccuracyAtKUpper, "accuracyAtKUpper", mismatches);
        CheckClose(expected.MacroF1Lower, actual.MacroF1Lower, "macroF1Lower", mismatches);
        CheckClose(expected.MacroF1Upper, actual.MacroF1Upper, "macroF1Upper", mismatches);

        VerifyBuckets(expected.TextBuckets, actual.TextBuckets, "text", mismatches);
        VerifyBuckets(expected.CategoryBuckets, actual.CategoryBuckets, "category", mismatches);

        if (mismatches.Count > 0)
        {
            Console.Error.WriteLine(
                $"classification-online: pin drifted ({string.Join(", ", mismatches)}).");
            return 1;
        }

        Console.WriteLine("classification-online: pin matches.");
        return 0;
    }

    private static void VerifyBuckets(
        IReadOnlyList<OnlineBucketPin> expected,
        IReadOnlyList<OnlineBucketPin> actual,
        string dimension,
        List<string> mismatches)
    {
        if (expected.Count != actual.Count)
        {
            mismatches.Add($"{dimension} bucket count");
            return;
        }

        for (int i = 0; i < expected.Count; i++)
        {
            if (expected[i].Bucket != actual[i].Bucket || expected[i].Documents != actual[i].Documents)
            {
                mismatches.Add($"{dimension} bucket {i}");
                continue;
            }

            CheckClose(expected[i].Accuracy, actual[i].Accuracy, $"{dimension}:{expected[i].Bucket}:accuracy", mismatches);
            CheckClose(expected[i].MacroF1, actual[i].MacroF1, $"{dimension}:{expected[i].Bucket}:macroF1", mismatches);
        }
    }

    private static void CheckClose(double expected, double actual, string field, List<string> mismatches)
    {
        if (Math.Abs(expected - actual) > 1e-9)
            mismatches.Add(field);
    }

    private static SearchDocument ToDocument(ClassificationDocument document) =>
        new(document.Id, document.Text, Category: document.Category);

    private static List<ClassificationDocument> Shuffle(
        IReadOnlyList<ClassificationDocument> documents,
        int seed)
    {
        var shuffled = documents.OrderBy(document => document.Id, StringComparer.Ordinal).ToList();
        var random = new Random(seed); // NOSONAR:S2245 (fixed seed: the order is part of the reproduction)

        for (int i = shuffled.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        return shuffled;
    }

    private sealed class CommandOptions
    {
        public string DataBaseDir { get; set; } =
            Path.Combine(AppContext.BaseDirectory, "../../../data");

        public int? Limit { get; set; }
        public ClassificationTextMode TextMode { get; set; } = ClassificationTextMode.SubjectAndBody;
        public string Model { get; set; } = "nb";
        public double WarmupFraction { get; set; } = 0.8;
        public int RetrainEvery { get; set; }
        public int TopK { get; set; } = 3;
        public int Blocks { get; set; } = 20;
        public int Seed { get; set; } = 7;
        public double CoverageTarget { get; set; } = 0.5;
        public bool Learn { get; set; } = true;
        public string? WritePath { get; set; }
        public string? VerifyPath { get; set; }
    }
}
