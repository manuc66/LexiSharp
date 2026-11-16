using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using LexiSharp.Core;
using LexiSharp.Eval;
using LexiSharp.Postgres;

namespace LexiSharp.Benchmarks;

/// <summary>
/// What a search costs on the stock-PostgreSQL text engines: index build, corpus-statistics build,
/// and the query latency distribution — <c>ts_rank_cd</c> against the portable BM25 at several
/// candidate-pool sizes.
/// </summary>
/// <remarks>
/// <para>
/// The evaluation harness deliberately reports no latency: its number would be dominated by
/// whatever connection shape its lane happens to have, and a metric table that mixes quality with a
/// deployment detail invites the wrong comparison. This lane is the other question, asked on
/// purpose. It measures one process, one thread, one query at a time, against a server on the same
/// host, with the engine's own pooled <c>NpgsqlDataSource</c> warmed — the shape a request path
/// has, not a connection handshake per call.
/// </para>
/// <para>
/// <b>What the pool costs, in milliseconds.</b> The portable BM25 selects its candidates with
/// <c>ts_rank_cd</c> and re-ranks the top <c>CandidatePool</c>; the pool is a recall bound, and the
/// quality page states what it costs in nDCG. Here is the other side of that choice: the same
/// corpus, the same queries, at pool 1,000 / 5,000 / unbounded. A pool that looks cheap in nDCG
/// and expensive here (or the reverse) is why both measurements exist.
/// </para>
/// <para>
/// <b>The statistics build is not a search.</b> <c>ts_stat</c> walks the table, so the first search
/// after a write pays for the whole corpus. It is measured by calling
/// <see cref="PostgresBm25SearchEngine.RefreshStatistics"/> explicitly, printed in its own column,
/// and excluded from the query distribution — a caller that writes between searches pays it, and
/// should see the number rather than infer it from a p99.
/// </para>
/// </remarks>
internal static class PostgresLane
{
    /// <summary>Where the BEIR corpora live, relative to the repository root and to any directory above the assembly.</summary>
    private const string DefaultDataDirectory = "bench/LexiSharp.Eval/data";

    private static readonly string[] DefaultDatasets = ["nfcorpus", "scifact"];

    private static long _sink;

    public static void Run(string[] args)
    {
        Options options = Parse(args);

        PrintHeader(options);

        var dropper = new List<(string Table, string Connection)>();

        try
        {
            foreach (string dataset in options.Datasets)
                Measure(dataset, options, dropper);
        }
        finally
        {
            if (options.Keep)
            {
                foreach (var (table, _) in dropper)
                    Console.WriteLine($"kept      {table} (not dropped)");
            }
            else
            {
                foreach (var (table, connection) in dropper)
                {
                    using var engine = new PostgresTextSearchEngine(
                        connection,
                        new PostgresIndexOptions { Table = table },
                        autoCreateSchema: false);

                    engine.DropSchema();
                }
            }
        }
    }

    private static void Measure(string datasetName, Options options, List<(string, string)> dropper)
    {
        BeirDataset dataset = BeirDataset.Resolve(datasetName);

        BeirCorpus corpus = BeirLoader.LoadOrDownloadAsync(options.DataDirectory, dataset)
            .GetAwaiter()
            .GetResult();

        IReadOnlyList<SearchDocument> documents = Evaluation.BuildDocuments(corpus);

        string[] queries = Evaluation
            .BuildQueries(corpus, limit: null, excludeQueryDocument: false)
            .Select(query => query.Query.Text)
            .ToArray();

        string table = $"lexisharp_bench_pg_{dataset.Name}_{Guid.NewGuid().ToString("N")[..8]}";

        dropper.Add((table, options.Connection));

        var indexOptions = new PostgresIndexOptions { Table = table };

        // Indexed once, by one engine, and read by every row: the table and the analysis are the
        // same for all of them, so rebuilding per row would measure the rebuild.
        var indexing = Stopwatch.StartNew();

        using (var writer = new PostgresTextSearchEngine(options.Connection, indexOptions))
        {
            writer.Index(documents);

            // A read that touches the index, so the timing is not the first plan the server compiles.
            _sink += writer.Search(queries[0], new SearchOptions(Limit: options.Limit)).Count;
        }

        indexing.Stop();

        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"== {dataset.Name} — {documents.Count:N0} documents, {queries.Length:N0} queries, "
                + $"Limit {options.Limit}, {options.Passes} pass(es)"));
        Console.WriteLine(
            $"index     {indexing.Elapsed.TotalMilliseconds:N0} ms (one table, shared by every row)");
        Console.WriteLine();
        Console.WriteLine("  engine                     stats ms      p50      p95      p99     mean    q/s");

        var rows = Rows(options, table).ToList();
        var engines = new List<ITextSearchEngine>(rows.Count);
        var statistics = new List<double?>(rows.Count);
        var samples = new List<double>[rows.Count];

        try
        {
            for (int i = 0; i < rows.Count; i++)
            {
                engines.Add(rows[i].Engine());
                statistics.Add(rows[i].Statistics());
                samples[i] = new List<double>(queries.Length * options.Passes);
            }

            // Warm-up: every engine's connection pool, the server's plan cache and the JIT. Nothing
            // here is reported, and the passes below start from a process that has already answered
            // these queries.
            foreach (string query in queries.Take(Math.Min(queries.Length, 50)))
            {
                for (int i = 0; i < engines.Count; i++)
                    _sink += engines[i].Search(query, new SearchOptions(Limit: options.Limit)).Count;
            }

            // Interleaved rather than row after row: a row measured alone pays for whatever the
            // previous row warmed (buffer cache, pool, tiered JIT) or left behind (a gen2 GC), and
            // a pool-size sweep is exactly where that reads as a ranking.
            for (int pass = 0; pass < options.Passes; pass++)
            {
                foreach (string query in queries)
                {
                    for (int i = 0; i < engines.Count; i++)
                    {
                        long start = Stopwatch.GetTimestamp();
                        _sink += engines[i].Search(query, new SearchOptions(Limit: options.Limit)).Count;
                        samples[i].Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    }
                }
            }

            for (int i = 0; i < rows.Count; i++)
            {
                samples[i].Sort();

                double mean = samples[i].Average();
                double qps = mean > 0 ? 1_000 / mean : 0;

                Console.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"  {rows[i].Name,-25} {Format(statistics[i]),8} {Percentile(samples[i], 50.0),8:0.00} "
                        + $"{Percentile(samples[i], 95.0),8:0.00} {Percentile(samples[i], 99.0),8:0.00} "
                        + $"{mean,8:0.00} {qps,6:N0}"));
            }
        }
        finally
        {
            foreach (ITextSearchEngine engine in engines)
                (engine as IDisposable)?.Dispose();
        }

        Console.WriteLine();
    }

    /// <summary>
    /// The rows measured on one table, in the order a reader decides with them: the engine that was
    /// there before, the mode that changes its candidate set, then BM25 at increasing pool sizes.
    /// </summary>
    private static IEnumerable<Row> Rows(Options options, string table) =>
    [
        new(
            "ts_rank_cd (all terms)",
            () => new PostgresTextSearchEngine(
                options.Connection,
                new PostgresIndexOptions { Table = table, MatchMode = PostgresMatchMode.AllTerms },
                autoCreateSchema: false),
            () => null),
        new(
            "ts_rank_cd (any term)",
            () => new PostgresTextSearchEngine(
                options.Connection,
                new PostgresIndexOptions { Table = table, MatchMode = PostgresMatchMode.AnyTerm },
                autoCreateSchema: false),
            () => null),
        new(
            "bm25 pool 1,000",
            () => new PostgresBm25SearchEngine(
                options.Connection,
                new PostgresBm25Options { Table = table, CandidatePool = 1000 },
                autoCreateSchema: false),
            () => Statistics(() => new PostgresBm25SearchEngine(
                options.Connection,
                new PostgresBm25Options { Table = table, CandidatePool = 1000 },
                autoCreateSchema: false))),
        new(
            "bm25 pool 5,000",
            () => new PostgresBm25SearchEngine(
                options.Connection,
                new PostgresBm25Options { Table = table, CandidatePool = 5000 },
                autoCreateSchema: false),
            () => Statistics(() => new PostgresBm25SearchEngine(
                options.Connection,
                new PostgresBm25Options { Table = table, CandidatePool = 5000 },
                autoCreateSchema: false))),
        new(
            "bm25 pool unbounded",
            () => new PostgresBm25SearchEngine(
                options.Connection,
                new PostgresBm25Options { Table = table, CandidatePool = 0 },
                autoCreateSchema: false),
            () => Statistics(() => new PostgresBm25SearchEngine(
                options.Connection,
                new PostgresBm25Options { Table = table, CandidatePool = 0 },
                autoCreateSchema: false))),
    ];

    /// <summary>
    /// One statistics build, on its own engine: the snapshot belongs to the instance, so timing it
    /// on the instance the row then searches with would start that engine from a warm snapshot and
    /// hide exactly the cost this column exists for.
    /// </summary>
    private static double Statistics(Func<PostgresBm25SearchEngine> create)
    {
        using PostgresBm25SearchEngine engine = create();
        var timer = Stopwatch.StartNew();
        _sink += engine.RefreshStatistics();
        timer.Stop();
        return timer.Elapsed.TotalMilliseconds;
    }

    private static string Format(double? value) =>
        value is { } milliseconds ? milliseconds.ToString("N0", CultureInfo.InvariantCulture) : "—";

    /// <summary>The percentile of a sorted sample, nearest-rank: no interpolation and no outlier removal.</summary>
    private static double Percentile(List<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
            return 0;

        int rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }

    private static void PrintHeader(Options options)
    {
        Console.WriteLine("LexiSharp PostgreSQL lane — index, statistics and query latency on stock PostgreSQL");
        Console.WriteLine($"host      {RuntimeInformation.OSDescription} / {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"cpu       {Environment.ProcessorCount} logical processors");
        Console.WriteLine($"runtime   {RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"server    {options.Connection}");
        Console.WriteLine(
            "shape     one process, one thread, one query at a time, the engine's pooled data source warmed; "
            + "a search is a page of 10");
        Console.WriteLine($"datasets  {string.Join(", ", options.Datasets)}");
        Console.WriteLine();
    }

    private static Options Parse(string[] args)
    {
        var datasets = new List<string>();
        string? connection = null;
        string? data = null;
        int limit = 10;
        int passes = 3;
        bool keep = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dataset" when i + 1 < args.Length:
                    datasets.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--connection" when i + 1 < args.Length:
                    connection = args[++i];
                    break;
                case "--data" when i + 1 < args.Length:
                    data = args[++i];
                    break;
                case "--limit" when i + 1 < args.Length:
                    limit = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--passes" when i + 1 < args.Length:
                    passes = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--keep":
                    keep = true;
                    break;
                default:
                    throw new ArgumentException(
                        $"Unrecognised postgres argument '{args[i]}'. Usage: postgres --connection <cs> "
                        + "[--dataset nfcorpus,scifact] [--data <dir>] [--limit 10] [--passes 3] [--keep].");
            }
        }

        if (string.IsNullOrWhiteSpace(connection))
            throw new ArgumentException("postgres needs --connection <Npgsql connection string>.");

        if (limit <= 0 || passes <= 0)
            throw new ArgumentException("--limit and --passes must be positive.");

        if (datasets.Count == 0)
            datasets.AddRange(DefaultDatasets);

        foreach (string dataset in datasets)
            _ = BeirDataset.Resolve(dataset);

        return new Options([.. datasets], connection, ResolveDataDirectory(data), limit, passes, keep);
    }

    /// <summary>
    /// The directory holding the BEIR corpora. Defaulted against the working directory first and then
    /// against the tree above the assembly, so the documented command runs from the repository root
    /// and from a `bin/Release` directory alike.
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

    private sealed record Options(
        IReadOnlyList<string> Datasets,
        string Connection,
        string DataDirectory,
        int Limit,
        int Passes,
        bool Keep);

    private sealed record Row(string Name, Func<ITextSearchEngine> Engine, Func<double?> Statistics);
}
