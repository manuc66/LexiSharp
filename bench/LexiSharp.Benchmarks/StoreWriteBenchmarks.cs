using BenchmarkDotNet.Attributes;
using LexiSharp.Core;
using LexiSharp.Indexing;

namespace LexiSharp.Benchmarks;

/// <summary>
/// The write path: the overlay, the write-ahead journal, and a corpus written into a segment on disk.
/// </summary>
/// <remarks>
/// <para>
/// The one operation this repository measured once and drew the wrong conclusion from. Every query
/// shape measured since has repaid its measurement; the write path has repaid it three times, with a
/// quadratic in the overlay's position collection, a segment materialized three times, and a
/// string encoded through a byte[] that was then copied.
/// </para>
/// <para>
/// Read the <c>Allocated</c> column for what the write costs in churn and the <c>Mean</c> column for
/// what it costs in time — and note that reducing the first by 30% did not move the second at all, so
/// the two are not the same question.
/// </para>
/// <para>
/// Its own class because it needs a corpus no other class here builds: a large vocabulary, so the term
/// table and the per-term postings are the width a real corpus's are.
/// </para>
/// <para>
/// The <c>AddWithJournal*</c> rows price one <c>Add</c> under each <see cref="JournalFlushPolicy"/>,
/// over the same corpus cut to 500 documents. They write into a directory of their own per invocation,
/// on the assembly's filesystem rather than <c>Path.GetTempPath()</c> — <c>/tmp</c> is a tmpfs on this
/// host, where a device flush is nearly free — and they are run one row per process, with
/// <c>--job medium</c>, so a row's iterations are not interleaved with another policy's. The policy
/// table in <c>docs/indexing.md</c> is the reading, with the command that reproduces it.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class StoreWriteBenchmarks
{
    private const int DocumentCount = 20_000;
    private const int VocabularySize = 100_000;
    private const int WordsPerDocument = 124;

    private SearchDocument[] _documents = null!;
    private string _directory = null!;
    private string _journalRoot = null!;

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(7);
        var words = new string[VocabularySize];

        for (int i = 0; i < words.Length; i++)
            words[i] = "term" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);

        _documents = new SearchDocument[DocumentCount];

        for (int i = 0; i < _documents.Length; i++)
        {
            var builder = new System.Text.StringBuilder(WordsPerDocument * 8);

            for (int w = 0; w < WordsPerDocument; w++)
            {
                builder.Append(words[random.Next(words.Length)]);
                builder.Append(' ');
            }

            _documents[i] = new SearchDocument(
                "doc-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                builder.ToString());
        }

        _directory = Path.Combine(Path.GetTempPath(), "lexisharp-write-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        // The journal rows write here rather than under Path.GetTempPath(). On this host /tmp is a
        // tmpfs, where a device flush is nearly free, so a row measured there would report a flush cost
        // the journal never paid. The assembly's own directory is on the filesystem the repository
        // lives on, and it is where the rows below are meant to be read.
        _journalRoot = Path.Combine(AppContext.BaseDirectory, "journal-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_journalRoot);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);

        if (Directory.Exists(_journalRoot))
            Directory.Delete(_journalRoot, recursive: true);
    }

    /// <summary>Build the corpus into the overlay and write it as one segment.</summary>
    [Benchmark]
    public int Commit()
    {
        var store = new SegmentedTextIndex(_directory);

        store.Index(_documents);
        using var committed = store.Commit();

        return committed.Count;
    }

    /// <summary>The overlay half alone: tokenizing the corpus, with no segment written.</summary>
    [Benchmark]
    public int Index()
    {
        var store = new SegmentedTextIndex(_directory);
        store.Index(_documents);

        return store.Count;
    }

    /// <summary>Documents the journal rows add: five grouped flushes, and short enough to run often.</summary>
    private const int JournalDocuments = 500;

    private static readonly JournalOptions JournalOff = JournalOptions.Disabled;
    private static readonly JournalOptions JournalGrouped = JournalOptions.Default;
    private static readonly JournalOptions JournalPerWrite = new() { Flush = JournalFlushPolicy.PerWrite };
    private static readonly JournalOptions JournalOnCommit = new() { Flush = JournalFlushPolicy.OnCommit };

    /// <summary>One <c>Add</c> with no journal: the baseline the journal's cost is measured against.</summary>
    [Benchmark(OperationsPerInvoke = JournalDocuments)]
    public int AddWithJournalDisabled() => AddDocuments(JournalOff);

    /// <summary>One <c>Add</c> under the default journal: a hundred entries or fifty milliseconds per flush.</summary>
    [Benchmark(OperationsPerInvoke = JournalDocuments)]
    public int AddWithJournalGrouped() => AddDocuments(JournalGrouped);

    /// <summary>One <c>Add</c> under a journal that forces every entry to the device before returning.</summary>
    [Benchmark(OperationsPerInvoke = JournalDocuments)]
    public int AddWithJournalPerWrite() => AddDocuments(JournalPerWrite);

    /// <summary>One <c>Add</c> under a journal that reaches the operating system and flushes at commit.</summary>
    [Benchmark(OperationsPerInvoke = JournalDocuments)]
    public int AddWithJournalOnCommit() => AddDocuments(JournalOnCommit);

    private int AddDocuments(JournalOptions options)
    {
        string directory = Path.Combine(_journalRoot, Guid.NewGuid().ToString("N"));

        try
        {
            var store = new SegmentedTextIndex(directory, journal: options);

            for (int i = 0; i < JournalDocuments; i++)
                store.Add(_documents[i]);

            // Disposal is part of the policy's cost: a grouped journal flushes its tail there, and a
            // row that excluded it would price a store that nobody closes.
            store.Dispose();

            return JournalDocuments;
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
