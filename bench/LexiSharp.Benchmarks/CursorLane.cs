using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using LexiSharp.Core;
using LexiSharp.Eval;
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

    /// <summary>Where the BEIR corpora live, relative to the repository root and to any directory above the assembly.</summary>
    private const string DefaultDataDirectory = "bench/LexiSharp.Eval/data";

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
        LaneOptions options = Parse(args);

        PrintHeader(options);
        WarmUp();

        if (options.Corpus is not null)
        {
            MeasureCorpus(options);
            return;
        }

        foreach (int documents in options.Sizes)
            Measure(documents, options.Sweep, options.Both);
    }

    private static void WarmUp()
    {
        var documents = CorpusFactory.CreateZipfDocuments(1_000, WordsPerDocument, VocabularySize);
        var engine = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());

        engine.Index(documents);
        _sink += engine.Search(OneTailTerm, new SearchOptions(Limit: Limit)).Count;

        Console.WriteLine("warm-up   1,000 documents indexed and one search served (not measured)");
    }

    private static void Measure(int documentCount, bool sweep, bool both)
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
            MeasureQuery(index, engine, accumulatorEngine, cursorEngine, query, documentCount, clearNanoseconds, both);

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
                clearNanoseconds,
                both);
        }
    }

    /// <summary>
    /// The corpus this run measures, from the quality table or from the bench-only list.
    /// </summary>
    private static BeirCorpus LoadCorpus(LaneOptions options)
    {
        BenchCorpus? bench = BenchCorpora.FirstOrDefault(
            candidate => string.Equals(candidate.Name, options.Corpus, StringComparison.OrdinalIgnoreCase));

        if (bench is not null)
        {
            return BeirLoader
                .LoadOrDownloadAsync(options.DataDirectory, bench.Name, bench.Url, bench.Md5)
                .GetAwaiter().GetResult();
        }

        BeirDataset dataset = BeirDataset.All.FirstOrDefault(
                candidate => string.Equals(candidate.Name, options.Corpus, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException(
                $"Unknown corpus '{options.Corpus}'. Known: " +
                $"{string.Join(", ", BeirDataset.All.Select(d => d.Name).Concat(BenchCorpora.Select(c => c.Name)))}.");

        return BeirLoader.LoadOrDownloadAsync(options.DataDirectory, dataset).GetAwaiter().GetResult();
    }

    /// <summary>
    /// A corpus for a latency question, which has no published quality figure for that reason.
    /// </summary>
    private sealed record BenchCorpus(string Name, string Url, string Md5);

    /// <summary>
    /// Real corpora the quality table does not carry, for a size that table does not reach.
    /// </summary>
    /// <remarks>
    /// <see cref="BeirDataset.All"/> holds the corpora the quality matrix scores, each with the nDCG
    /// it is expected to reach. These load through the same downloader and the same jsonl parser and
    /// carry no expected score, because nothing scores them: TREC-COVID is here for its 171,332 real
    /// documents, which is the only size at which the crossing above 10,000 documents can be read at
    /// all without generating the corpus. The checksum is the one the archive had on 2026-10-29, when
    /// this lane first fetched it.
    /// </remarks>
    private static readonly BenchCorpus[] BenchCorpora =
    [
        new(
            "trec-covid",
            "https://public.ukp.informatik.tu-darmstadt.de/thakur/BEIR/datasets/trec-covid.zip",
            "ce62140cb23feb9becf6270d0d1fe6d1"),
    ];

    /// <summary>
    /// The same sweep over a BEIR corpus — real documents, real query text, and the variable document
    /// lengths the synthetic corpus holds constant at 50 tokens.
    /// </summary>
    /// <remarks>
    /// The fixed queries are skipped: <c>w1x</c> and <c>w20000x</c> name terms of a generated
    /// vocabulary, so running them against a real corpus would measure a query that matches nothing.
    /// The sweep is the instrument the decision is written in — one term at a target document
    /// frequency — and it is the only row this mode needs, because the pass rule is expressed in
    /// document frequency and the ordinal space, not in a query shape.
    /// </remarks>
    private static void MeasureCorpus(LaneOptions options)
    {
        BeirCorpus corpus = LoadCorpus(options);
        IReadOnlyList<SearchDocument> documents = Evaluation.BuildDocuments(corpus);

        var index = new InMemoryTextIndex();
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

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
        int[]? lengths = ((IPostingArrayIndex)index).LengthsByOrdinal;

        Console.WriteLine();
        Console.WriteLine($"--- {corpus.Name} (real documents) ---");
        Console.WriteLine($"host load at build       {LoadAverage()}");
        Console.WriteLine($"index build             {Time(build.Elapsed.TotalMicroseconds)}  (one build, Stopwatch)");
        Console.WriteLine($"index                   {Number(index.Count)} documents, {Number(index.VocabularySize)} terms, " +
                          $"ordinal space {Number(index.OrdinalSpace)}");
        Console.WriteLine($"queries                 {Number(corpus.Queries.Count)} in the corpus file (not used; the sweep is)");
        Console.WriteLine($"accumulator clear       {Time(clearNanoseconds / 1_000.0)} isolated Array.Clear of " +
                          $"{Number(index.OrdinalSpace)} bytes (best of 7)");

        if (lengths is not null && lengths.Length > 0)
        {
            int[] sorted = [.. lengths];
            Array.Sort(sorted);

            Console.WriteLine(
                $"document length         min {Number(sorted[0])}, p50 {Number(sorted[sorted.Length / 2])}, " +
                $"mean {sorted.Average().ToString("N1", CultureInfo.InvariantCulture)}, " +
                $"p95 {Number(sorted[(int)(sorted.Length * 0.95)])}, max {Number(sorted[^1])} tokens");
        }

        if (!options.Sweep)
        {
            Console.WriteLine(
                "sweep                   off — this mode measures the document-frequency crossing, so " +
                "pass --sweep or nothing is timed");
            return;
        }

        foreach (var (term, df) in SweepTerms(index))
        {
            MeasureQuery(
                index,
                engine,
                accumulatorEngine,
                cursorEngine,
                new QueryShape($"df {Number(df)} sweep", term),
                corpus.Documents.Count,
                clearNanoseconds,
                both: options.Both);
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
        long clearNanoseconds,
        bool both = false)
    {
        var options = new SearchOptions(Limit: Limit);
        int iterations = MeasuredIterations(documentCount);

        // Every engine that will be timed is warmed inside the same window. A pass timed cold is a
        // measurement of the JIT and of tiered promotion, which is the one thing the five-second
        // warm-up exists to exclude.
        var warmUp = Stopwatch.StartNew();
        long warmUpSearches = 0;

        while (warmUp.Elapsed < WarmUpDuration)
        {
            _sink += engine.Search(query.Text, options).Count;

            if (both)
            {
                _sink += accumulatorEngine.Search(query.Text, options).Count;
                _sink += cursorEngine.Search(query.Text, options).Count;
            }

            warmUpSearches++;
        }

        // The three readings are taken in alternation, one of each per iteration, not in three blocks.
        // On a loaded host the drift between two blocks is larger than the effect being measured: a
        // first reading of this lane reported the default pass at 2.90 µs and the very same pass
        // forced at 7.60 µs on one query, three blocks apart, with nothing changed but the moment.
        // Alternating makes the drift common to the three readings instead of charging it to one.
        (double[] microseconds, double[]? accumulatorMicroseconds, double[]? cursorMicroseconds, int hits) =
            TimePasses(engine, accumulatorEngine, cursorEngine, query.Text, options, iterations, both);

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

        if (both && cursorMicroseconds is not null && accumulatorMicroseconds is not null)
            Console.WriteLine(ForcedComparison(cursorMicroseconds, accumulatorMicroseconds, iterations, pass));
    }

    /// <summary>
    /// Both forced passes on the same query, and which of them the engine picked.
    /// </summary>
    /// <remarks>
    /// The row above times whichever pass the engine chose, so on its own it cannot say whether that
    /// choice was right: at a document frequency where the rule says "cursor", the row measures the
    /// cursor and finds it fast. This line is the other side of the crossing, and it is what makes
    /// the pass rule checkable rather than assumed.
    /// </remarks>
    private static string ForcedComparison(
        double[] cursorMicroseconds,
        double[] accumulatorMicroseconds,
        int iterations,
        RankedTextSearchEngine.ScoringPass chosen)
    {
        double cursor = cursorMicroseconds[iterations / 2];
        double accumulator = accumulatorMicroseconds[iterations / 2];

        (string faster, double ratio) = cursor <= accumulator
            ? ("cursor", accumulator / cursor)
            : ("accumulator", cursor / accumulator);

        string verdict = chosen == RankedTextSearchEngine.ScoringPass.PerDocument
            ? "the rule chose per-document: below the accumulation floor, so neither pass was in play"
            : ratio < NoiseRatio
                ? $"within {NoiseRatio.ToString("N2", CultureInfo.InvariantCulture)}× of each other, " +
                  $"so no winner is read; the rule chose {Pass(chosen)}"
                : faster == Pass(chosen)
                    ? $"the rule chose {Pass(chosen)}"
                    : $"the rule chose {Pass(chosen)} — DISAGREES";

        return $"      forced p50  cursor {Time(cursor),9}  accumulator {Time(accumulator),9}  " +
               $"→ {faster} is {ratio.ToString("N2", CultureInfo.InvariantCulture)}× faster, {verdict}";
    }

    /// <summary>
    /// How close two medians have to be before the lane refuses to name a winner.
    /// </summary>
    /// <remarks>
    /// Not a statistical test: run-to-run drift on this host exceeds any effect worth acting on, and a
    /// first reading of this lane had the same pass on the same query at 2.90 and 7.60 µs, three
    /// blocks apart. A crossing read out of two numbers a few percent apart would be read out of that
    /// drift.
    /// </remarks>
    private const double NoiseRatio = 1.10;

    /// <summary>
    /// The same query through up to three engines, interleaved inside one loop.
    /// </summary>
    /// <remarks>
    /// Interleaving rather than three timed blocks is the difference between measuring the pass and
    /// measuring the host. A block of a thousand searches takes a few milliseconds; a scheduler
    /// hiccup, another process waking up or a Gen2 collection landing on one block moves that block's
    /// median and leaves the others alone, which is exactly the shape of a false crossing.
    /// </remarks>
    private static (double[] Default, double[]? Accumulator, double[]? Cursor, int Hits) TimePasses(
        RankedTextSearchEngine engine,
        RankedTextSearchEngine accumulatorEngine,
        RankedTextSearchEngine cursorEngine,
        string query,
        SearchOptions options,
        int iterations,
        bool both)
    {
        var microseconds = new double[iterations];
        double[]? accumulatorMicroseconds = both ? new double[iterations] : null;
        double[]? cursorMicroseconds = both ? new double[iterations] : null;
        int hits = 0;

        for (int i = 0; i < iterations; i++)
        {
            microseconds[i] = TimeOne(engine, query, options, ref hits);

            if (both)
            {
                accumulatorMicroseconds![i] = TimeOne(accumulatorEngine, query, options, ref hits);
                cursorMicroseconds![i] = TimeOne(cursorEngine, query, options, ref hits);
            }
        }

        Array.Sort(microseconds);

        if (accumulatorMicroseconds is not null)
            Array.Sort(accumulatorMicroseconds);

        if (cursorMicroseconds is not null)
            Array.Sort(cursorMicroseconds);

        return (microseconds, accumulatorMicroseconds, cursorMicroseconds, hits);
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

    private static LaneOptions Parse(string[] args)
    {
        var sizes = new List<int>();
        bool sweep = false;
        bool both = false;
        string? corpus = null;
        string? data = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (arg == "--sweep")
            {
                sweep = true;
                continue;
            }

            if (arg == "--both")
            {
                both = true;
                continue;
            }

            if (arg == "--corpus")
            {
                corpus = Next(args, ref i, "--corpus");
                continue;
            }

            if (arg == "--data")
            {
                data = Next(args, ref i, "--data");
                continue;
            }

            if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count <= 0)
            {
                throw new ArgumentException(
                    $"Unrecognised cursor argument '{arg}'. Usage: cursor [document-count ...] [--sweep] [--both] " +
                    "[--corpus <nfcorpus|scifact|arguana> [--data <dir>]] " +
                    "(default 10000 100000; 500000 needs about 3.2 GB of managed heap).");
            }

            if (!sizes.Contains(count))
                sizes.Add(count);
        }

        if (sizes.Count == 0)
            sizes.AddRange(DefaultDocumentCounts);

        sizes.Sort();
        return new LaneOptions([.. sizes], sweep, both, corpus, ResolveDataDirectory(data));
    }

    /// <summary>The value after a switch, or a message naming the switch rather than an index error.</summary>
    private static string Next(string[] args, ref int index, string name) =>
        ++index < args.Length
            ? args[index]
            : throw new ArgumentException($"{name} needs a value.");

    /// <summary>
    /// The directory holding the BEIR corpora. Defaulted against the working directory first and then
    /// against the tree above the assembly, so the documented command runs from the repository root
    /// and from a `bin/Release` directory alike.
    /// </summary>
    private static string ResolveDataDirectory(string? given)
    {
        if (given is not null)
            return given;

        foreach (string candidate in Candidates())
        {
            if (Directory.Exists(candidate))
                return candidate;
        }

        return DefaultDataDirectory;

        static IEnumerable<string> Candidates()
        {
            yield return DefaultDataDirectory;

            for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                yield return Path.Combine(directory.FullName, DefaultDataDirectory);
        }
    }

    private static void PrintHeader(LaneOptions options)
    {
        Console.WriteLine("LexiSharp cursor lane — tail-query p50/p99 and allocation per corpus size");
        Console.WriteLine($"host      {RuntimeInformation.OSDescription} / {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"cpu       {Environment.ProcessorCount} logical processors");
        Console.WriteLine(
            $"runtime   {RuntimeInformation.FrameworkDescription}, ServerGC={GCSettings.IsServerGC}, " +
            $"{(IntPtr.Size == 8 ? "64-bit" : "32-bit")} process");

        if (options.Corpus is null)
        {
            Console.WriteLine(
                $"corpus    Zipf 1.07 over {Number(VocabularySize)} terms, {WordsPerDocument} words/document; " +
                "default tokenizer and BM25 (k1 1.5, b 0.75)");
            Console.WriteLine($"queries   {string.Join(" | ", Queries.Select(q => q.Text))}; Limit {Limit}");
            Console.WriteLine($"sizes     {string.Join(", ", options.Sizes.Select(s => Number(s)))}");
        }
        else
        {
            Console.WriteLine(
                $"corpus    BEIR '{options.Corpus}' from {options.DataDirectory} (real documents, " +
                "variable lengths); default tokenizer and BM25 (k1 1.5, b 0.75)");
            Console.WriteLine("queries   the sweep only: a real corpus has no w1x, so the fixed shapes are skipped");
        }

        Console.WriteLine(options.Sweep
            ? $"sweep     one term near each document frequency in {string.Join(", ", SweepFrequencies.Select(f => Number(f)))}"
            : "sweep     off (pass --sweep for one query per document-frequency target)");
        Console.WriteLine(options.Both
            ? "both      on: every query is timed through the accumulator and the cursor pass as well as the chosen one"
            : "both      off (pass --both to time both passes and see which the rule should have chosen)");
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

    /// <summary>What the command line asked for. <see cref="Corpus"/> replaces the generated corpus.</summary>
    private sealed record LaneOptions(
        int[] Sizes, bool Sweep, bool Both, string? Corpus, string DataDirectory);
}
