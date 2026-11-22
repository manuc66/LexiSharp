using System.Diagnostics;
using System.Globalization;
using LexiSharp.Core;
using LexiSharp.Indexing;

namespace LexiSharp.Benchmarks;

/// <summary>
/// What one learned-sparse query costs in the shipped <see cref="SparseTextSearchEngine"/>, on the
/// synthetic SPLADE shape the pruning lane uses.
/// </summary>
/// <remarks>
/// <para>
/// The sparse-wand lane measures a contiguous <c>(ordinal, weight)</c> layout and says the dictionary
/// engine's indirection is where the order of magnitude is. This lane measures that engine directly,
/// so a layout change can be compared against the measurement it is meant to improve rather than
/// against an argument.
/// </para>
/// <para>
/// The corpus is synthetic because no SPLADE model ships here: each document draws 200–300 distinct
/// terms from a Zipf popularity over 30,000, with weights skewed low. The query shapes separate the
/// fixed cost from the posting work: 40 absent terms (no postings, so only lookups), 40 tail terms
/// (one or a few postings each), and 1 / 10 / 40 / 100 head terms (the union a real expansion
/// reaches). The vectors are loaded through <see cref="SparseTextSearchEngine.Import"/>, which skips
/// the provider, so the number is storage and scoring rather than ONNX inference.
/// </para>
/// </remarks>
internal static class SparseEngineLane
{
    private const int VocabularySize = 30_000;
    private const int Limit = 10;
    private const int DefaultDocuments = 50_000;
    private const int DefaultTermsPerDocument = 173;
    private const int Iterations = 50;
    private const int WarmupSearches = 3;

    private static long _sink;

    public static void Run(string[] args)
    {
        // The import profile is the half of this lane that does not need the query shapes: a
        // layout change is decided on the build, and running the searches only adds noise to a
        // sampling profile or to a wall-clock comparison of two imports.
        bool importOnly = false;
        int remove = 0;
        int vocabulary = VocabularySize;
        var positional = new List<string>(2);

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--import-only":
                    importOnly = true;
                    break;

                case "--remove" when i + 1 < args.Length:
                    remove = ParsePositive(args[++i], "removals");
                    break;

                case "--vocabulary" when i + 1 < args.Length:
                    vocabulary = ParsePositive(args[++i], "vocabulary");
                    break;

                default:
                    positional.Add(args[i]);
                    break;
            }
        }

        int documents = positional.Count > 0 ? ParsePositive(positional[0], "documents") : DefaultDocuments;
        int termsPerDocument = positional.Count > 1 ? ParsePositive(positional[1], "terms per document") : DefaultTermsPerDocument;

        if (remove > documents)
            throw new ArgumentException($"sparse-engine cannot remove {Number(remove)} of {Number(documents)} documents.");

        // Each document draws its terms from the same fixed vocabulary, so asking for more distinct
        // terms than the vocabulary holds would never fill the vector and would hang the lane.
        if (termsPerDocument > vocabulary)
            throw new ArgumentException(
                $"sparse-engine needs at most {Number(vocabulary)} terms per document, got {Number(termsPerDocument)}.");

        Console.WriteLine(
            $"sparse-engine   {Number(documents)} documents, {Number(termsPerDocument)} terms/document, " +
            $"vocabulary {Number(vocabulary)}; absent / rare / head-1 / head-10 / head-40 / head-100 queries");

        var generation = Stopwatch.StartNew();
        var corpus = Corpus.Generate(documents, termsPerDocument, vocabulary, seed: 20_261);
        generation.Stop();

        var engine = new SparseTextSearchEngine(new FixedProvider(corpus.Queries));
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var process = Process.GetCurrentProcess();
        process.Refresh();
        TimeSpan cpuBefore = process.TotalProcessorTime;
        var build = Stopwatch.StartNew();
        engine.Import(corpus.Entries);
        build.Stop();
        process.Refresh();
        TimeSpan cpu = process.TotalProcessorTime - cpuBefore;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Console.WriteLine(
            $"corpus          {Number(corpus.Entries.Count)} documents, {Number(corpus.Postings)} postings " +
            $"({Number(corpus.Postings / Math.Max(corpus.Entries.Count, 1))} per document), " +
            $"generated in {generation.Elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)} s");
        Console.WriteLine($"index build     {Time(build.Elapsed.TotalMilliseconds * 1_000)}  (Import, one pass)");
        Console.WriteLine(
            $"import alloc    {Number(allocated)} B total, {Number(allocated / Math.Max(corpus.Entries.Count, 1))} B/document");
        Console.WriteLine($"import cpu      {Time(cpu.TotalMilliseconds * 1_000)}  (process CPU, all threads)");
        Console.WriteLine($"engine          {Number(engine.Count)} documents, {Number(engine.VocabularySize)} terms");

        if (remove > 0)
            MeasureRemoval(engine, documents, remove);

        if (importOnly)
            return;

        foreach (var query in corpus.Queries)
            Measure(engine, query);
    }

    /// <summary>
    /// Removes evenly spaced documents and reports per-removal cost, the worst single removal and
    /// the memory the engine still holds afterwards.
    /// </summary>
    /// <remarks>
    /// Removal walks each of the document's posting lists to close the gap; this measures what that
    /// costs when the lists are millions of entries long, which is the shape a re-crawl produces.
    /// </remarks>
    private static void MeasureRemoval(SparseTextSearchEngine engine, int documents, int remove)
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        long beforeMemory = GC.GetTotalMemory(forceFullCollection: true);
        long beforeWorkingSet = process.WorkingSet64;
        TimeSpan beforeCpu = process.TotalProcessorTime;

        int step = Math.Max(documents / remove, 1);
        int removed = 0;
        double worst = 0;
        var total = Stopwatch.StartNew();

        for (int i = 0; i < remove; i++)
        {
            string id = ((long)i * step % documents).ToString(CultureInfo.InvariantCulture);

            long started = Stopwatch.GetTimestamp();
            if (engine.Remove(id))
                removed++;

            double microseconds = Stopwatch.GetElapsedTime(started).TotalMicroseconds;

            if (microseconds > worst)
                worst = microseconds;
        }

        total.Stop();
        process.Refresh();
        long afterMemory = GC.GetTotalMemory(forceFullCollection: true);
        long afterWorkingSet = process.WorkingSet64;

        Console.WriteLine(
            $"remove          {Number(removed)} documents in {Time(total.Elapsed.TotalMilliseconds * 1_000)}, " +
            $"{Time(total.Elapsed.TotalMilliseconds * 1_000 / Math.Max(removed, 1))}/document, " +
            $"worst {Time(worst)}");
        Console.WriteLine(
            $"remove cpu      {Time((process.TotalProcessorTime - beforeCpu).TotalMilliseconds * 1_000)}, " +
            $"engine now {Number(engine.Count)} documents");
        Console.WriteLine(
            $"remove memory   GC {Megabytes(afterMemory - beforeMemory)}, " +
            $"working set {Megabytes(beforeWorkingSet)} -> {Megabytes(afterWorkingSet)}");
    }

    private static string Megabytes(long bytes) =>
        (bytes / (1024.0 * 1024.0)).ToString("N1", CultureInfo.InvariantCulture) + " MiB";

    private static void Measure(SparseTextSearchEngine engine, Query query)
    {
        var options = new SearchOptions(Limit: Limit);

        for (int i = 0; i < WarmupSearches; i++)
            _sink += engine.Search(query.Text, options).Count;

        var microseconds = new double[Iterations];
        long allocated = 0;
        int hits = 0;

        for (int i = 0; i < Iterations; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            long started = Stopwatch.GetTimestamp();
            var results = engine.Search(query.Text, options);
            microseconds[i] = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            hits = results.Count;
            _sink += results.Count;
        }

        Array.Sort(microseconds);

        Console.WriteLine(
            $"  {query.Text,-4} {Number(query.Terms)} terms  p50 {Time(microseconds[Iterations / 2])}, " +
            $"p95 {Time(microseconds[(int)(Iterations * 0.95)])}, " +
            $"alloc/search {Number(allocated / Iterations)} B, hits {Number(hits)}");
    }

    private static int ParsePositive(string value, string name)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) || parsed <= 0)
            throw new ArgumentException($"sparse-engine expects a positive {name}, got '{value}'.");

        return parsed;
    }

    private static string Time(double microseconds) => microseconds >= 1_000
        ? (microseconds / 1_000).ToString("N2", CultureInfo.InvariantCulture) + " ms"
        : microseconds.ToString("N2", CultureInfo.InvariantCulture) + " µs";

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private sealed record Query(string Text, IReadOnlyDictionary<string, float> Weights, int Terms);

    private sealed class FixedProvider(IReadOnlyList<Query> queries) : ISparseEmbeddingProvider
    {
        private static readonly IReadOnlyDictionary<string, float> Empty =
            new Dictionary<string, float>();

        public Task<IReadOnlyDictionary<string, float>> GetSparseEmbeddingAsync(
            string text,
            EmbeddingUse use,
            CancellationToken cancellationToken = default)
        {
            if (use == EmbeddingUse.Query)
            {
                foreach (var query in queries)
                {
                    if (string.Equals(query.Text, text, StringComparison.Ordinal))
                        return Task.FromResult(query.Weights);
                }
            }

            return Task.FromResult(Empty);
        }
    }

    /// <summary>The synthetic SPLADE-shaped corpus, loaded through Import so no provider is timed.</summary>
    private sealed class Corpus
    {
        private Corpus(
            IReadOnlyList<SparseIndexEntry> entries,
            IReadOnlyList<Query> queries,
            long postings)
        {
            Entries = entries;
            Queries = queries;
            Postings = postings;
        }

        public IReadOnlyList<SparseIndexEntry> Entries { get; }

        public IReadOnlyList<Query> Queries { get; }

        public long Postings { get; }

        public static Corpus Generate(
            int documents,
            int termsPerDocument,
            int vocabulary,
            int seed)
        {
            var random = new Random(seed);
            var entries = new List<SparseIndexEntry>(documents);
            long postings = 0;

            for (int document = 0; document < documents; document++)
            {
                var weights = new Dictionary<string, float>(Math.Min(termsPerDocument, vocabulary), StringComparer.Ordinal);

                while (weights.Count < termsPerDocument)
                {
                    int term = Draw(random, vocabulary);

                    weights[$"t{term}"] = 0.1f + (float)(random.NextDouble() * 3.0);
                }

                postings += weights.Count;

                entries.Add(new SparseIndexEntry(
                    new SearchDocument(
                        document.ToString(CultureInfo.InvariantCulture),
                        document.ToString(CultureInfo.InvariantCulture)),
                    weights));
            }

            var querySet = new List<Query>
            {
                QueryOf("absent40", Terms("absent", 40)),
                QueryOf("rare40", Terms("t", 40, start: Math.Max(vocabulary - 100, 0))),
                QueryOf("head1", Terms("t", 1)),
                QueryOf("head10", Terms("t", 10)),
                QueryOf("head40", Terms("t", 40)),
                QueryOf("head100", Terms("t", 100)),
            };

            return new Corpus(entries, querySet, postings);
        }

        private static Query QueryOf(string text, IReadOnlyList<string> terms)
        {
            var weights = new Dictionary<string, float>(terms.Count, StringComparer.Ordinal);

            foreach (string term in terms)
                weights[term] = 1f;

            return new Query(text, weights, weights.Count);
        }

        private static string[] Terms(string prefix, int count, int start = 0)
        {
            var terms = new string[count];

            for (int i = 0; i < count; i++)
                terms[i] = prefix + (start + i).ToString(CultureInfo.InvariantCulture);

            return terms;
        }

        /// <summary>A Zipf-shaped draw: lower ids are more frequent, as in the sparse-wand corpus.</summary>
        private static int Draw(Random random, int vocabulary)
        {
            double u = random.NextDouble();
            int term = (int)(vocabulary * Math.Pow(u, 1.07));

            return Math.Min(term, vocabulary - 1);
        }
    }
}
