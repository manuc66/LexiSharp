using System.Globalization;
using System.Text.Json;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;

namespace LexiSharp.Cli;

/// <summary>
/// The <c>segment</c> command: build a corpus into a store on disk, change it, and search it.
/// </summary>
/// <remarks>
/// Every subcommand is durable on its own, because a store's overlay is not: an <c>add</c> that is not
/// committed is lost when the process exits, so the CLI commits where the change would otherwise
/// disappear. The analysis is recorded beside the segments and every read uses the recorded one — a
/// store's dictionary holds normalized terms, and searching it with a different tokenizer reads a
/// different index without saying so.
/// </remarks>
public static class SegmentCommands
{
    internal const string Usage =
        "Usage: lexisharp segment build <corpus-dir> [--store <dir>] [--analyzer <name>]\n"
        + "  Index the corpus into a store and commit it. The store defaults to .lexisharp in the\n"
        + "  current directory, which is where 'lexisharp search' looks. Analyzer: default or english.\n"
        + "Usage: lexisharp segment add <store-dir> --corpus <dir>\n"
        + "  Add the corpus's documents to the store's live overlay and commit.\n"
        + "Usage: lexisharp segment remove <store-dir> --id <doc-id>\n"
        + "  Tombstone a document and commit.\n"
        + "Usage: lexisharp segment commit <store-dir>\n"
        + "  Compact the store: merge its tombstones and rewrite one segment on disk.\n"
        + "Usage: lexisharp segment search <store-dir> --query <text> [--top-k <n>] [--json <path>]\n"
        + "  Search the committed store and print the ranked hits.\n"
        + "Usage: lexisharp segment info <store-dir>\n"
        + "  Report the store's counts without searching it.";

    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };

    /// <summary>The analysis a store was built with, recorded beside its segments.</summary>
    private sealed record StoreManifest(string Analyzer, int Documents);

    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("segment needs a subcommand.");
            Console.Error.WriteLine(Usage);
            return 2;
        }

        if (args[0] is "--help" or "-h" or "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        string subcommand = args[0];
        string? storeDir = null;
        string? corpusDir = null;
        string? query = null;
        string? documentId = null;
        string? jsonPath = null;
        string analyzer = "default";
        int topK = 10;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--store" when i + 1 < args.Length:
                    storeDir = args[++i];
                    break;
                case "--corpus" when i + 1 < args.Length:
                    corpusDir = args[++i];
                    break;
                case "--query" when i + 1 < args.Length:
                    query = args[++i];
                    break;
                case "--id" when i + 1 < args.Length:
                    documentId = args[++i];
                    break;
                case "--json" when i + 1 < args.Length:
                    jsonPath = args[++i];
                    break;
                case "--analyzer" when i + 1 < args.Length:
                    analyzer = args[++i];
                    break;
                case "--top-k" when i + 1 < args.Length && int.TryParse(args[i + 1], out int depth):
                    topK = depth;
                    i++;
                    break;
                default:
                    if (args[i].StartsWith('-'))
                    {
                        Console.Error.WriteLine($"Unknown argument: {args[i]}");
                        Console.Error.WriteLine(Usage);
                        return 2;
                    }

                    // The one positional is the corpus to build from, or the store to read: build
                    // names the corpus first and the store with --store, every other subcommand names
                    // the store first.
                    if (subcommand == "build" ? corpusDir is not null : storeDir is not null)
                    {
                        Console.Error.WriteLine($"Unknown argument: {args[i]}");
                        Console.Error.WriteLine(Usage);
                        return 2;
                    }

                    if (subcommand == "build")
                        corpusDir = args[i];
                    else
                        storeDir = args[i];

                    break;
            }
        }

        try
        {
            return subcommand switch
            {
                "build" => Build(corpusDir, storeDir, analyzer),
                "add" => Add(storeDir, corpusDir),
                "remove" => Remove(storeDir, documentId),
                "commit" => Commit(storeDir),
                "search" => Search(storeDir, query, topK, jsonPath),
                "info" => Info(storeDir),
                _ => Unknown(subcommand),
            };
        }
        catch (Exception exception) when (
            exception is IOException
                or ArgumentException
                or UnauthorizedAccessException
                or JsonException
                or FormatException)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            return 1;
        }
    }

    private static int Unknown(string subcommand)
    {
        Console.Error.WriteLine($"Unknown segment subcommand: {subcommand}");
        Console.Error.WriteLine(Usage);
        return 2;
    }

    private static int Build(string? corpusDir, string? storeDir, string analyzer)
    {
        if (corpusDir is null)
        {
            Console.Error.WriteLine("build requires <corpus-dir>.");
            return 2;
        }

        storeDir ??= NestedStoreDirectory;

        var documents = Program.LoadCorpus(corpusDir);
        var tokenizer = BuildTokenizer(analyzer);

        Directory.CreateDirectory(storeDir);

        using var store = new SegmentedTextIndex(storeDir, tokenizer);
        store.Index(documents);
        using var committed = store.Commit();

        WriteManifest(storeDir, analyzer, committed.Count);

        Console.WriteLine($"Built {committed.Count.ToString("N0", CultureInfo.InvariantCulture)} documents from {corpusDir}");
        Console.WriteLine($"Store:   {Path.GetFullPath(storeDir)} ({committed.SegmentCount} segment, analyzer {analyzer})");
        return 0;
    }

    private static int Add(string? storeDir, string? corpusDir)
    {
        if (storeDir is null || corpusDir is null)
        {
            Console.Error.WriteLine("add requires <store-dir> and --corpus <dir>.");
            return 2;
        }

        var documents = Program.LoadCorpus(corpusDir);
        var tokenizer = ReadTokenizer(storeDir, out string analyzer);

        using var store = new SegmentedTextIndex(storeDir, tokenizer);

        foreach (var document in documents)
            store.Add(document);

        using var committed = store.Commit();
        WriteManifest(storeDir, analyzer, committed.Count);

        Console.WriteLine($"Added {documents.Count.ToString("N0", CultureInfo.InvariantCulture)} documents; {committed.Count.ToString("N0", CultureInfo.InvariantCulture)} now in the store.");
        return 0;
    }

    private static int Remove(string? storeDir, string? documentId)
    {
        if (storeDir is null || documentId is null)
        {
            Console.Error.WriteLine("remove requires <store-dir> and --id <doc-id>.");
            return 2;
        }

        var tokenizer = ReadTokenizer(storeDir, out string analyzer);

        using var store = new SegmentedTextIndex(storeDir, tokenizer);

        if (!store.Remove(documentId))
        {
            Console.Error.WriteLine($"No document '{documentId}' in the store.");
            return 1;
        }

        int tombstones = store.TombstoneCount;

        using var committed = store.Commit();
        WriteManifest(storeDir, analyzer, committed.Count);

        Console.WriteLine($"Removed '{documentId}'; {tombstones} tombstone(s) reclaimed, {committed.Count.ToString("N0", CultureInfo.InvariantCulture)} documents remain.");
        return 0;
    }

    private static int Commit(string? storeDir)
    {
        if (storeDir is null)
        {
            Console.Error.WriteLine("commit requires <store-dir>.");
            return 2;
        }

        var tokenizer = ReadTokenizer(storeDir, out string analyzer);

        using var store = new SegmentedTextIndex(storeDir, tokenizer);

        int before = store.Count;
        int segments = store.SegmentCount;

        using var committed = store.Commit();
        WriteManifest(storeDir, analyzer, committed.Count);

        Console.WriteLine($"Committed {before.ToString("N0", CultureInfo.InvariantCulture)} documents from {segments} segment(s) to {committed.Count.ToString("N0", CultureInfo.InvariantCulture)} in {committed.SegmentCount} segment on disk.");
        return 0;
    }

    private static int Search(string? storeDir, string? query, int topK, string? jsonPath)
    {
        if (storeDir is null || query is null)
        {
            Console.Error.WriteLine("search requires <store-dir> and --query <text>.");
            return 2;
        }

        var tokenizer = ReadTokenizer(storeDir, out string analyzer);

        using var store = new SegmentedTextIndex(storeDir, tokenizer);
        var engine = new RankedTextSearchEngine(store, new Bm25Scorer(), tokenizer);

        var results = engine.Search(query, new SearchOptions(topK));

        Console.WriteLine($"Store:   {Path.GetFullPath(storeDir)} ({store.Count.ToString("N0", CultureInfo.InvariantCulture)} documents, analyzer {analyzer})");
        Console.WriteLine($"Query:   {ConsoleText.Line(query, 200)}");
        Console.WriteLine();

        for (int i = 0; i < results.Count; i++)
        {
            var result = results[i];

            // The id and the text come from the corpus: neither reaches the terminal with a control
            // or format character intact. See ConsoleText.
            Console.WriteLine($"{(i + 1).ToString(CultureInfo.InvariantCulture),3}. {result.Score.ToString("0.0000", CultureInfo.InvariantCulture),10}  {ConsoleText.Line(result.DocumentId, 200)}");
            Console.WriteLine($"     {ConsoleText.Line(result.Document.Text, 96)}");
        }

        if (results.Count == 0)
            Console.WriteLine("(no matches)");

        if (jsonPath is not null)
        {
            WriteRunJson(jsonPath, query, results);
            Console.WriteLine();
            Console.WriteLine($"Results written to {jsonPath}");
        }

        return 0;
    }

    private static int Info(string? storeDir)
    {
        if (storeDir is null)
        {
            Console.Error.WriteLine("info requires <store-dir>.");
            return 2;
        }

        var tokenizer = ReadTokenizer(storeDir, out string analyzer);

        using var store = new SegmentedTextIndex(storeDir, tokenizer);

        Console.WriteLine($"Store:       {Path.GetFullPath(storeDir)}");
        Console.WriteLine($"Analyzer:    {analyzer}");
        Console.WriteLine($"Documents:   {store.Count.ToString("N0", CultureInfo.InvariantCulture)}");
        Console.WriteLine($"Segments:    {store.SegmentCount}");
        Console.WriteLine($"Tombstones:  {store.TombstoneCount}");
        Console.WriteLine($"Pending:     {store.PendingCount}");
        Console.WriteLine($"Vocabulary:  {store.VocabularySize.ToString("N0", CultureInfo.InvariantCulture)}");
        Console.WriteLine($"Tokens:      {store.CorpusTokenCount.ToString("N0", CultureInfo.InvariantCulture)}");
        Console.WriteLine($"ShouldMerge: {store.ShouldMerge()}");
        return 0;
    }

    private static void WriteRunJson(string path, string query, IReadOnlyList<SearchResult> results)
    {
        var payload = new
        {
            query,
            results = results.Select(result => new
            {
                id = result.DocumentId,
                score = result.Score,
            }),
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload, ManifestJson));
    }

    /// <summary>The tokenizer an analyzer name denotes. Mirrors the evaluation harness's names.</summary>
    private static ITokenizer BuildTokenizer(string analyzer) => analyzer switch
    {
        "default" => Tokenizer.Default,
        "english" => new Tokenizer(new TokenizerOptions
        {
            Stemmer = new PorterStemmer(),
            RemoveStopWords = true,
            StopWords = StopWords.EnglishFunction,
            KeepSingleCharTerms = true,
        }),
        _ => throw new ArgumentException(
            $"Unknown analyzer '{analyzer}'. Expected 'default' or 'english'."),
    };

    private static string ManifestPath(string storeDir) => Path.Combine(storeDir, "store.json");

    /// <summary>
    /// Where <c>build</c> puts a store when the caller names none, and where <c>search</c> looks when
    /// the directory it was given is not a store itself.
    /// </summary>
    internal const string NestedStoreDirectory = ".lexisharp";

    /// <summary>
    /// The store a search should read, when the path names one: the directory itself, or the
    /// <c>.lexisharp</c> directory inside it.
    /// </summary>
    /// <remarks>
    /// The test is the manifest rather than a segment file, because the manifest is what records the
    /// analyzer. A directory whose segments have no manifest cannot be opened without guessing an
    /// analysis, so it is not a store this command can search — and reporting "not a store" about it
    /// would be the wrong answer to the wrong question.
    /// </remarks>
    internal static bool TryResolveStore(string path, out string storeDirectory)
    {
        if (File.Exists(ManifestPath(path)))
        {
            storeDirectory = path;
            return true;
        }

        string nested = Path.Combine(path, NestedStoreDirectory);

        if (File.Exists(ManifestPath(nested)))
        {
            storeDirectory = nested;
            return true;
        }

        storeDirectory = string.Empty;
        return false;
    }

    private static void WriteManifest(string storeDir, string analyzer, int documents)
    {
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(ManifestPath(storeDir), JsonSerializer.Serialize(new StoreManifest(analyzer, documents), ManifestJson));
    }

    /// <summary>
    /// The tokenizer the store was built with, from the manifest beside its segments.
    /// </summary>
    /// <exception cref="FileNotFoundException">The directory is not a store.</exception>
    /// <exception cref="FormatException">The manifest is missing or malformed.</exception>
    private static ITokenizer ReadTokenizer(string storeDir, out string analyzer)
    {
        string path = ManifestPath(storeDir);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Not a store: {path} was not found. Run 'lexisharp segment build' first.", path);
        }

        var manifest = JsonSerializer.Deserialize<StoreManifest>(File.ReadAllText(path))
            ?? throw new FormatException($"Malformed store manifest: {path}");

        analyzer = manifest.Analyzer;

        return BuildTokenizer(analyzer);
    }
}
