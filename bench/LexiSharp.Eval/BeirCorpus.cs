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
/// <param name="DownloadUrls">
/// Where the zip can be fetched, in the order they are tried. Every entry must serve the same
/// bytes as <paramref name="ExpectedMd5"/> names — a second source of the same artifact, not a
/// second corpus. The published mirror is a single fileserver with no failover, and it has been
/// unreachable for hours, which turned a green gate red for a reason no commit could have caused;
/// the fallback is the Internet Archive's copy of that same URL, whose bytes are checked against
/// the same hash before the zip is extracted.
/// </param>
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
    IReadOnlyList<string> DownloadUrls,
    string ExpectedMd5,
    double Bm25Ndcg10Ref,
    bool Graded,
    double? PublishedNdcg10Ref = null,
    long? PublishedTotalTerms = null,
    int? Documents = null,
    int? NonEmptyDocuments = null,
    string? PublishedSource = null,
    bool InAll = true)
{
    public static readonly IReadOnlyList<BeirDataset> All =
    [
        new(
            "nfcorpus",
            PublishedAndArchived("nfcorpus", archivedAt: "20250628051701"),
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
            PublishedAndArchived("scifact", archivedAt: "20261022010350"),
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
            PublishedAndArchived("arguana", archivedAt: "20250504011344"),
            "8ad3e3c2a5867cdced806d6503f29b99",
            Bm25Ndcg10Ref: 0.315,
            Graded: false,
            PublishedNdcg10Ref: 0.3970,
            PublishedTotalTerms: 969_528,
            Documents: 8_674,
            NonEmptyDocuments: 8_673,
            PublishedSource: "https://github.com/castorini/anserini/blob/master/src/main/resources/reproduce/from-document-collection/configs/beir-v1.0.0-arguana.flat.yaml"),
        // Not part of --dataset all: the three above are the pinned comparison, and this one is the
        // candidate gate's evidence on a corpus of another nature (financial Q&A, short queries).
        // The 38 documents with no text are why Anserini indexes 57,600 of the file's 57,638.
        new(
            "fiqa",
            PublishedAndArchived("fiqa", archivedAt: "20250504011332"),
            "17918ed23cd04fb15047f73e6c3bd9d9",
            Bm25Ndcg10Ref: 0.236,
            Graded: false,
            PublishedNdcg10Ref: 0.2361,
            PublishedTotalTerms: 5_288_635,
            Documents: 57_600,
            NonEmptyDocuments: 57_600,
            PublishedSource: "https://github.com/castorini/anserini/blob/master/src/main/resources/reproduce/from-document-collection/configs/beir-v1.0.0-fiqa.flat.yaml",
            InAll: false),
    ];

    /// <summary>
    /// The published download for a corpus, then the Internet Archive's capture of that same URL.
    /// </summary>
    /// <remarks>
    /// The archive is not a second source of the corpus, it is a second copy of the same file: the
    /// zip is still verified against <see cref="ExpectedMd5"/>, so a capture that held different
    /// bytes would be rejected rather than indexed. The timestamp names one capture, and the
    /// <c>id_</c> suffix asks for the raw bytes instead of the archive's rewritten HTML page around
    /// them. A fallback is worth the second URL because the published host is one unmirrored
    /// fileserver: on 2026-11-21 it answered nothing for hours and a push turned the pinned
    /// reference red with no commit to blame, which is the one failure this gate must not have.
    /// </remarks>
    private static IReadOnlyList<string> PublishedAndArchived(string name, string archivedAt) =>
    [
        $"https://public.ukp.informatik.tu-darmstadt.de/thakur/BEIR/datasets/{name}.zip",
        $"https://web.archive.org/web/{archivedAt}id_/https://public.ukp.informatik.tu-darmstadt.de/thakur/BEIR/datasets/{name}.zip",
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
    public static Task<BeirCorpus> LoadOrDownloadAsync(string dataBaseDir, BeirDataset dataset) =>
        LoadOrDownloadAsync(dataBaseDir, dataset.Name, dataset.DownloadUrls, dataset.ExpectedMd5);

    /// <summary>
    /// The same load for a corpus that has no published quality figure.
    /// </summary>
    /// <remarks>
    /// A corpus in <see cref="BeirDataset.All"/> carries an expected nDCG because it is a row in the
    /// quality table. A benchmark lane's real documents are not: measuring the cursor pass's density
    /// crossing asks what 171,332 real documents cost to search, not what BM25 scores on them, and
    /// giving that corpus a quality figure to satisfy a constructor would put a number in the tree
    /// that nothing measures. So the download-and-parse half takes the three things it actually uses.
    /// </remarks>
    public static Task<BeirCorpus> LoadOrDownloadAsync(
        string dataBaseDir, string name, string downloadUrl, string expectedMd5) =>
        LoadOrDownloadAsync(dataBaseDir, name, [downloadUrl], expectedMd5);

    /// <summary>
    /// The download-and-parse half, with every source that serves the same artifact.
    /// </summary>
    /// <remarks>
    /// The sources are tried in order and each one is checked against <paramref name="expectedMd5"/>
    /// before it is extracted, so a list is a list of ways to fetch one corpus. Adding a corpus with
    /// no published figure (a benchmark lane's) passes a single URL and gets the same checksum gate.
    /// </remarks>
    public static async Task<BeirCorpus> LoadOrDownloadAsync(
        string dataBaseDir, string name, IReadOnlyList<string> downloadUrls, string expectedMd5)
    {
        LexiSharp.PathSafety.ThrowIfUnsafe(dataBaseDir, nameof(dataBaseDir));

        string root = await EnsureDatasetAsync(dataBaseDir, name, downloadUrls, expectedMd5);

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
            Name = name,
            Documents = documents,
            Queries = queries,
            TestRelevance = relevance,
        };
    }

    /// <summary>Returns the directory containing the BEIR-formatted files, downloading them first when missing.</summary>
    private static async Task<string> EnsureDatasetAsync(
        string dataBaseDir, string name, IReadOnlyList<string> downloadUrls, string expectedMd5)
    {
        string nested = Path.Combine(dataBaseDir, name);

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
        string zipPath = Path.Combine(dataBaseDir, $"{name}.zip");

        await DownloadVerifiedAsync(name, downloadUrls, expectedMd5, zipPath);

        Console.WriteLine("Download complete, checksum verified.");
        ZipFile.ExtractToDirectory(zipPath, dataBaseDir, overwriteFiles: true);
        File.Delete(zipPath);

        return Directory.Exists(nested) ? nested : dataBaseDir;
    }

    /// <summary>
    /// Fetches the zip from the first source that serves <paramref name="expectedMd5"/>'s bytes.
    /// </summary>
    /// <remarks>
    /// A source that answers with a different file is skipped and named in the failure rather than
    /// silently accepted: the hash is what makes a number a pin, and it is also what makes the
    /// second source a copy of the same corpus instead of a different one. A mismatch is reported as
    /// a mismatch even when another source merely failed to answer, because "the archive serves a
    /// different file" and "the host is down" need different fixes.
    /// </remarks>
    private static async Task DownloadVerifiedAsync(
        string name, IReadOnlyList<string> downloadUrls, string expectedMd5, string zipPath)
    {
        var failures = new List<string>();
        bool checksumMismatch = false;

        // 100 s is the HttpClient default, and it covers the whole exchange: a large zip on a busy
        // runner can exceed it, and a host that does not answer leaves the connect hanging until it
        // expires. Both used to arrive as a cancelled task and an abort rather than as a network
        // error. Ten minutes matches the 20 Newsgroups download next door; the retry covers a
        // transient drop, and the source after the retries covers a host that is simply gone.
        //
        // The connect gets its own, much shorter budget. It is the part that hangs when a host is
        // gone (2026-11-21: three attempts of a ten-minute timeout each, with nothing to show), and
        // no timeout can separate it from a slow body. Fifteen seconds is generous for a TCP
        // handshake and TLS, and it turns "the published host is down" into a fallback that starts
        // in under a minute instead of half an hour.
        using var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };

        const int attempts = 3;

        foreach (string downloadUrl in downloadUrls)
        {
            Console.WriteLine($"Downloading {name} (BEIR) from {downloadUrl}");

            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    await using (Stream response = await client.GetStreamAsync(downloadUrl))
                    await using (FileStream zip = File.Create(zipPath))
                        await response.CopyToAsync(zip);

                    string actualMd5 = await ComputeMd5Async(zipPath);

                    if (string.Equals(actualMd5, expectedMd5, StringComparison.OrdinalIgnoreCase))
                        return;

                    // The next source, not another attempt at this one: the same URL answering with
                    // the same wrong bytes twenty seconds later is the expected outcome of a capture
                    // that drifted, and it is not what a retry is for.
                    File.Delete(zipPath);
                    checksumMismatch = true;
                    failures.Add($"{downloadUrl}: checksum mismatch, expected {expectedMd5}, got {actualMd5}");
                    break;
                }
                catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
                {
                    failures.Add($"{downloadUrl}: {exception.Message}");

                    if (attempt == attempts)
                        break;

                    Console.WriteLine($"Download failed ({exception.Message}); retrying ({attempt}/{attempts}).");
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
                }
            }
        }

        string detail = string.Join("; ", failures);

        if (checksumMismatch)
        {
            throw new InvalidOperationException(
                $"{name} download checksum mismatch — no source served the file the pin names ({detail}).");
        }

        throw new HttpRequestException($"{name} could not be downloaded from any source ({detail}).");
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