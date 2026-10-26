using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using LexiSharp.Core;
using LexiSharp.Hybrid;
using LexiSharp.Indexing;

namespace LexiSharp.Benchmarks;

/// <summary>
/// Memory, recall@10 and scan latency of the int8 representation in
/// <see cref="InMemoryVectorSearchEngine"/>, per corpus size, against the float32 scan on the same
/// corpus and the same queries.
/// </summary>
/// <remarks>
/// <para>
/// A direct run rather than a BenchmarkDotNet class, for the reason <see cref="ScaleLane"/> gives:
/// a 500,000-document float32 engine holds 768 MB in one block, and a job that rebuilt that block
/// per invocation would measure the build. One engine is built per representation per size, in a
/// process that does nothing else, and released before the next one is built.
/// </para>
/// <para>
/// The corpus at 10,000 documents is the cached <c>multilingual-e5-small</c> embeddings the
/// evaluation harness writes (<c>e5-small.docs.bin</c> under
/// <c>bench/LexiSharp.Eval/data/&lt;corpus&gt;</c>), pooled from ArguAna, NFCorpus and SciFact:
/// 17,490 real document vectors and 5,752 real query vectors. A size above the pool — 100,000 and
/// 500,000 here — cannot be real on this host, because the largest cached corpus is ArguAna's
/// 8,674 documents, so those rows are drawn from a 64-cluster Gaussian fitted to the real pool and
/// say so. The queries stay the real ones at every size.
/// </para>
/// <para>
/// Recall@10 is the overlap between the int8 page and the float32 page for the same query, so it
/// measures what quantisation costs the ranking and not whether the ranking is any good. Both
/// engines receive the same vectors through <see cref="InMemoryVectorSearchEngine.Import"/>, which
/// is the store path: no encoder runs in this lane.
/// </para>
/// </remarks>
internal static class QuantizationLane
{
    private const int Seed = 20261026;
    private const int DefaultQueryCount = 200;
    private const int TopK = 10;
    private const int Clusters = 64;
    private const int ClusterIterations = 12;

    /// <summary>
    /// How long each representation is searched before it is timed. The scale lane's measurement is
    /// the reason: tiered compilation with dynamic PGO charges the first calls with instrumented
    /// code, and a short warm-up times the JIT rather than the scan.
    /// </summary>
    private static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(5);

    private static readonly int[] DefaultSizes = [10_000, 100_000, 500_000];

    /// <summary>Keeps a page from being treated as dead code by the JIT.</summary>
    private static long _sink;

    public static void Run(string[] args)
    {
        int[] sizes = DefaultSizes;
        int queryCount = DefaultQueryCount;
        string? dataRoot = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--sizes" when i + 1 < args.Length:
                    sizes = args[++i]
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(value => int.Parse(value, CultureInfo.InvariantCulture))
                        .ToArray();
                    break;

                case "--queries" when i + 1 < args.Length:
                    queryCount = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;

                case "--data" when i + 1 < args.Length:
                    dataRoot = args[++i];
                    break;

                default:
                    throw new ArgumentException(
                        $"Unknown argument '{args[i]}'. Usage: quantize [--sizes 10000,100000,500000] "
                        + "[--queries 200] [--data bench/LexiSharp.Eval/data]");
            }
        }

        dataRoot ??= FindDataRoot();

        Pool pool = LoadPool(dataRoot);
        Mixture? mixture = null;

        Console.WriteLine();
        Console.WriteLine("int8 quantisation of InMemoryVectorSearchEngine");
        Console.WriteLine($"  fit pool: {pool.Documents.Length:N0} document vectors, {pool.Queries.Length:N0} query vectors, {pool.Dimension} dimensions");
        Console.WriteLine($"  source:   {pool.Description}");
        Console.WriteLine($"  queries:  {Math.Min(queryCount, pool.Queries.Length):N0} per row, top {TopK}, real cached query embeddings");
        Console.WriteLine($"  host:     {Environment.ProcessorCount} logical processors, .NET {Environment.Version}, ServerGC={System.Runtime.GCSettings.IsServerGC}");
        Console.WriteLine();

        float[][] queries = pool.Queries.Take(Math.Min(queryCount, pool.Queries.Length)).ToArray();

        Console.WriteLine(
            $"{"docs",10} {"corpus",-9} {"rep",-5} {"B/vector",10} {"payload",8} {"recall@10",10} {"p50 ms",8} {"mean ms",8} {"ns/doc",8}");

        foreach (int size in sizes)
        {
            float[][] documents;
            string corpusKind;

            if (size <= pool.Documents.Length)
            {
                documents = pool.Documents[..size];
                corpusKind = pool.Real ? "real" : "synthetic";
            }
            else
            {
                mixture ??= FitMixture(pool.Documents, pool.Dimension);
                documents = Generate(mixture, size, pool.Dimension);
                corpusKind = "synthetic";
            }

            RunSize(size, corpusKind, documents, queries, pool.Dimension);

            // Let the corpus and the released engine go before the next size builds its own: the
            // point of the lane is that one block is held at a time.
            documents = Array.Empty<float[]>();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Console.WriteLine();
        Console.WriteLine("recall@10 is overlap with the float32 top 10 on the same vectors; a synthetic row is a corpus");
        Console.WriteLine("drawn from a 64-cluster Gaussian fitted to the real pool, not a retrieval result.");
        Console.WriteLine($"sink={_sink}");
    }

    private static void RunSize(int size, string corpusKind, float[][] documents, float[][] queries, int dimension)
    {
        var texts = new SearchDocument[size];

        for (int i = 0; i < size; i++)
            texts[i] = new SearchDocument("d" + i.ToString(CultureInfo.InvariantCulture), string.Empty);

        EngineRun exact = BuildAndRun(documents, texts, queries, dimension, VectorQuantization.None);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        EngineRun quantized = BuildAndRun(documents, texts, queries, dimension, VectorQuantization.Int8);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        int overlap = 0;

        for (int q = 0; q < queries.Length; q++)
        {
            var exactPage = exact.Pages[q].ToHashSet(StringComparer.Ordinal);
            overlap += quantized.Pages[q].Count(id => exactPage.Contains(id));
        }

        double recall = overlap / (double)(queries.Length * TopK);

        PrintRow(size, corpusKind, "f32", exact, dimension, recall: null);
        PrintRow(size, corpusKind, "int8", quantized, dimension, recall);
    }

    private static void PrintRow(int size, string corpusKind, string representation, EngineRun run, int dimension, double? recall)
    {
        int payload = representation == "f32"
            ? (4 * dimension) + 4
            : dimension + 4 + 4;

        double[] sorted = run.Milliseconds.Order().ToArray();
        double p50 = sorted[sorted.Length / 2];
        double mean = run.Milliseconds.Average();

        Console.WriteLine(
            $"{size,10:N0} {corpusKind,-9} {representation,-5} {run.RetainedBytes / (double)size,10:N0} {payload,8:N0} "
            + $"{(recall is null ? "-" : recall.Value.ToString("0.0000", CultureInfo.InvariantCulture)),10} "
            + $"{p50,8:0.00} {mean,8:0.00} {run.Milliseconds.Sum() * 1_000_000 / ((double)size * run.Milliseconds.Length),8:0.0}");
    }

    /// <summary>
    /// Builds one engine over <paramref name="documents"/>, records what it retains, warms it, then
    /// times one search per query and keeps each page.
    /// </summary>
    private static EngineRun BuildAndRun(
        float[][] documents,
        SearchDocument[] texts,
        float[][] queries,
        int dimension,
        VectorQuantization quantization)
    {
        var provider = new QueryProvider(queries, dimension);
        var options = new InMemoryVectorSearchOptions { Quantization = quantization };

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long before = GC.GetTotalMemory(forceFullCollection: true);

        var engine = new InMemoryVectorSearchEngine(provider, options);

        engine.Import(Enumerable.Range(0, documents.Length)
            .Select(i => new VectorIndexEntry(texts[i], documents[i])));

        long after = GC.GetTotalMemory(forceFullCollection: true);
        long retained = after - before;

        var searchOptions = new SearchOptions(Limit: TopK);

        // Warm up on the same calls that are timed, cycling the query set until the fixed wall time
        // has passed. Without this the row reads the tier-1 instrumented scan.
        var warmUp = Stopwatch.StartNew();
        int call = 0;

        while (warmUp.Elapsed < WarmUp)
        {
            var page = engine.Search(QueryText(call % queries.Length), searchOptions);
            _sink += page.Count;
            call++;
        }

        var pages = new string[queries.Length][];
        var milliseconds = new double[queries.Length];

        for (int q = 0; q < queries.Length; q++)
        {
            string text = QueryText(q);
            var timer = Stopwatch.StartNew();
            var page = engine.Search(text, searchOptions);
            timer.Stop();

            milliseconds[q] = timer.Elapsed.TotalMilliseconds;
            pages[q] = page.Select(result => result.DocumentId).ToArray();
            _sink += page.Count;
        }

        return new EngineRun(pages, milliseconds, retained);
    }

    private static string QueryText(int index) => "q" + index.ToString(CultureInfo.InvariantCulture);

    private sealed record EngineRun(string[][] Pages, double[] Milliseconds, long RetainedBytes);

    /// <summary>A vector pool loaded from the evaluation harness's cache, or a synthetic one.</summary>
    private sealed record Pool(float[][] Documents, float[][] Queries, int Dimension, string Description, bool Real);

    private static string FindDataRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        for (int depth = 0; depth < 8 && directory is not null; depth++, directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "bench", "LexiSharp.Eval", "data");

            if (Directory.Exists(candidate))
                return candidate;
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "bench", "LexiSharp.Eval", "data");
    }

    private static Pool LoadPool(string dataRoot)
    {
        string[] corpora = ["arguana", "nfcorpus", "scifact"];
        var documents = new List<float[]>();
        var queries = new List<float[]>();
        int dimension = 0;
        var used = new List<string>();

        foreach (string corpus in corpora)
        {
            string directory = Path.Combine(dataRoot, corpus);
            string meta = Path.Combine(directory, "e5-small.json");
            string docsFile = Path.Combine(directory, "e5-small.docs.bin");
            string queriesFile = Path.Combine(directory, "e5-small.queries.bin");

            if (!File.Exists(meta) || !File.Exists(docsFile) || !File.Exists(queriesFile))
                continue;

            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(meta));

            int corpusDimension = json.RootElement.GetProperty("dimension").GetInt32();
            int docCount = json.RootElement.GetProperty("docCount").GetInt32();
            int queryCount = json.RootElement.GetProperty("queryCount").GetInt32();

            if (dimension == 0)
                dimension = corpusDimension;
            else if (corpusDimension != dimension)
                throw new InvalidOperationException(
                    $"Cached corpora disagree on dimension ({dimension} and {corpusDimension}).");

            documents.AddRange(ReadVectors(docsFile, docCount, corpusDimension));
            queries.AddRange(ReadVectors(queriesFile, queryCount, corpusDimension));
            used.Add($"{corpus} ({docCount:N0}+{queryCount:N0})");
        }

        if (documents.Count == 0)
        {
            // No model, no cache: a deterministic pool with the same shape so the lane still runs.
            // Every row it prints is then synthetic, and the caller is told so.
            const int dimensionValue = 384;
            var random = new Random(Seed);
            var synthetic = new float[2_000][];

            for (int i = 0; i < synthetic.Length; i++)
                synthetic[i] = UnitGaussian(random, dimensionValue);

            return new Pool(
                synthetic,
                synthetic[..256],
                dimensionValue,
                "no cached e5 vectors found — deterministic Gaussian pool, all rows synthetic",
                Real: false);
        }

        return new Pool(documents.ToArray(), queries.ToArray(), dimension, string.Join(", ", used), Real: true);
    }

    private static float[][] ReadVectors(string path, int count, int dimension)
    {
        byte[] bytes = File.ReadAllBytes(path);

        if (bytes.Length < (long)count * dimension * sizeof(float))
            throw new InvalidDataException($"{path} is shorter than {count} × {dimension} floats.");

        ReadOnlySpan<float> values = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes);
        var vectors = new float[count][];

        for (int i = 0; i < count; i++)
            vectors[i] = values.Slice(i * dimension, dimension).ToArray();

        return vectors;
    }

    /// <summary>
    /// A Gaussian mixture fitted to the real pool: k-means centers, each with the RMS residual of
    /// its own cluster. Drawing documents from it keeps the concentration of the real embedding
    /// distribution — a corpus of isotropic random directions would make quantisation look worse
    /// than it is on a real model — while giving every generated vector its own components.
    /// </summary>
    private sealed record Mixture(float[][] Centers, int[] Weights, float[] Sigma);

    private static Mixture FitMixture(float[][] pool, int dimension)
    {
        var random = new Random(Seed ^ Clusters);
        var centers = new float[Clusters][];
        var chosen = new HashSet<int>();

        for (int c = 0; c < Clusters; c++)
        {
            int index;

            do
            {
                index = random.Next(pool.Length);
            }
            while (!chosen.Add(index));

            centers[c] = (float[])pool[index].Clone();
        }

        var sums = new float[Clusters][];

        for (int c = 0; c < Clusters; c++)
            sums[c] = new float[dimension];

        for (int iteration = 0; iteration < ClusterIterations; iteration++)
        {
            var counts = new int[Clusters];

            foreach (float[] sum in sums)
                Array.Clear(sum);

            for (int d = 0; d < pool.Length; d++)
            {
                int best = NearestCenter(pool[d], centers);
                counts[best]++;

                float[] sum = sums[best];
                float[] vector = pool[d];

                for (int i = 0; i < dimension; i++)
                    sum[i] += vector[i];
            }

            for (int c = 0; c < Clusters; c++)
            {
                if (counts[c] == 0)
                    continue;

                float inverse = 1f / counts[c];

                for (int i = 0; i < dimension; i++)
                    centers[c][i] = sums[c][i] * inverse;
            }
        }

        // Final assignment, then the per-cluster RMS residual the generated documents are spread
        // by. The center used for generation is the normalized mean direction, because the stored
        // embeddings are unit length.
        var residualSquared = new double[Clusters];
        var finalCounts = new int[Clusters];

        for (int d = 0; d < pool.Length; d++)
        {
            int best = NearestCenter(pool[d], centers);
            double sum = 0;

            for (int i = 0; i < dimension; i++)
            {
                double delta = pool[d][i] - centers[best][i];
                sum += delta * delta;
            }

            residualSquared[best] += sum;
            finalCounts[best]++;
        }

        var sigma = new float[Clusters];

        for (int c = 0; c < Clusters; c++)
        {
            if (finalCounts[c] > 0)
                sigma[c] = (float)Math.Sqrt(residualSquared[c] / finalCounts[c] / dimension);

            Normalize(centers[c]);
        }

        // A cluster with no members would generate a dense line rather than a neighbourhood; give
        // it the smallest observed spread instead.
        float smallest = sigma.Where(value => value > 0).DefaultIfEmpty(0.01f).Min();

        for (int c = 0; c < Clusters; c++)
        {
            if (finalCounts[c] == 0)
            {
                finalCounts[c] = 1;
                sigma[c] = smallest;
            }
        }

        return new Mixture(centers, finalCounts, sigma);
    }

    private static int NearestCenter(float[] vector, float[][] centers)
    {
        int best = 0;
        float bestScore = float.NegativeInfinity;

        for (int c = 0; c < centers.Length; c++)
        {
            float score = VectorSimilarity.DotProduct(vector, centers[c]);

            if (score > bestScore)
            {
                bestScore = score;
                best = c;
            }
        }

        return best;
    }

    private static float[][] Generate(Mixture mixture, int count, int dimension)
    {
        var random = new Random(Seed ^ count);
        var cumulative = new double[mixture.Weights.Length];
        double total = 0;

        for (int c = 0; c < mixture.Weights.Length; c++)
        {
            total += mixture.Weights[c];
            cumulative[c] = total;
        }

        var documents = new float[count][];

        for (int d = 0; d < count; d++)
        {
            double target = random.NextDouble() * total;
            int cluster = Array.BinarySearch(cumulative, target);

            if (cluster < 0)
                cluster = ~cluster;

            cluster = Math.Min(cluster, cumulative.Length - 1);

            float sigma = mixture.Sigma[cluster];
            float[] center = mixture.Centers[cluster];
            var vector = new float[dimension];

            for (int i = 0; i < dimension; i++)
                vector[i] = center[i] + (sigma * NextGaussian(random));

            Normalize(vector);
            documents[d] = vector;
        }

        return documents;
    }

    private static float[] UnitGaussian(Random random, int dimension)
    {
        var vector = new float[dimension];

        for (int i = 0; i < dimension; i++)
            vector[i] = NextGaussian(random);

        Normalize(vector);
        return vector;
    }

    private static float NextGaussian(Random random)
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = random.NextDouble();

        return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
    }

    private static void Normalize(float[] vector)
    {
        float sum = 0;

        for (int i = 0; i < vector.Length; i++)
            sum += vector[i] * vector[i];

        if (sum == 0)
            return;

        float scale = 1 / MathF.Sqrt(sum);

        for (int i = 0; i < vector.Length; i++)
            vector[i] *= scale;
    }

    /// <summary>
    /// Serves the cached query vectors by the lane's own <c>q&lt;index&gt;</c> keys. Documents are
    /// loaded through <see cref="InMemoryVectorSearchEngine.Import"/>, which never asks for one.
    /// </summary>
    private sealed class QueryProvider : IEmbeddingProvider
    {
        private readonly float[][] _queries;

        public QueryProvider(float[][] queries, int dimension)
        {
            _queries = queries;
            Dimension = dimension;
        }

        public int Dimension { get; }

        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
            string text,
            EmbeddingUse use,
            CancellationToken cancellationToken = default)
        {
            if (use != EmbeddingUse.Query)
                throw new InvalidOperationException("The lane indexes documents through Import; no passage is embedded.");

            return Task.FromResult<ReadOnlyMemory<float>>(
                _queries[int.Parse(text.AsSpan(1), CultureInfo.InvariantCulture)]);
        }
    }
}
