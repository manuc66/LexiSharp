using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace LexiSharp.Eval;

internal sealed record BeirDocument(string Id, string Title, string Text);

internal sealed record BeirQuery(string Id, string Text);

internal sealed class BeirCorpus
{
    public required IReadOnlyList<BeirDocument> Documents { get; init; }
    public required IReadOnlyList<BeirQuery> Queries { get; init; }

    /// <summary>Per test query, its graded relevance map (document id -> qrel score).</summary>
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> TestRelevance { get; init; }
}

internal static class BeirLoader
{
    private const string DownloadUrl =
        "https://public.ukp.informatik.tu-darmstadt.de/thakur/BEIR/datasets/nfcorpus.zip";

    private const string ExpectedMd5 = "a89dba18a62ef92f7d323ec890a0d38d";

    public static async Task<BeirCorpus> LoadOrDownloadAsync(string dataDir)
    {
        string root = await EnsureDatasetAsync(dataDir);

        var documents = ParseJsonLines(
            Path.Combine(root, "corpus.jsonl"),
            json => new BeirDocument(
                GetString(json, "_id")!,
                GetString(json, "title") ?? string.Empty,
                GetString(json, "text") ?? string.Empty));

        var queries = ParseJsonLines(
            Path.Combine(root, "queries.jsonl"),
            json => new BeirQuery(
                GetString(json, "_id")!,
                GetString(json, "text") ?? string.Empty));

        var relevance = ParseQrels(Path.Combine(root, "qrels", "test.tsv"));

        return new BeirCorpus
        {
            Documents = documents,
            Queries = queries,
            TestRelevance = relevance,
        };
    }

    /// <summary>Returns the directory containing the BEIR-formatted files, downloading them first when missing.</summary>
    private static async Task<string> EnsureDatasetAsync(string dataDir)
    {
        string nested = Path.Combine(dataDir, "nfcorpus");

        foreach (string candidate in new[] { dataDir, nested })
        {
            if (File.Exists(Path.Combine(candidate, "corpus.jsonl"))
                && File.Exists(Path.Combine(candidate, "queries.jsonl"))
                && File.Exists(Path.Combine(candidate, "qrels", "test.tsv")))
            {
                return candidate;
            }
        }

        Directory.CreateDirectory(dataDir);
        string zipPath = Path.Combine(dataDir, "nfcorpus.zip");

        Console.WriteLine($"Downloading NFCorpus (BEIR) from {DownloadUrl}");
        using var client = new HttpClient();
        await using (Stream response = await client.GetStreamAsync(DownloadUrl))
        await using (FileStream zip = File.Create(zipPath))
            await response.CopyToAsync(zip);

        string actualMd5 = await ComputeMd5Async(zipPath);

        if (!string.Equals(actualMd5, ExpectedMd5, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(zipPath);
            throw new InvalidOperationException(
                $"NFCorpus download checksum mismatch: expected {ExpectedMd5}, got {actualMd5}.");
        }

        Console.WriteLine("Download complete, checksum verified.");
        ZipFile.ExtractToDirectory(zipPath, dataDir, overwriteFiles: true);
        File.Delete(zipPath);

        return Directory.Exists(nested) ? nested : dataDir;
    }

    private static async Task<string> ComputeMd5Async(string path)
    {
        using var md5 = MD5.Create();
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(await md5.ComputeHashAsync(stream));
    }

    private static IReadOnlyList<T> ParseJsonLines<T>(string path, Func<JsonDocument, T> selector)
    {
        var items = new List<T>();

        foreach (string line in File.ReadLines(path))
        {
            using JsonDocument json = JsonDocument.Parse(line);
            items.Add(selector(json));
        }

        return items;
    }

    private static string? GetString(JsonDocument json, string property) =>
        json.RootElement.TryGetProperty(property, out JsonElement value) ? value.GetString() : null;

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> ParseQrels(string path)
    {
        var qrels = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);

        foreach (string line in File.ReadLines(path).Skip(1))
        {
            string[] parts = line.Split('\t');

            if (parts.Length < 3 || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double score)
                || score <= 0)
            {
                continue;
            }

            string queryId = parts[0];
            string docId = parts[1];

            if (!qrels.TryGetValue(queryId, out Dictionary<string, double>? byDocument))
            {
                byDocument = new Dictionary<string, double>(StringComparer.Ordinal);
                qrels[queryId] = byDocument;
            }

            byDocument[docId] = Math.Max(byDocument.GetValueOrDefault(docId), score);
        }

        return qrels.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyDictionary<string, double>)pair.Value,
            StringComparer.Ordinal);
    }
}