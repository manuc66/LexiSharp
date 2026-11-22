using System.Globalization;
using System.Runtime.InteropServices;
using LexiSharp.Core;
using LexiSharp.Eval;
using LexiSharp.Indexing;
using LexiSharp.Ranking;

namespace LexiSharp.Benchmarks;

/// <summary>
/// How much of a scorer's theoretical upper bound a real query's best document uses.
/// </summary>
/// <remarks>
/// <para>
/// The bound is valid but not necessarily tight: it sums each term's independent maximum, so it
/// bounds a document that achieved every term's best value at once, and no such document need exist.
/// The number measured here is <c>oracleMax / UpperBound</c>, the share of the bound the corpus
/// actually reaches. A value near one means the divisor is close to the largest score the query can
/// produce; a value near zero means it mostly rescales a query without saying much about its best
/// document.
/// </para>
/// <para>
/// <c>oracleMax</c> is read by searching every matching document (<c>Limit = document count</c>) and
/// taking the largest score. That makes the query cost O(corpus) per row, which is why this is a
/// lane and not a test: the real search only touches candidates.
/// </para>
/// <para>
/// The queries are the corpus's judged test queries, searched as literal text
/// (<see cref="SearchOptions.ParseQuerySyntax"/> off), because a bound is declined for syntax. A
/// second block asks the same question of deliberately wide queries — the most frequent vocabulary
/// terms — where the bound has the most room to be loose.
/// </para>
/// </remarks>
internal static class BoundTightnessLane
{
    private const string DefaultDataDirectory = "bench/LexiSharp.Eval/data";

    private static readonly string[] DefaultDatasets = ["nfcorpus", "scifact"];

    private static readonly (string Name, Func<ITextScorer> Create)[] Scorers =
    [
        ("BM25", () => new Bm25Scorer()),
        ("BM25+", () => new Bm25PlusScorer()),
        ("BM25L", () => new Bm25LScorer()),
        ("TF-IDF", () => new TfIdfScorer()),
    ];

    private static readonly int[] WideWidths = [5, 10, 25, 50];

    private static long _sink;

    public static void Run(string[] args)
    {
        Options options = Parse(args);

        Console.WriteLine("LexiSharp bound-tightness lane — oracleMax / theoretical upper bound");
        Console.WriteLine($"host      {RuntimeInformation.OSDescription} / {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"cpu       {Environment.ProcessorCount} logical processors");
        Console.WriteLine(
            "metric    oracleMax is the highest score over every matching document; " +
            "tightness = oracleMax / UpperBound, so 1 means the bound is reached");

        foreach (string dataset in options.Datasets)
            Measure(dataset, options);
    }

    private static void Measure(string datasetName, Options options)
    {
        BeirDataset dataset = BeirDataset.All.FirstOrDefault(
                candidate => string.Equals(candidate.Name, datasetName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException(
                $"Unknown dataset '{datasetName}'. Known: " +
                $"{string.Join(", ", BeirDataset.All.Select(d => d.Name))}.");

        BeirCorpus corpus = BeirLoader
            .LoadOrDownloadAsync(options.DataDirectory, dataset)
            .GetAwaiter().GetResult();

        IReadOnlyList<SearchDocument> documents = Evaluation.BuildDocuments(corpus);
        var index = new InMemoryTextIndex();
        var builder = new RankedTextSearchEngine(index, new Bm25Scorer());

        builder.Index(documents);

        List<EvaluatedQuery> queries = Evaluation.BuildQueries(corpus, options.QueryLimit, excludeQueryDocument: false);
        var searchOptions = new SearchOptions(Limit: Math.Max(index.Count, 1), ParseQuerySyntax: false);

        Console.WriteLine();
        Console.WriteLine($"--- {dataset.Name} ---");
        Console.WriteLine(
            $"documents {Number(index.Count)}, vocabulary {Number(index.VocabularySize)}, " +
            $"judged queries {Number(queries.Count)}");

        Console.WriteLine("scorer     queries  bounded  empty  violations  p50    p90    min    mean   <0.5  <0.8");

        foreach (var (name, create) in Scorers)
        {
            var engine = new RankedTextSearchEngine(index, create());
            var tightness = new List<double>(queries.Count);
            int unbounded = 0;
            int empty = 0;
            int violations = 0;
            double worstViolation = 0;

            foreach (EvaluatedQuery query in queries)
            {
                if (!engine.TryGetQueryBound(query.Query.Text, searchOptions, out QueryScoreBound bound) ||
                    bound.UpperBound <= 0)
                {
                    unbounded++;
                    continue;
                }

                IReadOnlyList<SearchResult> results = engine.Search(query.Query.Text, searchOptions);
                double oracle = 0;

                for (int i = 0; i < results.Count; i++)
                {
                    if (results[i].Score > oracle)
                        oracle = results[i].Score;
                }

                if (oracle <= 0)
                {
                    empty++;
                    continue;
                }

                if (oracle > bound.UpperBound)
                {
                    violations++;
                    worstViolation = Math.Max(worstViolation, oracle / bound.UpperBound);

                    if (violations <= 3)
                    {
                        Console.WriteLine(
                            $"  ABOVE BOUND {name} {query.Query.Id}: oracle {oracle.ToString("N3", CultureInfo.InvariantCulture)} " +
                            $"> bound {bound.UpperBound.ToString("N3", CultureInfo.InvariantCulture)} " +
                            $"({Percent(oracle / bound.UpperBound)}) query '{query.Query.Text}'");
                    }

                    continue;
                }

                tightness.Add(oracle / bound.UpperBound);
            }

            PrintRow(name, queries.Count, unbounded, empty, violations, worstViolation, tightness);
        }

        if (options.Wide)
            MeasureWide(index, searchOptions);
    }

    /// <summary>
    /// The same ratio for queries built from the most frequent terms, where each term's independent
    /// maximum is hardest to reach together.
    /// </summary>
    private static void MeasureWide(InMemoryTextIndex index, SearchOptions searchOptions)
    {
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        string[] vocabulary = [.. index.Vocabulary
            .OrderByDescending(index.DocumentFrequency)
            .ThenBy(term => term, StringComparer.Ordinal)];

        Console.WriteLine();
        Console.WriteLine("wide queries (BM25; the most frequent vocabulary terms, one query per width)");
        Console.WriteLine("terms  matches  tightness");

        foreach (int width in WideWidths)
        {
            if (width > vocabulary.Length)
                break;

            string query = string.Join(' ', vocabulary.Take(width));
            IReadOnlyList<SearchResult> results = engine.Search(query, searchOptions);

            if (!engine.TryGetQueryBound(query, searchOptions, out QueryScoreBound bound) || bound.UpperBound <= 0)
            {
                Console.WriteLine($"{width,5}  {Number(results.Count),7}  declined");
                continue;
            }

            double oracle = 0;

            for (int i = 0; i < results.Count; i++)
            {
                if (results[i].Score > oracle)
                    oracle = results[i].Score;
            }

            _sink += results.Count;

            string ratio = oracle > bound.UpperBound
                ? $"ABOVE BOUND ({Percent(oracle / bound.UpperBound)})"
                : Percent(oracle / bound.UpperBound);

            Console.WriteLine($"{width,5}  {Number(results.Count),7}  {ratio}");
        }
    }

    private static void PrintRow(
        string name,
        int queries,
        int unbounded,
        int empty,
        int violations,
        double worstViolation,
        List<double> tightness)
    {
        if (tightness.Count == 0)
        {
            Console.WriteLine(
                $"{name,-10} {Number(queries),7}  {Number(unbounded),7}  {Number(empty),6}  " +
                $"{Number(violations),10}  (no measurable query)");
            return;
        }

        tightness.Sort();

        double p50 = Quantile(tightness, 0.50);
        double p90 = Quantile(tightness, 0.90);
        double mean = tightness.Average();
        int belowHalf = tightness.Count(value => value < 0.5);
        int belowEight = tightness.Count(value => value < 0.8);

        string violationNote = violations > 0
            ? $"  worst {Percent(worstViolation)}"
            : string.Empty;

        Console.WriteLine(
            $"{name,-10} {Number(queries),7}  {Number(tightness.Count + empty + violations),7}  " +
            $"{Number(empty),6}  {Number(violations),10}  " +
            $"{Percent(p50),6} {Percent(p90),6} {Percent(tightness[0]),6} {Percent(mean),6}  " +
            $"{Number(belowHalf),5}  {Number(belowEight),5}{violationNote}");
    }

    private static double Quantile(List<double> sorted, double fraction)
    {
        // Nearest-rank on a sorted list: the smallest value at or above the requested share.
        int index = (int)Math.Ceiling(fraction * sorted.Count) - 1;

        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    private static Options Parse(string[] args)
    {
        var datasets = new List<string>();
        string dataDirectory = ResolveDataDirectory(null);
        int? queryLimit = null;
        bool wide = true;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data" when i + 1 < args.Length:
                    dataDirectory = ResolveDataDirectory(args[++i]);
                    break;

                case "--query-limit" when i + 1 < args.Length:
                    queryLimit = int.Parse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture);
                    break;

                case "--no-wide":
                    wide = false;
                    break;

                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException($"bound-tightness does not know '{args[i]}'.");

                    datasets.Add(args[i]);
                    break;
            }
        }

        return new Options(
            datasets.Count == 0 ? DefaultDatasets : [.. datasets],
            dataDirectory,
            queryLimit,
            wide);
    }

    /// <summary>
    /// The directory holding the BEIR corpora, resolved against the working directory and then
    /// against the tree above the assembly, so the command runs from the repository root and from a
    /// <c>bin/Release</c> directory alike.
    /// </summary>
    private static string ResolveDataDirectory(string? given)
    {
        if (given is not null)
            return given;

        foreach (string candidate in Candidates())
        {
            if (Directory.Exists(candidate))
                return candidate;
        }

        return DefaultDataDirectory;

        static IEnumerable<string> Candidates()
        {
            yield return DefaultDataDirectory;

            for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                yield return Path.Combine(directory.FullName, DefaultDataDirectory);
        }
    }

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Percent(double value) =>
        (value * 100).ToString("N1", CultureInfo.InvariantCulture) + "%";

    private sealed record Options(
        IReadOnlyList<string> Datasets,
        string DataDirectory,
        int? QueryLimit,
        bool Wide);
}
