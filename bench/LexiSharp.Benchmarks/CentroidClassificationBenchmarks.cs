using BenchmarkDotNet.Attributes;
using LexiSharp.Classification;
using LexiSharp.Hybrid;

namespace LexiSharp.Benchmarks;

/// <summary>
/// The arithmetic inside a centroid prediction: one query against every class mean. The two
/// cosine lanes exist to price the cached norms — a centroid's norm is fixed at training time,
/// and the query's is the same for every category, so recomputing either is repeated work.
/// </summary>
[MemoryDiagnoser]
public class CentroidClassificationBenchmarks
{
    private const int Dimension = 384;
    private const int CategoryCount = 20;
    private const int VectorsPerCategory = 100;
    private const int QueryCount = 1_000;

    private CentroidClassifier<(string Category, float[] Vector)> _classifier = null!;
    private (string Category, float[] Vector)[] _training = Array.Empty<(string Category, float[] Vector)>();
    private ReadOnlyMemory<float>[] _queries = Array.Empty<ReadOnlyMemory<float>>();
    private float[] _queryNorms = Array.Empty<float>();
    private Dictionary<string, float> _centroidNorms = null!;

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(7); // NOSONAR:S2245 (fixed seed: the benchmark must replay)
        var training = new (string Category, float[] Vector)[CategoryCount * VectorsPerCategory];

        for (int category = 0; category < CategoryCount; category++)
        {
            // A distinct direction per category, with noise so the means are not identical.
            for (int i = 0; i < VectorsPerCategory; i++)
            {
                var vector = new float[Dimension];

                for (int d = 0; d < Dimension; d++)
                    vector[d] = (float)(random.NextDouble() - 0.5);

                vector[category] += 4f;
                training[(category * VectorsPerCategory) + i] = ("c" + category, vector);
            }
        }

        _classifier = new CentroidClassifier<(string Category, float[] Vector)>(
            item => item.Vector,
            item => item.Category);
        _classifier.Train(training);
        _training = training;

        _queries = new ReadOnlyMemory<float>[QueryCount];
        _queryNorms = new float[QueryCount];

        for (int i = 0; i < _queries.Length; i++)
        {
            var vector = new float[Dimension];

            for (int d = 0; d < Dimension; d++)
                vector[d] = (float)(random.NextDouble() - 0.5);

            vector[random.Next(CategoryCount)] += 4f;
            _queries[i] = vector;
            _queryNorms[i] = VectorSimilarity.Norm(vector);
        }

        _centroidNorms = _classifier.Centroids.ToDictionary(
            pair => pair.Key,
            pair => VectorSimilarity.Norm(pair.Value.Span),
            StringComparer.Ordinal);
    }

    /// <summary>The old shape: every cosine recomputes both norms.</summary>
    [Benchmark(Baseline = true)]
    public double RecomputedNorms()
    {
        double sum = 0;

        foreach (var query in _queries)
        {
            foreach (var centroid in _classifier.Centroids.Values)
                sum += VectorSimilarity.CosineSimilarity(query.Span, centroid.Span);
        }

        return sum;
    }

    /// <summary>The current shape: the centroid norms are cached, the query norm is computed once.</summary>
    [Benchmark]
    public double CachedNorms()
    {
        double sum = 0;

        for (int i = 0; i < _queries.Length; i++)
        {
            ReadOnlySpan<float> query = _queries[i].Span;
            float queryNorm = _queryNorms[i];

            foreach (var (category, centroid) in _classifier.Centroids)
            {
                sum += VectorSimilarity.CosineSimilarity(
                    query,
                    centroid.Span,
                    queryNorm,
                    _centroidNorms[category]);
            }
        }

        return sum;
    }

    /// <summary>The public prediction path, including allocation and ranking.</summary>
    [Benchmark]
    public int Predict()
    {
        int total = 0;

        foreach (var query in _queries)
            total += _classifier.Predict(query, limit: 3).Count;

        return total;
    }

    /// <summary>Training: one finite-check and one accumulation pass per vector.</summary>
    [Benchmark]
    public int Train()
    {
        var classifier = new CentroidClassifier<(string Category, float[] Vector)>(
            item => item.Vector,
            item => item.Category);

        classifier.Train(_training);
        return classifier.CategoryCount;
    }
}
