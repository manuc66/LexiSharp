using BenchmarkDotNet.Attributes;
using LexiSharp.Core;
using LexiSharp.Indexing;

namespace LexiSharp.Benchmarks;

/// <summary>
/// The write path: building a corpus into a segment on disk.
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
/// </remarks>
[MemoryDiagnoser]
public class StoreWriteBenchmarks
{
    private const int DocumentCount = 20_000;
    private const int VocabularySize = 100_000;
    private const int WordsPerDocument = 124;

    private SearchDocument[] _documents = null!;
    private string _directory = null!;

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
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
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
}
