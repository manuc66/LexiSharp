using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;

namespace LexiSharp.Benchmarks;

/// <summary>
/// Index build cost and tail-query cost per corpus size, at sizes BenchmarkDotNet cannot hold.
/// </summary>
/// <remarks>
/// <para>
/// A BenchmarkDotNet job rebuilds its setup between invocations, so a 1,000,000-document index
/// would cost the build time on every launch and hold several gigabytes while it does. This lane
/// builds one index per size, once, in a process that does nothing else, and reports the four
/// numbers a corpus-size question needs: build wall time, retained managed bytes per document,
/// query latency for a one-term and a three-term tail query, and allocation per search.
/// </para>
/// <para>
/// It is a direct run rather than a benchmark class for that reason, and the numbers are therefore
/// a stopwatch in a plain process, not a BenchmarkDotNet report: no warm-up beyond the iteration
/// counts printed with each row, no outlier removal, no forced collections around the measured
/// operation. Read the absolute latencies as host-bound; the byte counts are exact.
/// </para>
/// <para>
/// The corpus is <see cref="CorpusFactory.CreateZipfDocuments"/>, which is also what
/// <see cref="CandidateSearchBenchmarks"/> and <see cref="ScoringPathBenchmarks"/> use, so a row
/// here is comparable with a row there at 10,000 documents. The query terms are the same three tail
/// terms those classes use, and the lane prints each term's document frequency at every size,
/// because the vocabulary is held at 30,000 words while the corpus grows: a tail term thickens with
/// scale, and the printed <c>df</c> is what keeps that visible.
/// </para>
/// </remarks>
internal static class ScaleLane
{
    private const int WordsPerDocument = 50;
    private const int VocabularySize = 30_000;

    private const string OneTermQuery = "w20000x";
    private const string ThreeTermQuery = "w20000x w25000x w29000x";

    private const int LargestDocumentCount = 1_000_000;
    private const string IncludeLargestVariable = "LEXISHARP_SCALE_INCLUDE_1M";

    private static readonly int[] DefaultDocumentCounts = [10_000, 100_000];

    /// <summary>Keeps the measured searches from being treated as dead code.</summary>
    private static long _sink;

    public static void Run(string[] args)
    {
        int[] sizes = Parse(args);

        PrintHeader(sizes);

        // A first-touch build is a measurement of the JIT and of the empty static caches, not of a
        // corpus size, so the smallest work is done once and discarded before anything is timed.
        WarmUp();

        foreach (int documents in sizes)
            Measure(documents);

        if (Array.BinarySearch(sizes, LargestDocumentCount) < 0)
        {
            Console.WriteLine(
                $"\n{Number(LargestDocumentCount)} documents not run. " +
                $"Pass --include-1m or set {IncludeLargestVariable}=1 to add that size; " +
                "it needs several gigabytes of managed heap and a build measured in minutes.");
        }
    }

    private static void WarmUp()
    {
        var documents = CorpusFactory.CreateZipfDocuments(1_000, WordsPerDocument, VocabularySize);
        var engine = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());

        engine.Index(documents);
        _sink += engine.Search(OneTermQuery, new SearchOptions(Limit: 10)).Count;

        Console.WriteLine("warm-up   1,000 documents indexed and one search served (not measured)");
    }

    private static void Measure(int documentCount)
    {
        var generation = Stopwatch.StartNew();
        var documents = CorpusFactory.CreateZipfDocuments(documentCount, WordsPerDocument, VocabularySize);
        generation.Stop();

        var index = new InMemoryTextIndex();
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        // The corpus array and engine exist on both sides of this reading, so the delta is what the
        // index added and not the documents it was built from.
        long before = ManagedHeapBytes();

        var build = Stopwatch.StartNew();
        engine.Index(documents);
        build.Stop();

        long afterBuild = ManagedHeapBytes();

        var oneTerm = MeasureQuery(index, engine, OneTermQuery, documentCount);
        var threeTerm = MeasureQuery(index, engine, ThreeTermQuery, documentCount);

        // The flat posting copies the term-at-a-time pass walks are built on first use, after the
        // build this lane just timed. This second reading prices them.
        long afterQueries = ManagedHeapBytes();

        Console.WriteLine();
        Console.WriteLine($"--- {Number(documentCount)} documents ---");
        Console.WriteLine($"corpus generation       {Time(generation.Elapsed.TotalMicroseconds)}");
        Console.WriteLine($"index build             {Time(build.Elapsed.TotalMicroseconds)}  (one build, Stopwatch)");
        Console.WriteLine($"retained after build    {Fixed(Megabytes(afterBuild - before), 10, 1)} MB  " +
                          $"({Fixed((afterBuild - before) / (double)documentCount, 8, 0)} B/document, managed heap)");
        Console.WriteLine($"retained after queries  {Fixed(Megabytes(afterQueries - before), 10, 1)} MB  " +
                          $"({Fixed((afterQueries - before) / (double)documentCount, 8, 0)} B/document, managed heap)");
        Console.WriteLine($"index                   {Number(index.Count)} documents, {Number(index.VocabularySize)} terms");
        PrintQuery(oneTerm);
        PrintQuery(threeTerm);
    }

    private static QueryMeasurement MeasureQuery(
        InMemoryTextIndex index,
        RankedTextSearchEngine engine,
        string query,
        int documentCount)
    {
        var options = new SearchOptions(Limit: 10);
        int warmup = WarmUpIterations(documentCount);
        int iterations = MeasuredIterations(documentCount);

        for (int i = 0; i < warmup; i++)
            _sink += engine.Search(query, options).Count;

        var microseconds = new double[iterations];
        int hits = 0;

        for (int i = 0; i < iterations; i++)
        {
            long started = Stopwatch.GetTimestamp();
            var results = engine.Search(query, options);
            microseconds[i] = Stopwatch.GetElapsedTime(started).TotalMicroseconds;

            hits = results.Count;
            _sink += hits;
        }

        Array.Sort(microseconds);

        // Counted on its own loop: GC.GetAllocatedBytesForCurrentThread is exact, and a timing loop
        // next to it would only add the counter reads to the measured interval.
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < iterations; i++)
            _sink += engine.Search(query, options).Count;

        long allocated = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / iterations;

        int documentFrequency = 0;
        foreach (string term in query.Split(' '))
            documentFrequency += index.DocumentFrequency(term);

        return new QueryMeasurement(
            query,
            documentFrequency,
            hits,
            warmup,
            iterations,
            microseconds[0],
            microseconds[iterations / 2],
            microseconds.Average(),
            allocated);
    }

    private static void PrintQuery(QueryMeasurement measurement)
    {
        Console.WriteLine(
            $"query {measurement.Query,-28} df sum {Number(measurement.DocumentFrequency),7}  " +
            $"hits {Number(measurement.Hits),3}  " +
            $"min {Time(measurement.MinimumMicroseconds),9}  " +
            $"p50 {Time(measurement.MedianMicroseconds),9}  " +
            $"mean {Time(measurement.MeanMicroseconds),9}  " +
            $"{Number(measurement.AllocatedBytes),5} B/search");
        Console.WriteLine(
            $"      {measurement.WarmUpIterations} warm-up + {measurement.Iterations} timed searches; " +
            "allocation over a separate loop of the same length");
    }

    private static int[] Parse(string[] args)
    {
        bool includeLargest = Environment.GetEnvironmentVariable(IncludeLargestVariable) == "1";
        var sizes = new List<int>();

        foreach (string arg in args)
        {
            if (arg == "--include-1m")
            {
                includeLargest = true;
                continue;
            }

            if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) ||
                count <= 0)
            {
                throw new ArgumentException(
                    $"Unrecognised scale argument '{arg}'. " +
                    $"Usage: scale [document-count ...] [--include-1m] (or {IncludeLargestVariable}=1).");
            }

            if (!sizes.Contains(count))
                sizes.Add(count);
        }

        if (sizes.Count == 0)
            sizes.AddRange(DefaultDocumentCounts);

        if (includeLargest && !sizes.Contains(LargestDocumentCount))
            sizes.Add(LargestDocumentCount);

        sizes.Sort();
        return [.. sizes];
    }

    private static void PrintHeader(int[] sizes)
    {
        Console.WriteLine("LexiSharp scale lane — index build and tail-query cost per corpus size");
        Console.WriteLine($"host      {RuntimeInformation.OSDescription} / {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"cpu       {Environment.ProcessorCount} logical processors");
        Console.WriteLine(
            $"runtime   {RuntimeInformation.FrameworkDescription}, ServerGC={GCSettings.IsServerGC}, " +
            $"{(IntPtr.Size == 8 ? "64-bit" : "32-bit")} process");
        Console.WriteLine(
            $"corpus    Zipf 1.07 over {Number(VocabularySize)} terms, {WordsPerDocument} words/document; " +
            "default tokenizer and BM25");
        Console.WriteLine($"sizes     {string.Join(", ", sizes.Select(s => Number(s)))}");
        Console.WriteLine("retained  GC.GetTotalMemory after two forced, blocking, compacting collections, before and after the build: managed heap only, corpus excluded");
        Console.WriteLine();
    }

    /// <summary>
    /// A full, blocking, compacting collection before reading the heap, so the reading is what the
    /// process still holds rather than what it has not collected yet.
    /// </summary>
    private static long ManagedHeapBytes()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        return GC.GetTotalMemory(forceFullCollection: false);
    }

    private static int WarmUpIterations(int documentCount) =>
        documentCount >= 1_000_000 ? 20 : documentCount >= 100_000 ? 50 : 100;

    private static int MeasuredIterations(int documentCount) =>
        documentCount >= 1_000_000 ? 100 : documentCount >= 100_000 ? 300 : 1_000;

    private static string Time(double microseconds) => microseconds >= 1_000
        ? (microseconds / 1_000).ToString("N2", CultureInfo.InvariantCulture) + " ms"
        : microseconds.ToString("N2", CultureInfo.InvariantCulture) + " µs";

    private static double Megabytes(long bytes) => bytes / (1024.0 * 1024.0);

    private static string Number(long value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Fixed(double value, int width, int decimals) =>
        value.ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
            .PadLeft(width);

    private readonly record struct QueryMeasurement(
        string Query,
        int DocumentFrequency,
        int Hits,
        int WarmUpIterations,
        int Iterations,
        double MinimumMicroseconds,
        double MedianMicroseconds,
        double MeanMicroseconds,
        long AllocatedBytes);
}
