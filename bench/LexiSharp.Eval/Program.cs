using System.Globalization;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;

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
        bool rerank = false;
        int rerankCandidates = 100;
        bool stem = false;
        string analyzer = "default";
        bool excludeQueryDocument = false;
        string ndcgGain = "exponential";
        bool fingerprintOnly = false;
        Bm25Parameters? referenceBm25 = null;
        string? verifyAgainst = null;
        bool writePins = false;

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
                case "--rerank":
                    rerank = true;
                    break;
                case "--rerank-top" when i + 1 < args.Length:
                    rerankCandidates = ParsePositive(args[++i], "--rerank-top");
                    break;
                case "--stem" when i + 1 < args.Length:
                    stem = ParseStem(args[++i]);
                    break;
                case "--analyzer" when i + 1 < args.Length:
                    analyzer = args[++i];
                    break;
                case "--exclude-query-doc":
                    excludeQueryDocument = true;
                    break;
                case "--ndcg-gain" when i + 1 < args.Length:
                    ndcgGain = args[++i];
                    break;
                case "--fingerprint":
                    fingerprintOnly = true;
                    break;
                case "--reference-bm25" when i + 1 < args.Length:
                    referenceBm25 = ParseBm25Parameters(args[++i]);
                    break;
                // The two-argument form has to be tested first: a bare --verify-reference would
                // otherwise consume the next flag as its path.
                case "--verify-reference" when i + 1 < args.Length && !args[i + 1].StartsWith("--"):
                    verifyAgainst = args[++i];
                    break;
                case "--verify-reference":
                    verifyAgainst = string.Empty;
                    break;
                case "--write":
                    writePins = true;
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

        // The regression net runs before anything else: a caller who asked to verify wants an answer,
        // not a table plus an answer.
        if (verifyAgainst is not null)
        {
            string pinnedPath = verifyAgainst.Length == 0
                ? LocatePinnedFile(dataBaseDir)
                : Path.GetFullPath(verifyAgainst);

            return await ReferenceVerifier.RunAsync(
                PinnedReference.Load(pinnedPath), dataBaseDir, writePins, CancellationToken.None);
        }

        // The one tokenizer every config shares, so the table is comparable within a run.
        (ITokenizer tokenizer, string analysisDescription) = BuildAnalysis(analyzer, stem);

        IReadOnlyList<BeirDataset> datasets = datasetArg == "all"
            ? BeirDataset.All
            : [BeirDataset.Resolve(datasetArg)];

        IReranker? reranker = null;

        if (rerank)
        {
            string rerankCache = Path.Combine(dataBaseDir, "models", "cross-encoder");
            await CrossEncoderModels.EnsureFilesAsync(rerankCache, CancellationToken.None);

            var options = new Microsoft.ML.OnnxRuntime.SessionOptions();
            options.AppendExecutionProvider_CPU();
            options.IntraOpNumThreads = 4;
            options.InterOpNumThreads = 1;

            var session = new Microsoft.ML.OnnxRuntime.InferenceSession(
                Path.Combine(rerankCache, "onnx", "model.onnx"), options);

            reranker = new CrossEncoderReranker(new BertTokenizer(Path.Combine(rerankCache, "vocab.txt")), session);

            Console.WriteLine(
                $"Cross-encoder reranker enabled ({CrossEncoderModels.ModelId}, {rerankCandidates} candidates/query).");
            Console.WriteLine();
        }

        Console.WriteLine("LexiSharp evaluation harness — BEIR corpora");
        Console.WriteLine($"Data directory: {dataBaseDir}");
        Console.WriteLine($"Tokenization: {analysisDescription}");

        NdcgGain gain = ParseNdcgGain(ndcgGain);
        Console.WriteLine(
            $"nDCG gains: {(gain == NdcgGain.Linear ? "linear (trec_eval / pytrec_eval, the BEIR paper's convention)" : "exponential (2^rel - 1, the library default)")}.");

        if (excludeQueryDocument)
        {
            Console.WriteLine(
                "Excluded: the document whose id equals the query id, which is what Anserini's " +
                "-removeQuery does. Affects only corpora where queries are documents (ArguAna: " +
                "1298 of 1406 test queries); a no-op elsewhere.");
        }

        Console.WriteLine();

        foreach (BeirDataset dataset in datasets)
        {
            await RunDatasetAsync(
                dataset, dataBaseDir, topK, limit, tokenizer, analysisDescription, gain,
                excludeQueryDocument, fingerprintOnly, referenceBm25, dense, denseSeq, tuned, reranker, rerankCandidates);
            Console.WriteLine();
        }

        return 0;
    }

    /// <summary>
    /// Builds the shared tokenizer for a run, and the description of the analysis it applies.
    /// <c>lucene-english</c> is the analysis the published BM25 baselines were produced with —
    /// Lucene's <c>EnglishAnalyzer</c>: Porter stemming, its 33-word stop word list, and
    /// single-character terms kept — and it is the only setting under which a LexiSharp number is
    /// comparable with those published figures.
    /// </summary>
    private static (ITokenizer Tokenizer, string Description) BuildAnalysis(string analyzer, bool stem)
    {
        TokenizerOptions options = analyzer switch
        {
            "default" when stem => new TokenizerOptions { Stemmer = new PorterStemmer() },
            "default" => TokenizerOptions.Default,
            "porter" => new TokenizerOptions
            {
                Stemmer = new PorterStemmer(),
                RemoveStopWords = true,
            },
            "lucene-english" => new TokenizerOptions
            {
                Stemmer = new PorterStemmer(),
                RemoveStopWords = true,
                StopWords = StopWords.LuceneEnglish,
                // Lucene's StandardTokenizer emits one-character terms and its stop list keeps "i",
                // so dropping them here would make this index hold terms Lucene's does not.
                KeepSingleCharTerms = true,
            },
            _ => throw new ArgumentException(
                $"--analyzer expects 'default', 'porter' or 'lucene-english', got '{analyzer}'."),
        };

        // Tokenizer.Default is a shared instance built from TokenizerOptions.Default; naming its
        // configuration by value rather than by identity is what makes the printed line trustworthy.
        return (new Tokenizer(options), DescribeAnalysis(options));
    }

    /// <summary>Names the analysis in force, so a recorded number states what produced it.</summary>
    private static string DescribeAnalysis(TokenizerOptions options)
    {
        string stemming = options.Stemmer is null
            ? "no stemming"
            : $"Porter stemming ({options.Stemmer.GetType().Name})";

        string stopWords = options.RemoveStopWords
            ? options.StopWords is null || ReferenceEquals(options.StopWords, StopWords.English)
                ? "LexiSharp stop words"
                : "Lucene stop words"
            : "stop words kept";

        string singleChar = options.KeepSingleCharTerms ? ", single-char terms kept" : string.Empty;

        return $"lowercase, diacritics stripped, {stemming}, {stopWords}{singleChar}";
    }

    private static async Task RunDatasetAsync(
        BeirDataset dataset, string dataBaseDir, int topK, int? limit, ITokenizer tokenizer,
        string analysisDescription, NdcgGain gain, bool excludeQueryDocument, bool fingerprintOnly,
        Bm25Parameters? referenceBm25, bool dense, int denseSeq, bool tuned,
        IReranker? reranker, int rerankCandidates)
    {
        Console.WriteLine($"== {dataset.Name} ==");

        var corpus = await BeirLoader.LoadOrDownloadAsync(dataBaseDir, dataset);

        int testQueries = corpus.TestRelevance.Count;

        Console.WriteLine($"Corpus: {corpus.Documents.Count} documents, {corpus.Queries.Count} queries, {testQueries} test queries with relevance; evaluating {(limit is null ? testQueries : Math.Min(testQueries, limit.Value))}.");

        // The fingerprint comes first, and independently of every score below: it says whether this
        // run indexed the same thing as the reference, which is the question a percentage gap
        // against a published number cannot answer.
        var index = new InMemoryTextIndex(tokenizer);
        index.Index(Evaluation.BuildDocuments(corpus));
        PrintFingerprint(dataset, index, analysisDescription);

        if (fingerprintOnly)
            return;

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
            Console.WriteLine("Dense configs disabled — re-run with --dense to add multilingual-e5-small (CPU, first run downloads the model and encodes the corpus). Re-run with --rerank to add a cross-encoder second stage.");
        }

        var (results, tunedDescription) = Evaluation.Run(
            corpus, topK, limit, tokenizer, excludeQueryDocument, gain, referenceBm25,
            denseVectors, tuned, reranker, rerankCandidates);

        PrintTable(results, topK);
        PrintReference(dataset, analysisDescription, gain);

        Console.WriteLine($"BM25 tuned in-sample on the same queries (oracle, not a fair baseline): {tunedDescription}");
        Console.WriteLine(
            "Note: absolute scores depend on the analysis, which is why the run states it. The " +
            "relative ordering of the configs above is meaningful within a run; the comparison to " +
            "the published reference is meaningful only under the analysis the reference used " +
            "(--analyzer lucene-english for the Anserini/Pyserini figures).");
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

    private static void PrintReference(BeirDataset dataset, string analysisDescription, NdcgGain gain)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"Reference (BEIR paper, Thakur et al. 2021, Table 2): BM25 nDCG@10 = " +
            $"{dataset.Bm25Ndcg10Ref.ToString("0.000", CultureInfo.InvariantCulture)} on {dataset.Name}.");
        Console.WriteLine("  https://arxiv.org/abs/2104.08663");
        Console.WriteLine($"Dataset: {dataset.Name} via the BEIR public mirror, md5 {dataset.ExpectedMd5}.");
        Console.WriteLine(dataset.Graded
            ? "  Relevance is graded (3 levels) in the qrels."
            : "  Relevance is binary (0/1) in the qrels.");

        if (dataset.AnseriniNdcg10Ref is { } anserini)
        {
            Console.WriteLine(
                $"Reference (Anserini regression for the same flat index, current): BM25 nDCG@10 = " +
                $"{anserini.ToString("0.0000", CultureInfo.InvariantCulture)} on {dataset.Name}.");
            Console.WriteLine($"  {dataset.AnseriniSource}");
        }

        Console.WriteLine($"This run's analysis: {analysisDescription}.");
        Console.WriteLine(
            "  A published BM25 number is only comparable with a run whose analysis, BM25 parameters" +
            " and task conventions match it; a percentage gap against a reference is otherwise a" +
            " statement about the analysis, not about the ranking engine.");

        if (gain == NdcgGain.Exponential && dataset.Graded)
        {
            Console.WriteLine(
                "  nDCG here uses 2^rel - 1. The reference figures were computed by trec_eval, which" +
                " uses gain = rel; pass --ndcg-gain linear to compare like with like. On a binary" +
                " corpus the two are identical, so this only matters for a graded one.");
        }
    }

    /// <summary>
    /// Prints what this run's index holds, next to what an independent implementation's index of the
    /// same corpus holds. A run whose analysis differs is visible here, as integers, before any
    /// score is compared with anything.
    /// </summary>
    private static void PrintFingerprint(BeirDataset dataset, ITextIndex index, string analysisDescription)
    {
        var fingerprint = IndexFingerprint.Of(index);

        Console.WriteLine();
        Console.WriteLine($"Index fingerprint — {analysisDescription}");
        Console.WriteLine($"  documents           : {fingerprint.Documents}");
        Console.WriteLine($"  documents (non-empty): {fingerprint.NonEmptyDocuments}");
        Console.WriteLine($"  total terms         : {fingerprint.TotalTerms}");

        if (dataset.AnseriniTotalTerms is not { } expected || dataset.Documents is not { } documents)
            return;

        var reference = new IndexFingerprint(
            documents, dataset.NonEmptyDocuments ?? documents, expected);

        double difference = fingerprint.RelativeDifference(reference);

        Console.WriteLine(
            $"  reference (Anserini) : {reference.Documents} / {reference.NonEmptyDocuments} / " +
            $"{reference.TotalTerms} terms — difference {difference:P1}");
        Console.WriteLine(
            difference <= FingerprintTolerance
                ? "  -> the two indexes agree to within the tolerance; scores below are comparable with that reference."
                : $"  -> the two indexes differ by more than the {FingerprintTolerance:P0} tolerance: the scores below " +
                  "are NOT comparable with that reference, whatever they read.");
    }

    /// <summary>
    /// How far an index fingerprint may drift from the reference before the run says so. The
    /// measured gap is 1.1–2.8% on these three corpora (Porter plus Lucene's stop list against
    /// Lucene's own filter chain), so a tighter band would report a disagreement where none exists.
    /// </summary>
    private const double FingerprintTolerance = 0.03;

    /// <summary>
    /// Finds the pinned reference file next to the harness's own output, whether it is run through
    /// <c>dotnet run</c> from the project directory or from the build output directory.
    /// </summary>
    private static string LocatePinnedFile(string dataBaseDir)
    {
        string relative = PinnedReference.RelativePath;

        foreach (string candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, relative),
                     Path.Combine(AppContext.BaseDirectory, "../../../" + relative),
                     Path.Combine(Directory.GetCurrentDirectory(), relative),
                     Path.Combine(Directory.GetCurrentDirectory(), "bench/LexiSharp.Eval", relative),
                 })
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        throw new FileNotFoundException(
            $"Could not find the pinned reference file ({relative}). Pass its path after " +
            "--verify-reference, or run from bench/LexiSharp.Eval.");
    }

    private static Bm25Parameters ParseBm25Parameters(string value)
    {
        string[] parts = value.Split(',');

        if (parts.Length != 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double k1)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double b))
        {
            throw new ArgumentException($"--reference-bm25 expects 'k1,b', got '{value}'.");
        }

        return new Bm25Parameters(k1, b);
    }

    private static NdcgGain ParseNdcgGain(string value) => value switch
    {
        "exponential" => NdcgGain.Exponential,
        "linear" => NdcgGain.Linear,
        _ => throw new ArgumentException(
            $"--ndcg-gain must be 'exponential' or 'linear', got '{value}'."),
    };

    private static bool ParseStem(string value) => value switch
    {
        "porter" => true,
        "none" => false,
        _ => throw new ArgumentException($"--stem must be 'porter' or 'none', got '{value}'."),
    };

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
                     [--no-tuned] [--stem porter] [--dense] [--dense-seq <n>] [--rerank] [--rerank-top <n>]

              --data <dir>      Base directory for datasets (default: <project>/data).
                                Downloaded and checksum-verified on first run.
              --dataset <name>  Dataset to evaluate: nfcorpus, scifact or arguana (default: nfcorpus).
                                Use 'all' to run every registered dataset back-to-back.
              --top-k <n>       Retrieval depth and metric cutoff (default: 10).
              --limit <n>       Evaluate only the first n test queries (smoke runs).
              --no-tuned        Skip the in-sample k1/b oracle tuning (fast on heavy datasets
                                like arguana, where the 5×5 grid over long queries is the
                                dominant cost).
              --stem porter     Stem every term with LexiSharp.Linguistics.PorterStemmer (English,
                                opt-in; off by default, so the tables stay comparable).
              --analyzer <name> Analysis for the whole run: 'default' (lowercase, diacritics folded,
                                no stemming, stop words kept), 'porter' (stemming + LexiSharp stop
                                words) or 'lucene-english' (Porter + Lucene's 33-word stop list +
                                single-character terms kept). 'lucene-english' is the analysis the
                                published BM25 baselines used, and the only one under which these
                                numbers are comparable with them.
              --exclude-query-doc
                                Exclude the document whose id equals the query id, which is what
                                Anserini's -removeQuery does. Affects only corpora that number their
                                queries as documents (ArguAna: 1298 of 1406 test queries); a no-op
                                on the others.
              --ndcg-gain <name>
                                nDCG gain convention: 'exponential' (2^rel - 1, the library default)
                                or 'linear' (gain = rel, what trec_eval/pytrec_eval use, and so what
                                the published BEIR figures were computed with). Identical on binary
                                relevance; pass 'linear' to compare with a published number.
              --reference-bm25 <k1,b>
                                Add a BM25 row at these parameters, e.g. 0.9,0.4 — the defaults the
                                published BM25 baselines were produced with, which is neither of the
                                two rows above. Needed to measure a published operating point.
              --verify-reference [<file>]
                                Replay every configuration in reference/pinned.json and report which
                                ones drifted. Exits 1 on any drift, so it can gate a build. The file
                                defaults to the one shipped with the harness.
              --write           Accepted alongside --verify-reference and deliberately does not
                                re-record: re-recording is a separate, reviewed act, not a flag that
                                accepts whatever the code now does.
              --fingerprint     Print the index fingerprint (documents, non-empty documents, total
                                terms) against the reference index, then stop. No scoring, no
                                network beyond the corpus itself. Answers "did this run index the
                                same thing?" without spending a retrieval.
              --dense           Add dense retrieval configs using multilingual-e5-small (ONNX Runtime).
                                First run downloads the model and encodes corpus+queries on CPU
                                (threads capped at 4); embeddings are cached for later runs.
              --dense-seq <n>   Max tokens per sequence when embedding (default: 256). Lower = faster.
              --rerank          Add a second-stage cross-encoder reranker (ms-marco MiniLM-L-6-v2,
                                ONNX Runtime): re-scores the top-N lexical/dense candidates per query.
                                First run downloads ~90 MB into data/models/cross-encoder/.
              --rerank-top <n>  Number of candidates fed to the cross-encoder (default: 100).
              --help, -h        Show this help.
            """);
    }
}