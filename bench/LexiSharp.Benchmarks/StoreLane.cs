using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;

namespace LexiSharp.Benchmarks;

/// <summary>
/// What the path a deployment runs costs per query: write, commit, reopen, search.
/// </summary>
/// <remarks>
/// <para>
/// Every latency this repository publishes is measured on <see cref="InMemoryTextIndex"/>, built in the
/// process that times it. That is the right instrument for comparing scorers and scoring passes, and it
/// is not the path a service runs: a corpus that survives a restart lives in
/// <see cref="SegmentedTextIndex"/>, and the number a reader needs is what a query costs after a reopen.
/// </para>
/// <para>
/// Four configurations, all on the same documents and the same three queries as the cursor lane: the
/// in-memory index as the baseline, the store opened as an array, the same with a decoded-postings
/// cache, and the store mapped. The cache row exists because it changes which scoring pass can run — a
/// store without one cannot lend flat posting arrays, so it keeps the term-at-a-time accumulator and
/// forfeits the cursor pass's tail win entirely.
/// </para>
/// <para>
/// The write is measured with the journal disabled, and it is not this lane's subject: the journal's
/// cost per write under each flush policy is measured by <see cref="StoreWriteBenchmarks"/> and read on
/// <c>docs/indexing.md</c>. The store directory is created beside the assembly rather than under
/// <c>Path.GetTempPath()</c>, because <c>/tmp</c> is a tmpfs on this host, where a mapped read would be
/// a memory read and the segment write would not touch the device a deployment uses.
/// </para>
/// </remarks>
internal static class StoreLane
{
    private const int WordsPerDocument = 50;
    private const int VocabularySize = 30_000;
    private const int Limit = 10;

    /// <summary>
    /// Posting entries each segment may hold decoded. Enough for the head term of a 100,000-document
    /// corpus (95,402 entries) and not for a 500,000-document one (477,361), which is the point: the
    /// row shows what the cache buys and the number says when it stops covering the hot term.
    /// </summary>
    private const int DecodedCacheEntries = 200_000;

    private static readonly TimeSpan WarmUpDuration = TimeSpan.FromSeconds(5);

    private static readonly (string Label, string Text)[] Queries =
    [
        ("one tail term", "w20000x"),
        ("three tail terms", "w20000x w25000x w29000x"),
        ("one head term", "w1x"),
    ];

    private static readonly int[] DefaultDocumentCounts = [10_000, 100_000];

    private static long _sink;

    public static void Run(string[] args)
    {
        int[] sizes = Parse(args);

        PrintHeader(sizes);
        WarmUp();

        foreach (int documents in sizes)
            Measure(documents);
    }

    private static void WarmUp()
    {
        SearchDocument[] documents = CorpusFactory.CreateZipfDocuments(1_000, WordsPerDocument, VocabularySize);

        var engine = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());
        engine.Index(documents);
        _sink += engine.Search("w20000x", new SearchOptions(Limit: Limit)).Count;

        string directory = Path.Combine(AppContext.BaseDirectory, "store-bench-warm-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);

        try
        {
            using var writer = new SegmentedTextIndex(directory, journal: JournalOptions.Disabled);
            writer.Index(documents);

            using SegmentedTextIndex committed = writer.Commit();
            _sink += new RankedTextSearchEngine(committed, new Bm25Scorer())
                .Search("w20000x", new SearchOptions(Limit: Limit)).Count;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        Console.WriteLine("warm-up   1,000 documents indexed in memory and in a store, one search each (not measured)");
    }

    private static void Measure(int documentCount)
    {
        var generation = Stopwatch.StartNew();
        SearchDocument[] documents = CorpusFactory.CreateZipfDocuments(documentCount, WordsPerDocument, VocabularySize);
        generation.Stop();

        string directory = Path.Combine(AppContext.BaseDirectory, "store-bench-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);

        try
        {
            Console.WriteLine();
            Console.WriteLine($"--- {Number(documentCount)} documents ---");
            Console.WriteLine($"host load                {LoadAverage()}");
            Console.WriteLine($"corpus generation        {Time(generation.Elapsed.TotalMicroseconds)}");

            var memory = new InMemoryTextIndex();
            var build = Stopwatch.StartNew();
            memory.Index(documents);
            build.Stop();

            Console.WriteLine($"in-memory build          {Time(build.Elapsed.TotalMicroseconds)}");

            var write = Stopwatch.StartNew();
            using (var writer = new SegmentedTextIndex(directory, journal: JournalOptions.Disabled))
            {
                writer.Index(documents);
                write.Stop();

                var commit = Stopwatch.StartNew();
                using SegmentedTextIndex committed = writer.Commit();
                commit.Stop();

                Console.WriteLine($"store write              {Time(write.Elapsed.TotalMicroseconds)}  (overlay, journal disabled)");
                Console.WriteLine(
                    $"store commit             {Time(commit.Elapsed.TotalMicroseconds)}  " +
                    $"({Bytes(DirectorySize(directory))} in {CountSegments(directory)} segment file(s))");
            }

            var results = new List<Result>
            {
                MeasureQueries("in-memory", new RankedTextSearchEngine(memory, new Bm25Scorer()), documentCount, TimeSpan.Zero),
            };

            foreach ((string label, SegmentSourceMode mode, int? cache) in Configurations())
            {
                var open = Stopwatch.StartNew();
                using var store = new SegmentedTextIndex(directory, mode: mode, decodedCacheEntries: cache);
                open.Stop();

                results.Add(MeasureQueries(label, new RankedTextSearchEngine(store, new Bm25Scorer()), documentCount, open.Elapsed));
            }

            Print(results, memory.DocumentFrequency("w1x"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The three ways to open the same segment, in the order a reader is likely to try them.
    /// </summary>
    private static IEnumerable<(string Label, SegmentSourceMode Mode, int? Cache)> Configurations()
    {
        yield return ("store-array", SegmentSourceMode.Array, null);
        yield return ("store-array+cache", SegmentSourceMode.Array, DecodedCacheEntries);
        yield return ("store-mapped", SegmentSourceMode.Mapped, null);
    }

    /// <summary>
    /// One engine, the three queries: five seconds of warm-up each, then a fixed count, sorted.
    /// </summary>
    private static Result MeasureQueries(
        string configuration,
        RankedTextSearchEngine engine,
        int documentCount,
        TimeSpan open)
    {
        var options = new SearchOptions(Limit: Limit);
        int iterations = MeasuredIterations(documentCount);

        var p50 = new double[Queries.Length];
        var p99 = new double[Queries.Length];
        var allocated = new long[Queries.Length];

        for (int q = 0; q < Queries.Length; q++)
        {
            string text = Queries[q].Text;

            var warmUp = Stopwatch.StartNew();

            while (warmUp.Elapsed < WarmUpDuration)
                _sink += engine.Search(text, options).Count;

            var microseconds = new double[iterations];
            int hits = 0;

            for (int i = 0; i < iterations; i++)
                microseconds[i] = TimeOne(engine, text, options, ref hits);

            Array.Sort(microseconds);

            long before = GC.GetAllocatedBytesForCurrentThread();

            for (int i = 0; i < iterations; i++)
                _sink += engine.Search(text, options).Count;

            allocated[q] = (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
            p50[q] = microseconds[iterations / 2];
            p99[q] = microseconds[Math.Clamp((int)Math.Ceiling(iterations * 0.99) - 1, 0, iterations - 1)];

            if (hits == 0)
                throw new InvalidOperationException($"'{text}' returned no document through {configuration}");
        }

        return new Result(configuration, open, p50, p99, allocated);
    }

    /// <summary>One search, timed on the calling thread.</summary>
    private static double TimeOne(
        RankedTextSearchEngine engine,
        string query,
        SearchOptions options,
        ref int hits)
    {
        long started = Stopwatch.GetTimestamp();
        int count = engine.Search(query, options).Count;
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMicroseconds;

        _sink += count;
        hits = count;
        return elapsed;
    }

    /// <summary>
    /// The four configurations side by side, which is the whole point: a store row read alone says
    /// nothing about what it costs against the index every other table measures.
    /// </summary>
    private static void Print(List<Result> results, long headTermFrequency)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"open                     {string.Join("  ", results.Skip(1).Select(r => $"{r.Name} {Time(r.Open.TotalMicroseconds)}"))}");
        Console.WriteLine();

        for (int index = 0; index < Queries.Length; index++)
        {
            Console.WriteLine(
                $"query {Queries[index].Label,-16} p50   " +
                string.Join("  ", results.Select(r => $"{r.Name} {Time(r.P50[index])}")));
            Console.WriteLine(
                $"{"",-23} p99   " +
                string.Join("  ", results.Select(r => $"{r.Name} {Time(r.P99[index])}")));
            Console.WriteLine(
                $"{"",-23} alloc " +
                string.Join("  ", results.Select(r => $"{r.Name} {Number(r.Allocated[index])} B")));
            Console.WriteLine();
        }

        Console.WriteLine(
            $"note                     the decoded cache is {Number(DecodedCacheEntries)} posting entries per segment, " +
            $"and this corpus's head term has {Number(headTermFrequency)} entries");
    }

    /// <summary>Bytes a directory's files occupy — what the store costs on disk.</summary>
    private static long DirectorySize(string directory) =>
        new DirectoryInfo(directory).GetFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);

    private static int CountSegments(string directory) =>
        Directory.GetFiles(directory, "segment-*.lxs").Length;

    private static int MeasuredIterations(int documentCount) =>
        documentCount >= 500_000 ? 100 : documentCount >= 100_000 ? 300 : 1_000;

    private static int[] Parse(string[] args)
    {
        var sizes = new List<int>();

        foreach (string arg in args)
        {
            if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count <= 0)
            {
                throw new ArgumentException(
                    $"Unrecognised store argument '{arg}'. Usage: store [document-count ...] " +
                    "(default 10000 100000; 500000 needs about 3.2 GB of managed heap).");
            }

            if (!sizes.Contains(count))
                sizes.Add(count);
        }

        if (sizes.Count == 0)
            sizes.AddRange(DefaultDocumentCounts);

        sizes.Sort();
        return [.. sizes];
    }

    private static void PrintHeader(int[] sizes)
    {
        Console.WriteLine("LexiSharp store lane — write, commit, reopen and query the path a deployment runs");
        Console.WriteLine($"host      {RuntimeInformation.OSDescription} / {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"cpu       {Environment.ProcessorCount} logical processors");
        Console.WriteLine(
            $"runtime   {RuntimeInformation.FrameworkDescription}, ServerGC={GCSettings.IsServerGC}, " +
            $"{(IntPtr.Size == 8 ? "64-bit" : "32-bit")} process");
        Console.WriteLine(
            $"corpus    Zipf 1.07 over {Number(VocabularySize)} terms, {WordsPerDocument} words/document; " +
            "default tokenizer and BM25 (k1 1.5, b 0.75)");
        Console.WriteLine($"queries   {string.Join(" | ", Queries.Select(q => q.Text))}; Limit {Limit}");
        Console.WriteLine($"sizes     {string.Join(", ", sizes.Select(size => Number(size)))}");
        Console.WriteLine($"store     beside the assembly, journal disabled for the write; decoded cache {Number(DecodedCacheEntries)} entries");
        Console.WriteLine("counts    time is a Stopwatch over individual searches, not a BenchmarkDotNet report");
        Console.WriteLine();
    }

    private static string LoadAverage()
    {
        try
        {
            string[] fields = File.ReadAllText("/proc/loadavg")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (fields.Length >= 3)
                return $"{fields[0]} {fields[1]} {fields[2]} (1, 5, 15 minutes)";
        }
        catch (IOException)
        {
            // Fall through.
        }
        catch (UnauthorizedAccessException)
        {
            // Fall through.
        }

        return "unavailable";
    }

    private static string Time(double microseconds) => microseconds >= 1_000
        ? (microseconds / 1_000).ToString("N2", CultureInfo.InvariantCulture) + " ms"
        : microseconds.ToString("N2", CultureInfo.InvariantCulture) + " µs";

    private static string Bytes(long bytes) => bytes >= 1_048_576
        ? (bytes / 1_048_576.0).ToString("N1", CultureInfo.InvariantCulture) + " MB"
        : (bytes / 1_024.0).ToString("N0", CultureInfo.InvariantCulture) + " KB";

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private sealed record Result(string Name, TimeSpan Open, double[] P50, double[] P99, long[] Allocated);
}
