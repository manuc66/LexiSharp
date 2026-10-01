using System.Globalization;
using LexiSharp.Core;
using LexiSharp.Expansion;
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
        bool queryFrequency = false;
        bool scoreRounding = false;
        bool saturationConstant = true;
        bool singlePrecision = false;
        string? runPath = null;
        int jobs = DefaultJobs();
        string? indexCache = null;

        // Per-query analysis is opt-in and writes a file rather than printing a table: it is one row
        // per query per configuration, which is tens of thousands of lines on ArguAna and has no
        // readable form on a terminal. The baseline defaults to the plain BM25 row because that is
        // the row every other row in this harness is read against.
        string? analyzePath = null;
        string analyzeBaseline = "BM25 (k1=1.5, b=0.75)";
        string? analyzeCandidate = null;

        // Query-side expansion sweep. Empty means off, so a default run learns nothing and pays
        // nothing: the expander's learning pass is the one genuinely expensive step in a run, at
        // seconds and hundreds of megabytes per learned model on SciFact.
        var expandDensities = new List<double>();
        var expandRankings = new List<ExpansionRanking>();

        // The ANN curve is a separate lane from the metrics table: it reports a recall/latency curve
        // over the pgvector index, and it needs a live server, so it is opt-in on both counts.
        var annEfValues = new List<int>();
        string? annConnection = null;

        // Off by default, because every query in a BEIR corpus is natural language and the published
        // figures apply no query language to it: the query string goes to the analyzer whole. Left on,
        // a straight double quote in an argument — 138 of ArguAna's 1,406 test queries, measured —
        // becomes a phrase delimiter and the query collapses. Stated in the output either way, since
        // which of the two ran is part of what a number means.
        bool parseQuerySyntax = false;
        bool querySyntaxReference = false;

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
                case "--query-term-frequency":
                    queryFrequency = true;
                    break;
                case "--reference-score-rounding":
                    scoreRounding = true;
                    break;
                case "--omit-saturation-constant":
                    saturationConstant = false;
                    break;
                case "--single-precision-bm25":
                    singlePrecision = true;
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
                case "--run" when i + 1 < args.Length:
                    runPath = args[++i];
                    break;
                case "--jobs" when i + 1 < args.Length:
                    jobs = ParsePositive(args[++i], "--jobs");
                    break;
                case "--query-syntax":
                    parseQuerySyntax = true;
                    break;
                case "--reference-index-statistics":
                    querySyntaxReference = true;
                    break;
                case "--index-cache" when i + 1 < args.Length:
                    indexCache = args[++i];
                    break;
                case "--analyze" when i + 1 < args.Length:
                    analyzePath = args[++i];
                    break;
                case "--analyze-baseline" when i + 1 < args.Length:
                    analyzeBaseline = args[++i];
                    break;
                case "--analyze-candidate" when i + 1 < args.Length:
                    analyzeCandidate = args[++i];
                    break;
                case "--ann-ef" when i + 1 < args.Length:
                    annEfValues.AddRange(ParsePositiveInts(args[++i], "--ann-ef"));
                    break;
                case "--ann-connection" when i + 1 < args.Length:
                    annConnection = args[++i];
                    break;
                case "--expand-density" when i + 1 < args.Length:
                    expandDensities.AddRange(ParseDensities(args[++i]));
                    break;
                case "--expand-ranking" when i + 1 < args.Length:
                    expandRankings.AddRange(ParseRanking(args[++i]));
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
        (ITokenizer tokenizer, string analysisDescription, TokenizerOptions tokenizerOptions) = BuildAnalysis(analyzer, stem);

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
            $"nDCG gains: {(gain == NdcgGain.Linear ? "linear (gain = rel, the convention the published figures use)" : "exponential (2^rel - 1, the library default)")}.");

        if (excludeQueryDocument)
        {
            Console.WriteLine(
                "Excluded: the document whose id equals the query id, which is the convention the " +
                "published ArguAna figure was produced under. Affects only corpora that number " +
                "their queries as documents (ArguAna: 1298 of 1406 test queries); a no-op elsewhere.");
        }

        if (parseQuerySyntax)
        {
            Console.WriteLine(
                "Query syntax ON: \"a phrase\" requires adjacency, term* is a prefix, term~ is fuzzy. " +
                "The published baselines apply none of this to their queries, so a run with it on is " +
                "not comparable with them.");
        }
        else
        {
            Console.WriteLine(
                "Query syntax OFF: each query is literal text, as in the published baselines. A straight " +
                "double quote is a separator here, not a phrase delimiter — on ArguAna that is the " +
                "difference between 9.8% of queries collapsing and none of them doing so.");
        }

        Console.WriteLine();

        foreach (BeirDataset dataset in datasets)
        {
            await RunDatasetAsync(
                dataset, dataBaseDir, topK, limit, tokenizer, tokenizerOptions, analysisDescription, gain,
                excludeQueryDocument, fingerprintOnly, referenceBm25, queryFrequency, scoreRounding,
                saturationConstant, singlePrecision, dense, denseSeq,
                tuned, reranker, rerankCandidates, runPath, jobs, indexCache, parseQuerySyntax,
                querySyntaxReference, analyzePath, analyzeBaseline, analyzeCandidate,
                expandDensities, expandRankings, annEfValues, annConnection);
            Console.WriteLine();
        }

        return 0;
    }

    /// <summary>
    /// Queries in flight by default: enough to keep the machine busy, short of taking every core.
    /// </summary>
    /// <remarks>
    /// One core is left free so a sweep does not make the machine unusable, and the ceiling is 8
    /// because the work is memory-bound rather than compute-bound and more workers stop paying for
    /// themselves well before a large core count. <c>--jobs</c> overrides it, which the sweep script
    /// pins so a recorded run states what it was allowed to use.
    /// </remarks>
    private static int DefaultJobs() => Math.Clamp(Environment.ProcessorCount - 1, 1, 8);

    /// <summary>
    /// Builds the shared tokenizer for a run, and the description of the analysis it applies.
    /// <c>english</c> is the analysis the published BM25 baselines were produced with — Porter
    /// stemming, a 33-word function-word list, and single-character terms kept — and it is the
    /// only setting under which a LexiSharp number is comparable with those published figures.
    /// </summary>
    private static (ITokenizer Tokenizer, string Description, TokenizerOptions Options) BuildAnalysis(string analyzer, bool stem)
    {
        // The published baselines' word segmentation: the Unicode text-segmentation boundaries, which join
        // across eight characters the library's own tokenizer treats as separators, and no diacritic
        // folding. Opt-in and named for the rule it implements, because that rule is a published annex
        // rather than one implementation's choice. It is here to measure what the difference is worth,
        // not to become a second default.
        if (analyzer == "uax29")
        {
            TokenizerOptions boundaries = new()
            {
                Stemmer = new PorterStemmer(),
                RemoveStopWords = true,
                StopWords = StopWords.EnglishFunction,
                KeepSingleCharTerms = true,
                FoldDiacritics = false,
                WordSegmentation = WordSegmentation.UnicodeWordBoundaries,
                // Before the stop word list, not after: `it's` escapes a list tested on `it's` and only
                // then becomes `it`, which is how this index came to hold an `it` the reference does not.
                StripPossessives = true,
                // The one code point .NET's invariant casing and the reference's disagree on, out of 505
                // positions whose lowercase differs. Their mapping is the Unicode *simple* one — U+0130 to
                // U+0069, one character — which is what their index holds: a five-character `celil`.
                FoldTurkishDottedI = true,
            };

            return (
                new Tokenizer(boundaries),
                "measured separator rules: comma and semicolon between digits, colon between letters, " +
                "full stop and apostrophes within one class, underscore and soft hyphen anywhere, " +
                "underscore also at the start of a token; lowercase, no diacritic folding, Porter stemming " +
                "(PorterStemmer), function-word stop list, single-char terms kept, possessives stripped " +
                "before the stop list, U+0130 folded by the simple mapping",
                boundaries);
        }

        TokenizerOptions options = analyzer switch
        {
            "default" when stem => new TokenizerOptions { Stemmer = new PorterStemmer() },
            "default" => TokenizerOptions.Default,
            "porter" => new TokenizerOptions
            {
                Stemmer = new PorterStemmer(),
                RemoveStopWords = true,
            },
            "english" => new TokenizerOptions
            {
                Stemmer = new PorterStemmer(),
                RemoveStopWords = true,
                StopWords = StopWords.EnglishFunction,
                // The reference analysis keeps single-character terms and its stop list keeps "i",
                // so dropping them here would make this index hold terms the reference does not.
                KeepSingleCharTerms = true,
            },
            _ => throw new ArgumentException(
                $"--analyzer expects 'default', 'porter', 'english' or 'uax29', got '{analyzer}'."),
        };

        // Tokenizer.Default is a shared instance built from TokenizerOptions.Default; naming its
        // configuration by value rather than by identity is what makes the printed line trustworthy.
        return (new Tokenizer(options), DescribeAnalysis(options), options);
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
                : "function-word stop list"
            : "stop words kept";

        string singleChar = options.KeepSingleCharTerms ? ", single-char terms kept" : string.Empty;

        return $"lowercase, diacritics stripped, {stemming}, {stopWords}{singleChar}";
    }

    private static async Task RunDatasetAsync(
        BeirDataset dataset, string dataBaseDir, int topK, int? limit, ITokenizer tokenizer, TokenizerOptions tokenizerOptions,
        string analysisDescription, NdcgGain gain, bool excludeQueryDocument, bool fingerprintOnly,
        Bm25Parameters? referenceBm25, bool queryFrequency, bool scoreRounding, bool saturationConstant,
        bool singlePrecision, bool dense, int denseSeq, bool tuned,
        IReranker? reranker, int rerankCandidates, string? runPath, int jobs, string? indexCache,
        bool parseQuerySyntax, bool referenceIndexStatistics,
        string? analyzePath, string analyzeBaseline, string? analyzeCandidate,
        IReadOnlyList<double> expandDensities, IReadOnlyList<ExpansionRanking> expandRankings,
        IReadOnlyList<int> annEfValues, string? annConnection)
    {
        Console.WriteLine($"== {dataset.Name} ==");

        var corpus = await BeirLoader.LoadOrDownloadAsync(dataBaseDir, dataset);

        int testQueries = corpus.TestRelevance.Count;

        Console.WriteLine($"Corpus: {corpus.Documents.Count} documents, {corpus.Queries.Count} queries, {testQueries} test queries with relevance; evaluating {(limit is null ? testQueries : Math.Min(testQueries, limit.Value))}.");

        // The fingerprint comes first, and independently of every score below: it says whether this
        // run indexed the same thing as the reference, which is the question a percentage gap
        // against a published number cannot answer.
        //
        // One index, built once and then read by every config. Tokenizing the corpus was the largest
        // cost in a run before this, and it was being repeated once per row of the table for an
        // index that came out identical each time. With --index-cache the build is repeated once per
        // process instead of once per run, which is what a sweep of eighteen stages needs.
        var index = IndexCache.LoadOrBuild(
            indexCache, dataset, corpus, tokenizer, analysisDescription,
            referenceIndexStatistics ? new IndexOptions(
                // The average is over the EXACT lengths: 969,528 / 8,673 = 111.786925. The reference
                // index stores 108.372651, the mean of its decoded one-byte lengths, and using that
                // instead moves every score by 6e-03 — a hundred times the residual being chased — so
                // its scorer averages the exact lengths while its index stores quantized ones.
                AverageLengthDivisor.NonEmptyDocuments, DocumentLengthQuantization.OneByte)
            : null);
        // Which conventions the run used, on the same footing as the analysis line: a score read
        // without them is not comparable with one read with them.
        Console.WriteLine(
            $"Index conventions — average length over {(index.DocumentLengthQuantization == DocumentLengthQuantization.OneByte ? "quantized lengths" : "exact lengths")}, " +
            $"{(index.LengthDivisor == AverageLengthDivisor.AllDocuments ? "all documents" : "non-empty documents")}, " +
            // Two more rules, because a score read without them is not comparable with one read with
            // them. Between them they were worth 486 of the 487 occurrences by which this index and the
            // reference index used to differ: a possessive removed after the stop word list is too late,
            // because the list is consulted on `it's` rather than on `it`.
            $"{(tokenizerOptions.StripPossessives ? "possessives stripped before the stop list" : "possessives kept")}, " +
            $"{(tokenizerOptions.FoldDiacritics ? "diacritics folded" : "diacritics kept")}, avgdl {index.AverageDocumentLength:F6}.");

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

        if (annEfValues.Count > 0)
            await RunAnnCurveAsync(annEfValues, annConnection, denseVectors, corpus, dataset, dataBaseDir, topK, limit);

        var queryExpansions = new List<(string, ITermExpander)>();

        // Learned from the corpus text as the loader read it — title included in the indexed field
        // but not in the window counts, because PmiTermExpander reads SearchDocument.Text. Stated
        // because it is a real asymmetry between what the graph learns and what it is matched
        // against, not a rounding detail.
        var learningCorpus = Evaluation.BuildDocuments(corpus);

        foreach (double density in expandDensities.Distinct().OrderByDescending(value => value))
        foreach (var ranking in expandRankings.Distinct())
        {
            var options = new PmiTermExpanderOptions { MaxWindowDensity = density, Ranking = ranking };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var learned = PmiTermExpander.LearnFrom(learningCorpus, tokenizer, options);
            sw.Stop();

            string name =
                $"BM25 + query expansion (density<={density.ToString("0.####", CultureInfo.InvariantCulture)}, {ranking})";

            Console.WriteLine($"Learned {name} in {sw.Elapsed.TotalSeconds:0.0}s.");

            queryExpansions.Add((name, learned));
        }

        var (results, tunedDescription) = Evaluation.Run(
            corpus, index, topK, limit, tokenizer, excludeQueryDocument, gain, referenceBm25,
            queryFrequency, scoreRounding, saturationConstant, singlePrecision, denseVectors, tuned, reranker,
            rerankCandidates,
            jobs, parseQuerySyntax, captureOutcomes: analyzePath is not null, queryExpansions: queryExpansions);

        if (analyzePath is not null)
            WriteQueryAnalysis(analyzePath, analyzeBaseline, analyzeCandidate, corpus, index, tokenizer, results, topK, limit,
                excludeQueryDocument, parseQuerySyntax);

        if (runPath is not null)
        {
            WriteRun(runPath, corpus, index, tokenizer, topK, limit, excludeQueryDocument, queryFrequency, scoreRounding,
                saturationConstant, singlePrecision,
                referenceBm25 ?? new Bm25Parameters(0.9, 0.4), parseQuerySyntax);
        }

        PrintTable(results, topK);
        PrintReference(dataset, analysisDescription, gain);

        Console.WriteLine($"BM25 tuned in-sample on the same queries (oracle, not a fair baseline): {tunedDescription}");
        Console.WriteLine(
            "Note: absolute scores depend on the analysis, which is why the run states it. The " +
            "relative ordering of the configs above is meaningful within a run; the comparison to " +
            "the published reference is meaningful only under the analysis the reference used " +
            "(--analyzer english for the published figures).");
    }

    /// <summary>
    /// Writes the BM25 run as a TREC run file, so a recorded number can be read back one query at a
    /// time instead of only in aggregate.
    /// </summary>
    /// <remarks>
    /// The point of the file is attribution: an aggregate says how far off a run is, and a run file
    /// says which queries and which documents. It is written from the index this run built and the
    /// query list the table scored, at <c>--top-k</c> depth, which is why depth is stated in the
    /// console line rather than left implicit. When no <c>--reference-bm25</c> was given, the
    /// retrieval stack's own defaults are used, since that is the operating point a published
    /// figure would have been produced at.
    /// </remarks>
    private static void WriteRun(
        string path, BeirCorpus corpus, InMemoryTextIndex index, ITokenizer tokenizer, int topK,
        int? limit, bool excludeQueryDocument, bool queryFrequency, bool scoreRounding, bool saturationConstant,
        bool singlePrecision, Bm25Parameters bm25, bool parseQuerySyntax)
    {
        List<EvaluatedQuery> queries = Evaluation.BuildQueries(corpus, limit, excludeQueryDocument);
        var engine = new RankedTextSearchEngine(
            index,
            new Bm25Scorer(
                bm25.K1, bm25.B,
                queryFrequency ? QueryTermWeighting.QueryFrequency : QueryTermWeighting.Distinct,
                saturationConstant,
                singlePrecision ? Bm25Arithmetic.SinglePrecision : Bm25Arithmetic.Double),
            tokenizer);

        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);

        using var writer = new StreamWriter(path);

        foreach (EvaluatedQuery evaluated in queries)
        {
            IReadOnlyList<SearchResult> results = engine.Search(
                evaluated.Query.Text,
                new SearchOptions(
                    topK,
                    ExcludedDocumentIds: evaluated.Excluded,
                    ParseQuerySyntax: parseQuerySyntax)
                    // The run file is what a recorded score is read back from, so the rounding the
                    // reference applies before writing one has to be applied here too, or the
                    // comparison is against a number the reference never wrote down.
                    .WithScoreRounding(scoreRounding
                        ? ScoreRounding.FourDecimals
                        : ScoreRounding.None));

            for (int rank = 0; rank < results.Count; rank++)
            {
                writer.WriteLine(
                    $"{evaluated.Query.Id} Q0 {results[rank].DocumentId} {rank + 1} "
                    + $"{results[rank].Score.ToString("R", CultureInfo.InvariantCulture)} LexiSharp");
            }
        }

        Console.WriteLine(
            $"Run file: {Path.GetFullPath(path)} — {queries.Count:N0} queries, BM25 "
            + $"(k1={bm25.K1.ToString("0.##", CultureInfo.InvariantCulture)}, "
            + $"b={bm25.B.ToString("0.##", CultureInfo.InvariantCulture)}), depth {topK}, "
            + $"{(queryFrequency ? "query-term-frequency" : "distinct")} weighting, "
            + $"{(parseQuerySyntax ? "query syntax on" : "query syntax off")}"
            // Stated on the run file itself and not only in the conventions line: when this is on, a
            // score read back from the file is not the scorer's score, and a reader comparing the two
            // has to know which of the two they are holding.
            + $", {(scoreRounding ? "four-decimal score rounding" : "raw scores")}.");
    }

    /// <summary>
    /// Writes the per-query analysis file and prints the mismatch-band summary.
    /// </summary>
    /// <remarks>
    /// The query list is rebuilt through <see cref="Evaluation.BuildQueries"/> rather than taken from
    /// the run, so it is the same list the metrics were computed over. Two copies of that list would
    /// look comparable and not be — the failure mode the reference corpus's own harness already
    /// documents, and the reason <c>--limit</c> and <c>--exclude-query-doc</c> have to travel with it.
    /// </remarks>
    private static void WriteQueryAnalysis(
        string path,
        string baseline,
        string? candidate,
        BeirCorpus corpus,
        InMemoryTextIndex index,
        ITokenizer tokenizer,
        IReadOnlyList<ConfigResult> results,
        int topK,
        int? limit,
        bool excludeQueryDocument,
        bool parseQuerySyntax)
    {
        var analyzed = results.Where(result => result.Outcomes is not null).ToList();

        if (analyzed.Count != results.Count)
        {
            throw new InvalidOperationException(
                $"{results.Count - analyzed.Count} of {results.Count} configurations reported no per-query " +
                "detail, so the analysis would be missing rows rather than shorter.");
        }

        var queries = Evaluation.BuildQueries(corpus, limit, excludeQueryDocument);
        var coverage = QueryAnalysis.Describe(queries, index, corpus, tokenizer);

        var byConfig = analyzed.ToDictionary(result => result.Name, result => result.Outcomes!);

        var report = new QueryAnalysisReport(queries, coverage, byConfig);

        // The named configuration's name is validated before the file is written, so a typo fails on a
        // short console message rather than after producing a file with empty delta columns.
        report.OutcomesOf(baseline);

        QueryAnalysis.Write(path, report, baseline);

        Console.WriteLine();
        Console.WriteLine($"Per-query analysis: {Path.GetFullPath(path)}");
        Console.WriteLine(
            $"  {queries.Count:N0} queries x {byConfig.Count} configurations, measured at depth {MetricDepth.Cutoff} " +
            $"(retrieval depth {topK}{(parseQuerySyntax ? ", query syntax on" : ", query syntax off")}).");
        Console.WriteLine($"  Delta columns are against '{baseline}'.");

        // A candidate is named separately because pairing the baseline with itself would print a table
        // of zeros, which reads as a result and is not one. With no candidate the file still carries
        // every per-query metric; the band table is the one summary that needs a pair.
        if (candidate is not null)
            QueryAnalysis.WriteBandSummary(Console.Out, report, candidate, baseline);
        else
            Console.WriteLine("  Pass --analyze-candidate with a configuration name for the mismatch-band summary.");
    }

    /// <summary>
    /// Measures the pgvector ANN curve: recall of the index against an exact in-process top-k, and
    /// the latency each candidate-list size costs.
    /// </summary>
    /// <remarks>
    /// Needs a live server and the dense cache. The vectors are read from the same cache the
    /// in-memory dense lane uses, so the curve is a property of the index and not of the encoder.
    /// </remarks>
    private static async Task RunAnnCurveAsync(
        IReadOnlyList<int> efValues,
        string? connection,
        DenseVectors? dense,
        BeirCorpus corpus,
        BeirDataset dataset,
        string dataBaseDir,
        int topK,
        int? limit)
    {
        if (connection is null)
        {
            Console.Error.WriteLine("--ann-ef needs --ann-connection (Npgsql connection string).");
            return;
        }

        if (dense is null)
        {
            Console.Error.WriteLine("--ann-ef needs --dense: the curve is measured over the cached embeddings.");
            return;
        }

        var documents = Evaluation.BuildDocuments(corpus);
        var queries = corpus.Queries
            .Where(query => corpus.TestRelevance.ContainsKey(query.Id))
            .OrderBy(query => query.Id, StringComparer.Ordinal)
            .Select(query => (Text: query.Text, Vector: Lookup(dense, query.Text)))
            .ToList();

        // The dense lane keys its vectors by text; a query the cache never saw cannot be measured
        // and must not be measured as a zero.
        static float[] Lookup(DenseVectors vectors, string text)
        {
            if (vectors.QueryEmbeddings.TryGetValue(text, out float[]? vector))
                return vector;

            throw new InvalidOperationException(
                "A query has no cached embedding, so the ANN curve cannot be computed for it.");
        }

        Console.WriteLine();
        Console.WriteLine($"== pgvector ANN curve ({dataset.Name}, {documents.Count} documents, {queries.Count} queries) ==");

        var points = VectorAnnBenchmark.Measure(
            connection, documents, dense.DocumentVectors, queries, efValues, topK);

        Console.WriteLine($"{"ef_search",8}{"recall@" + topK,12}{"latency",16}");
        Console.WriteLine(new string('-', 36));

        foreach (var point in points)
            Console.WriteLine(point.ToString());
    }

    /// <summary>
    /// Parses a comma-separated list of window-density cuts.
    /// </summary>
    /// <remarks>
    /// The value is a share of the corpus's sliding windows, so 0 means no term is ever excluded as
    /// too common and 1 excludes nothing below the whole corpus. Accepting the bare list rather than
    /// a repeated flag keeps a sweep to one argument, and a bad value is refused here rather than
    /// silently becoming a run whose neighbour sets are not what the command line said.
    /// </remarks>
    private static IEnumerable<int> ParsePositiveInts(string value, string flag)
    {
        foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed <= 0)
                throw new ArgumentException($"{flag} expects a comma-separated list of positive integers, got '{part}'.");

            yield return parsed;
        }
    }

    private static IEnumerable<double> ParseDensities(string value)
    {
        foreach (string part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double density)
                || density is < 0 or > 1)
            {
                throw new ArgumentException(
                    $"--expand-density expects a comma-separated list of shares in [0, 1], got '{part}'.");
            }

            yield return density;
        }
    }

    private static IEnumerable<ExpansionRanking> ParseRanking(string value) => value switch
    {
        "count" => [ExpansionRanking.CoOccurrenceCount],
        "pmi" => [ExpansionRanking.PositiveMutualInformation],
        "both" => Enum.GetValues<ExpansionRanking>(),
        _ => throw new ArgumentException(
            $"--expand-ranking expects 'count', 'pmi' or 'both', got '{value}'."),
    };

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

        if (dataset.PublishedNdcg10Ref is { } published)
        {
            Console.WriteLine(
                $"Reference (independent implementation, same flat index, current): BM25 nDCG@10 = " +
                $"{published.ToString("0.0000", CultureInfo.InvariantCulture)} on {dataset.Name}.");
            Console.WriteLine($"  {dataset.PublishedSource}");
        }

        Console.WriteLine($"This run's analysis: {analysisDescription}.");
        Console.WriteLine(
            "  A published BM25 number is only comparable with a run whose analysis, BM25 parameters" +
            " and task conventions match it; a percentage gap against a reference is otherwise a" +
            " statement about the analysis, not about the ranking engine.");

        if (gain == NdcgGain.Exponential && dataset.Graded)
        {
            Console.WriteLine(
                "  nDCG here uses 2^rel - 1. The published figures use gain = rel; pass" +
                " --ndcg-gain linear to compare like with like. On a binary corpus the two are" +
                " identical, so this only matters for a graded one.");
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

        if (dataset.PublishedTotalTerms is not { } expected || dataset.Documents is not { } documents)
            return;

        var reference = new IndexFingerprint(
            documents, dataset.NonEmptyDocuments ?? documents, expected);

        double difference = fingerprint.RelativeDifference(reference);

        Console.WriteLine(
            $"  reference          : {reference.Documents} / {reference.NonEmptyDocuments} / " +
            $"{reference.TotalTerms} terms — difference {difference:P1}");
        Console.WriteLine(
            difference <= FingerprintTolerance
                ? "  -> the two indexes agree to within the tolerance; scores below are comparable with that reference."
                : $"  -> the two indexes differ by more than the {FingerprintTolerance:P0} tolerance: the scores below " +
                  "are NOT comparable with that reference, whatever they read.");
    }

    /// <summary>
    /// How far an index fingerprint may drift from the reference before the run says so. The
    /// measured gap is 1.1–2.8% on these three corpora, so a tighter band would report a
    /// disagreement where none exists.
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
                                words) or 'english' (Porter + the conventional 33-word stop list +
                                single-character terms kept). 'english' is the analysis the
                                published BM25 baselines used, and the only one under which these
                                numbers are comparable with them.
              --exclude-query-doc
                                Exclude the document whose id equals the query id, which is what
                                published figures was produced under. Affects only corpora that number their
                                queries as documents (ArguAna: 1298 of 1406 test queries); a no-op
                                on the others.
              --ndcg-gain <name>
                                nDCG gain convention: 'exponential' (2^rel - 1, the library default)
                                or 'linear' (gain = rel, the convention the published figures were
                                computed with). Identical on binary relevance; pass 'linear' to
                                compare with a published number.
              --query-term-frequency
                                Score a repeated query term once per occurrence, as a reference
                                implementation does,
                                instead of once. Worth +0.052 where a query is a document: on
                                ArguAna (every test query is a whole argument) 0.219 -> 0.271 at
                                k1=0.9/b=0.4, 1406 queries, --analyzer english. On NFCorpus and
                                SciFact, whose queries are short, it changes nothing to four
                                decimals. The linear gain convention gives the same 0.271.
              --reference-bm25 <k1,b>
                                Add a BM25 row at these parameters, e.g. 0.9,0.4 — the defaults the
                                published BM25 baselines were produced with, which is neither of the
                                two rows above. Needed to measure a published operating point.
              --reference-score-rounding
                                Round each returned score onto a ten-thousandth grid and walk down
                                each run of near-equal scores by one millionth a step, which is what
                                the reference does to the scores it writes to a run file. Measured on
                                ArguAna against that run: 97.76% of 14,168 paired scores then match on
                                the raw bits, against 6.18% without it. The rest sit on a rounding
                                boundary, because its scores are single precision and this library's
                                are not. It changes no ranking, so the metric columns are unaffected.
              --omit-saturation-constant
                                Leave the k1+1 out of BM25's numerator, which is the same ranking
                                scaled by 1/(k1+1) but not the same score. The reference's BM25 does
                                not have it, and a score compared against one has to be on the same
                                scale or it is off by the factor before any rounding is discussed.
              --single-precision-bm25
                                Compute each term's contribution in single precision and round the
                                total once, with the length normalisation read from a single-precision
                                reciprocal table instead of evaluated per document. Same ranking, one
                                fewer significant figure, and it is the figure the reference has, so this
                                is what a raw-score comparison needs.
              --jobs <n>          Queries scored concurrently, so a run uses more than one core (default: one
                                fewer than the core count, capped at 8). The totals are summed in
                                query order either way, so the reported figures do not depend on it.
              --index-cache <file>
                                Keep the built index beside the corpus and reload it next time,
                                instead of tokenizing the corpus once per process. Refuses a cache
                                written under a different analysis; rebuilds when the corpus file has
                                changed since.
              --run <file>      Write the BM25 run as a TREC run file at --top-k depth, so a recorded
                                number can be read back one query at a time instead of only in
                                aggregate. Uses the retrieval stack's defaults (k1=0.9, b=0.4) when
                                --reference-bm25 is not given.
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
              --ann-ef <list>  Measure the pgvector HNSW candidate-list curve over these ef_search
                                values (comma-separated, e.g. 10,40,160,320): the recall of the
                                index against an exact in-process top-k, and the latency each size
                                costs. Needs --dense (it reads the cached embeddings) and
                                --ann-connection. One table is dropped and rebuilt per value.
              --ann-connection <s>
                                Npgsql connection string for --ann-ef, e.g.
                                "Host=localhost;Port=5432;Username=...;Password=...;Database=...".
                                The target database needs the vector extension.
              --analyze <file>  Write one row per query per configuration: the query's lexical coverage
                                of its own judged documents, the metrics, and the per-query deltas
                                against --analyze-baseline. Off by default — capturing a ranking per
                                query per configuration is real memory on the larger corpora.
              --analyze-baseline <name>
                                Configuration the delta columns are measured against. Names are the
                                table's display names, in full, e.g. "BM25 (k1=1.5, b=0.75)". An
                                unknown name lists the ones the run has.
              --analyze-candidate <name>
                                Also print the mean deltas per mismatch band, this configuration
                                against the baseline. Omitted, no band table: pairing the baseline
                                with itself would print zeros that read as a result.
              --expand-density <shares>
                                Add a query-side expansion row per window-density cut, e.g. 0.05,0.1,
                                along with its --analyze rows if --analyze is set. The share is the
                                fraction of the corpus's sliding windows a term may appear in and
                                still be a neighbour; 0 keeps every term, 1 excludes only terms in
                                every window. The expander is learned from the corpus text of this
                                run, matching the tokenizer and analysis in effect.
              --expand-ranking <stat>
                                Which statistic orders an input term's candidate neighbours before
                                the budget is applied: 'count' (co-occurrence count, the classic
                                bias) or 'pmi' (positive mutual information). 'both' adds one row
                                per statistic.
              --help, -h        Show this help.
            """);
    }
}