using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace LexiSharp.Eval;

/// <summary>Which part of a message the classifier sees.</summary>
internal enum ClassificationTextMode
{
    /// <summary>The subject and the body, with every header removed.</summary>
    SubjectAndBody,

    /// <summary>The subject line only — the short-text variant of the corpus.</summary>
    SubjectOnly,
}

/// <summary>One labelled document of the 20 Newsgroups corpus.</summary>
internal sealed record ClassificationDocument(string Id, string Text, string Category);

/// <summary>The train/test split of the 20 Newsgroups corpus, after header removal and deduplication.</summary>
internal sealed record ClassificationCorpus(
    string Name,
    string SourceMd5,
    IReadOnlyList<ClassificationDocument> Train,
    IReadOnlyList<ClassificationDocument> Test,
    IReadOnlyList<string> Categories);

/// <summary>
/// Downloads, extracts and parses the 20 Newsgroups « bydate » split.
/// </summary>
/// <remarks>
/// The corpus is the standard 11 314 / 7 532 split, one directory per category. Only the
/// <c>Subject</c> header and the body survive: every other header is dropped, and in particular
/// <c>Newsgroups</c>, which would hand the label to the model. Documents whose extracted text
/// already appeared — in either half — are removed before evaluation, because the corpus carries
/// duplicates and a test document identical to a training one measures memorization.
/// </remarks>
internal static class TwentyNewsgroups
{
    public const string DatasetName = "20news-bydate";
    public const string DownloadUrl = "https://ndownloader.figshare.com/files/5975967";
    public const string ExpectedMd5 = "D6E9E45CB8CB77EC5276DFA6DFC14318";

    /// <summary>Loads the corpus, downloading and extracting it on first use.</summary>
    /// <param name="dataBaseDir">Directory the archive and extracted corpus live in.</param>
    /// <param name="limit">Optional cap per split, drawn by a fixed-seed shuffle for a quick run.</param>
    /// <param name="mode">Whether the model sees the full message or the subject only.</param>
    public static async Task<ClassificationCorpus> LoadOrDownloadAsync(
        string dataBaseDir,
        int? limit = null,
        ClassificationTextMode mode = ClassificationTextMode.SubjectAndBody)
    {
        LexiSharp.PathSafety.ThrowIfUnsafe(dataBaseDir, nameof(dataBaseDir));

        string root = Path.Combine(dataBaseDir, DatasetName);
        string trainDir = Path.Combine(root, "20news-bydate-train");
        string testDir = Path.Combine(root, "20news-bydate-test");

        if (!Directory.Exists(trainDir) || !Directory.Exists(testDir))
            await DownloadAndExtractAsync(dataBaseDir, root);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var train = Deduplicate(ReadSplit(trainDir, mode), seen);
        var test = Deduplicate(ReadSplit(testDir, mode), seen);

        if (limit is { } cap)
        {
            train = DeterministicTake(train, cap);
            test = DeterministicTake(test, cap);
        }

        string[] categories = train
            .Select(document => document.Category)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(category => category, StringComparer.Ordinal)
            .ToArray();

        string name = mode == ClassificationTextMode.SubjectOnly
            ? DatasetName + "-subject"
            : DatasetName;

        return new ClassificationCorpus(name, ExpectedMd5, train, test, categories);
    }

    /// <summary>
    /// Reduces one raw message to the text the classifier sees: the subject and the body, with
    /// every header removed.
    /// </summary>
    /// <param name="raw">The file as it appears in the corpus.</param>
    internal static string ParseMessage(string raw) =>
        ParseMessage(raw, ClassificationTextMode.SubjectAndBody);

    /// <summary>
    /// Reduces one raw message to the text the classifier sees, according to
    /// <paramref name="mode"/>.
    /// </summary>
    /// <param name="raw">The file as it appears in the corpus.</param>
    /// <param name="mode">Whether the body is kept.</param>
    internal static string ParseMessage(string raw, ClassificationTextMode mode)
    {
        ArgumentNullException.ThrowIfNull(raw);

        string normalized = raw.Replace("\r\n", "\n", StringComparison.Ordinal);
        int separator = normalized.IndexOf("\n\n", StringComparison.Ordinal);
        string headers = separator >= 0 ? normalized[..separator] : normalized;
        string body = separator >= 0 ? normalized[(separator + 2)..] : string.Empty;

        string? subject = null;

        foreach (string line in headers.Split('\n'))
        {
            if (!line.StartsWith("Subject:", StringComparison.OrdinalIgnoreCase))
                continue;

            subject = line["Subject:".Length..].Trim();
            break;
        }

        if (mode == ClassificationTextMode.SubjectOnly)
            return subject ?? string.Empty;

        body = body.Trim();

        if (string.IsNullOrEmpty(subject))
            return body;

        return body.Length == 0 ? subject : subject + "\n" + body;
    }

    /// <summary>Extracts a gzipped tar into <paramref name="destinationRoot"/>, refusing traversal.</summary>
    /// <param name="archivePath">The <c>.tar.gz</c> file.</param>
    /// <param name="destinationRoot">Directory the entries are written under.</param>
    internal static void Extract(string archivePath, string destinationRoot)
    {
        LexiSharp.PathSafety.ThrowIfUnsafe(archivePath, nameof(archivePath));
        LexiSharp.PathSafety.ThrowIfUnsafe(destinationRoot, nameof(destinationRoot));

        Directory.CreateDirectory(destinationRoot);

        string rootFull = Path.GetFullPath(destinationRoot) + Path.DirectorySeparatorChar;

        using FileStream file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);

        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.DataStream is null)
                continue;

            string destination = Path.GetFullPath(Path.Combine(destinationRoot, entry.Name));

            if (!destination.StartsWith(rootFull, StringComparison.Ordinal))
                throw new InvalidOperationException($"Archive entry escapes the destination: {entry.Name}");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            using FileStream output = File.Create(destination);
            entry.DataStream.CopyTo(output);
        }
    }

    private static List<ClassificationDocument> ReadSplit(string root, ClassificationTextMode mode)
    {
        var documents = new List<ClassificationDocument>();

        foreach (string categoryDirectory in Directory
                     .EnumerateDirectories(root)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            string category = Path.GetFileName(categoryDirectory);

            foreach (string file in Directory
                         .EnumerateFiles(categoryDirectory)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                string text = ParseMessage(File.ReadAllText(file), mode);

                if (text.Length == 0)
                    continue;

                string name = Path.GetFileName(file);
                documents.Add(new ClassificationDocument(category + "/" + name, text, category));
            }
        }

        return documents;
    }

    private static List<ClassificationDocument> Deduplicate(
        List<ClassificationDocument> documents,
        HashSet<string> seen)
    {
        var unique = new List<ClassificationDocument>(documents.Count);

        foreach (var document in documents)
        {
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document.Text)));

            if (seen.Add(hash))
                unique.Add(document);
        }

        return unique;
    }

    private static List<ClassificationDocument> DeterministicTake(
        List<ClassificationDocument> documents,
        int count)
    {
        if (documents.Count <= count)
            return documents;

        var shuffled = new List<ClassificationDocument>(documents);
        var random = new Random(7); // NOSONAR:S2245 (fixed seed: the subset is part of the reproduction)

        for (int i = shuffled.Count - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        return shuffled.GetRange(0, count);
    }

    private static async Task DownloadAndExtractAsync(string dataBaseDir, string root)
    {
        Directory.CreateDirectory(dataBaseDir);
        string archivePath = Path.Combine(dataBaseDir, DatasetName + ".tar.gz");

        if (!File.Exists(archivePath) || !string.Equals(await ComputeMd5Async(archivePath), ExpectedMd5, StringComparison.OrdinalIgnoreCase))
        {
            string temporary = archivePath + ".tmp";

            Console.WriteLine($"Downloading {DatasetName} from {DownloadUrl}");

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            await using (Stream response = await client.GetStreamAsync(DownloadUrl))
            await using (FileStream file = File.Create(temporary))
                await response.CopyToAsync(file);

            string actualMd5 = await ComputeMd5Async(temporary);

            if (!string.Equals(actualMd5, ExpectedMd5, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(temporary);
                throw new InvalidOperationException(
                    $"{DatasetName} download checksum mismatch: expected {ExpectedMd5}, got {actualMd5}.");
            }

            File.Move(temporary, archivePath, overwrite: true);
            Console.WriteLine("Download complete, checksum verified.");
        }

        Console.WriteLine("Extracting...");
        Extract(archivePath, root);
    }

    private static async Task<string> ComputeMd5Async(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(await MD5.HashDataAsync(stream));
    }
}
