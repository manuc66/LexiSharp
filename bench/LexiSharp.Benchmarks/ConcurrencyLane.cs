using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;

namespace LexiSharp.Benchmarks;

/// <summary>
/// Throughput, tail latency and allocation for one shared index under 1, 2, 4 and 8 callers, raw
/// and behind <see cref="SynchronizedTextSearchEngine"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ConcurrentSearchTests"/> proves the answers are right; this lane is the measurement
/// the docs do not have: what a read-only index serves at eight callers, and what the opt-in
/// wrapper's read lock costs when the number is measured against the unwrapped engine in the same
/// session. Both sides run the same corpus, the same query mix and the same worker loop, and the
/// two are interleaved run by run, so host drift is shared rather than charged to one of them.
/// </para>
/// <para>
/// It is a direct run like <see cref="ScaleLane"/>, not a BenchmarkDotNet class: a benchmark job
/// rebuilds its setup between invocations, and this needs one 100,000-document index shared by
/// every worker, warmed once per shape and timed in a plain process. The numbers are therefore a
/// stopwatch, not a BenchmarkDotNet report: no outlier removal, no confidence interval, and only
/// <c>p50</c> / <c>p99</c> over the calls each run made.
/// </para>
/// <para>
/// Each shape — one engine kind at one caller count — is warmed for five seconds of wall time, not
/// a call count. Tiered compilation promotes the search chain to its optimized tier at roughly
/// 0.5–0.7 s on this host, and a call-count warm-up times the instrumented tier; see the scale
/// lane's remarks and the benchmarks page for the measurement that established it. The workers are
/// dedicated threads started together behind a gate, not pool threads, because pool ramp-up would
/// be measured as if it were the engine.
/// </para>
/// <para>
/// The per-call interval contains two <see cref="Stopwatch.GetTimestamp"/> reads, because it is
/// taken around the call in the worker loop; at a one-term tail query near four microseconds that
/// is a low-single-digit percentage of the reading, and no correction is applied. The clock is
/// checked every sixteen calls, so each worker can overshoot its two-second window by up to sixteen
/// calls — a few head-query lengths in this mix; throughput divides counted calls by the wall time
/// from gate release to the last worker's exit, which includes that overshoot.
/// </para>
/// </remarks>
internal static class ConcurrencyLane
{
    private const int DocumentCount = 100_000;
    private const int WordsPerDocument = 50;
    private const int VocabularySize = 30_000;
    private const int DefaultRuns = 3;

    /// <summary>
    /// A one-term tail query, a three-term tail query and a head-ish query, so the lane does not
    /// only measure the cheap case. The same tail terms <see cref="CandidateSearchBenchmarks"/> and
    /// <see cref="ScaleLane"/> use; <c>w1x</c> is the head term that class also queries.
    /// </summary>
    private static readonly string[] Queries =
    [
        "w20000x",
        "w20000x w25000x w29000x",
        "w1x",
    ];

    private static readonly string[] QueryNames = ["tail-1", "tail-3", "head-1"];

    private static readonly int[] CallerCounts = [1, 2, 4, 8];

    /// <summary>The two sides of the comparison. Their order within a run number flips each run.</summary>
    private static readonly string[] Implementations = ["raw", "sync"];

    private static readonly TimeSpan WarmUpDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TimingDuration = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AllocationDuration = TimeSpan.FromSeconds(1);

    private const int SampleCheckInterval = 16;

    /// <summary>
    /// Unpriced calls each worker makes before the allocation counter starts, so the thread's
    /// <see cref="ArrayPool{T}.Shared"/> partition holds the corpus-sized score buffers it will
    /// reuse. Without it the first rent on a partition allocates 1.3 MB at 100,000 documents and
    /// that one-time cost is divided into every search of the window.
    /// </summary>
    private const int AllocationWarmUpCalls = 64;

    /// <summary>Keeps the measured searches from being treated as dead code.</summary>
    private static long _sink;

    public static void Run(string[] args)
    {
        int runs = Parse(args);

        var index = new InMemoryTextIndex();
        var raw = new RankedTextSearchEngine(index, new Bm25Scorer());
        raw.Index(CorpusFactory.CreateZipfDocuments(DocumentCount, WordsPerDocument, VocabularySize));

        using var sync = new SynchronizedTextSearchEngine(raw);
        var engines = new ITextSearchEngine[] { raw, sync };

        PrintHeader(raw, index, runs);

        var results = new List<RunResult>(CallerCounts.Length * Implementations.Length * runs);

        foreach (int callers in CallerCounts)
        {
            // One warm-up per shape, both kinds, before the first timed run. The warm-up is not
            // kept or printed as a result; it exists to promote the call chain to tier 1.
            for (int i = 0; i < engines.Length; i++)
                WarmUp(engines[i], Implementations[i], callers);

            for (int run = 1; run <= runs; run++)
            {
                // Interleaved run by run rather than kind by kind: the two sides of the comparison
                // then share the host state of this minute instead of being separated by minutes.
                // The order flips on every run so neither side is always measured second.
                for (int step = 0; step < engines.Length; step++)
                {
                    int engineIndex = run % 2 == 1 ? step : engines.Length - 1 - step;
                    results.Add(Measure(engines[engineIndex], Implementations[engineIndex], callers, run));
                }
            }
        }

        PrintPerRun(results);
        PrintSummary(results, runs);
        PrintRatios(results, runs);

        Console.WriteLine($"load end  {LoadAverage()}");
        Console.WriteLine($"memory end {Memory()}");
    }

    private static void WarmUp(ITextSearchEngine engine, string implementation, int callers)
    {
        long[] calls = new long[callers];
        long[] sinks = new long[callers];
        var options = new SearchOptions(Limit: 10);
        var queries = Queries;

        RunWorkers(callers, (ready, gate, slot) =>
        {
            ready.Signal();
            gate.Wait();

            long started = Stopwatch.GetTimestamp();
            long count = 0;
            long sink = 0;

            while (true)
            {
                sink += engine.Search(queries[(int)(count % queries.Length)], options).Count;
                count++;

                if ((count % SampleCheckInterval) == 0 && Stopwatch.GetElapsedTime(started) >= WarmUpDuration)
                    break;
            }

            calls[slot] = count;
            sinks[slot] = sink;
        });

        long total = 0;
        foreach (long count in calls)
            total += count;

        foreach (long sink in sinks)
            _sink += sink;

        Console.WriteLine(
            $"warm-up   {callers,2} caller(s) {implementation,-4}, {Number(total),9} searches over " +
            $"{Fixed(WarmUpDuration.TotalSeconds, 0, 0)} s");
    }

    private static RunResult Measure(ITextSearchEngine engine, string implementation, int callers, int run)
    {
        var samples = new List<CallSample>?[callers];
        long[] timedCalls = new long[callers];
        long[] sinks = new long[callers];
        var options = new SearchOptions(Limit: 10);
        var queries = Queries;

        RunWorkers(callers, (ready, gate, slot) =>
        {
            ready.Signal();
            gate.Wait();

            var workerSamples = new List<CallSample>(16_384);
            samples[slot] = workerSamples;

            long started = Stopwatch.GetTimestamp();
            long count = 0;
            long sink = 0;

            while (true)
            {
                int query = (int)(count % queries.Length);
                long callStarted = Stopwatch.GetTimestamp();
                sink += engine.Search(queries[query], options).Count;
                workerSamples.Add(new CallSample(Stopwatch.GetTimestamp() - callStarted, query));
                count++;

                if ((count % SampleCheckInterval) == 0 && Stopwatch.GetElapsedTime(started) >= TimingDuration)
                    break;
            }

            timedCalls[slot] = count;
            sinks[slot] = sink;
        }, out double timedSeconds);

        foreach (long sink in sinks)
            _sink += sink;

        long searches = 0;
        foreach (long count in timedCalls)
            searches += count;

        var (mixP50, mixP99, queryP50, queryP99) = Percentiles(samples);

        long allocated = MeasureAllocation(engine, callers, out long allocationCalls);

        return new RunResult(
            callers,
            run,
            implementation,
            searches,
            timedSeconds,
            searches / timedSeconds,
            mixP50,
            mixP99,
            queryP50,
            queryP99,
            allocationCalls == 0 ? 0 : allocated / allocationCalls,
            allocationCalls);
    }

    /// <summary>
    /// Allocation per search over a separate loop of the same mix, counted per worker with
    /// <see cref="GC.GetAllocatedBytesForCurrentThread"/> and summed. A timing loop next to the
    /// counter would add the counter reads to the interval; a shared counter would not exist.
    /// </summary>
    private static long MeasureAllocation(ITextSearchEngine engine, int callers, out long totalCalls)
    {
        long[] calls = new long[callers];
        long[] allocated = new long[callers];
        long[] sinks = new long[callers];
        var options = new SearchOptions(Limit: 10);
        var queries = Queries;

        RunWorkers(callers, (ready, gate, slot) =>
        {
            ready.Signal();
            gate.Wait();

            long sink = 0;

            // Rent each thread's pooled buffers before the counter starts; see AllocationWarmUpCalls.
            for (int i = 0; i < AllocationWarmUpCalls; i++)
                sink += engine.Search(queries[i % queries.Length], options).Count;

            long before = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            long count = 0;

            while (true)
            {
                sink += engine.Search(queries[(int)(count % queries.Length)], options).Count;
                count++;

                if ((count % SampleCheckInterval) == 0 && Stopwatch.GetElapsedTime(started) >= AllocationDuration)
                    break;
            }

            allocated[slot] = GC.GetAllocatedBytesForCurrentThread() - before;
            calls[slot] = count;
            sinks[slot] = sink;
        });

        foreach (long sink in sinks)
            _sink += sink;

        long bytes = 0;
        totalCalls = 0;

        for (int i = 0; i < callers; i++)
        {
            bytes += allocated[i];
            totalCalls += calls[i];
        }

        return bytes;
    }

    /// <summary>
    /// Starts <paramref name="count"/> dedicated threads, releases them together, and returns the
    /// wall time from the release to the last worker's exit.
    /// </summary>
    private static void RunWorkers(int count, Action<CountdownEvent, ManualResetEventSlim, int> work) =>
        RunWorkers(count, work, out _);

    private static void RunWorkers(
        int count,
        Action<CountdownEvent, ManualResetEventSlim, int> work,
        out double elapsedSeconds)
    {
        using var ready = new CountdownEvent(count);
        using var gate = new ManualResetEventSlim(false);

        var threads = new Thread[count];
        var failures = new Exception?[count];

        for (int i = 0; i < count; i++)
        {
            int slot = i;

            threads[i] = new Thread(() =>
            {
                try
                {
                    work(ready, gate, slot);
                }
                catch (Exception exception)
                {
                    failures[slot] = exception;
                    gate.Set();
                }
            })
            {
                IsBackground = true,
                Name = "lexisharp-concurrency-" + slot.ToString(CultureInfo.InvariantCulture),
            };

            threads[i].Start();
        }

        ready.Wait();

        long started = Stopwatch.GetTimestamp();
        gate.Set();

        foreach (var thread in threads)
            thread.Join();

        elapsedSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;

        foreach (var failure in failures)
        {
            if (failure is not null)
                throw new InvalidOperationException("A concurrency worker failed.", failure);
        }
    }

    private static (double MixP50, double MixP99, double[] QueryP50, double[] QueryP99) Percentiles(
        List<CallSample>?[] samples)
    {
        int total = 0;
        var queryCounts = new int[Queries.Length];

        foreach (var worker in samples)
        {
            if (worker is null)
                continue;

            total += worker.Count;

            foreach (var sample in worker)
                queryCounts[sample.Query]++;
        }

        var all = new long[total];
        var perQuery = new long[Queries.Length][];
        var offsets = new int[Queries.Length];

        for (int q = 0; q < Queries.Length; q++)
            perQuery[q] = new long[queryCounts[q]];

        int position = 0;

        foreach (var worker in samples)
        {
            if (worker is null)
                continue;

            foreach (var sample in worker)
            {
                all[position++] = sample.Ticks;
                perQuery[sample.Query][offsets[sample.Query]++] = sample.Ticks;
            }
        }

        Array.Sort(all);

        var queryP50 = new double[Queries.Length];
        var queryP99 = new double[Queries.Length];

        for (int q = 0; q < Queries.Length; q++)
        {
            Array.Sort(perQuery[q]);

            if (perQuery[q].Length == 0)
            {
                queryP50[q] = double.NaN;
                queryP99[q] = double.NaN;
                continue;
            }

            queryP50[q] = Percentile(perQuery[q], 0.50);
            queryP99[q] = Percentile(perQuery[q], 0.99);
        }

        return (
            total == 0 ? double.NaN : Percentile(all, 0.50),
            total == 0 ? double.NaN : Percentile(all, 0.99),
            queryP50,
            queryP99);
    }

    /// <summary>Nearest-rank percentile over an ascending array.</summary>
    private static double Percentile(long[] sortedTicks, double percentile)
    {
        int rank = (int)Math.Ceiling(percentile * sortedTicks.Length) - 1;
        rank = Math.Clamp(rank, 0, sortedTicks.Length - 1);

        return sortedTicks[rank] * 1_000_000.0 / Stopwatch.Frequency;
    }

    private static void PrintHeader(RankedTextSearchEngine engine, InMemoryTextIndex index, int runs)
    {
        var options = new SearchOptions(Limit: 10);

        Console.WriteLine("LexiSharp concurrency lane — throughput and tail latency for one shared index");
        Console.WriteLine($"host      {RuntimeInformation.OSDescription} / {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"cpu       {CpuModel()}, {Environment.ProcessorCount} logical processors");
        Console.WriteLine(
            $"runtime   {RuntimeInformation.FrameworkDescription}, ServerGC={GCSettings.IsServerGC}, " +
            $"latency mode {GCSettings.LatencyMode}, {(IntPtr.Size == 8 ? "64-bit" : "32-bit")} process");
        Console.WriteLine($"load      {LoadAverage()}");
        Console.WriteLine($"memory    {Memory()}");
        Console.WriteLine(
            $"corpus    Zipf 1.07 over {Number(VocabularySize)} terms, {WordsPerDocument} words/document, " +
            $"{Number(DocumentCount)} documents, one index shared by every caller");
        Console.WriteLine("options   Limit: 10, default tokenizer, BM25; queries issued round-robin 1:1:1");

        for (int q = 0; q < Queries.Length; q++)
        {
            long documentFrequency = 0;
            var terms = Queries[q].Split(' ');

            foreach (string term in terms)
                documentFrequency += index.DocumentFrequency(term);

            Console.WriteLine(
                $"query     {QueryNames[q],-6} \"{Queries[q]}\"  " +
                $"df {(terms.Length == 1 ? "" : "sum ")}{Number(documentFrequency),7}  " +
                $"hits {Number(engine.Search(Queries[q], options).Count),3}");
        }

        Console.WriteLine(
            $"shape     warm-up {Fixed(WarmUpDuration.TotalSeconds, 0, 0)} s of wall time per engine kind and caller count; " +
            $"timing {Fixed(TimingDuration.TotalSeconds, 0, 0)} s + allocation {Fixed(AllocationDuration.TotalSeconds, 0, 0)} s per run");
        Console.WriteLine(
            $"runs      {runs} per row, raw and synchronized interleaved run by run; p50/p99 are nearest-rank over every call");
        Console.WriteLine();
    }

    private static void PrintPerRun(List<RunResult> results)
    {
        Console.WriteLine("--- per run ---");
        Console.WriteLine("callers impl run  searches  throughput      mix p50      mix p99  alloc/search");

        foreach (var result in results)
        {
            Console.WriteLine(
                $"{result.Callers,7} {result.Implementation,4} {result.Run,3}  " +
                $"{Number(result.Searches),9}  {Throughput(result.Throughput),10}  " +
                $"{Time(result.MixP50),9}  {Time(result.MixP99),9}  " +
                $"{Number(result.AllocatedPerSearch),7} B");
        }

        Console.WriteLine();
    }

    private static void PrintSummary(List<RunResult> results, int runs)
    {
        Console.WriteLine($"--- summary (median of {runs} runs, min–max in parentheses) ---");
        Console.WriteLine(
            "callers impl  throughput                   mix p50                    mix p99                    alloc/search");

        foreach (int callers in CallerCounts)
        {
            foreach (string implementation in Implementations)
            {
                var rows = Sorted(results, callers, implementation);

                Console.WriteLine(
                    $"{callers,7} {implementation,4}  " +
                    $"{MedianRange(rows.Select(r => r.Throughput), Throughput),-27}  " +
                    $"{MedianRange(rows.Select(r => r.MixP50), Time),-26}  " +
                    $"{MedianRange(rows.Select(r => r.MixP99), Time),-26}  " +
                    $"{MedianRange(rows.Select(r => (double)r.AllocatedPerSearch), value => Number(value) + " B")}");
            }
        }

        Console.WriteLine();
    }

    private static void PrintRatios(List<RunResult> results, int runs)
    {
        Console.WriteLine($"--- synchronized / raw, ratio of the medians over {runs} runs (per-run range in parentheses) ---");
        Console.WriteLine("callers  throughput ratio   mix p50 ratio   mix p99 ratio");

        foreach (int callers in CallerCounts)
        {
            var raw = Sorted(results, callers, "raw");
            var sync = Sorted(results, callers, "sync");

            Console.WriteLine(
                $"{callers,7}  " +
                $"{MedianRatio(raw, sync, r => r.Throughput),-18}  " +
                $"{MedianRatio(raw, sync, r => r.MixP50),-15}  " +
                $"{MedianRatio(raw, sync, r => r.MixP99)}");
        }

        Console.WriteLine();
        Console.WriteLine("query latency (median of the run p50/p99 values)");
        Console.WriteLine(
            "callers impl   tail-1 p50        tail-1 p99        tail-3 p50        tail-3 p99        head-1 p50        head-1 p99");

        foreach (int callers in CallerCounts)
        {
            foreach (string implementation in Implementations)
            {
                var rows = Sorted(results, callers, implementation);
                var cells = new List<string>();

                for (int q = 0; q < Queries.Length; q++)
                {
                    cells.Add(MedianRange(rows.Select(r => r.QueryP50[q]), Time));
                    cells.Add(MedianRange(rows.Select(r => r.QueryP99[q]), Time));
                }

                Console.WriteLine(
                    $"{callers,7} {implementation,-4}  " +
                    string.Join("  ", cells.Select(cell => cell.PadLeft(15))));
            }
        }

        Console.WriteLine();
    }

    private static List<RunResult> Sorted(List<RunResult> results, int callers, string implementation) =>
        results
            .Where(r => r.Callers == callers && r.Implementation == implementation)
            .OrderBy(r => r.Run)
            .ToList();

    private static string MedianRange(IEnumerable<double> values, Func<double, string> format)
    {
        double[] sorted = values.OrderBy(v => v).ToArray();

        return $"{format(Median(sorted))} ({format(sorted[0])}–{format(sorted[^1])})";
    }

    private static double Median(double[] sorted) => sorted.Length % 2 == 1
        ? sorted[sorted.Length / 2]
        : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2.0;

    private static string MedianRatio(
        List<RunResult> raw,
        List<RunResult> sync,
        Func<RunResult, double> selector)
    {
        double[] rawValues = raw.Select(selector).OrderBy(value => value).ToArray();
        double[] syncValues = sync.Select(selector).OrderBy(value => value).ToArray();
        double[] perRun = raw
            .Zip(sync, (a, b) => selector(b) / selector(a))
            .OrderBy(value => value)
            .ToArray();

        return $"{Fixed(Median(syncValues) / Median(rawValues), 0, 3)}x " +
               $"({Fixed(perRun[0], 0, 3)}–{Fixed(perRun[^1], 0, 3)})";
    }

    private static int Parse(string[] args)
    {
        int runs = DefaultRuns;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--runs" && i + 1 < args.Length &&
                int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) &&
                parsed > 0)
            {
                runs = parsed;
                i++;
                continue;
            }

            throw new ArgumentException(
                $"Unrecognised concurrency argument '{args[i]}'. Usage: concurrency [--runs N].");
        }

        return runs;
    }

    private static string CpuModel()
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/cpuinfo"))
            {
                if (line.StartsWith("model name", StringComparison.Ordinal))
                    return line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
            }
        }
        catch (IOException)
        {
            // Fall through to the generic description.
        }
        catch (UnauthorizedAccessException)
        {
            // Fall through to the generic description.
        }

        return "unknown CPU";
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

    private static string Memory()
    {
        try
        {
            long total = 0;
            long available = 0;

            foreach (string line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                    total = MemoryKilobytes(line);
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                    available = MemoryKilobytes(line);
            }

            if (total > 0)
            {
                return $"{Fixed(available / 1024.0 / 1024.0, 0, 1)} GiB available of " +
                       $"{Fixed(total / 1024.0 / 1024.0, 0, 1)} GiB (instantaneous reading)";
            }
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

    private static long MemoryKilobytes(string line)
    {
        string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return fields.Length >= 2 &&
               long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
            ? value
            : 0;
    }

    private static string Throughput(double searchesPerSecond) =>
        Number(searchesPerSecond) + "/s";

    private static string Time(double microseconds)
    {
        if (double.IsNaN(microseconds))
            return "-";

        return microseconds >= 1_000
            ? (microseconds / 1_000).ToString("N2", CultureInfo.InvariantCulture) + " ms"
            : microseconds.ToString("N2", CultureInfo.InvariantCulture) + " µs";
    }

    private static string Number(double value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Fixed(double value, int width, int decimals) =>
        value.ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
            .PadLeft(width);

    private readonly record struct CallSample(long Ticks, int Query);

    private readonly record struct RunResult(
        int Callers,
        int Run,
        string Implementation,
        long Searches,
        double Seconds,
        double Throughput,
        double MixP50,
        double MixP99,
        double[] QueryP50,
        double[] QueryP99,
        long AllocatedPerSearch,
        long AllocationCalls);
}
