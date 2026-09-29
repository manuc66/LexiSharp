using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace LexiSharp.Eval;

internal sealed record BeirDocument(string Id, string Title, string Text);

internal sealed record BeirQuery(string Id, string Text);

internal sealed class BeirCorpus
{
    public required string Name { get; init; }
    public required IReadOnlyList<BeirDocument> Documents { get; init; }
    public required IReadOnlyList<BeirQuery> Queries { get; init; }

    /// <summary>Per test query, its graded relevance map (document id -> qrel score).</summary>
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, double>> TestRelevance { get; init; }
}

/// <summary>
/// A BEIR dataset, its published BM25 baseline (Thakur et al. 2021, Table 2), and — where an
/// independent implementation publishes one for the same index and task — its current reference.
/// </summary>
/// <param name="Bm25Ndcg10Ref">The figure in Table 2 of the BEIR paper.</param>
/// <param name="PublishedNdcg10Ref">
/// BM25 nDCG@10 from the Anserini regression for this corpus's "flat" index, which is the
/// reproducible counterpart of <paramref name="Bm25Ndcg10Ref"/> and the one a comparison should
/// use. Null where none is published.
/// </param>
/// <param name="PublishedTotalTerms">
/// The <c>total terms</c> that Anserini's index of this corpus holds, as a fingerprint of the
/// analysis a reference was produced with. Null where none is published.
/// </param>
/// <param name="Documents">The document count Anserini's index reports.</param>
/// <param name="NonEmptyDocuments">
/// How many of those documents produced at least one term. ArguAna is the one corpus where this is
/// fewer than <paramref name="Documents"/>, which is itself worth checking against.
/// </param>
/// <param name="PublishedSource">Where the Anserini figure and fingerprint are published.</param>
internal sealed record BeirDataset(
    string Name,
    string DownloadUrl,
    string ExpectedMd5,
    double Bm25Ndcg10Ref,
    bool Graded,
    double? PublishedNdcg10Ref = null,
    long? PublishedTotalTerms = null,
    int? Documents = null,
    int? NonEmptyDocuments = null,
    string? PublishedSource = null)
{
    public static readonly IReadOnlyList<BeirDataset> All =
    [
        new(
            "nfcorpus",
            "https://public.ukp.informatik.tu-darmstadt.de/thakur/BEIR/datasets/nfcorpus.zip",
            "a89dba18a62ef92f7d323ec890a0d38d",
            Bm25Ndcg10Ref: 0.325,
            Graded: true,
            PublishedNdcg10Ref: 0.3218,
            PublishedTotalTerms: 637_485,
            Documents: 3_633,
            NonEmptyDocuments: 3_633,
            PublishedSource: "https://github.com/castorini/anserini/blob/master/src/main/resources/reproduce/from-document-collection/configs/beir-v1.0.0-nfcorpus.flat.yaml"),
        new(
            "scifact",
            "https://public.ukp.informatik.tu-darmstadt.de/thakur/BEIR/datasets/scifact.zip",
            "5f7d1de60b170fc8027bb7898e2efca1",
            Bm25Ndcg10Ref: 0.665,
            Graded: false,
            PublishedNdcg10Ref: 0.6789,
            PublishedTotalTerms: 838_128,
            Documents: 5_183,
            NonEmptyDocuments: 5_183,
            PublishedSource: "https://github.com/castorini/anserini/blob/master/src/main/resources/reproduce/from-document-collection/configs/beir-v1.0.0-scifact.flat.yaml"),
        new(
            "arguana",
            "https://public.ukp.informatik.tu-darmstadt.de/thakur/BEIR/datasets/arguana.zip",
            "8ad3e3c2a5867cdced806d6503f29b99",
            Bm25Ndcg10Ref: 0.315,
            Graded: false,
            PublishedNdcg10Ref: 0.3970,
            PublishedTotalTerms: 969_528,
            Documents: 8_674,
            NonEmptyDocuments: 8_673,
            PublishedSource: "https://github.com/castorini/anserini/blob/master/src/main/resources/reproduce/from-document-collection/configs/beir-v1.0.0-arguana.flat.yaml"),
    ];

    public static BeirDataset Resolve(string name)
    {
        foreach (BeirDataset dataset in All)
        {
            if (dataset.Name == name)
                return dataset;
        }

        throw new ArgumentException(
            $"Unknown dataset '{name}'. Available: {string.Join(", ", All.Select(dataset => dataset.Name))}.");
    }
}

/// <summary>
/// An independent implementation's index of a BEIR corpus, reduced to the three counts that
/// reveal whether two runs indexed the same thing: the number of documents, the number of
/// documents that produced at least one term, and the number of terms in the whole index.
/// </summary>
/// <remarks>
/// This is a fingerprint, not a score. BM25 effectiveness is only comparable across two runs
/// whose analysis agrees, and the cheapest way to find out that it does not is to count terms
/// before spending a single retrieval: a stop word list, a stemmer or a tokenizer that differs
/// moves this number by percent, long before it moves nDCG by a visible amount. Anserini
/// publishes these counts per corpus in its regression configuration files; see
/// <see cref="BeirDataset.PublishedSource"/>.
/// </remarks>
public sealed record IndexFingerprint(int Documents, int NonEmptyDocuments, long TotalTerms)
{
    /// <summary>Reads the fingerprint of a built index.</summary>
    public static IndexFingerprint Of(LexiSharp.Core.ITextIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);

        int nonEmpty = 0;

        foreach (var document in index.Documents)
        {
            if (index.DocumentLength(document.Id) > 0)
                nonEmpty++;
        }

        return new IndexFingerprint(index.Count, nonEmpty, index.CorpusTokenCount);
    }

    /// <summary>
    /// The relative difference against another fingerprint, the largest of the three counts
    /// compared one to one. Zero means the two indexes hold the same documents and the same
    /// number of terms.
    /// </summary>
    public double RelativeDifference(IndexFingerprint other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return Math.Max(
            Math.Abs(Documents - other.Documents) / (double)Math.Max(Documents, 1),
            Math.Max(
                Math.Abs(NonEmptyDocuments - other.NonEmptyDocuments) / (double)Math.Max(NonEmptyDocuments, 1),
                Math.Abs(TotalTerms - other.TotalTerms) / (double)Math.Max(other.TotalTerms, 1)));
    }
}

internal static class BeirLoader
{
    public static async Task<BeirCorpus> LoadOrDownloadAsync(string dataBaseDir, BeirDataset dataset)
    {
        string root = await EnsureDatasetAsync(dataBaseDir, dataset);

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
            Name = dataset.Name,
            Documents = documents,
            Queries = queries,
            TestRelevance = relevance,
        };
    }

    /// <summary>Returns the directory containing the BEIR-formatted files, downloading them first when missing.</summary>
    private static async Task<string> EnsureDatasetAsync(string dataBaseDir, BeirDataset dataset)
    {
        string nested = Path.Combine(dataBaseDir, dataset.Name);

        foreach (string candidate in new[] { dataBaseDir, nested })
        {
            if (File.Exists(Path.Combine(candidate, "corpus.jsonl"))
                && File.Exists(Path.Combine(candidate, "queries.jsonl"))
                && File.Exists(Path.Combine(candidate, "qrels", "test.tsv")))
            {
                return candidate;
            }
        }

        Directory.CreateDirectory(dataBaseDir);
        string zipPath = Path.Combine(dataBaseDir, $"{dataset.Name}.zip");

        Console.WriteLine($"Downloading {dataset.Name} (BEIR) from {dataset.DownloadUrl}");
        using var client = new HttpClient();
        await using (Stream response = await client.GetStreamAsync(dataset.DownloadUrl))
        await using (FileStream zip = File.Create(zipPath))
            await response.CopyToAsync(zip);

        string actualMd5 = await ComputeMd5Async(zipPath);

        if (!string.Equals(actualMd5, dataset.ExpectedMd5, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(zipPath);
            throw new InvalidOperationException(
                $"{dataset.Name} download checksum mismatch: expected {dataset.ExpectedMd5}, got {actualMd5}.");
        }

        Console.WriteLine("Download complete, checksum verified.");
        ZipFile.ExtractToDirectory(zipPath, dataBaseDir, overwriteFiles: true);
        File.Delete(zipPath);

        return Directory.Exists(nested) ? nested : dataBaseDir;
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