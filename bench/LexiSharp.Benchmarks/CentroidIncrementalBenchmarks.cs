using BenchmarkDotNet.Attributes;
using LexiSharp.Classification;

namespace LexiSharp.Benchmarks;

/// <summary>
/// What one document costs a centroid model: the full training pass against a single Learn,
/// with the model's sums carried in double precision.
/// </summary>
[MemoryDiagnoser]
public class CentroidIncrementalBenchmarks
{
    private const int CategoryCount = 20;
    private const int DocumentsPerCategory = 100;
    private const int Dimension = 384;

    private (string Category, float[] Vector)[] _training = null!;
    private CentroidClassifier<(string Category, float[] Vector)> _classifier = null!;
    private float[] _query = null!;

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(7); // NOSONAR:S2245 (fixed seed: reproducible benchmark corpus)
        _training = new (string, float[])[CategoryCount * DocumentsPerCategory];

        for (int c = 0; c < CategoryCount; c++)
        {
            string category = "C" + c.ToString(System.Globalization.CultureInfo.InvariantCulture);

            for (int d = 0; d < DocumentsPerCategory; d++)
            {
                var vector = new float[Dimension];

                for (int i = 0; i < vector.Length; i++)
                    vector[i] = (float)(random.NextDouble() - 0.5);

                vector[c % Dimension] += 4;
                _training[(c * DocumentsPerCategory) + d] = (category, vector);
            }
        }

        _query = new float[Dimension];

        for (int i = 0; i < _query.Length; i++)
            _query[i] = (float)(random.NextDouble() - 0.5);

        _classifier = new CentroidClassifier<(string Category, float[] Vector)>(
            item => item.Vector,
            item => item.Category);
        _classifier.Train(_training);
    }

    [Benchmark(Baseline = true)]
    public int Train()
    {
        var classifier = new CentroidClassifier<(string Category, float[] Vector)>(
            item => item.Vector,
            item => item.Category);
        classifier.Train(_training);
        return classifier.CategoryCount;
    }

    [Benchmark]
    public int Predict() => _classifier.Predict(_query, limit: 3).Count;

    [Benchmark]
    public int LearnThenUnlearn()
    {
        var item = _training[0];
        _classifier.Learn(item);
        _classifier.Unlearn(item);
        return _classifier.CategoryCount;
    }
}
