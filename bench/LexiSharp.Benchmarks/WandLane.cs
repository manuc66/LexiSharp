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
/// The share of a query's posting entries a block bound could prune, measured on the corpus the
/// scale lane builds, with two of its tail queries and a head term.
/// </summary>
/// <remarks>
/// <para>
/// A pruned traversal — block-max WAND or MaxScore — only pays when it can skip posting entries the
/// current term-at-a-time pass reads and scores. This lane measures that share directly: it resolves
/// each query term's postings from the same <see cref="InMemoryTextIndex"/> the engine searches, cuts
/// them into the 128-ordinal windows <c>PostingBlocks</c> writes, computes the bound each window
/// carries, and runs a document-at-a-time traversal that skips a window when the bound proves no
/// document in it can beat the current top-10 threshold. The count it reports is entries, not time:
/// a traversal's own bookkeeping, its heap, and the cost of building the bounds are outside it.
/// </para>
/// <para>
/// <b>The bound.</b> BM25's per-term contribution
/// <c>idf · tf · (k1+1) / (tf + k1 · (1 − b + b · dl / avgdl))</c> rises with <c>tf</c> and falls with
/// the document length, so a window's maximum is computed at the window's largest term frequency and
/// its shortest document: exactly the <c>maxFrequency</c> and <c>minDocumentLength</c> the
/// <c>PostingBlocks</c> header stores per window (the reader exposes them as its
/// <c>MaxFrequency</c> and <c>MinDocumentLength</c> properties).
/// That is a true block bound, not a term-level global maximum; the lane also runs the term-level
/// variant for comparison, because the two have different costs. This corpus writes every document
/// at exactly 50 tokens, so every window's shortest document is 50 and the bound is a bound on term
/// frequency alone — a property of the generator, not of a real corpus.
/// </para>
/// <para>
/// <b>Why the traversal is a lower bound.</b> The cursors say exactly which terms hold the document at
/// the smallest current ordinal, so a term-level upper bound is taken over the terms that can
/// contribute rather than over a prefix MaxScore would order by maximum. When the summed bound of
/// the current windows is below the threshold, every document in the intersection of those windows
/// is skipped. That is safe, and
/// it is not as aggressive as a full block-max WAND, which can use its pivot to skip documents this
/// traversal still scores. So the skipped fraction here is a <b>lower bound</b> on what WAND would
/// skip. Each mode's final page is compared with the engine's own answer, score for score, which is
/// what establishes the traversal is not pruning a document that belongs in the page.
/// </para>
/// <para>
/// <b>What the entry count does not say.</b> A bound is only cheap when a window holds many entries.
/// A rare term has about one entry per occupied 128-ordinal window, so its block header is as large as
/// the entry it replaces and the "skipped entries" fraction overstates the saving. The lane prints
/// entries per occupied window so that the density is visible beside the fraction, and the docs
/// state the caveat.
/// </para>
/// <para>
/// It is a direct run for the same reason <see cref="ScaleLane"/> is: the corpora are 100,000 and
/// 500,000 documents, which a BenchmarkDotNet job cannot hold across its setup invocations. The
/// query latencies printed beside the entry counts are a <see cref="Stopwatch"/> in a plain process
/// with the same five-second warm-up the scale lane uses.
/// </para>
/// </remarks>
internal static class WandLane
{
    private const int WordsPerDocument = 50;
    private const int VocabularySize = 30_000;

    /// <summary>
    /// Ordinals per block: <c>PostingBlocks.BlockSpan</c>, which is internal to the library. The
    /// windows below are cut the same way, by <c>ordinal / BlockSpan</c>, so a bound computed here
    /// is the bound the format would carry.
    /// </summary>
    private const int BlockSpan = 128;

    private const int Limit = 10;

    private static readonly int[] DefaultDocumentCounts = [100_000];

    private static readonly QueryShape[] Queries =
    [
        new("one tail term", "w20000x", ["w20000x"]),
        new("three tail terms", "w20000x w25000x w29000x", ["w20000x", "w25000x", "w29000x"]),
        new("one head term", "w1x", ["w1x"]),
    ];

    private static readonly TimeSpan WarmUpDuration = TimeSpan.FromSeconds(5);

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
        var engine = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());

        engine.Index(documents);
        _sink += engine.Search("w20000x", new SearchOptions(Limit: Limit)).Count;

        Console.WriteLine("warm-up   1,000 documents indexed and one search served (not measured)");
    }

    private static void Measure(int documentCount)
    {
        var generation = Stopwatch.StartNew();
        var documents = CorpusFactory.CreateZipfDocuments(documentCount, WordsPerDocument, VocabularySize);
        generation.Stop();

        var index = new InMemoryTextIndex();
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        var build = Stopwatch.StartNew();
        engine.Index(documents);
        build.Stop();

        // The accumulator every accumulating search rents is a byte[] as long as the ordinal space,
        // and Rent clears it before the pass starts. The isolated clear is a bracket on what a
        // pruned traversal cannot remove, not a claim about its cost inside a search.
        long clearNanoseconds = MeasureClear(index.OrdinalSpace, runs: 7);

        int shortest = int.MaxValue;
        int longest = 0;

        for (int ordinal = 0; ordinal < index.OrdinalSpace; ordinal++)
        {
            var document = index.DocumentAt(ordinal);

            if (document is null)
                continue;

            int length = index.DocumentLength(document.Id);

            if (length < shortest)
                shortest = length;

            if (length > longest)
                longest = length;
        }

        Console.WriteLine();
        Console.WriteLine($"--- {Number(documentCount)} documents ---");
        Console.WriteLine($"host load at build       {LoadAverage()}");
        Console.WriteLine($"corpus generation       {Time(generation.Elapsed.TotalMicroseconds)}");
        Console.WriteLine($"index build             {Time(build.Elapsed.TotalMicroseconds)}  (one build, Stopwatch)");
        Console.WriteLine($"index                   {Number(index.Count)} documents, {Number(index.VocabularySize)} terms, " +
                          $"ordinal space {Number(index.OrdinalSpace)}");
        Console.WriteLine($"lengths                 average {Fixed(index.AverageDocumentLength, 0, 2)}, " +
                          $"min {Number(shortest)}, max {Number(longest)}, N = {Number(index.StatisticDocumentCount)}");
        Console.WriteLine($"accumulator clear       {Time(clearNanoseconds / 1_000.0)} isolated Array.Clear of " +
                          $"{Number(index.OrdinalSpace)} bytes (best of 7)");

        foreach (var query in Queries)
            MeasureQuery(index, engine, query, clearNanoseconds);
    }

    private static void MeasureQuery(
        InMemoryTextIndex index,
        RankedTextSearchEngine engine,
        QueryShape query,
        long clearNanoseconds)
    {
        double k1 = 1.5;
        double b = 0.75;
        double saturation = k1 + 1.0;
        double averageLength = index.AverageDocumentLength;
        int documentCount = index.StatisticDocumentCount;

        var postings = new TermPostings[query.Terms.Length];

        for (int i = 0; i < query.Terms.Length; i++)
            postings[i] = TermPostings.Build(index, query.Terms[i], k1, b, saturation, averageLength, documentCount);

        int entries = 0;
        int blocks = 0;

        foreach (var term in postings)
        {
            entries += term.Count;
            blocks += term.BlockCount;
        }

        // The engine's own answer, which every traversal is checked against.
        var reference = engine.Search(query.Text, new SearchOptions(Limit: Limit));

        var exhaustive = Traverse(postings, index, Prune.None, k1, b, saturation, averageLength);
        var globalMax = Traverse(postings, index, Prune.GlobalMax, k1, b, saturation, averageLength);
        var blockMax = Traverse(postings, index, Prune.BlockMax, k1, b, saturation, averageLength);

        int[] candidates = CandidateDocuments(postings, index.OrdinalSpace);

        var search = TimeSearch(engine, query.Text, documentCount, out var latency);

        Console.WriteLine();
        Console.WriteLine($"query  {query.Text}   ({query.Label})");

        foreach (var term in postings)
        {
            Console.WriteLine(
                $"  term {term.Term,-9} df {Number(term.Count),7}  " +
                $"windows {Number(term.BlockCount),6}  " +
                $"entries/window {Fixed(term.Count / (double)Math.Max(term.BlockCount, 1), 6, 2)}  " +
                $"1-entry {Percent(term.OneEntryBlocks / (double)Math.Max(term.BlockCount, 1), 1),7}  " +
                $"idf {Fixed(term.Idf, 5, 4)}  max {Fixed(term.GlobalMax, 6, 4)}");
        }

        Console.WriteLine(
            $"  postings {Number(entries)} entries in {Number(blocks)} windows; " +
            $"{Number(candidates.Length)} candidate documents; hits {Number(search.Count)}");
        Console.WriteLine(
            $"  current pass    {Number(exhaustive.Scored)} entries scored, " +
            $"{Number(exhaustive.Documents)} documents visited; " +
            $"p50 {Time(latency.MedianMicroseconds)}, min {Time(latency.MinimumMicroseconds)}, " +
            $"mean {Time(latency.MeanMicroseconds)} over {Number(latency.Iterations)} searches");

        PrintTraversal("global max", globalMax, entries, exhaustive, reference);
        PrintTraversal("block max ", blockMax, entries, exhaustive, reference);

        // What the posting share leaves of the measured search, if the per-entry figure the
        // repository carries (12.6 ns at 1,000,000 documents) held here. This is a share of the
        // measured p50, not a predicted latency for a traversal nobody has written.
        double postingShare = entries * 12.6 / (latency.MedianMicroseconds * 1_000.0);
        Console.WriteLine(
            $"  posting share   {Percent(postingShare, 1)} of the measured p50 at 12.6 ns/entry " +
            $"(that figure is from another session at 1,000,000 documents, not re-measured here); " +
            $"accumulator clear alone {Percent(clearNanoseconds / (latency.MedianMicroseconds * 1_000.0), 1)}");
    }

    private static void PrintTraversal(
        string label,
        TraversalResult result,
        int entries,
        TraversalResult exhaustive,
        IReadOnlyList<SearchResult> reference)
    {
        long read = entries - result.Jumped;
        double skippedFraction = entries == 0 ? 0 : result.Jumped / (double)entries;
        double readCeiling = read == 0 ? double.PositiveInfinity : entries / (double)read;
        double scoringCeiling = result.Scored == 0 ? double.PositiveInfinity : entries / (double)result.Scored;

        Console.WriteLine(
            $"  {label}       entries read {Number(read)} of {Number(entries)} " +
            $"({Number(result.Jumped)} skipped, {Percent(skippedFraction, 1)}); " +
            $"contributions {Number(result.Scored)} ({Number(result.UnscoredAdvanced)} read, not scored); " +
            $"theta {Fixed(result.Threshold, 0, 4)}");
        Console.WriteLine(
            $"  {new string(' ', label.Length)}       ceiling: reads {Fixed(readCeiling, 5, 2)}x, " +
            $"weight arithmetic {Fixed(scoringCeiling, 5, 2)}x (postings only, bookkeeping excluded)");

        bool matches = Matches(result.Page, reference);

        Console.WriteLine(
            $"  {new string(' ', label.Length)}       page " +
            (matches ? "matches the exhaustive search exactly" : "DOES NOT MATCH the exhaustive search") +
            $" (exhaustive scored {Number(exhaustive.Scored)} entries)");
    }

    /// <summary>
    /// One document-at-a-time traversal over the same postings, with the requested pruning test.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Prune.None"/> is the shape of the current pass in entry terms: every entry of every
    /// term contributes. It exists to check the traversal against the engine, not to be a fast path.
    /// </para>
    /// <para>
    /// <see cref="Prune.GlobalMax"/> is the term-level bound: at the smallest current ordinal, the
    /// terms that hold a posting there are known exactly, and the sum of their whole-list maxima is
    /// an upper bound on the document's score. Below the threshold, the document is not scored — but
    /// its entries are still read to advance the cursors, which is why this variant saves arithmetic
    /// and not reads.
    /// </para>
    /// <para>
    /// <see cref="Prune.BlockMax"/> is the window bound. Let <c>d</c> be the smallest current ordinal
    /// and <c>w</c> the earliest window end among the terms' current windows. For any document
    /// <c>x</c> in <c>[d, w]</c>, a term whose cursor is at or below <c>x</c> has <c>x</c> inside its
    /// current window (the window ends at or after <c>w</c>), so its contribution is at most that
    /// window's bound; a term whose cursor is above <c>x</c> does not hold <c>x</c> at all. The sum of
    /// the current bounds therefore bounds every document in <c>[d, w]</c>, and the whole range is
    /// skipped when that sum is below the threshold. Each cursor then moves to its first entry past
    /// <c>w</c>. Being conservative — it never uses a pivot, and it scores every document the bound
    /// does not clear — its skips are a lower bound on a full block-max WAND's.
    /// </para>
    /// </remarks>
    private static TraversalResult Traverse(
        TermPostings[] postings,
        InMemoryTextIndex index,
        Prune prune,
        double k1,
        double b,
        double saturation,
        double averageLength)
    {
        int count = postings.Length;
        var cursor = new int[count];
        var top = new TopWindow(Limit, index);

        long scored = 0;
        long jumped = 0;
        long unscoredAdvanced = 0;
        long documents = 0;

        while (true)
        {
            int smallest = int.MaxValue;

            for (int t = 0; t < count; t++)
            {
                if (cursor[t] < postings[t].Count && postings[t].Ordinals[cursor[t]] < smallest)
                    smallest = postings[t].Ordinals[cursor[t]];
            }

            if (smallest == int.MaxValue)
                break;

            if (top.IsFull)
            {
                double threshold = top.Threshold;

                if (prune == Prune.BlockMax)
                {
                    // A whole window range first: the intersection of the current windows. If the sum
                    // of their bounds is below the threshold, every document in it is skipped and each
                    // cursor jumps to its first entry past the range — those entries are never read.
                    int windowEnd = int.MaxValue;
                    double bound = 0;

                    for (int t = 0; t < count; t++)
                    {
                        if (cursor[t] >= postings[t].Count)
                            continue;

                        int block = postings[t].EntryBlock[cursor[t]];
                        windowEnd = Math.Min(windowEnd, postings[t].BlockLastOrdinal[block]);
                        bound += postings[t].BlockMax[block];
                    }

                    if (windowEnd != int.MaxValue && bound < threshold)
                    {
                        for (int t = 0; t < count; t++)
                        {
                            int from = cursor[t];
                            int to = from;

                            if (from < postings[t].Count)
                            {
                                to = Array.BinarySearch(postings[t].Ordinals, from, postings[t].Count - from, windowEnd + 1);

                                if (to < 0)
                                    to = ~to;
                            }

                            jumped += to - from;
                            cursor[t] = to;
                        }

                        continue;
                    }

                    // The window range did not clear. The document at the smallest ordinal may still
                    // be below the threshold: the terms holding a posting there are known exactly, and
                    // their current windows bound its score. Its contributions are then not computed,
                    // though its entries are read to advance the cursors.
                    double documentBound = 0;

                    for (int t = 0; t < count; t++)
                    {
                        if (cursor[t] < postings[t].Count && postings[t].Ordinals[cursor[t]] == smallest)
                            documentBound += postings[t].BlockMax[postings[t].EntryBlock[cursor[t]]];
                    }

                    if (documentBound < threshold)
                    {
                        for (int t = 0; t < count; t++)
                        {
                            if (cursor[t] < postings[t].Count && postings[t].Ordinals[cursor[t]] == smallest)
                            {
                                cursor[t]++;
                                unscoredAdvanced++;
                            }
                        }

                        continue;
                    }
                }
                else if (prune == Prune.GlobalMax)
                {
                    double bound = 0;

                    for (int t = 0; t < count; t++)
                    {
                        if (cursor[t] < postings[t].Count && postings[t].Ordinals[cursor[t]] == smallest)
                            bound += postings[t].GlobalMax;
                    }

                    if (bound < threshold)
                    {
                        for (int t = 0; t < count; t++)
                        {
                            if (cursor[t] < postings[t].Count && postings[t].Ordinals[cursor[t]] == smallest)
                            {
                                cursor[t]++;
                                unscoredAdvanced++;
                            }
                        }

                        continue;
                    }
                }
            }

            // The document at `smallest`, scored in query-term order so the sum is the same
            // sequence of additions the engine's plan performs and the result can be compared
            // bit for bit.
            double score = 0;

            for (int t = 0; t < count; t++)
            {
                if (cursor[t] < postings[t].Count && postings[t].Ordinals[cursor[t]] == smallest)
                {
                    int i = cursor[t];
                    score += Contribution(postings[t].Idf, postings[t].Frequencies[i], postings[t].Lengths[i],
                        k1, b, saturation, averageLength);
                    scored++;
                    cursor[t]++;
                }
            }

            documents++;
            top.Add(score, smallest);
        }

        return new TraversalResult(scored, jumped, unscoredAdvanced, documents, top.Threshold, top.Page);
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

    /// <summary>The documents holding at least one of the query's terms.</summary>
    private static int[] CandidateDocuments(TermPostings[] postings, int ordinalSpace)
    {
        var seen = new bool[ordinalSpace];
        var documents = new List<int>();

        foreach (var term in postings)
        {
            foreach (int ordinal in term.Ordinals)
            {
                if (seen[ordinal])
                    continue;

                seen[ordinal] = true;
                documents.Add(ordinal);
            }
        }

        documents.Sort();
        return [.. documents];
    }

    private static bool Matches(IReadOnlyList<TopWindow.Entry> page, IReadOnlyList<SearchResult> reference)
    {
        if (page.Count != reference.Count)
            return false;

        for (int i = 0; i < page.Count; i++)
        {
            if (page[i].Id != reference[i].DocumentId || page[i].Score != reference[i].Score) // NOSONAR:S1244 (bit equality is the check)
                return false;
        }

        return true;
    }

    /// <summary>
    /// The engine's own search, warmed the way the scale lane warms it and timed over individual
    /// searches. The result is kept so the traversal's page can be compared against it.
    /// </summary>
    private static IReadOnlyList<SearchResult> TimeSearch(
        RankedTextSearchEngine engine,
        string query,
        int documentCount,
        out Latency latency)
    {
        var options = new SearchOptions(Limit: Limit);
        int iterations = documentCount >= 500_000 ? 100 : 300;

        var warmUp = Stopwatch.StartNew();
        long warmUpSearches = 0;

        while (warmUp.Elapsed < WarmUpDuration)
        {
            _sink += engine.Search(query, options).Count;
            warmUpSearches++;
        }

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
            microseconds.Average(),
            iterations,
            warmUpSearches);

        return result;
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

    private static int[] Parse(string[] args)
    {
        var sizes = new List<int>();

        foreach (string arg in args)
        {
            if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count <= 0)
            {
                throw new ArgumentException(
                    $"Unrecognised wand argument '{arg}'. Usage: wand [document-count ...] " +
                    "(default 100000; 500000 needs about 3.2 GB of managed heap).");
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
        Console.WriteLine("LexiSharp pruning-headroom lane — posting entries a block bound can skip, per query");
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
        Console.WriteLine("bound     per 128-ordinal window: idf · maxTf · (k1+1) / (maxTf + k1 · (1 − b + b · minLen/avgdl))");
        Console.WriteLine("counts    entries are posting entries; time is a Stopwatch, not a BenchmarkDotNet report");
        Console.WriteLine();
    }

    /// <summary>The host's one/five/fifteen-minute load, so a run beside another session is visible.</summary>
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

    private enum Prune
    {
        None,
        GlobalMax,
        BlockMax,
    }

    private sealed record QueryShape(string Label, string Text, string[] Terms);

    private readonly record struct Latency(
        double MinimumMicroseconds,
        double MedianMicroseconds,
        double MeanMicroseconds,
        int Iterations,
        long WarmUpSearches);

    private readonly record struct TraversalResult(
        long Scored,
        long Jumped,
        long UnscoredAdvanced,
        long Documents,
        double Threshold,
        IReadOnlyList<TopWindow.Entry> Page);

    /// <summary>
    /// One term's postings as the flat arrays the accumulating pass walks, plus the per-window bound
    /// the posting format carries and the window each entry belongs to.
    /// </summary>
    private sealed class TermPostings
    {
        private TermPostings(
            string term,
            int[] ordinals,
            int[] frequencies,
            int[] lengths,
            double idf,
            double globalMax,
            double[] blockMax,
            int[] blockLastOrdinal,
            int[] entryBlock,
            int oneEntryBlocks)
        {
            Term = term;
            Ordinals = ordinals;
            Frequencies = frequencies;
            Lengths = lengths;
            Idf = idf;
            GlobalMax = globalMax;
            BlockMax = blockMax;
            BlockLastOrdinal = blockLastOrdinal;
            EntryBlock = entryBlock;
            OneEntryBlocks = oneEntryBlocks;
        }

        public string Term { get; }

        public int[] Ordinals { get; }

        public int[] Frequencies { get; }

        public int[] Lengths { get; }

        public double Idf { get; }

        public double GlobalMax { get; }

        public double[] BlockMax { get; }

        public int[] BlockLastOrdinal { get; }

        public int[] EntryBlock { get; }

        public int OneEntryBlocks { get; }

        public int Count => Ordinals.Length;

        public int BlockCount => BlockLastOrdinal.Length;

        public static TermPostings Build(
            InMemoryTextIndex index,
            string term,
            double k1,
            double b,
            double saturation,
            double averageLength,
            int documentCount)
        {
            var ordinals = new List<int>();
            var frequencies = new List<int>();
            var lengths = new List<int>();

            for (int ordinal = 0; ordinal < index.OrdinalSpace; ordinal++)
            {
                var document = index.DocumentAt(ordinal);

                if (document is null)
                    continue;

                int frequency = index.TermFrequency(document.Id, term);

                if (frequency == 0)
                    continue;

                ordinals.Add(ordinal);
                frequencies.Add(frequency);
                lengths.Add(index.DocumentLength(document.Id));
            }

            int df = ordinals.Count;
            double idf = documentCount == 0
                ? 0
                : Math.Log(1.0 + ((documentCount - df + 0.5) / (df + 0.5)));

            var blockMax = new List<double>(Math.Max(df, 1));
            var blockLastOrdinal = new List<int>(Math.Max(df, 1));
            var entryBlock = new int[df];
            int oneEntryBlocks = 0;
            int i = 0;

            while (i < df)
            {
                int window = ordinals[i] / BlockSpan;
                int end = i + 1;

                while (end < df && ordinals[end] / BlockSpan == window)
                    end++;

                int maxFrequency = 0;
                int minLength = int.MaxValue;

                for (int j = i; j < end; j++)
                {
                    if (frequencies[j] > maxFrequency)
                        maxFrequency = frequencies[j];

                    if (lengths[j] < minLength)
                        minLength = lengths[j];
                }

                for (int j = i; j < end; j++)
                    entryBlock[j] = blockMax.Count;

                blockMax.Add(Contribution(idf, maxFrequency, minLength, k1, b, saturation, averageLength));
                blockLastOrdinal.Add(ordinals[end - 1]);

                if (end - i == 1)
                    oneEntryBlocks++;

                i = end;
            }

            // A window's bound must cover every entry the window holds; if it did not, a pruned pass
            // would drop a document that belongs in the page. Checked here once per term because the
            // traversal's correctness rests on it.
            double globalMax = 0;

            for (int e = 0; e < df; e++)
            {
                double contribution = Contribution(idf, frequencies[e], lengths[e], k1, b, saturation, averageLength);
                double bound = blockMax[entryBlock[e]];

                if (bound < contribution)
                {
                    throw new InvalidOperationException(
                        $"the window bound for {term} entry {e} ({bound}) is below its contribution ({contribution})");
                }

                if (contribution > globalMax)
                    globalMax = contribution;
            }

            return new TermPostings(
                term,
                [.. ordinals],
                [.. frequencies],
                [.. lengths],
                idf,
                globalMax,
                [.. blockMax],
                [.. blockLastOrdinal],
                entryBlock,
                oneEntryBlocks);
        }
    }

    /// <summary>
    /// The engine's bounded top-<see cref="Limit"/> window, in the same order: higher score first,
    /// ties by ordinal document id, which is the default tie-break.
    /// </summary>
    private sealed class TopWindow
    {
        private readonly int _limit;
        private readonly InMemoryTextIndex _index;
        private readonly List<Entry> _entries = [];

        public TopWindow(int limit, InMemoryTextIndex index)
        {
            _limit = limit;
            _index = index;
        }

        public bool IsFull => _entries.Count >= _limit;

        /// <summary>The worst retained score, or 0 while the window still has room.</summary>
        public double Threshold => IsFull ? _entries[^1].Score : 0;

        public IReadOnlyList<Entry> Page => _entries;

        public void Add(double score, int ordinal)
        {
            string id = _index.DocumentAt(ordinal)!.Id;
            int at = _entries.Count;

            var entry = new Entry(id, score);

            while (at > 0 && RanksBefore(entry, _entries[at - 1]))
                at--;

            _entries.Insert(at, entry);

            if (_entries.Count > _limit)
                _entries.RemoveAt(_entries.Count - 1);
        }

        private static bool RanksBefore(Entry candidate, Entry incumbent) =>
            candidate.Score > incumbent.Score
            || (candidate.Score == incumbent.Score && string.CompareOrdinal(candidate.Id, incumbent.Id) < 0); // NOSONAR:S1244

        public readonly record struct Entry(string Id, double Score);
    }
}
