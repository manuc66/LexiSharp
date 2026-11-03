using System.Globalization;
using BenchmarkDotNet.Attributes;
using LexiSharp.Core;
using LexiSharp.Indexing;

namespace LexiSharp.Benchmarks;

/// <summary>
/// The dense scan: one dot product and one division per document, over a corpus that does not fit
/// in cache at a realistic embedding width.
/// </summary>
/// <remarks>
/// The two rows are the same engine at two widths, and they mean different things. At 384
/// dimensions the corpus is 15 MB, so the scan is bounded by how fast memory arrives; at 8
/// dimensions it is 320 KB and stays in cache, so the row is bounded by what the loop does per
/// document — the null and filter checks, the vector read, the window insertion. A change to the
/// per-document arithmetic shows in the second row if it is going to show anywhere.
/// <para>
/// Vectors are fixed-seed random directions and the provider hands back a fixed query, because the
/// engine caches the query embedding: the row measures the scan, not an encoder. Nothing here
/// measures recall or quality — it is the same scan the dense engine runs, on synthetic vectors.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class DenseSearchBenchmarks
{
    private const int DocumentCount = 10_000;

    /// <summary>Embedding width: a realistic one, and one that makes the loop arithmetic-bound.</summary>
    [Params(384, 8)]
    public int Dimension { get; set; }

    private InMemoryVectorSearchEngine _engine = null!;
    private SearchOptions _options = null!;

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(17);
        var vectors = new Dictionary<string, float[]>(DocumentCount, StringComparer.Ordinal);
        var documents = new List<SearchDocument>(DocumentCount);

        for (int i = 0; i < DocumentCount; i++)
        {
            string id = "doc-" + i.ToString(CultureInfo.InvariantCulture);
            var vector = new float[Dimension];

            for (int d = 0; d < Dimension; d++)
                vector[d] = (float)(random.NextDouble() - 0.5);

            vectors[id] = vector;
            documents.Add(new SearchDocument(id, id));
        }

        var query = new float[Dimension];

        for (int d = 0; d < Dimension; d++)
            query[d] = (float)(random.NextDouble() - 0.5);

        _engine = new InMemoryVectorSearchEngine(new FixedProvider(vectors, query));
        _engine.Index(documents);
        _options = new SearchOptions(Limit: 10);
    }

    /// <summary>One full scan, cut to the page.</summary>
    [Benchmark]
    public int Search() => _engine.Search("query", _options).Count;

    /// <summary>Fixed vectors per document id, and a fixed query: the encoder is not under test.</summary>
    private sealed class FixedProvider(Dictionary<string, float[]> vectors, float[] query) : IEmbeddingProvider
    {
        public int Dimension => query.Length;

        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
            string text,
            EmbeddingUse use,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ReadOnlyMemory<float>>(use == EmbeddingUse.Query ? query : vectors[text]);
    }
}
