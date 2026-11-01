using BenchmarkDotNet.Attributes;
using LexiSharp.Classification;
using LexiSharp.Core;
using LexiSharp.Embeddings;

namespace LexiSharp.Benchmarks;

/// <summary>
/// The two nearest-neighbour paths: BM25 over stored term frequencies, and cosine over stored
/// vectors. Both are exact scans, so the interesting numbers are the per-query cost and the memory
/// the store retains, not a throughput a sharded index would report.
/// </summary>
[MemoryDiagnoser]
public class NearestNeighborClassificationBenchmarks
{
    private const int DocumentCount = 2_000;
    private const int TokensPerDocument = 20;
    private const int Dimension = 256;

    private SearchDocument[] _training = null!;
    private NearestNeighborClassifier _bm25 = null!;
    private NearestNeighborClassifier _bm25K25 = null!;
    private NearestNeighborClassifier _dense = null!;
    private string[] _queries = null!;

    [GlobalSetup]
    public void Setup()
    {
        string[] categories = { "A", "B", "C", "D", "E" };
        var random = new Random(7); // NOSONAR:S2245 (fixed seed: reproducible benchmark corpus)
        _training = new SearchDocument[DocumentCount];
        _queries = new string[16];

        for (int i = 0; i < _training.Length; i++)
        {
            _training[i] = new SearchDocument(
                "doc-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                CorpusFactory.CreateDocuments(1, TokensPerDocument)[0].Text,
                Category: categories[random.Next(categories.Length)]);
        }

        for (int i = 0; i < _queries.Length; i++)
            _queries[i] = CorpusFactory.CreateDocuments(1, 6)[0].Text;

        _bm25 = new NearestNeighborClassifier(options: new NearestNeighborOptions { K = 5 });
        _bm25.Train(_training);

        _bm25K25 = new NearestNeighborClassifier(options: new NearestNeighborOptions { K = 25 });
        _bm25K25.Train(_training);

        _dense = new NearestNeighborClassifier(
            new HashingEmbeddingProvider(Dimension),
            new NearestNeighborOptions { K = 5 });
        _dense.Train(_training);
    }

    [Benchmark]
    public int TrainBm25()
    {
        var classifier = new NearestNeighborClassifier();
        classifier.Train(_training);
        return classifier.Count;
    }

    [Benchmark(Baseline = true)]
    public int PredictBm25K5() => Predict(_bm25);

    [Benchmark]
    public int PredictBm25K25() => Predict(_bm25K25);

    [Benchmark]
    public int TrainDense()
    {
        var classifier = new NearestNeighborClassifier(new HashingEmbeddingProvider(Dimension));
        classifier.Train(_training);
        return classifier.Count;
    }

    [Benchmark]
    public int PredictDenseK5() => Predict(_dense);

    private int Predict(NearestNeighborClassifier classifier)
    {
        int total = 0;

        foreach (string query in _queries)
            total += classifier.Predict(query, limit: 3).Count;

        return total;
    }
}
