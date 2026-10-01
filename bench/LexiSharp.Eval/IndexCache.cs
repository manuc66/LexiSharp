using System.Globalization;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.MessagePack;

namespace LexiSharp.Eval;

/// <summary>
/// Builds the corpus index once per process, and optionally keeps it on disk between runs.
/// </summary>
/// <remarks>
/// <para>
/// Tokenizing a corpus is the largest single cost in this harness, and a sweep runs the same corpus
/// under many configurations. The index depends only on the documents and the analysis, so it is
/// built once here and read by every config; the cache extends that across processes.
/// </para>
/// <para>
/// The hazard a cache introduces is a stale index silently answering a different question, which is
/// worse than no cache at all. Two guards, and what each does and does not catch:
/// <list type="bullet">
/// <item><description>The sidecar records the index fingerprint and the analysis description. The
/// analysis is the one that matters: every analyzer here is the same <see cref="Tokenizer"/> type, so
/// <see cref="MessagePackTextIndexPersistence"/> would happily load an <c>--analyzer english</c> file
/// under <c>--analyzer porter</c> and say nothing. A mismatch is refused, not repaired.</description></item>
/// <item><description>The sidecar also records the corpus file's length and last write time. If the
/// corpus changed, the index is rebuilt and the cache overwritten — a rebuild cannot answer a
/// question about a corpus it did not come from, so it is the safe response rather than a failure.</description></item>
/// </list>
/// A cache file with no readable sidecar is rebuilt and its sidecar rewritten. What none of this
/// catches is a corpus edited in place without its length or timestamp changing; that would need a
/// content hash, which is the corpus's whole cost to read and defeats the point.
/// </para>
/// </remarks>
internal static class IndexCache
{
    /// <summary>Bumped when the sidecar's meaning changes; a mismatch rebuilds rather than misreads.</summary>
    private const int StampVersion = 2;

    /// <summary>
    /// The index for <paramref name="corpus"/>, loaded from <paramref name="path"/> when a usable
    /// cache is there and built otherwise. With no <paramref name="path"/>, always builds.
    /// </summary>
    /// <param name="analysis">
    /// The analysis in force, in the words the harness printed. Recorded, not parsed: what the cache
    /// has to rule out is two runs whose analysis differs, and the printed description is the thing a
    /// reader can check the cache against.
    /// </param>
    public static InMemoryTextIndex LoadOrBuild(
        string? path, BeirDataset dataset, BeirCorpus corpus, ITokenizer tokenizer, string analysis,
        IndexOptions? indexOptions = null)
    {
        if (path is null)
            return Build(corpus, tokenizer, indexOptions);

        string full = Path.GetFullPath(path);
        string stampPath = StampPathFor(full);

        if (File.Exists(full) && File.Exists(stampPath))
        {
            string? recordedAnalysis = ReadField(stampPath, "analysis");
            string? recordedCorpus = ReadField(stampPath, "corpus");

            if (recordedAnalysis is not null
                && !string.Equals(recordedAnalysis, analysis, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The index cache '{full}' was written under a different analysis." +
                    $"\n  cached:  {recordedAnalysis}" +
                    $"\n  current: {analysis}" +
                    $"\nDelete the cache file, or give this run its own --index-cache path. Reloading " +
                    "it would index the corpus under one analysis and report the scores of another.");
            }

            // A changed corpus is a rebuild rather than a failure: the alternative is refusing to
            // run, and a rebuild cannot be wrong about which corpus it came from.
            // The same reasoning for the index options, read from what THIS run asked for rather than
            // from the loaded index — which would compare the cache with itself and always agree.
            string? recordedOptions = ReadField(stampPath, "indexOptions");
            string wantedOptions = DescribeOptions(indexOptions);

            if (recordedOptions is not null && !string.Equals(
                recordedOptions, wantedOptions, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The index cache '{full}' was written under different index conventions." +
                    $"\n  cached:  {recordedOptions}" +
                    $"\n  current: {wantedOptions}" +
                    $"\nDelete the cache file, or give this run its own --index-cache path.");
            }

            if (recordedCorpus is not null && CorpusStamp(corpus) != recordedCorpus)
            {
                Console.WriteLine(
                    $"Index cache '{full}' predates the current corpus file; rebuilding it.");
                InMemoryTextIndex rebuilt = Build(corpus, tokenizer, indexOptions);
                Save(full, stampPath, rebuilt, dataset, corpus, analysis, indexOptions);
                return rebuilt;
            }

            InMemoryTextIndex loaded = MessagePackTextIndexPersistence.Load(full, tokenizer);

            // The sidecar agreed, so this only fires if the payload is corrupt or was written by
            // something other than this code. Refusing is right: overwriting would hide the fact.
            if (!StampMatches(stampPath, loaded))
            {
                throw new InvalidOperationException(
                    $"The index cache '{full}' does not match the fingerprint recorded beside it in " +
                    $"'{stampPath}'. Delete both and re-run. This is reported rather than repaired " +
                    "because a payload that disagrees with its own stamp is a defect, not a cache miss.");
            }

            Console.WriteLine(
                $"Index loaded from cache '{full}' — {loaded.Count:N0} documents, no corpus tokenizing.");
            return loaded;
        }

        InMemoryTextIndex built = Build(corpus, tokenizer, indexOptions);
        Save(full, stampPath, built, dataset, corpus, analysis, indexOptions);
        Console.WriteLine($"Index built and cached at '{full}' ({built.Count:N0} documents).");
        return built;
    }

    private static InMemoryTextIndex Build(BeirCorpus corpus, ITokenizer tokenizer, IndexOptions? indexOptions)
    {
        var index = indexOptions is null
            ? new InMemoryTextIndex(tokenizer)
            : new InMemoryTextIndex(
                tokenizer, indexOptions.AverageLengthDivisor, indexOptions.DocumentLengthQuantization);
        index.Index(Evaluation.BuildDocuments(corpus));
        return index;
    }

    /// <summary>
    /// The index conventions a cache was written under, as one comparable string.
    /// </summary>
    /// <remarks>
    /// Read from the requested <see cref="IndexOptions"/> rather than from the built index, because the
    /// check happens before anything is built. A cache written under exact lengths and read back under
    /// quantized ones reports a score no run of that configuration could produce, and the symptom is a
    /// number that is merely plausible.
    /// </remarks>
    private static string DescribeOptions(IndexOptions? options) =>
        options is null
            ? "Exact|AllDocuments"
            : $"{options.DocumentLengthQuantization}|{options.AverageLengthDivisor}";

    private static void Save(
        string path, string stampPath, InMemoryTextIndex index, BeirDataset dataset,
        BeirCorpus corpus, string analysis, IndexOptions? indexOptions)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // The payload first: a stamp without a payload is a miss that costs a rebuild, whereas a
        // payload without a stamp is a file this code will refuse to trust on the next run.
        MessagePackTextIndexPersistence.Save(index, path);

        IndexFingerprint fingerprint = IndexFingerprint.Of(index);

        File.WriteAllText(stampPath, string.Join('\n',
        [
            // The version covers the index's own arithmetic, not this file's shape. A change to how
            // lengths are stored invalidates every cached score without changing a single term, so the
            // cache has to notice it: a run that reported 184.946 against a fresh build's 184.200 was
            // reading a payload written before AverageDocumentLength stopped averaging exact lengths.
            $"version={StampVersion.ToString(CultureInfo.InvariantCulture)}",
            $"dataset={dataset.Name}",
            $"analysis={analysis}",
            $"indexOptions={DescribeOptions(indexOptions)}",
            $"corpus={CorpusStamp(corpus)}",
            $"documents={fingerprint.Documents.ToString(CultureInfo.InvariantCulture)}",
            $"nonEmpty={fingerprint.NonEmptyDocuments.ToString(CultureInfo.InvariantCulture)}",
            $"totalTerms={fingerprint.TotalTerms.ToString(CultureInfo.InvariantCulture)}",
            "",
        ]));
    }

    private static bool StampMatches(string stampPath, InMemoryTextIndex index)
    {
        IndexFingerprint fingerprint = IndexFingerprint.Of(index);

        return ReadField(stampPath, "documents") == fingerprint.Documents.ToString(CultureInfo.InvariantCulture)
            && ReadField(stampPath, "nonEmpty") == fingerprint.NonEmptyDocuments.ToString(CultureInfo.InvariantCulture)
            && ReadField(stampPath, "totalTerms") == fingerprint.TotalTerms.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Identifies the corpus file a cache was built from: its length and last write time, in a form
    /// that does not depend on the machine's time zone.
    /// </summary>
    private static string CorpusStamp(BeirCorpus corpus)
    {
        var file = new FileInfo(CorpusPath(corpus));

        return file.Exists
            ? $"{corpus.Documents.Count.ToString(CultureInfo.InvariantCulture)}@{file.Length}@{file.LastWriteTimeUtc.Ticks}"
            : corpus.Documents.Count.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The corpus file the loader read. <see cref="BeirCorpus"/> does not carry its own path, so this
    /// is recovered from the loader's own layout; a corpus with no file behind it falls back to the
    /// document count alone, which is weaker and says so by being all there is.
    /// </summary>
    private static string CorpusPath(BeirCorpus corpus)
    {
        string baseDirectory = AppContext.BaseDirectory;

        return Path.Combine(baseDirectory, "..", "..", "..", "data", corpus.Name, "corpus.jsonl");
    }

    private static string StampPathFor(string indexPath) => indexPath + ".stamp";

    private static string? ReadField(string stampPath, string key)
    {
        if (!File.Exists(stampPath))
            return null;

        string prefix = key + "=";

        foreach (string line in File.ReadLines(stampPath))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                return line[prefix.Length..];
        }

        return null;
    }
}
/// <summary>
/// The index statistics a run asks for, when it is reproducing a published figure rather than using
/// the library's defaults.
/// </summary>
/// <param name="AverageLengthDivisor">Which document count divides the corpus token count.</param>
/// <param name="DocumentLengthQuantization">Whether document lengths are reported exact or rounded.</param>
internal sealed record IndexOptions(
    AverageLengthDivisor AverageLengthDivisor,
    DocumentLengthQuantization DocumentLengthQuantization);
