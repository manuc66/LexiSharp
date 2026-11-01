using System.Globalization;
using LexiSharp.Classification;
using LexiSharp.Core;
using LexiSharp.Onnx;

namespace LexiSharp.Eval;

/// <summary>
/// The <c>classification-centroid</c> verb: embeds 20 Newsgroups with the multilingual E5 export,
/// trains Naive Bayes and a nearest-centroid model, selects the blend weight on a held-out slice
/// of the training split, and reports both models and the blend on the official test split.
/// </summary>
/// <remarks>
/// This is a measurement lane, not a pinned reference: the E5 export is an external artifact, so
/// unlike the BM25 tables there is no md5 in the repository to replay against. The corpus itself
/// is the standard bydate split, deduplicated exactly as the lexical classification lane does.
/// </remarks>
internal static class CentroidClassificationCommand
{
    private const double ValidationFraction = 0.15;
    private const double Temperature = 0.1;
    private const int MaximumSequenceLength = 512;
    private const int EmbeddingBatchSize = 8;

    public static async Task<int> RunAsync(string[] args)
    {
        string dataBaseDir = Path.Combine(AppContext.BaseDirectory, "../../../data");
        string modelDir = Path.Combine(AppContext.BaseDirectory, "../../../data/models");
        var textMode = ClassificationTextMode.SubjectAndBody;
        int? limit = null;
        double validationFraction = ValidationFraction;
        double temperature = Temperature;
        int maxSequenceLength = MaximumSequenceLength;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data" when i + 1 < args.Length:
                    dataBaseDir = args[++i];
                    break;
                case "--model" when i + 1 < args.Length:
                    modelDir = args[++i];
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
                        Console.Error.WriteLine("classification-centroid: --text expects 'full' or 'subject'.");
                        return 2;
                    }

                    break;
                case "--limit" when i + 1 < args.Length:
                    if (!TryReadPositive(args[++i], out int parsedLimit))
                    {
                        Console.Error.WriteLine("classification-centroid: --limit expects a positive integer.");
                        return 2;
                    }

                    limit = parsedLimit;
                    break;
                case "--validation" when i + 1 < args.Length:
                    if (!TryReadFraction(args[++i], out validationFraction))
                    {
                        Console.Error.WriteLine("classification-centroid: --validation expects a number in (0, 1).");
                        return 2;
                    }

                    break;
                case "--temperature" when i + 1 < args.Length:
                    if (!TryReadPositiveDouble(args[++i], out temperature))
                    {
                        Console.Error.WriteLine("classification-centroid: --temperature expects a finite number greater than 0.");
                        return 2;
                    }

                    break;
                case "--max-seq" when i + 1 < args.Length:
                    if (!TryReadPositive(args[++i], out maxSequenceLength))
                    {
                        Console.Error.WriteLine("classification-centroid: --max-seq expects a positive integer.");
                        return 2;
                    }

                    break;
                default:
                    Console.Error.WriteLine($"classification-centroid: unknown argument '{args[i]}'.");
                    return 2;
            }
        }

        string? onnxPath = ResolveModelFile(modelDir, Path.Combine("onnx", "model.onnx"))
            ?? ResolveModelFile(modelDir, "model.onnx");
        string? sentencePiecePath = ResolveModelFile(modelDir, "sentencepiece.bpe.model");

        if (onnxPath is null || sentencePiecePath is null)
        {
            Console.Error.WriteLine(
                $"classification-centroid: expected model.onnx (or onnx/model.onnx) and sentencepiece.bpe.model under '{modelDir}'.");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        var corpus = await TwentyNewsgroups.LoadOrDownloadAsync(dataBaseDir, limit, textMode);

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"20 Newsgroups ({corpus.Name}): {corpus.Train.Count} train, {corpus.Test.Count} test, " +
            $"{corpus.Categories.Count} categories"));

        using var encoder = CreateEncoder(onnxPath, sentencePiecePath, maxSequenceLength);

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"E5: {Path.GetFileName(onnxPath)}, {encoder.Dimension} dimensions, query prefix on both sides"));

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Encoding {corpus.Train.Count + corpus.Test.Count} texts (batch {EmbeddingBatchSize}, max {maxSequenceLength} tokens)..."));

        var watch = System.Diagnostics.Stopwatch.StartNew();

        IReadOnlyList<ReadOnlyMemory<float>> trainVectors = await encoder.GetTextEmbeddingsAsync(
            corpus.Train.Select(document => document.Text).ToArray(),
            EmbeddingUse.Query,
            cancellation.Token);

        IReadOnlyList<ReadOnlyMemory<float>> testVectors = await encoder.GetTextEmbeddingsAsync(
            corpus.Test.Select(document => document.Text).ToArray(),
            EmbeddingUse.Query,
            cancellation.Token);

        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Embedded in {watch.Elapsed.TotalSeconds:0.0}s."));

        var split = Split(corpus.Train, validationFraction);
        var fitModel = TrainModel(corpus.Train, trainVectors, split.Fit, temperature);
        var validation = PredictAll(
            corpus.Train,
            trainVectors,
            split.Validation,
            fitModel.NaiveBayes,
            fitModel.Centroid,
            corpus.Categories.Count);

        double bestWeight = SelectWeight(validation, corpus.Train, split.Validation, corpus.Categories);
        var fullModel = TrainModel(corpus.Train, trainVectors, Enumerable.Range(0, corpus.Train.Count).ToArray(), temperature);
        var test = PredictAll(
            corpus.Test,
            testVectors,
            Enumerable.Range(0, corpus.Test.Count).ToArray(),
            fullModel.NaiveBayes,
            fullModel.Centroid,
            corpus.Categories.Count);

        Console.WriteLine();
        Console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Held-out selection: {split.Validation.Count} training documents, best weight {bestWeight:0.00}"));
        Console.WriteLine();

        var rows = new[]
        {
            Evaluate("Naive Bayes", test, corpus.Test, corpus.Categories, weight: 0),
            Evaluate("Centroid (E5)", test, corpus.Test, corpus.Categories, weight: 1),
            Evaluate($"Fusion (w={bestWeight:0.00})", test, corpus.Test, corpus.Categories, bestWeight),
        };

        Console.WriteLine("| Method | Accuracy | Macro-F1 |");
        Console.WriteLine("|---|---:|---:|");

        foreach (var row in rows)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {row.Method} | {row.Accuracy:0.0000} | {row.MacroF1:0.0000} |"));
        }

        return 0;
    }

    private static OnnxTextEncoder CreateEncoder(string onnxPath, string sentencePiecePath, int maxSequenceLength)
    {
        int unknown = new SentencePieceOnnxTokenizer(sentencePiecePath).UnknownId;

        var tokenizer = new SentencePieceOnnxTokenizer(sentencePiecePath, new SentencePieceOnnxTokenizerOptions
        {
            IdMap = id => id == unknown ? 3 : id + 1,
            PrefixId = 0,
            SuffixId = 2,
        });

        return new OnnxTextEncoder(new OnnxTextEncoderOptions
        {
            ModelPath = onnxPath,
            Tokenizer = tokenizer,
            OutputName = "last_hidden_state",
            Pooling = OnnxPooling.Mean,
            Normalize = true,
            MaxSequenceLength = maxSequenceLength,
            MaxBatchSize = EmbeddingBatchSize,
            QueryPrefix = "query: ",
            PassagePrefix = "passage: ",
            IntraOpNumThreads = 4,
            InterOpNumThreads = 1,
        });
    }

    private static (NaiveBayesClassifier NaiveBayes, CentroidClassifier<LabelledVector> Centroid) TrainModel(
        IReadOnlyList<ClassificationDocument> documents,
        IReadOnlyList<ReadOnlyMemory<float>> vectors,
        IReadOnlyList<int> indices,
        double temperature)
    {
        var training = new SearchDocument[indices.Count];
        var labelled = new LabelledVector[indices.Count];

        for (int i = 0; i < indices.Count; i++)
        {
            int index = indices[i];
            training[i] = new SearchDocument(documents[index].Id, documents[index].Text, Category: documents[index].Category);
            labelled[i] = new LabelledVector(documents[index].Category, vectors[index]);
        }

        var naiveBayes = new NaiveBayesClassifier();
        naiveBayes.Train(training);

        var centroid = new CentroidClassifier<LabelledVector>(
            item => item.Vector,
            item => item.Category,
            new CentroidClassifierOptions { Temperature = temperature });

        centroid.Train(labelled);

        return (naiveBayes, centroid);
    }

    private static Prediction[] PredictAll(
        IReadOnlyList<ClassificationDocument> documents,
        IReadOnlyList<ReadOnlyMemory<float>> vectors,
        IReadOnlyList<int> indices,
        NaiveBayesClassifier naiveBayes,
        CentroidClassifier<LabelledVector> centroid,
        int categoryCount)
    {
        var predictions = new Prediction[indices.Count];

        for (int i = 0; i < indices.Count; i++)
        {
            int index = indices[i];

            predictions[i] = new Prediction(
                naiveBayes.Predict(documents[index].Text, limit: categoryCount),
                centroid.Predict(vectors[index], limit: categoryCount));
        }

        return predictions;
    }

    private static double SelectWeight(
        Prediction[] predictions,
        IReadOnlyList<ClassificationDocument> documents,
        IReadOnlyList<int> indices,
        IReadOnlyList<string> categories)
    {
        double bestWeight = 0;
        double bestScore = double.NegativeInfinity;

        for (int step = 0; step <= 20; step++)
        {
            double weight = step / 20.0;
            var row = Evaluate("candidate", predictions, documents, indices, categories, weight);

            if (row.MacroF1 > bestScore)
            {
                bestScore = row.MacroF1;
                bestWeight = weight;
            }
        }

        return bestWeight;
    }

    private static MetricRow Evaluate(
        string method,
        Prediction[] predictions,
        IReadOnlyList<ClassificationDocument> documents,
        IReadOnlyList<int> indices,
        IReadOnlyList<string> categories,
        double weight)
    {
        var expected = new string[indices.Count];
        var predicted = new string?[indices.Count];

        for (int i = 0; i < indices.Count; i++)
        {
            expected[i] = documents[indices[i]].Category;
            predicted[i] = BestCategory(predictions[i], weight);
        }

        return new MetricRow(method, Metrics(expected, predicted, categories));
    }

    private static MetricRow Evaluate(
        string method,
        Prediction[] predictions,
        IReadOnlyList<ClassificationDocument> documents,
        IReadOnlyList<string> categories,
        double weight)
    {
        var indices = Enumerable.Range(0, documents.Count).ToArray();
        return Evaluate(method, predictions, documents, indices, categories, weight);
    }

    private static string? BestCategory(Prediction prediction, double weight)
    {
        if (weight <= 0)
            return prediction.Lexical.Count > 0 ? prediction.Lexical[0].Category : null;

        if (weight >= 1)
            return prediction.Semantic.Count > 0 ? prediction.Semantic[0].Category : null;

        var scores = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var result in prediction.Lexical)
            scores[result.Category] = (1 - weight) * result.Probability;

        foreach (var result in prediction.Semantic)
        {
            scores.TryGetValue(result.Category, out double current);
            scores[result.Category] = current + (weight * result.Probability);
        }

        string? best = null;
        double bestScore = double.NegativeInfinity;

        foreach (var (category, score) in scores)
        {
            if (score > bestScore || (score == bestScore && string.CompareOrdinal(category, best) < 0))
            {
                best = category;
                bestScore = score;
            }
        }

        return best;
    }

    private static (double Accuracy, double MacroF1) Metrics(
        IReadOnlyList<string> expected,
        IReadOnlyList<string?> predicted,
        IReadOnlyList<string> categories)
    {
        int correct = 0;
        var truePositives = new Dictionary<string, int>(StringComparer.Ordinal);
        var falsePositives = new Dictionary<string, int>(StringComparer.Ordinal);
        var falseNegatives = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (string category in categories)
        {
            truePositives[category] = 0;
            falsePositives[category] = 0;
            falseNegatives[category] = 0;
        }

        for (int i = 0; i < expected.Count; i++)
        {
            string actual = expected[i];
            string? guess = predicted[i];

            if (guess == actual)
            {
                correct++;
                truePositives[actual]++;
            }
            else
            {
                falseNegatives[actual]++;

                if (guess is not null && falsePositives.ContainsKey(guess))
                    falsePositives[guess]++;
            }
        }

        double f1Sum = 0;

        foreach (string category in categories)
        {
            int tp = truePositives[category];
            int fp = falsePositives[category];
            int fn = falseNegatives[category];
            double precision = tp + fp == 0 ? 0 : (double)tp / (tp + fp);
            double recall = tp + fn == 0 ? 0 : (double)tp / (tp + fn);
            f1Sum += precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
        }

        return ((double)correct / expected.Count, f1Sum / categories.Count);
    }

    private static DataSplit Split(IReadOnlyList<ClassificationDocument> documents, double validationFraction)
    {
        var indices = Enumerable.Range(0, documents.Count).ToArray();
        var random = new Random(7); // NOSONAR:S2245 (fixed seed: the split must replay)

        for (int i = indices.Length - 1; i > 0; i--)
        {
            int other = random.Next(i + 1);
            (indices[i], indices[other]) = (indices[other], indices[i]);
        }

        int validationCount = Math.Max(1, (int)Math.Round(documents.Count * validationFraction));
        validationCount = Math.Min(validationCount, documents.Count - 1);

        return new DataSplit(
            indices[..validationCount],
            indices[validationCount..]);
    }

    private static string? ResolveModelFile(string directory, string relativePath)
    {
        string path = Path.Combine(directory, relativePath);
        return File.Exists(path) ? path : null;
    }

    private static bool TryReadPositive(string value, out int parsed) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed > 0;

    private static bool TryReadPositiveDouble(string value, out double parsed) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
        && double.IsFinite(parsed)
        && parsed > 0;

    private static bool TryReadFraction(string value, out double parsed) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
        && double.IsFinite(parsed)
        && parsed > 0
        && parsed < 1;

    private sealed record LabelledVector(string Category, ReadOnlyMemory<float> Vector);

    private sealed record Prediction(
        IReadOnlyList<ClassificationResult> Lexical,
        IReadOnlyList<ClassificationResult> Semantic);

    private sealed record DataSplit(IReadOnlyList<int> Validation, IReadOnlyList<int> Fit);

    private sealed record MetricRow(string Method, (double Accuracy, double MacroF1) Metrics)
    {
        public double Accuracy => Metrics.Accuracy;

        public double MacroF1 => Metrics.MacroF1;
    }
}
