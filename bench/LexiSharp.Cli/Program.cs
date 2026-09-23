using System.Globalization;
using System.Text.Json;
using LexiSharp.Benchmarking;
using LexiSharp.Core;
using LexiSharp.Ranking;
using LexiSharp.Sources;

namespace LexiSharp.Cli;

public static class Program
{
    private const string Usage =
        "Usage: lexisharp benchmark <corpus-dir> --queries <file> --qrels <file> [options]\n"
        + "  --queries <file>    Labeled queries: a JSON object {\"id\": \"text\", ...} or a TSV 'id\\ttext'\n"
        + "  --qrels <file>       Relevance judgments: TSV 'qid\\tdocid[\\tgrade]', one per line ('#' comments)\n"
        + "  --top-k <n>          Retrieval depth for every metric (default: 10)\n"
        + "  --limit <n>          Cap the number of queries evaluated (default: all)\n"
        + "  --configs <list>     Comma-separated: bm25, bm25-tuned, tfidf, ql, hybrid (default: bm25,tfidf,ql,hybrid,bm25-tuned)\n"
        + "  --json <path>        Write the results as JSON to this file\n"
        + "  --help, -h           Show this help";

    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "benchmark")
        {
            args = args[1..];
        }

        string? corpusDir = null;
        string? queriesPath = null;
        string? qrelsPath = null;
        string? jsonPath = null;
        int topK = 10;
        int? limit = null;
        string[] configNames = ["bm25", "tfidf", "ql", "hybrid", "bm25-tuned"];

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--queries" when i + 1 < args.Length:
                    queriesPath = args[++i];
                    break;
                case "--qrels" when i + 1 < args.Length:
                    qrelsPath = args[++i];
                    break;
                case "--top-k" when i + 1 < args.Length:
                    topK = ParsePositive(args[++i], "--top-k");
                    break;
                case "--limit" when i + 1 < args.Length:
                    limit = ParsePositive(args[++i], "--limit");
                    break;
                case "--configs" when i + 1 < args.Length:
                    configNames = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
                case "--json" when i + 1 < args.Length:
                    jsonPath = args[++i];
                    break;
                case "--help":
                case "-h":
                    Console.WriteLine(Usage);
                    return 0;
                default:
                    if (corpusDir is null)
                        corpusDir = args[i];
                    else
                    {
                        Console.Error.WriteLine($"Unknown argument: {args[i]}");
                        Console.Error.WriteLine(Usage);
                        return 2;
                    }
                    break;
            }
        }

        if (corpusDir is null || queriesPath is null || qrelsPath is null)
        {
            Console.Error.WriteLine("corpus-dir, --queries and --qrels are required.");
            Console.Error.WriteLine(Usage);
            return 2;
        }

        try
        {
            return Run(corpusDir, queriesPath, qrelsPath, topK, limit, configNames, jsonPath);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            return 1;
        }
    }

    private static int Run(
        string corpusDir,
        string queriesPath,
        string qrelsPath,
        int topK,
        int? limit,
        string[] configNames,
        string? jsonPath)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        var configs = ResolveConfigs(configNames);

        var documents = LoadCorpus(corpusDir);
        var queries = LoadQueries(queriesPath, qrelsPath, limit);

        Console.WriteLine("LexiSharp benchmark");
        Console.WriteLine($"Corpus:       {corpusDir}");
        Console.WriteLine($"Documents:    {documents.Count}");
        Console.WriteLine($"Queries:      {queries.Count} ({queries.Count(q => q.RelevantDocumentIds.Count > 0)} judged)");
        Console.WriteLine($"Top-k:        {topK}");
        Console.WriteLine();

        var results = CorpusBenchmark.Run(documents, queries, configs, new BenchmarkOptions { TopK = topK }, cts.Token);

        PrintTable(results, topK);

        if (jsonPath is not null)
        {
            WriteJson(jsonPath, corpusDir, documents.Count, queries, topK, results);
            Console.WriteLine();
            Console.WriteLine($"Results written to {jsonPath}");
        }

        return 0;
    }

    private static IReadOnlyList<SearchDocument> LoadCorpus(string corpusDir)
    {
        var root = Path.GetFullPath(corpusDir);

        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Corpus directory not found: {root}");

        var documents = new List<SearchDocument>();
        documents.AddRange(MarkdownLoader.LoadDirectory(root).Select(document => document.ToSearchDocument()));
        documents.AddRange(TextFileLoader
            .ScanDirectory(root, new TextFileLoadOptions { Extensions = new[] { ".txt" } })
            .Select(document => document.ToSearchDocument()));

        if (documents.Count == 0)
            throw new ArgumentException($"No markdown or text documents found under {root}.", nameof(corpusDir));

        return documents;
    }

    private static IReadOnlyList<BenchmarkQuery> LoadQueries(string queriesPath, string qrelsPath, int? limit)
    {
        var rawQueries = ReadQueries(queriesPath);
        var qrels = ReadQrels(qrelsPath);

        var queries = rawQueries
            .Select(item => new BenchmarkQuery(item.Id, item.Text, qrels.GetValueOrDefault(item.Id) ?? Array.Empty<string>()))
            .ToList();

        if (queries.Count == 0)
            throw new ArgumentException($"No queries found in {queriesPath}.", nameof(queriesPath));

        if (limit is not null && limit < queries.Count)
            queries = queries.GetRange(0, limit.Value);

        return queries;
    }

    private static IReadOnlyList<(string Id, string Text)> ReadQueries(string path)
    {
        string? firstLine = File.ReadLines(path).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));

        if (firstLine is not null && firstLine.AsSpan().TrimStart().StartsWith('{'))
            return ReadQueriesJson(path);

        return File.ReadLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            .Select(line => line.Split('\t', 2) is [string id, string text] && string.IsNullOrWhiteSpace(id) is false
                ? (id, text)
                : throw new FormatException($"Expected 'id\\ttext' but got: {line}"))
            .ToList();
    }

    private static IReadOnlyList<(string Id, string Text)> ReadQueriesJson(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new FormatException($"Query file {path} must be a JSON object of id -> query text.");

        var queries = new List<(string Id, string Text)>();

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                throw new FormatException($"Query {property.Name} must map to a string.");

            queries.Add((property.Name, property.Value.GetString()!));
        }

        return queries;
    }

    private static Dictionary<string, IReadOnlyList<string>> ReadQrels(string path)
    {
        var qrels = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;

            string[] columns = line.Split('\t');

            if (columns.Length < 2 || columns[0].Length == 0 || columns[1].Length == 0)
                throw new FormatException($"Expected 'qid\\tdocid[\\tgrade]' but got: {line}");

            if (!qrels.TryGetValue(columns[0], out var relevant))
            {
                relevant = new List<string>();
                qrels[columns[0]] = relevant;
            }

            ((List<string>)relevant).Add(columns[1]);
        }

        return qrels;
    }

    private static IReadOnlyList<BenchmarkConfig> ResolveConfigs(string[] names)
    {
        var configs = new List<BenchmarkConfig>(names.Length);

        foreach (string name in names)
        {
            configs.Add(name switch
            {
                "bm25" => BenchmarkConfig.Bm25(),
                "bm25-tuned" => BenchmarkConfig.Bm25Tuned(),
                "tfidf" => BenchmarkConfig.TfIdf(),
                "ql" => BenchmarkConfig.QueryLikelihood(),
                "hybrid" => BenchmarkConfig.HybridRrf(new Bm25Scorer(), new TfIdfScorer()),
                _ => throw new ArgumentException($"Unknown configuration '{name}'. Valid: bm25, bm25-tuned, tfidf, ql, hybrid.", nameof(names)),
            });
        }

        return configs;
    }

    private static void PrintTable(IReadOnlyList<BenchmarkConfigResult> results, int topK)
    {
        string header = $"{"Config",-22} {"nDCG@" + topK,10} {"MAP@" + topK,10} {"MRR@" + topK,10} {"R@" + topK,10} {"P@" + topK,10} {"F1@" + topK,10} {"ms/q",8}";

        Console.WriteLine(header);
        Console.WriteLine(new string('-', header.Length));

        foreach (var result in results)
        {
            var metrics = result.Metrics;
            string judged = result.JudgedQueries == 0
                ? "—"
                : string.Format(CultureInfo.InvariantCulture, "{0,8:0.00}", result.MillisecondsPerQuery);
            Console.WriteLine(
                $"{result.Name,-22} " +
                $"{Format(metrics.NdcgAtK),-10} " +
                $"{Format(metrics.MapAtK),-10} " +
                $"{Format(metrics.MrrAtK),-10} " +
                $"{Format(metrics.RecallAtK),-10} " +
                $"{Format(metrics.PrecisionAtK),-10} " +
                $"{Format(metrics.F1AtK),-10} " +
                judged);
        }

        static string Format(double value) => value.ToString("0.0000", CultureInfo.InvariantCulture);
    }

    private static void WriteJson(
        string jsonPath,
        string corpusDir,
        int documentCount,
        IReadOnlyList<BenchmarkQuery> queries,
        int topK,
        IReadOnlyList<BenchmarkConfigResult> results)
    {
        var payload = new
        {
            corpus = corpusDir,
            documents = documentCount,
            queries = queries.Count,
            judgedQueries = queries.Count(query => query.RelevantDocumentIds.Count > 0),
            topK,
            configs = results.Select(result => new
            {
                result.Name,
                result.Metrics.NdcgAtK,
                result.Metrics.MapAtK,
                result.Metrics.MrrAtK,
                result.Metrics.RecallAtK,
                result.Metrics.PrecisionAtK,
                result.Metrics.F1AtK,
                result.TotalMilliseconds,
                result.MillisecondsPerQuery,
                result.JudgedQueries,
            }),
        };

        string json = JsonSerializer.Serialize(
            payload,
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        File.WriteAllText(jsonPath, json);
    }

    private static int ParsePositive(string value, string argument)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed <= 0)
            throw new ArgumentException($"{argument} expects a positive integer, got '{value}'.", argument);

        return parsed;
    }
}