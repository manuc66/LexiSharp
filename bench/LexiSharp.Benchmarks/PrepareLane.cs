using System.Diagnostics;
using System.Globalization;
using LexiSharp.Core;
using LexiSharp.Eval;
using LexiSharp.Postgres;

namespace LexiSharp.Benchmarks;

/// <summary>
/// What a server-side prepared statement buys on the stock-PostgreSQL text engine: the same corpus,
/// the same queries, one connection that parses every command and one that prepares it after its
/// first use.
/// </summary>
/// <remarks>
/// <para>
/// Parameter binding and prepared statements are not the same thing. Npgsql binds every value as a
/// parameter either way; without <c>Max Auto Prepare</c> the statement is unnamed and parsed on each
/// execution. With it, the same text is parsed once and the server may reuse a plan — and, after
/// five executions, may switch to a generic one that cannot see the actual parameter values, which
/// is the risk this lane has to price rather than assume.
/// </para>
/// <para>
/// The two configurations must return the same ranking, ids and scores, or the timing is comparing
/// two different searches. That is checked on the warm-up queries and the run stops if it fails.
/// </para>
/// <para>
/// <b>What it measures.</b> One process, one thread, one query at a time, against a server on the
/// same host, one corpus and one query shape (no filters, one column, <c>Limit: 10</c>). It is a
/// statement about this shape, not about every SQL backend: a filter changes the statement text and
/// so the cache's hit rate, and the vector and sparse engines bind a different statement.
/// </para>
/// </remarks>
internal static class PrepareLane
{
    private const string DefaultDataDirectory = "bench/LexiSharp.Eval/data";

    private static long _sink;

    public static void Run(string[] args)
    {
        Options options = Parse(args);

        Console.WriteLine($"server    {Redact(options.Connection)}");
        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"  mode       p50      p90      p99     mean    (ms, {options.Passes} interleaved passes)"));
        Console.WriteLine();

        foreach (string datasetName in options.Datasets)
            Measure(datasetName, options);
    }

    /// <summary>The connection string a lane row uses: auto-prepared at <paramref name="autoPrepare"/>, or not at all.</summary>
    internal static string ConnectionFor(string connection, int autoPrepare) =>
        autoPrepare <= 0
            ? connection
            : connection + $";Max Auto Prepare={autoPrepare};Auto Prepare Min Usages=1";

    private static void Measure(string datasetName, Options options)
    {
        BeirDataset dataset = BeirDataset.Resolve(datasetName);

        BeirCorpus corpus = BeirLoader.LoadOrDownloadAsync(options.DataDirectory, dataset)
            .GetAwaiter()
            .GetResult();

        IReadOnlyList<SearchDocument> documents = Evaluation.BuildDocuments(corpus);

        string[] queries = Evaluation
            .BuildQueries(corpus, limit: null, excludeQueryDocument: false)
            .Select(query => query.Query.Text)
            .Take(options.Queries)
            .ToArray();

        string suffix = Guid.NewGuid().ToString("N")[..8];
        string plainTable = $"lexisharp_bench_plain_{dataset.Name}_{suffix}";
        string preparedTable = $"lexisharp_bench_prepared_{dataset.Name}_{suffix}";

        string plainConnection = ConnectionFor(options.Connection, 0);
        string preparedConnection = ConnectionFor(options.Connection, options.AutoPrepare);

        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"== {dataset.Name} — {documents.Count:N0} documents, {queries.Length:N0} queries, "
                + $"Limit {options.Limit}, Max Auto Prepare 0 vs {options.AutoPrepare}"));

        using var plain = new PostgresTextSearchEngine(
            plainConnection, new PostgresIndexOptions { Table = plainTable });
        using var prepared = new PostgresTextSearchEngine(
            preparedConnection, new PostgresIndexOptions { Table = preparedTable });

        try
        {
            var indexing = Stopwatch.StartNew();
            plain.Index(documents);
            prepared.Index(documents);
            indexing.Stop();

            Console.WriteLine(
                string.Create(CultureInfo.InvariantCulture, $"index     {indexing.Elapsed.TotalMilliseconds:N0} ms for both tables"));

            int warm = Math.Min(queries.Length, 50);

            for (int i = 0; i < warm; i++)
            {
                var left = plain.Search(queries[i], new SearchOptions(Limit: options.Limit));
                var right = prepared.Search(queries[i], new SearchOptions(Limit: options.Limit));

                if (!SameRanking(left, right))
                {
                    throw new InvalidOperationException(
                        $"The two configurations disagree on '{queries[i]}': the timing would compare "
                        + "two different searches.");
                }

                _sink += left.Count;
            }

            var samples = new List<double>[2] { new(queries.Length * options.Passes), new(queries.Length * options.Passes) };

            for (int pass = 0; pass < options.Passes; pass++)
            {
                foreach (string query in queries)
                {
                    for (int side = 0; side < 2; side++)
                    {
                        var engine = side == 0 ? plain : prepared;

                        long start = Stopwatch.GetTimestamp();
                        _sink += engine.Search(query, new SearchOptions(Limit: options.Limit)).Count;
                        samples[side].Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    }
                }
            }

            foreach (var (label, values) in new[] { ("plain", samples[0]), ("prepared", samples[1]) })
            {
                values.Sort();

                Console.WriteLine(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"  {label,-10} {Percentile(values, 50),8:F3} {Percentile(values, 90),8:F3} "
                        + $"{Percentile(values, 99),8:F3} {values.Average(),8:F3}"));
            }

            double plainMedian = Percentile(samples[0], 50);
            double preparedMedian = Percentile(samples[1], 50);

            Console.WriteLine(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"  prepared/plain on p50: {(preparedMedian / plainMedian - 1) * 100:+0.0;-0.0;0.0} %"));
            Console.WriteLine();
        }
        finally
        {
            plain.DropSchema();
            prepared.DropSchema();
        }
    }

    private static bool SameRanking(IReadOnlyList<SearchResult> left, IReadOnlyList<SearchResult> right)
    {
        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].DocumentId, right[i].DocumentId, StringComparison.Ordinal)
                || left[i].Score != right[i].Score)
            {
                return false;
            }
        }

        return true;
    }

    private static double Percentile(List<double> sorted, int percentile)
    {
        if (sorted.Count == 0)
            return double.NaN;

        int index = (int)Math.Ceiling(sorted.Count * percentile / 100.0) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    private static string Redact(string connection)
    {
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connection)
        {
            Password = string.IsNullOrEmpty(connection) ? null : "***",
        };

        return builder.ConnectionString;
    }

    private static Options Parse(string[] args)
    {
        var datasets = new List<string>();
        string? connection = null;
        string? data = null;
        int limit = 10;
        int passes = 4;
        int queries = 300;
        int autoPrepare = 20;

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

                case "--queries" when i + 1 < args.Length:
                    queries = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;

                case "--auto-prepare" when i + 1 < args.Length:
                    autoPrepare = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;

                default:
                    throw new ArgumentException(
                        $"Unrecognised prepare argument '{args[i]}'. Usage: prepare --connection <cs> "
                        + "[--dataset nfcorpus,scifact] [--data <dir>] [--limit 10] [--passes 4] "
                        + "[--queries 300] [--auto-prepare 20]");
            }
        }

        if (string.IsNullOrWhiteSpace(connection))
            throw new ArgumentException("prepare needs --connection <Npgsql connection string>.");

        if (datasets.Count == 0)
            datasets.Add("nfcorpus");

        return new Options(
            [.. datasets],
            connection,
            ResolveDataDirectory(data),
            limit,
            passes,
            queries,
            autoPrepare);
    }

    private static string ResolveDataDirectory(string? data)
    {
        if (!string.IsNullOrWhiteSpace(data))
            return data;

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, DefaultDataDirectory);

            if (Directory.Exists(candidate))
                return candidate;
        }

        return DefaultDataDirectory;
    }

    private sealed record Options(
        IReadOnlyList<string> Datasets,
        string Connection,
        string DataDirectory,
        int Limit,
        int Passes,
        int Queries,
        int AutoPrepare);
}
