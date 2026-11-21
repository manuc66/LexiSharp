using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;

namespace LexiSharp.Benchmarks;

/// <summary>
/// The pruning ceiling of a real segment's posting blocks, and what the current block format leaves a
/// block-streaming traversal to read.
/// </summary>
/// <remarks>
/// <para>
/// The <c>wand</c> lane answers the same question over the flat arrays an <see cref="InMemoryTextIndex"/>
/// holds. That lane does not measure the segment path, and the segment path is the one where the
/// argument "the in-memory accumulator is already too fast for pruning" does not transfer: a mapped
/// segment cannot hand out those arrays, and its blocks already carry the bound. This lane reads the
/// real headers <c>PostingBlocks</c> wrote, computes the same block bound, and counts the entries a
/// traversal could skip against the query's final top-10 threshold.
/// </para>
/// <para>
/// <b>It is a ceiling, not a traversal.</b> The threshold is taken from the engine's finished page,
/// which is the best a traversal that starts at zero could ever know. A real traversal grows its
/// threshold as it fills the window, so it skips less. The lane also reports the <b>superblock</b>
/// ceiling: whether the eight consecutive blocks a skip-table entry spans could be skipped together
/// if the skip entry carried a bound. Without that bound a traversal still reads eight headers, which
/// is the cost this lane exists to price.
/// </para>
/// <para>
/// <b>What it does not measure.</b> The traversal's cursor and heap bookkeeping, the cost of a pivot,
/// and the cost of a page fault on a cold mapping. It does measure the array and mapped engine on the
/// same query, so the latency a traversal would have to beat is on the page. The block headers are
/// read through the array source: the bounds are a property of the format, not of the source.
/// </para>
/// </remarks>
internal static class SegmentWandLane
{
    private const int WordsPerDocument = 50;
    private const int VocabularySize = 30_000;
    private const int Limit = 10;

    /// <summary>Blocks per skip-table entry, which is <c>PostingBlocks.SkipEvery</c>.</summary>
    private const int SkipEvery = 8;

    private static readonly int[] DefaultDocumentCounts = [100_000];

    private static readonly QueryShape[] Queries =
    [
        new("one tail term", "w20000x", ["w20000x"]),
        new("three tail terms", "w20000x w25000x w29000x", ["w20000x", "w25000x", "w29000x"]),
        new("one head term", "w1x", ["w1x"]),
    ];

    private static readonly TimeSpan WarmUpDuration = TimeSpan.FromSeconds(1);

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
        var documents = CorpusFactory.CreateZipfDocuments(1_000, WordsPerDocument, VocabularySize);
        var index = new InMemoryTextIndex();
        index.Index(documents);
        var engine = new RankedTextSearchEngine(new SegmentTextIndex(SegmentWriter.Write(index)), new Bm25Scorer());

        _sink += engine.Search("w20000x", new SearchOptions(Limit: Limit)).Count;

        Console.WriteLine("warm-up   1,000 documents written to a segment and one search served (not measured)");
    }

    private static void Measure(int documentCount)
    {
        var generation = Stopwatch.StartNew();
        var documents = CorpusFactory.CreateZipfDocuments(documentCount, WordsPerDocument, VocabularySize);
        generation.Stop();

        var index = new InMemoryTextIndex();
        var build = Stopwatch.StartNew();
        index.Index(documents);
        build.Stop();

        var write = Stopwatch.StartNew();
        byte[] bytes = SegmentWriter.Write(index);
        write.Stop();

        var reader = new SegmentReader(bytes);
        var arrayEngine = new RankedTextSearchEngine(new SegmentTextIndex(bytes), new Bm25Scorer());

        string path = Path.Combine(Path.GetTempPath(), $"lexisharp-segment-wand-{documentCount}.lxs");

        try
        {
            File.WriteAllBytes(path, bytes);

            using var mapped = MappedSegment.Open(path);
            var mappedEngine = new RankedTextSearchEngine(mapped.Index, new Bm25Scorer());

            Console.WriteLine();
            Console.WriteLine($"--- {Number(documentCount)} documents ---");
            Console.WriteLine($"host load at build       {LoadAverage()}");
            Console.WriteLine($"corpus generation       {Time(generation.Elapsed.TotalMicroseconds)}");
            Console.WriteLine($"index build             {Time(build.Elapsed.TotalMicroseconds)}  (in memory, Stopwatch)");
            Console.WriteLine($"segment write           {Time(write.Elapsed.TotalMicroseconds)}  ({Number(bytes.Length)} bytes, " +
                              $"{Fixed(bytes.Length / (double)Math.Max(index.Count, 1), 8, 1)} B/document)");
            Console.WriteLine($"segment                 {Number(reader.DocumentCount)} documents, {Number(reader.TermCount)} terms");
            Console.WriteLine($"average length          {Fixed(index.AverageDocumentLength, 8, 2)} tokens");

            double averageLength = AverageDocumentLength(reader);

            foreach (var query in Queries)
                MeasureQuery(reader, arrayEngine, mappedEngine, query, documentCount, averageLength);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static void MeasureQuery(
        SegmentReader reader,
        RankedTextSearchEngine arrayEngine,
        RankedTextSearchEngine mappedEngine,
        QueryShape query,
        int documentCount,
        double averageLength)
    {
        double k1 = 1.5;
        double b = 0.75;
        double saturation = k1 + 1.0;

        var terms = new TermBlocks[query.Terms.Length];

        for (int i = 0; i < query.Terms.Length; i++)
            terms[i] = TermBlocks.Build(reader, query.Terms[i], k1, b, saturation, averageLength);

        int entries = 0;
        int blocks = 0;
        int oneEntryBlocks = 0;

        foreach (var term in terms)
        {
            entries += term.Count;
            blocks += term.BlockCount;
            oneEntryBlocks += term.OneEntryBlocks;
        }

        var reference = arrayEngine.Search(query.Text, new SearchOptions(Limit: Limit));
        double threshold = reference.Count == Limit ? reference[^1].Score : 0;

        long blockCeiling = 0;
        long superblockCeiling = 0;

        foreach (var term in terms)
        {
            blockCeiling += term.EntriesBelow(threshold);
            superblockCeiling += term.EntriesInSuperblocksBelow(threshold, SkipEvery);
        }

        var arrayResult = TimeSearch(arrayEngine, query.Text, documentCount, out var arrayLatency);
        var mappedResult = TimeSearch(mappedEngine, query.Text, documentCount, out var mappedLatency);

        (var header, var decode) = TimeBlockWalk(reader, query.Terms[0]);

        Console.WriteLine();
        Console.WriteLine($"query  {query.Text}   ({query.Label})");

        foreach (var term in terms)
        {
            Console.WriteLine(
                $"  term {term.Term,-9} df {Number(term.DocumentFrequency),7}  " +
                $"blocks {Number(term.BlockCount),6}  " +
                $"entries/block {Fixed(term.Count / (double)Math.Max(term.BlockCount, 1), 6, 2)}  " +
                $"1-entry {Percent(term.OneEntryBlocks / (double)Math.Max(term.BlockCount, 1), 1),7}  " +
                $"idf {Fixed(term.Idf, 5, 4)}");
        }

        Console.WriteLine(
            $"  postings {Number(entries)} entries in {Number(blocks)} blocks; " +
            $"{Number(oneEntryBlocks)} one-entry blocks; threshold {Fixed(threshold, 6, 4)}");
        Console.WriteLine(
            $"  ceiling      per block {Number(blockCeiling)} entries ({Percent(Fraction(blockCeiling, entries), 1)}); " +
            $"per {SkipEvery}-block superblock {Number(superblockCeiling)} ({Percent(Fraction(superblockCeiling, entries), 1)})");
        Console.WriteLine(
            $"  block walk   headers {Time(header)}, full decode {Time(decode)} for the first term (p50 of 200/50 runs)");
        Console.WriteLine(
            $"  array engine p50 {Time(arrayLatency.MedianMicroseconds)}, p95 {Time(arrayLatency.P95Microseconds)}, " +
            $"min {Time(arrayLatency.MinimumMicroseconds)}; mapped p50 {Time(mappedLatency.MedianMicroseconds)}, " +
            $"p95 {Time(mappedLatency.P95Microseconds)}, min {Time(mappedLatency.MinimumMicroseconds)}");
        Console.WriteLine(
            $"  page         array {(Matches(arrayResult, reference) ? "matches" : "DOES NOT MATCH")} the reference; " +
            $"mapped {(Matches(mappedResult, reference) ? "matches" : "DOES NOT MATCH")} the reference");
    }

    /// <summary>
    /// The average length the segment reports. A segment holds every document's length; the reader
    /// exposes the same value through its index, which the caller already has in the engine.
    /// </summary>
    private static double AverageDocumentLength(SegmentReader reader)
    {
        long tokens = 0;

        for (int ordinal = 0; ordinal < reader.DocumentCount; ordinal++)
            tokens += reader.DocumentLength(ordinal);

        return reader.DocumentCount == 0 ? 0 : tokens / (double)reader.DocumentCount;
    }

    /// <summary>BM25's per-term contribution, in the same expression order the scorer evaluates.</summary>
    private static double Contribution(
        double idf,
        int termFrequency,
        int documentLength,
        double k1,
        double b,
        double saturation,
        double averageLength)
    {
        double normalization = 1.0 - b + (b * documentLength / averageLength);

        return idf * termFrequency * saturation / (termFrequency + (k1 * normalization));
    }

    /// <summary>The engine's own search, warmed for a second and timed over individual searches.</summary>
    private static IReadOnlyList<SearchResult> TimeSearch(
        RankedTextSearchEngine engine,
        string query,
        int documentCount,
        out Latency latency)
    {
        var options = new SearchOptions(Limit: Limit);
        int iterations = documentCount >= 500_000 ? 60 : 200;

        var warmUp = Stopwatch.StartNew();

        while (warmUp.Elapsed < WarmUpDuration)
            _sink += engine.Search(query, options).Count;

        var microseconds = new double[iterations];
        IReadOnlyList<SearchResult> result = Array.Empty<SearchResult>();

        for (int i = 0; i < iterations; i++)
        {
            long started = Stopwatch.GetTimestamp();
            result = engine.Search(query, options);
            microseconds[i] = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
            _sink += result.Count;
        }

        Array.Sort(microseconds);

        latency = new Latency(
            microseconds[0],
            microseconds[iterations / 2],
            microseconds[(int)(iterations * 0.95)],
            microseconds.Average(),
            iterations);

        return result;
    }

    /// <summary>
    /// The cost of walking one term's blocks twice: once reading headers only, once decoding every
    /// entry. The difference is what a pruned traversal would save by skipping a block's payload.
    /// </summary>
    private static (double HeaderMicroseconds, double DecodeMicroseconds) TimeBlockWalk(
        SegmentReader reader,
        string term)
    {
        for (int i = 0; i < 3; i++)
            Walk(reader, term, decode: false);

        double header = Median(() => Walk(reader, term, decode: false), 200);
        double decode = Median(() => Walk(reader, term, decode: true), 50);

        return (header, decode);
    }

    private static double Median(Func<int> run, int iterations)
    {
        var samples = new double[iterations];

        for (int i = 0; i < iterations; i++)
        {
            long started = Stopwatch.GetTimestamp();
            _sink += run();
            samples[i] = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
        }

        Array.Sort(samples);

        return samples[iterations / 2];
    }

    private static int Walk(SegmentReader reader, string term, bool decode)
    {
        if (!reader.TryFindTerm(term.AsSpan(), out _, out var postings))
            return 0;

        int entries = 0;

        while (postings.MoveNext())
        {
            if (!decode)
                continue;

            while (postings.TryReadEntry(out _, out _))
                entries++;
        }

        return entries;
    }

    private static bool Matches(IReadOnlyList<SearchResult> left, IReadOnlyList<SearchResult> right)
    {
        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            if (left[i].DocumentId != right[i].DocumentId || left[i].Score != right[i].Score) // NOSONAR:S1244 (bit equality is the check)
                return false;
        }

        return true;
    }

    private static double Fraction(long part, long total) => total == 0 ? 0 : part / (double)total;

    private static int[] Parse(string[] args)
    {
        var sizes = new List<int>();

        foreach (string arg in args)
        {
            if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count <= 0)
            {
                throw new ArgumentException(
                    $"Unrecognised segment-wand argument '{arg}'. Usage: segment-wand [document-count ...] " +
                    "(default 100000; the segment and the Zipf documents are both resident).");
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
        Console.WriteLine("LexiSharp segment pruning lane — block-bound ceiling, block walk cost, mapped vs array search");
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
        Console.WriteLine("bound     per 128-ordinal block: idf · maxTf · (k1+1) / (maxTf + k1 · (1 − b + b · minLen/avgdl))");
        Console.WriteLine("ceiling   entries in a block whose bound is below the engine's final top-10 threshold");
        Console.WriteLine("          the superblock column is the same test over the 8 blocks one skip entry spans");
        Console.WriteLine("counts    entries are posting entries; time is a Stopwatch, not a BenchmarkDotNet report");
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

    private static string Percent(double fraction, int decimals) =>
        (fraction * 100).ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) + " %";

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Fixed(double value, int width, int decimals) =>
        value.ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
            .PadLeft(width);

    private sealed record QueryShape(string Label, string Text, string[] Terms);

    private readonly record struct Latency(
        double MinimumMicroseconds,
        double MedianMicroseconds,
        double P95Microseconds,
        double MeanMicroseconds,
        int Iterations);

    /// <summary>One term's block headers as the segment stores them, plus the bound each one carries.</summary>
    private sealed record TermBlocks(
        string Term,
        int DocumentFrequency,
        double Idf,
        Block[] Blocks)
    {
        public int Count => Blocks.Sum(block => block.Count);

        public int BlockCount => Blocks.Length;

        public int OneEntryBlocks => Blocks.Count(block => block.Count == 1);

        public long EntriesBelow(double threshold)
        {
            long entries = 0;

            foreach (var block in Blocks)
            {
                if (block.Bound < threshold)
                    entries += block.Count;
            }

            return entries;
        }

        public long EntriesInSuperblocksBelow(double threshold, int superblock)
        {
            long entries = 0;
            long runEntries = 0;
            double runBound = 0;

            for (int i = 0; i < Blocks.Length; i++)
            {
                var block = Blocks[i];

                runEntries += block.Count;

                if (block.Bound > runBound)
                    runBound = block.Bound;

                if ((i + 1) % superblock == 0 || i + 1 == Blocks.Length)
                {
                    if (runBound < threshold)
                        entries += runEntries;

                    runEntries = 0;
                    runBound = 0;
                }
            }

            return entries;
        }

        public static TermBlocks Build(
            SegmentReader reader,
            string term,
            double k1,
            double b,
            double saturation,
            double averageLength)
        {
            if (!reader.TryFindTerm(term.AsSpan(), out int documentFrequency, out var postings))
                return new TermBlocks(term, 0, 0, []);

            double idf = documentFrequency == 0
                ? 0
                : Math.Log(1.0 + ((reader.DocumentCount - documentFrequency + 0.5) / (documentFrequency + 0.5)));

            var blocks = new List<Block>(Math.Max(postings.BlockCount, 1));

            while (postings.MoveNext())
            {
                double bound = Contribution(
                    idf,
                    postings.MaxFrequency,
                    postings.MinDocumentLength,
                    k1,
                    b,
                    saturation,
                    averageLength);

                blocks.Add(new Block(
                    postings.Count,
                    postings.MaxFrequency,
                    postings.MinDocumentLength,
                    postings.LastOrdinal,
                    bound));
            }

            return new TermBlocks(term, documentFrequency, idf, [.. blocks]);
        }
    }

    private readonly record struct Block(
        int Count,
        int MaxFrequency,
        int MinDocumentLength,
        int LastOrdinal,
        double Bound);
}
