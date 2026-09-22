using System.Globalization;

namespace LexiSharp.Eval;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string dataBaseDir = Path.Combine(AppContext.BaseDirectory, "../../../data");
        string datasetArg = "nfcorpus";
        int topK = 10;
        int? limit = null;
        int denseSeq = 256;
        bool dense = false;
        bool tuned = true;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data" when i + 1 < args.Length:
                    dataBaseDir = args[++i];
                    break;
                case "--dataset" when i + 1 < args.Length:
                    datasetArg = args[++i];
                    break;
                case "--top-k" when i + 1 < args.Length:
                    topK = ParsePositive(args[++i], "--top-k");
                    break;
                case "--limit" when i + 1 < args.Length:
                    limit = ParsePositive(args[++i], "--limit");
                    break;
                case "--dense-seq" when i + 1 < args.Length:
                    denseSeq = ParsePositive(args[++i], "--dense-seq");
                    break;
                case "--dense":
                    dense = true;
                    break;
                case "--no-tuned":
                    tuned = false;
                    break;
                case "--help":
                case "-h":
                    PrintHelp();
                    return 0;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    PrintHelp();
                    return 2;
            }
        }

        dataBaseDir = Path.GetFullPath(dataBaseDir);

        IReadOnlyList<BeirDataset> datasets = datasetArg == "all"
            ? BeirDataset.All
            : [BeirDataset.Resolve(datasetArg)];

        Console.WriteLine("LexiSharp evaluation harness — BEIR corpora");
        Console.WriteLine($"Data directory: {dataBaseDir}");
        Console.WriteLine();

        foreach (BeirDataset dataset in datasets)
        {
            await RunDatasetAsync(dataset, dataBaseDir, topK, limit, dense, denseSeq, tuned);
            Console.WriteLine();
        }

        return 0;
    }

    private static async Task RunDatasetAsync(
        BeirDataset dataset, string dataBaseDir, int topK, int? limit, bool dense, int denseSeq, bool tuned)
    {
        Console.WriteLine($"== {dataset.Name} ==");

        var corpus = await BeirLoader.LoadOrDownloadAsync(dataBaseDir, dataset);

        int testQueries = corpus.TestRelevance.Count;

        Console.WriteLine($"Corpus: {corpus.Documents.Count} documents, {corpus.Queries.Count} queries, {testQueries} test queries with relevance; evaluating {(limit is null ? testQueries : Math.Min(testQueries, limit.Value))}.");
        Console.WriteLine();

        DenseVectors? denseVectors = null;

        if (dense)
        {
            denseVectors = await DenseEmbedder.TryBuildAsync(
                corpus, Path.Combine(dataBaseDir, dataset.Name), Path.Combine(dataBaseDir, "models"), denseSeq, CancellationToken.None);

            Console.WriteLine("Dense configs enabled (multilingual-e5-small, Xenova ONNX export — MIT, weights from intfloat/multilingual-e5-small).");
        }
        else
        {
            Console.WriteLine("Dense configs disabled — re-run with --dense to add multilingual-e5-small (CPU, first run downloads the model and encodes the corpus).");
        }

        var (results, tunedDescription) = Evaluation.Run(corpus, topK, limit, denseVectors, tuned);

        PrintTable(results, topK);
        PrintReference(dataset);

        Console.WriteLine($"BM25 tuned in-sample on the same queries (oracle, not a fair baseline): {tunedDescription}");
        Console.WriteLine("Note: LexiSharp's default tokenizer lowercases, strips diacritics and splits on non-alphanumerics, but does not stem — so absolute scores differ from BEIR's published baselines, while the relative ordering of configs is meaningful.");
    }

    private static void PrintTable(IReadOnlyList<ConfigResult> results, int topK)
    {
        var header = new[] { "Config", $"nDCG@{topK}", $"MAP@{topK}", $"MRR@{topK}", $"R@{topK}", "Queries", "Time" };
        var widths = new[] { 30, 9, 9, 9, 9, 8, 8 };

        Console.WriteLine();
        Console.WriteLine(string.Join(" | ", header.Select((cell, i) =>
            i == 0 ? cell.PadRight(widths[0]) : cell.PadLeft(widths[i]))));
        Console.WriteLine(new string('-', widths.Sum() + (widths.Length - 1) * 3));

        foreach (var result in results)
        {
            var cells = new[]
            {
                result.Name.PadRight(widths[0]),
                result.NdcgAt10.ToString("0.000", CultureInfo.InvariantCulture).PadLeft(widths[1]),
                result.MapAt10.ToString("0.000", CultureInfo.InvariantCulture).PadLeft(widths[2]),
                result.MrrAt10.ToString("0.000", CultureInfo.InvariantCulture).PadLeft(widths[3]),
                result.RecallAt10.ToString("0.000", CultureInfo.InvariantCulture).PadLeft(widths[4]),
                result.Queries.ToString(CultureInfo.InvariantCulture).PadLeft(widths[5]),
                result.Elapsed.TotalSeconds.ToString("0.0s", CultureInfo.InvariantCulture).PadLeft(widths[6]),
            };

            Console.WriteLine(string.Join(" | ", cells));
        }
    }

    private static void PrintReference(BeirDataset dataset)
    {
        Console.WriteLine();
        Console.WriteLine($"Reference (BEIR paper, Thakur et al. 2021, Table 2): BM25 nDCG@10 = {dataset.Bm25Ndcg10Ref.ToString("0.000", CultureInfo.InvariantCulture)} on {dataset.Name}.");
        Console.WriteLine("  https://arxiv.org/abs/2104.08663");
        Console.WriteLine($"Dataset: {dataset.Name} via the BEIR public mirror, md5 {dataset.ExpectedMd5}.");
        Console.WriteLine(dataset.Graded
            ? "  Relevance is graded (3 levels) in the qrels."
            : "  Relevance is binary (0/1) in the qrels.");
    }

    private static int ParsePositive(string value, string option)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed <= 0)
            throw new ArgumentException($"{option} must be a positive integer, got '{value}'.");

        return parsed;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Usage: LexiSharp.Eval [--data <dir>] [--dataset <name|all>] [--top-k <n>] [--limit <n>]
                     [--no-tuned] [--dense] [--dense-seq <n>]

              --data <dir>      Base directory for datasets (default: <project>/data).
                                Downloaded and checksum-verified on first run.
              --dataset <name>  Dataset to evaluate: nfcorpus, scifact or arguana (default: nfcorpus).
                                Use 'all' to run every registered dataset back-to-back.
              --top-k <n>       Retrieval depth and metric cutoff (default: 10).
              --limit <n>       Evaluate only the first n test queries (smoke runs).
              --no-tuned        Skip the in-sample k1/b oracle tuning (fast on heavy datasets
                                like arguana, where the 5×5 grid over long queries is the
                                dominant cost).
              --dense           Add dense retrieval configs using multilingual-e5-small (ONNX Runtime).
                                First run downloads the model and encodes corpus+queries on CPU
                                (threads capped at 4); embeddings are cached for later runs.
              --dense-seq <n>   Max tokens per sequence when embedding (default: 256). Lower = faster.
              --help, -h        Show this help.
            """);
    }
}