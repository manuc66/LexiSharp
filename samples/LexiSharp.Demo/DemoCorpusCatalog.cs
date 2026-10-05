using System.Globalization;
using System.Text.Json;
using LexiSharp.Core;

namespace LexiSharp.Demo;

/// <summary>
/// A corpus the demo can index: the one it ships with, or one of the BEIR corpora the evaluation
/// harness keeps on disk.
/// </summary>
/// <param name="Key">The <c>--corpus</c> value that selects this corpus.</param>
/// <param name="Label">A human-readable name for the UI.</param>
/// <param name="Documents">Everything the five lanes are built over.</param>
/// <param name="SampleQueries">Queries offered as chips, each one with at least one known relevant document.</param>
public sealed record DemoCorpusSet(
    string Key,
    string Label,
    IReadOnlyList<SearchDocument> Documents,
    IReadOnlyList<string> SampleQueries);

/// <summary>One corpus the demo could run on, as reported by <c>/api/meta</c>.</summary>
/// <param name="Key">The <c>--corpus</c> value.</param>
/// <param name="Label">A human-readable name.</param>
/// <param name="Available">Whether the demo can load it right now, without a download.</param>
public sealed record DemoCorpusOption(string Key, string Label, bool Available);

/// <summary>
/// A command-line option the demo cannot honour, with what to do about it in the message. The base
/// of every failure a caller can fix by changing the command, so the host reports them all the
/// same way and none of them reaches the runtime as an unhandled exception.
/// </summary>
public class DemoOptionException : Exception
{
    /// <summary>Creates the exception with an actionable message.</summary>
    public DemoOptionException(string message) : base(message)
    {
    }
}

/// <summary>A corpus was asked for that cannot be loaded, with what to do about it in the message.</summary>
public sealed class DemoCorpusException : DemoOptionException
{
    /// <summary>Creates the exception with an actionable message.</summary>
    public DemoCorpusException(string message) : base(message)
    {
    }
}

/// <summary>
/// Resolves the <c>--corpus</c> option. The built-in corpus stays the default so that
/// <c>dotnet run --project samples/LexiSharp.Demo</c> works on a fresh clone; a BEIR corpus is read
/// from the evaluation harness's data directory, which is not in the repository, and the demo
/// reports how to obtain it rather than fetching 26 MB itself.
/// </summary>
public static class DemoCorpusCatalog
{
    /// <summary>Key of the corpus that ships with the demo, and the default when none is given.</summary>
    public const string BuiltIn = "demo";

    /// <summary>How many example queries a corpus offers.</summary>
    private const int SampleQueryCount = 6;

    /// <summary>
    /// The BEIR corpora the harness knows, with a short descriptor for the UI. Whether one is
    /// <em>available</em> is decided per data directory at run time, not here.
    /// </summary>
    private static readonly (string Key, string Label)[] Beir =
    [
        ("nfcorpus", "NFCorpus · nutrition articles"),
        ("scifact", "SciFact · scientific claims"),
        ("arguana", "ArguAna · argument retrieval"),
    ];

    /// <summary>Every corpus the demo accepts, in the order the UI should list them.</summary>
    public static IReadOnlyList<DemoCorpusOption> Describe(string? dataDirectory, string contentRoot)
    {
        string? root = FindDataDirectory(dataDirectory, contentRoot);

        var options = new List<DemoCorpusOption>
        {
            new(BuiltIn, "Built-in · hand-written demo corpus", true),
        };

        foreach ((string key, string label) in Beir)
            options.Add(new DemoCorpusOption(key, label, root is not null && ResolveDatasetDirectory(root, key) is not null));

        return options;
    }

    /// <summary>Loads the corpus named by <paramref name="key"/>.</summary>
    /// <param name="key">A key from <see cref="Describe"/>.</param>
    /// <param name="dataDirectory">Overrides the data directory instead of searching for one.</param>
    /// <param name="contentRoot">Where the search for a data directory starts.</param>
    /// <exception cref="DemoCorpusException">The corpus is unknown, or its files are not on disk.</exception>
    public static DemoCorpusSet Load(string key, string? dataDirectory, string contentRoot)
    {
        if (key == BuiltIn)
        {
            return new DemoCorpusSet(
                BuiltIn,
                "Built-in · hand-written demo corpus",
                DemoCorpus.Build(),
                DemoCorpus.SampleQueries);
        }

        foreach ((string candidate, string label) in Beir)
        {
            if (candidate == key)
                return LoadBeir(candidate, label, dataDirectory, contentRoot);
        }

        string available = string.Join(", ", new[] { BuiltIn }.Concat(Beir.Select(entry => entry.Key)));

        throw new DemoCorpusException($"Unknown corpus '{key}'. Available: {available}.");
    }

    /// <summary>
    /// Reads a BEIR-formatted corpus in the shape <c>LexiSharp.Eval.Evaluation.BuildDocuments</c>
    /// builds it — title as a text field, body as the text — so a lane ranks a BEIR corpus here the
    /// same way the harness ranks it there. The built-in corpus is indexed on body text alone, which
    /// is why its titles do not reach the index; see <see cref="DemoCorpus"/>.
    /// </summary>
    private static DemoCorpusSet LoadBeir(string key, string label, string? dataDirectory, string contentRoot)
    {
        string? root = FindDataDirectory(dataDirectory, contentRoot);

        if (root is null)
        {
            throw new DemoCorpusException(
                $"--corpus {key} reads the {key} files from the evaluation harness's data directory, "
                + "and none was found. Pass --data-dir <path>, or fetch the corpus with the harness: "
                + FetchHint(key));
        }

        string? directory = ResolveDatasetDirectory(root, key);

        if (directory is null)
        {
            throw new DemoCorpusException(
                $"The {key} corpus is not in {root}. Fetch it with the harness: {FetchHint(key)}");
        }

        return new DemoCorpusSet(key, label, ReadDocuments(directory, key), ReadSampleQueries(directory, key));
    }

    /// <summary>
    /// The harness downloads a dataset on first use, so the shortest command that produces it also
    /// runs a one-query evaluation. Quoted as a suggestion rather than executed.
    /// </summary>
    private static string FetchHint(string key) =>
        $"dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset {key} --limit 1";

    /// <summary>
    /// Finds the harness data directory: an explicit path if given, otherwise the nearest
    /// <c>bench/LexiSharp.Eval/data</c> at or above the content root, the working directory or the
    /// executable's own location. The demo is launched from the project directory by
    /// <c>dotnet run --project</c> and from <c>bin/</c> when executed directly, so all three starts
    /// are tried.
    /// </summary>
    private static string? FindDataDirectory(string? explicitDirectory, string contentRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitDirectory))
            return Path.GetFullPath(explicitDirectory);

        foreach (string start in new[] { contentRoot, Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (DirectoryInfo? dir = new(start); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "bench", "LexiSharp.Eval", "data");

                if (Directory.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Accepts both layouts the harness can leave behind: a directory per dataset, or the files
    /// unpacked straight into the data directory.
    /// </summary>
    private static string? ResolveDatasetDirectory(string root, string key)
    {
        foreach (string candidate in new[] { Path.Combine(root, key), root })
        {
            if (File.Exists(Path.Combine(candidate, "corpus.jsonl"))
                && File.Exists(Path.Combine(candidate, "queries.jsonl")))
            {
                return candidate;
            }
        }

        return null;
    }

    private static List<SearchDocument> ReadDocuments(string directory, string key)
    {
        var documents = new List<SearchDocument>();

        foreach (string line in File.ReadLines(Path.Combine(directory, "corpus.jsonl")))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            using JsonDocument json = JsonDocument.Parse(line);
            JsonElement root = json.RootElement;

            string id = String(root, "_id") ?? throw new DemoCorpusException($"{key}: a corpus line has no _id.");
            string text = String(root, "text") ?? string.Empty;
            string? title = String(root, "title");

            documents.Add(new SearchDocument(
                id,
                text,
                TextFields: string.IsNullOrEmpty(title)
                    ? null
                    : new Dictionary<string, string>(StringComparer.Ordinal) { ["title"] = title },
                // BEIR has no category, and the hit card renders one unconditionally. The corpus
                // name is the honest value: what the chip can actually tell you here.
                Category: key));
        }

        return documents;
    }

    /// <summary>
    /// Reads example queries from the corpus's own <c>queries.jsonl</c>, keeping only those with at
    /// least one positive judgement in <c>test.tsv</c> so every chip is a query that has a known
    /// relevant document. File order is preserved, which makes the chips the same on every start.
    /// </summary>
    private static List<string> ReadSampleQueries(string directory, string key)
    {
        HashSet<string> judged = ReadJudgedQueryIds(directory);
        var queries = new List<string>(SampleQueryCount);

        foreach (string line in File.ReadLines(Path.Combine(directory, "queries.jsonl")))
        {
            if (queries.Count == SampleQueryCount)
                break;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            using JsonDocument json = JsonDocument.Parse(line);
            JsonElement root = json.RootElement;

            string? id = String(root, "_id");
            string? text = String(root, "text");

            if (id is null || string.IsNullOrWhiteSpace(text))
                continue;

            if (judged.Count != 0 && !judged.Contains(id))
                continue;

            queries.Add(text);
        }

        if (queries.Count == 0)
            throw new DemoCorpusException($"{key}: no query in queries.jsonl has a positive judgement in qrels/test.tsv.");

        return queries;
    }

    /// <summary>
    /// The query ids carrying a positive score in <c>qrels/test.tsv</c>. Rows that do not parse are
    /// skipped rather than assuming a header line: the file has one today, and reading it that way
    /// keeps the demo working against a qrels file without one instead of silently dropping a
    /// judgement.
    /// </summary>
    private static HashSet<string> ReadJudgedQueryIds(string directory)
    {
        string path = Path.Combine(directory, "qrels", "test.tsv");

        if (!File.Exists(path))
            return [];

        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (string line in File.ReadLines(path))
        {
            string[] parts = line.Split('\t');

            if (parts.Length < 3)
                continue;

            if (double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double score) && score > 0)
                ids.Add(parts[0]);
        }

        return ids;
    }

    private static string? String(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) ? value.GetString() : null;
}