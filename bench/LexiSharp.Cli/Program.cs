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
        + "  --configs <list>     Comma-separated: bm25, bm25-tuned, tfidf, ql, hybrid, bm25-semantic (default: bm25,tfidf,ql,hybrid,bm25-tuned)\n"
        + "  --json <path>        Write the results as JSON to this file\n"
        + "  --help, -h           Show this help\n"
        + "\n"
        + "Usage: lexisharp diff <corpus-dir> --queries <file> --qrels <file> --baseline <cfg> --candidate <cfg>\n"
        + "  Compares two configurations query by query: which ones the candidate rescued, which it\n"
        + "  lost, and where the first judged document moved. A pair of means cannot show that.\n"
        + "  --baseline <cfg>     The reference configuration (same names as --configs)\n"
        + "  --candidate <cfg>    The configuration being evaluated\n"
        + "  --epsilon <n>        Score difference ignored when calling a query moved (default: 1e-9)\n"
        + "  Other options: --queries, --qrels, --top-k, --limit\n"
        + "  --help, -h           Show this help";

    public static int Main(string[] args)
    {
        // Two subcommands, same corpus/query plumbing. 'diff' is the one that answers "what did
        // this change actually do", so it is a first-class command rather than a benchmark flag.
        string command = "benchmark";

        if (args.Length > 0 && args[0] is "benchmark" or "diff")
        {
            command = args[0];
            args = args[1..];
        }

        string? corpusDir = null;
        string? queriesPath = null;
        string? qrelsPath = null;
        string? jsonPath = null;
        string? baselineName = null;
        string? candidateName = null;
        double epsilon = 1e-9;
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
                case "--baseline" when i + 1 < args.Length:
                    baselineName = args[++i];
                    break;
                case "--candidate" when i + 1 < args.Length:
                    candidateName = args[++i];
                    break;
                case "--epsilon" when i + 1 < args.Length:
                    epsilon = double.Parse(args[++i], CultureInfo.InvariantCulture);
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

        if (command == "diff" && (baselineName is null || candidateName is null))
        {
            Console.Error.WriteLine("diff requires --baseline and --candidate.");
            Console.Error.WriteLine(Usage);
            return 2;
        }

        try
        {
            if (command == "diff")
            {
                return RunDiff(
                    corpusDir, queriesPath, qrelsPath, topK, limit,
                    baselineName!, candidateName!, epsilon);
            }

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

    private static int RunDiff(
        string corpusDir,
        string queriesPath,
        string qrelsPath,
        int topK,
        int? limit,
        string baselineName,
        string candidateName,
        double epsilon)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        var configs = ResolveConfigs([baselineName, candidateName]);

        var documents = LoadCorpus(corpusDir);
        var queries = LoadQueries(queriesPath, qrelsPath, limit);

        Console.WriteLine($"LexiSharp diff: {configs[0].Name} -> {configs[1].Name}");
        Console.WriteLine($"Corpus:       {corpusDir} ({documents.Count} documents, {queries.Count} queries)");
        Console.WriteLine($"Top-k:        {topK}");
        Console.WriteLine();

        // Both configurations run over the same shared index inside one call, so the comparison
        // cannot be confounded by a corpus built twice.
        var results = CorpusBenchmark.Run(documents, queries, configs, new BenchmarkOptions { TopK = topK }, cts.Token);
        var comparison = BenchmarkComparer.Compare(results[0], results[1], epsilon);

        Console.WriteLine($"mean nDCG@{topK}: {results[0].Metrics.NdcgAtK:0.0000} -> {results[1].Metrics.NdcgAtK:0.0000} "
            + $"({comparison.MeanDelta:+0.0000;-0.0000;+0.0000} per query)");
        Console.WriteLine($"queries: {comparison.ImprovedCount} improved, {comparison.DegradedCount} degraded, "
            + $"{comparison.UnchangedCount} unchanged (net {comparison.Net:+0;-0;+0})");
        Console.WriteLine();

        PrintDeltas("DEGRADED by " + comparison.CandidateName, comparison.Degraded, topK);
        PrintDeltas("IMPROVED by " + comparison.CandidateName, comparison.Improved, topK);

        if (comparison.Degraded.Count == 0 && comparison.Improved.Count == 0)
        {
            Console.WriteLine("No query moved beyond the tolerance.");
            return 0;
        }

        if (comparison.Degraded.Count > comparison.Improved.Count)
        {
            Console.WriteLine($"Read: the candidate loses more queries than it gains ({comparison.DegradedCount} vs "
                + $"{comparison.ImprovedCount}). A mean can improve while this is true.");
        }

        return 0;
    }

    private static void PrintDeltas(string title, IReadOnlyList<BenchmarkQueryDelta> deltas, int topK)
    {
        if (deltas.Count == 0)
            return;

        Console.WriteLine(title);
        Console.WriteLine(new string('-', Math.Min(100, 40 + title.Length)));

        foreach (var delta in deltas)
        {
            Console.WriteLine($"  {delta.QueryId,-24} {delta.BaselineNdcg:0.000} -> {delta.CandidateNdcg:0.000} "
                + $"({delta.Delta:+0.000;-0.000;+0.000})  {delta.Reading}");
            Console.WriteLine($"    \"{Truncate(delta.QueryText, 70)}\"");
        }

        Console.WriteLine();
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max].TrimEnd() + "…";

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
            .Select(item => qrels.TryGetValue(item.Id, out var judged)
                // A judged query is graded whenever any judgment carries a level, so the CLI and
                // the BEIR harness agree on what a qrels file means.
                ? new BenchmarkQuery(item.Id, item.Text, judged)
                : new BenchmarkQuery(item.Id, item.Text, Array.Empty<string>()))
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

    private static Dictionary<string, IReadOnlyDictionary<string, double>> ReadQrels(string path)
    {
        var qrels = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);

        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                continue;

            string[] columns = line.Split('\t');

            if (columns.Length < 2 || columns[0].Length == 0 || columns[1].Length == 0)
                throw new FormatException($"Expected 'qid\\tdocid[\\tgrade]' but got: {line}");

            // The grade column is optional: absent or unparsable means "relevant", the binary
            // convention every qrels file starts from. A parsed grade is clamped to the binary
            // projection, so a qrels file mixing levels and bare judgments is still coherent.
            double grade = 1;

            if (columns.Length >= 3
                && double.TryParse(columns[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                && parsed > 0)
            {
                grade = parsed;
            }

            if (!qrels.TryGetValue(columns[0], out var byDocument))
            {
                byDocument = new Dictionary<string, double>(StringComparer.Ordinal);
                qrels[columns[0]] = byDocument;
            }

            byDocument[columns[1]] = Math.Max(byDocument.GetValueOrDefault(columns[1]), grade);
        }

        return qrels.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<string, double>)pair.Value,
            StringComparer.Ordinal);
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
                "bm25-semantic" => BenchmarkConfig.Bm25Semantic(),
                _ => throw new ArgumentException($"Unknown configuration '{name}'. Valid: bm25, bm25-tuned, tfidf, ql, hybrid, bm25-semantic.", nameof(names)),
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