using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;

namespace LexiSharp.Benchmarks;

/// <summary>
/// A host that does nothing but the operation, so a sampler reads the work rather than the harness.
/// </summary>
/// <remarks>
/// A profile taken under BenchmarkDotNet measures the harness's forced collections, and one taken inside
/// dotnet test measures a test host's idle thread pool. Both were tried; this is the third instrument.
/// Temporary: it goes when the measurement it exists for is recorded.
/// </remarks>
internal static class ProfileHost
{
    /// <summary>
    /// The block-bound pruning A/B under a sampler: one query in a loop, pruned or not, on the corpus
    /// the cursor lane uses, so a profile reads the traversal rather than the harness.
    /// </summary>
    /// <remarks>
    /// Both sides run the same corpus, the same query and the same engine, and only
    /// <see cref="SearchOptions.PruneByBlockBound"/> differs, so a difference between the two profiles
    /// is the bound's arithmetic and nothing else. The warm-up matters more here than in a timed run:
    /// a sampler that lands on tier-0 code reads the JIT, not the pass.
    /// </remarks>
    public static void RunPruning(bool prune, int seconds)
    {
        var index = new InMemoryTextIndex();
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        engine.Index(CorpusFactory.CreateZipfDocuments(100_000, 50, 30_000));

        var options = new SearchOptions(Limit: 10) { PruneByBlockBound = prune };
        const string query = "w20000x w25000x w29000x";

        var warmUp = System.Diagnostics.Stopwatch.StartNew();
        long sink = 0;

        while (warmUp.Elapsed.TotalSeconds < 5)
            sink += engine.Search(query, options).Count;

        var run = System.Diagnostics.Stopwatch.StartNew();
        long calls = 0;

        long before = GC.GetAllocatedBytesForCurrentThread();

        while (run.Elapsed.TotalSeconds < seconds)
        {
            sink += engine.Search(query, options).Count;
            calls++;
        }

        long allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / Math.Max(calls, 1);

        Console.WriteLine(
            $"prune={prune} calls={calls} ns/call={run.Elapsed.TotalNanoseconds / calls:N1} " +
            $"bytes/call={allocated} sink={sink}");
    }

    public static void Run(int iterations)
    {
        var random = new Random(7);
        var words = new string[100_000];

        for (int i = 0; i < words.Length; i++)
            words[i] = "term" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var documents = new SearchDocument[100_000];

        for (int i = 0; i < documents.Length; i++)
        {
            var builder = new System.Text.StringBuilder(1200);

            for (int w = 0; w < 124; w++)
            {
                builder.Append(words[random.Next(words.Length)]);
                builder.Append(' ');
            }

            documents[i] = new SearchDocument(
                "doc-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), builder.ToString());
        }

        for (int i = 0; i < iterations; i++)
        {
            var store = new SegmentedTextIndex(Path.Combine(Path.GetTempPath(), "lexisharp-profile-" + i));
            store.Index(documents);

            Console.WriteLine($"iteration {i} done");
        }
    }
}
