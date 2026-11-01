using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LexiSharp.Classification;
using LexiSharp.Core;

namespace LexiSharp.Eval;

/// <summary>A linear model's behaviour when the stream introduces labels it never trained on.</summary>
internal sealed record NewClassPin(
    string Dataset,
    string SourceMd5,
    int KnownCategories,
    int HeldOutCategories,
    string HeldOut,
    int KnownTrainDocuments,
    int StreamDocuments,
    int Seed,
    double CorrectionWeight,
    double UnseenCategoryBias,
    int RetrainEvery,
    double StaticAccuracy,
    double VirtualAccuracy,
    double VirtualFirstAccuracy,
    double VirtualRepeatAccuracy,
    double RetrainAccuracy,
    int VirtualLearns,
    int Retrains);

/// <summary>
/// The <c>classification-new-class</c> verb: hold categories out of the initial training, then
/// compare doing nothing, learning them as virtual categories, and retraining on a schedule.
/// </summary>
/// <remarks>
/// This is a decision experiment, not a leaderboard. The question is whether a virtual category is
/// worth its correction mass when the label was never in the training set, or whether a periodic
/// retrain is the only honest answer. The stream is labelled, so the measurement is an online
/// supervised run; the first occurrence of a category is scored before the model has ever seen it.
/// </remarks>
internal static class NewClassClassificationCommand
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

        string[] categories = corpus.Train
            .Select(document => document.Category)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(category => category, StringComparer.Ordinal)
            .ToArray();

        if (options.HeldOutCategories < 1 || options.HeldOutCategories >= categories.Length)
        {
            Console.Error.WriteLine(
                $"classification-new-class: --held-out must leave at least one training category " +
                $"(1..{categories.Length - 1}).");
            return 2;
        }

        string[] heldOut = categories[^options.HeldOutCategories..];
        var heldOutSet = new HashSet<string>(heldOut, StringComparer.Ordinal);

        var knownTrain = corpus.Train
            .Where(document => !heldOutSet.Contains(document.Category))
            .Select(ToDocument)
            .ToArray();

        var stream = corpus.Test
            .Where(document => heldOutSet.Contains(document.Category))
            .Select(ToDocument)
            .ToArray();

        Shuffle(stream, options.Seed);

        if (stream.Length == 0)
        {
            Console.Error.WriteLine("classification-new-class: the held-out stream is empty.");
            return 2;
        }

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"20 Newsgroups ({corpus.Name}): {knownTrain.Length} known train, {stream.Length} held-out test, " +
            $"{categories.Length - heldOut.Length} known categories, md5 {corpus.SourceMd5}"));

        Console.WriteLine($"Held out ({heldOut.Length}): {string.Join(", ", heldOut)}");

        double staticAccuracy = Static(knownTrain, stream);

        (double virtualAccuracy, double first, double repeat, int learns) =
            Virtual(knownTrain, stream, options);

        (double retrainAccuracy, int retrains) = Retrain(knownTrain, stream, options);

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Static (no learn): accuracy {staticAccuracy:F4}"));
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Virtual Learn: accuracy {virtualAccuracy:F4} (first {first:F4}, repeat {repeat:F4}), " +
            $"{learns} learns"));
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Retrain every {options.RetrainEvery}: accuracy {retrainAccuracy:F4}, {retrains} retrains"));

        var pin = new NewClassPin(
            corpus.Name,
            corpus.SourceMd5,
            categories.Length - heldOut.Length,
            heldOut.Length,
            string.Join(",", heldOut),
            knownTrain.Length,
            stream.Length,
            options.Seed,
            options.CorrectionWeight,
            options.UnseenCategoryBias,
            options.RetrainEvery,
            staticAccuracy,
            virtualAccuracy,
            first,
            repeat,
            retrainAccuracy,
            learns,
            retrains);

        if (options.WritePath is not null)
        {
            File.WriteAllText(options.WritePath, JsonSerializer.Serialize(pin, JsonOptions) + Environment.NewLine);
            Console.WriteLine($"Wrote {options.WritePath}");
        }

        return options.VerifyPath is null ? 0 : Verify(pin, options.VerifyPath);
    }

    private static double Static(SearchDocument[] knownTrain, SearchDocument[] stream)
    {
        var model = Train(knownTrain);
        int correct = 0;

        foreach (SearchDocument document in stream)
        {
            if (string.Equals(model.PredictBest(document.Text), document.Category, StringComparison.Ordinal))
                correct++;
        }

        return (double)correct / stream.Length;
    }

    private static (double Accuracy, double First, double Repeat, int Learns) Virtual(
        SearchDocument[] knownTrain,
        SearchDocument[] stream,
        CommandOptions options)
    {
        var model = new LinearClassifier(options: LinearOptions(options));
        model.Train(knownTrain);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        int correct = 0;
        int firstCorrect = 0;
        int firstCount = 0;
        int repeatCorrect = 0;
        int repeatCount = 0;
        var timer = Stopwatch.StartNew();

        foreach (SearchDocument document in stream)
        {
            bool first = !seen.Contains(document.Category!);
            bool hit = string.Equals(model.PredictBest(document.Text), document.Category, StringComparison.Ordinal);

            if (hit)
                correct++;

            if (first)
            {
                firstCount++;

                if (hit)
                    firstCorrect++;
            }
            else
            {
                repeatCount++;

                if (hit)
                    repeatCorrect++;
            }

            model.Learn(document.Text, document.Category!);
            seen.Add(document.Category!);
        }

        timer.Stop();

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Virtual learn time: {timer.Elapsed.TotalMilliseconds:F0} ms"));

        return (
            (double)correct / stream.Length,
            firstCount == 0 ? 0 : (double)firstCorrect / firstCount,
            repeatCount == 0 ? 0 : (double)repeatCorrect / repeatCount,
            stream.Length);
    }

    private static (double Accuracy, int Retrains) Retrain(
        SearchDocument[] knownTrain,
        SearchDocument[] stream,
        CommandOptions options)
    {
        var model = Train(knownTrain);
        var accumulated = new List<SearchDocument>(knownTrain);
        int correct = 0;
        int retrains = 0;
        var timer = Stopwatch.StartNew();

        for (int i = 0; i < stream.Length; i++)
        {
            if (string.Equals(model.PredictBest(stream[i].Text), stream[i].Category, StringComparison.Ordinal))
                correct++;

            accumulated.Add(stream[i]);

            if ((i + 1) % options.RetrainEvery == 0)
            {
                model.Train(accumulated);
                retrains++;
            }
        }

        timer.Stop();

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Retrain time: {timer.Elapsed.TotalMilliseconds:F0} ms"));

        return ((double)correct / stream.Length, retrains);
    }

    private static LinearClassifier Train(SearchDocument[] documents)
    {
        var model = new LinearClassifier();
        model.Train(documents);
        return model;
    }

    private static LinearClassifierOptions LinearOptions(CommandOptions options) =>
        new()
        {
            CorrectionWeight = options.CorrectionWeight,
            UnseenCategoryBias = options.UnseenCategoryBias,
        };

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
                        Console.Error.WriteLine("classification-new-class: --limit expects a positive integer.");
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
                        Console.Error.WriteLine("classification-new-class: --text expects 'full' or 'subject'.");
                        return false;
                    }

                    break;
                case "--held-out" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int heldOut)
                        || heldOut < 1)
                    {
                        Console.Error.WriteLine("classification-new-class: --held-out expects a positive integer.");
                        return false;
                    }

                    options.HeldOutCategories = heldOut;
                    break;
                case "--correction-weight" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out double weight)
                        || !double.IsFinite(weight) || weight <= 0)
                    {
                        Console.Error.WriteLine("classification-new-class: --correction-weight expects a positive number.");
                        return false;
                    }

                    options.CorrectionWeight = weight;
                    break;
                case "--bias" when i + 1 < args.Length:
                    if (!double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out double bias)
                        || !double.IsFinite(bias))
                    {
                        Console.Error.WriteLine("classification-new-class: --bias expects a finite number.");
                        return false;
                    }

                    options.UnseenCategoryBias = bias;
                    break;
                case "--retrain-every" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int every)
                        || every < 1)
                    {
                        Console.Error.WriteLine("classification-new-class: --retrain-every expects a positive integer.");
                        return false;
                    }

                    options.RetrainEvery = every;
                    break;
                case "--seed" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed)
                        || seed < 0)
                    {
                        Console.Error.WriteLine("classification-new-class: --seed expects a non-negative integer.");
                        return false;
                    }

                    options.Seed = seed;
                    break;
                case "--write" when i + 1 < args.Length:
                    options.WritePath = args[++i];
                    break;
                case "--verify" when i + 1 < args.Length:
                    options.VerifyPath = args[++i];
                    break;
                default:
                    Console.Error.WriteLine($"classification-new-class: unknown argument '{args[i]}'.");
                    return false;
            }
        }

        return true;
    }

    private static SearchDocument ToDocument(ClassificationDocument document) =>
        new(document.Id, document.Text, Category: document.Category);

    private static void Shuffle(SearchDocument[] documents, int seed)
    {
        var random = new Random(seed); // NOSONAR:S2245 (fixed seed: the stream order is reproducible)

        for (int i = documents.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (documents[i], documents[j]) = (documents[j], documents[i]);
        }
    }

    private static int Verify(NewClassPin actual, string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"classification-new-class: pin not found: {path}");
            return 1;
        }

        var expected = JsonSerializer.Deserialize<NewClassPin>(File.ReadAllText(path), JsonOptions);

        if (expected is null)
        {
            Console.Error.WriteLine("classification-new-class: pin is empty.");
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
        Check(expected.KnownCategories == actual.KnownCategories, "knownCategories");
        Check(expected.HeldOutCategories == actual.HeldOutCategories, "heldOutCategories");
        Check(expected.HeldOut == actual.HeldOut, "heldOut");
        Check(expected.KnownTrainDocuments == actual.KnownTrainDocuments, "knownTrainDocuments");
        Check(expected.StreamDocuments == actual.StreamDocuments, "streamDocuments");
        Check(expected.Seed == actual.Seed, "seed");
        Check(expected.CorrectionWeight == actual.CorrectionWeight, "correctionWeight");
        Check(expected.UnseenCategoryBias == actual.UnseenCategoryBias, "unseenCategoryBias");
        Check(expected.RetrainEvery == actual.RetrainEvery, "retrainEvery");
        CheckClose(expected.StaticAccuracy, actual.StaticAccuracy, "staticAccuracy", mismatches);
        CheckClose(expected.VirtualAccuracy, actual.VirtualAccuracy, "virtualAccuracy", mismatches);
        CheckClose(expected.VirtualFirstAccuracy, actual.VirtualFirstAccuracy, "virtualFirstAccuracy", mismatches);
        CheckClose(expected.VirtualRepeatAccuracy, actual.VirtualRepeatAccuracy, "virtualRepeatAccuracy", mismatches);
        CheckClose(expected.RetrainAccuracy, actual.RetrainAccuracy, "retrainAccuracy", mismatches);
        Check(expected.VirtualLearns == actual.VirtualLearns, "virtualLearns");
        Check(expected.Retrains == actual.Retrains, "retrains");

        if (mismatches.Count > 0)
        {
            Console.Error.WriteLine($"classification-new-class: pin drifted ({string.Join(", ", mismatches)}).");
            return 1;
        }

        Console.WriteLine("classification-new-class: pin matches.");
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
        public int HeldOutCategories { get; set; } = 5;
        public double CorrectionWeight { get; set; } = 20;
        public double UnseenCategoryBias { get; set; }
        public int RetrainEvery { get; set; } = 200;
        public int Seed { get; set; } = 7;
        public string? WritePath { get; set; }
        public string? VerifyPath { get; set; }
    }
}
