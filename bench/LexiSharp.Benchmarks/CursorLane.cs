using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;

namespace LexiSharp.Benchmarks;

/// <summary>
/// Tail-query latency, its distribution and its allocation, per corpus size — the before/after
/// measurement for the document-at-a-time cursor pass.
/// </summary>
/// <remarks>
/// <para>
/// Same corpus, same queries and same warm-up as <see cref="ScaleLane"/>, plus the isolated clear of
/// the ordinal-space buffer the accumulating pass rents (the method <see cref="WandLane"/> uses) and
/// a p99 beside the p50. The lane itself is a stopwatch in a plain process: no BenchmarkDotNet, no
/// outlier removal.
/// </para>
/// <para>
/// It measures <see cref="RankedTextSearchEngine.Search"/> as it is, and prints which scoring pass
/// served the query — the engine's own decision, from the same reach the engine computes, not a copy
/// of the rule in bench code. The label is asserted at the point of measurement against the
/// allocation the pass adds: the same query is run through an engine forced to each accumulating pass,
/// and the default engine's bytes per search must equal the one the label names. A reader can
/// therefore tell a cursor row from an accumulator row, and the clear-share column prints a dash for
/// every row that did not rent an accumulator to clear.
/// </para>
/// <para>
/// Each query is warmed for five seconds of wall time before it is timed — see
/// <see cref="ScaleLane"/> for why a call-count warm-up measures the JIT instead of the search.
/// </para>
/// </remarks>
internal static class CursorLane
{
    private const int WordsPerDocument = 50;
    private const int VocabularySize = 30_000;

    private const string OneTailTerm = "w20000x";
    private const string ThreeTailTerms = "w20000x w25000x w29000x";
    private const string OneHeadTerm = "w1x";

    private const int Limit = 10;

    private static readonly TimeSpan WarmUpDuration = TimeSpan.FromSeconds(5);

    private static readonly int[] DefaultDocumentCounts = [10_000, 100_000];

    /// <summary>
    /// Document frequencies a <c>--sweep</c> run samples, one query each, so the density at which
    /// the cursor pass stops winning is measured rather than inferred from the fixed queries.
    /// </summary>
    private static readonly int[] SweepFrequencies = [10, 25, 50, 250, 500, 1_000, 2_500, 5_000, 20_000];

    private static readonly QueryShape[] Queries =
    [
        new("one tail term", OneTailTerm),
        new("three tail terms", ThreeTailTerms),
        new("one head term", OneHeadTerm),
    ];

    private static long _sink;

    public static void Run(string[] args)
    {
        (int[] sizes, bool sweep) = Parse(args);

        PrintHeader(sizes, sweep);
        WarmUp();

        foreach (int documents in sizes)
            Measure(documents, sweep);
    }

    private static void WarmUp()
    {
        var documents = CorpusFactory.CreateZipfDocuments(1_000, WordsPerDocument, VocabularySize);
        var engine = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());

        engine.Index(documents);
        _sink += engine.Search(OneTailTerm, new SearchOptions(Limit: Limit)).Count;

        Console.WriteLine("warm-up   1,000 documents indexed and one search served (not measured)");
    }

    private static void Measure(int documentCount, bool sweep)
    {
        var generation = Stopwatch.StartNew();
        var documents = CorpusFactory.CreateZipfDocuments(documentCount, WordsPerDocument, VocabularySize);
        generation.Stop();

        var index = new InMemoryTextIndex();
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        // The same query run through each pass on purpose: the lane's label is the engine's own
        // decision, and comparing the default engine's allocation with each forced pass is what
        // asserts that the label describes the code that ran.
        var accumulatorEngine = new RankedTextSearchEngine(index, new Bm25Scorer())
        {
            ForcedPass = RankedTextSearchEngine.ScoringPass.Accumulator,
        };
        var cursorEngine = new RankedTextSearchEngine(index, new Bm25Scorer())
        {
            ForcedPass = RankedTextSearchEngine.ScoringPass.Cursor,
        };

        var build = Stopwatch.StartNew();
        engine.Index(documents);
        build.Stop();

        long clearNanoseconds = MeasureClear(index.OrdinalSpace, runs: 7);

        Console.WriteLine();
        Console.WriteLine($"--- {Number(documentCount)} documents ---");
        Console.WriteLine($"host load at build       {LoadAverage()}");
        Console.WriteLine($"corpus generation       {Time(generation.Elapsed.TotalMicroseconds)}");
        Console.WriteLine($"index build             {Time(build.Elapsed.TotalMicroseconds)}  (one build, Stopwatch)");
        Console.WriteLine($"index                   {Number(index.Count)} documents, {Number(index.VocabularySize)} terms, " +
                          $"ordinal space {Number(index.OrdinalSpace)}");
        Console.WriteLine($"accumulator clear       {Time(clearNanoseconds / 1_000.0)} isolated Array.Clear of " +
                          $"{Number(index.OrdinalSpace)} bytes (best of 7)");

        foreach (var query in Queries)
            MeasureQuery(index, engine, accumulatorEngine, cursorEngine, query, documentCount, clearNanoseconds);

        if (!sweep)
            return;

        foreach (var (term, df) in SweepTerms(index))
        {
            MeasureQuery(
                index,
                engine,
                accumulatorEngine,
                cursorEngine,
                new QueryShape($"df {Number(df)} sweep", term),
                documentCount,
                clearNanoseconds);
        }
    }

    /// <summary>
    /// One term near each <see cref="SweepFrequencies"/> target, closest first-served: the density
    /// axis the cursor pass's threshold is written in.
    /// </summary>
    private static IEnumerable<(string Term, int DocumentFrequency)> SweepTerms(InMemoryTextIndex index)
    {
        var bestTerm = new string[SweepFrequencies.Length];
        var bestDistance = new int[SweepFrequencies.Length];

        Array.Fill(bestDistance, int.MaxValue);
        Array.Fill(bestTerm, string.Empty);

        foreach (string term in index.Vocabulary)
        {
            int df = index.DocumentFrequency(term);

            for (int i = 0; i < SweepFrequencies.Length; i++)
            {
                int distance = Math.Abs(df - SweepFrequencies[i]);

                if (distance < bestDistance[i])
                {
                    bestDistance[i] = distance;
                    bestTerm[i] = term;
                }
            }
        }

        return bestTerm
            .Distinct(StringComparer.Ordinal)
            .Select(term => (term, index.DocumentFrequency(term)));
    }

    private static void MeasureQuery(
        InMemoryTextIndex index,
        RankedTextSearchEngine engine,
        RankedTextSearchEngine accumulatorEngine,
        RankedTextSearchEngine cursorEngine,
        QueryShape query,
        int documentCount,
        long clearNanoseconds)
    {
        var options = new SearchOptions(Limit: Limit);
        int iterations = MeasuredIterations(documentCount);

        var warmUp = Stopwatch.StartNew();
        long warmUpSearches = 0;

        while (warmUp.Elapsed < WarmUpDuration)
        {
            _sink += engine.Search(query.Text, options).Count;
            warmUpSearches++;
        }

        var microseconds = new double[iterations];
        int hits = 0;

        for (int i = 0; i < iterations; i++)
        {
            long started = Stopwatch.GetTimestamp();
            var results = engine.Search(query.Text, options);
            microseconds[i] = Stopwatch.GetElapsedTime(started).TotalMicroseconds;

            hits = results.Count;
            _sink += hits;
        }

        Array.Sort(microseconds);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < iterations; i++)
            _sink += engine.Search(query.Text, options).Count;

        long allocated = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / iterations;

        // The engine's own decision, not a copy of it: the same reach the engine computes, from the
        // distinct query terms, fed to the same helper the engine's two accumulating paths call.
        int reachable = 0;

        foreach (string term in query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal))
            reachable += index.DocumentFrequency(term);

        var pass = RankedTextSearchEngine.PassFor(index, reachable);

        // The cross-check: each pass allocates what it allocates, and the default engine must match
        // the one the decision names. On a cursor row the two are different, so the assertion also
        // proves the label is not the accumulator's.
        long accumulatorAllocated = MeasureAllocation(accumulatorEngine, query.Text, options);
        long cursorAllocated = MeasureAllocation(cursorEngine, query.Text, options);
        long expectedAllocated = pass == RankedTextSearchEngine.ScoringPass.Cursor
            ? cursorAllocated
            : accumulatorAllocated;

        if (allocated != expectedAllocated)
        {
            throw new InvalidOperationException(
                $"the lane says {pass} for '{query.Text}' but the default engine allocated {allocated} B/search, " +
                $"not the {expectedAllocated} B/search that pass predicts (cursor {cursorAllocated}, " +
                $"accumulator {accumulatorAllocated})");
        }

        if (pass != RankedTextSearchEngine.ScoringPass.PerDocument && cursorAllocated == accumulatorAllocated)
        {
            throw new InvalidOperationException(
                $"'{query.Text}' reports {pass} but the two forced passes allocate identically " +
                $"({cursorAllocated} B/search), so allocation cannot tell them apart");
        }

        double p50 = microseconds[iterations / 2];
        double p99 = microseconds[Math.Clamp((int)Math.Ceiling(iterations * 0.99) - 1, 0, iterations - 1)];

        Console.WriteLine(
            $"query {query.Text,-28} ({query.Label}) df sum {Number(reachable),7}  pass {Pass(pass),-11} " +
            $"hits {Number(hits),3}  min {Time(microseconds[0]),9}  p50 {Time(p50),9}  p99 {Time(p99),9}  " +
            $"mean {Time(microseconds.Average()),9}  {Number(allocated),6} B/search");
        Console.WriteLine(
            $"      clear share of p50 {(pass == RankedTextSearchEngine.ScoringPass.Accumulator ? Percent(clearNanoseconds / (p50 * 1_000.0), 1) : "—"),6}  " +
            $"allocation check: cursor {Number(cursorAllocated)} B, accumulator {Number(accumulatorAllocated)} B  " +
            $"{Number(warmUpSearches)} warm-up searches in {Fixed(warmUp.Elapsed.TotalSeconds, 0, 1)} s, " +
            $"{Number(iterations)} timed");
    }

    /// <summary>
    /// The pass as the row should read it: the engine's enum, in the lane's lowercase words. A cursor
    /// row has no corpus-sized clear to compare against, so its share column prints a dash.
    /// </summary>
    private static string Pass(RankedTextSearchEngine.ScoringPass pass) => pass switch
    {
        RankedTextSearchEngine.ScoringPass.Cursor => "cursor",
        RankedTextSearchEngine.ScoringPass.Accumulator => "accumulator",
        _ => "per-document",
    };

    /// <summary>
    /// Allocation per search for one engine and query. Allocation is deterministic on one thread, so
    /// this is an exact comparison and not a statistical one.
    /// </summary>
    private static long MeasureAllocation(
        RankedTextSearchEngine engine,
        string query,
        SearchOptions options,
        int warmup = 20,
        int iterations = 200)
    {
        for (int i = 0; i < warmup; i++)
            _sink += engine.Search(query, options).Count;

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < iterations; i++)
            _sink += engine.Search(query, options).Count;

        return (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
    }

    private static long MeasureClear(int bytes, int runs)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bytes);
        Array.Clear(buffer);

        long best = long.MaxValue;

        for (int i = 0; i < runs; i++)
        {
            long started = Stopwatch.GetTimestamp();
            Array.Clear(buffer);

            long nanoseconds = (long)Stopwatch.GetElapsedTime(started).TotalNanoseconds;

            if (nanoseconds < best)
                best = nanoseconds;
        }

        ArrayPool<byte>.Shared.Return(buffer);
        return best;
    }

    private static (int[] Sizes, bool Sweep) Parse(string[] args)
    {
        var sizes = new List<int>();
        bool sweep = false;

        foreach (string arg in args)
        {
            if (arg == "--sweep")
            {
                sweep = true;
                continue;
            }

            if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count <= 0)
            {
                throw new ArgumentException(
                    $"Unrecognised cursor argument '{arg}'. Usage: cursor [document-count ...] [--sweep] " +
                    "(default 10000 100000; 500000 needs about 3.2 GB of managed heap).");
            }

            if (!sizes.Contains(count))
                sizes.Add(count);
        }

        if (sizes.Count == 0)
            sizes.AddRange(DefaultDocumentCounts);

        sizes.Sort();
        return ([.. sizes], sweep);
    }

    private static void PrintHeader(int[] sizes, bool sweep)
    {
        Console.WriteLine("LexiSharp cursor lane — tail-query p50/p99 and allocation per corpus size");
        Console.WriteLine($"host      {RuntimeInformation.OSDescription} / {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"cpu       {Environment.ProcessorCount} logical processors");
        Console.WriteLine(
            $"runtime   {RuntimeInformation.FrameworkDescription}, ServerGC={GCSettings.IsServerGC}, " +
            $"{(IntPtr.Size == 8 ? "64-bit" : "32-bit")} process");
        Console.WriteLine(
            $"corpus    Zipf 1.07 over {Number(VocabularySize)} terms, {WordsPerDocument} words/document; " +
            "default tokenizer and BM25 (k1 1.5, b 0.75)");
        Console.WriteLine($"queries   {string.Join(" | ", Queries.Select(q => q.Text))}; Limit {Limit}");
        Console.WriteLine($"sizes     {string.Join(", ", sizes.Select(s => Number(s)))}");
        Console.WriteLine(sweep
            ? $"sweep     one term near each document frequency in {string.Join(", ", SweepFrequencies.Select(f => Number(f)))}"
            : "sweep     off (pass --sweep for one query per document-frequency target)");
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

    private static int MeasuredIterations(int documentCount) =>
        documentCount >= 500_000 ? 100 : documentCount >= 100_000 ? 300 : 1_000;

    private static string Time(double microseconds) => microseconds >= 1_000
        ? (microseconds / 1_000).ToString("N2", CultureInfo.InvariantCulture) + " ms"
        : microseconds.ToString("N2", CultureInfo.InvariantCulture) + " µs";

    private static string Percent(double fraction, int decimals) =>
        (fraction * 100).ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) + " %";

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Fixed(double value, int width, int decimals) =>
        value.ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
            .PadLeft(width);

    private sealed record QueryShape(string Label, string Text);
}
