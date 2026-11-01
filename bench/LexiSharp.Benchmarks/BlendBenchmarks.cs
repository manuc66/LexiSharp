using BenchmarkDotNet.Attributes;
using LexiSharp.Classification;
using LexiSharp.Core;

namespace LexiSharp.Benchmarks;

/// <summary>
/// The blend's bookkeeping: two lanes against three, with and without a candidate source, on the
/// same small label set. The arithmetic is O(categories × lanes), so the table is a sanity check
/// on allocation, not a ranking experiment.
/// </summary>
[MemoryDiagnoser]
public class BlendBenchmarks
{
    private BlendTextClassifier _two = null!;
    private BlendTextClassifier _three = null!;
    private BlendTextClassifier _withCandidate = null!;
    private string _text = "a text";

    [GlobalSetup]
    public void Setup()
    {
        string[] categories = { "A", "B", "C", "D", "E", "F", "G", "H" };
        ClassificationResult[] Distribution(int seed)
        {
            var random = new Random(seed); // NOSONAR:S2245 (fixed seed: reproducible benchmark corpus)
            return categories
                .Select(category => new ClassificationResult(category, random.NextDouble()))
                .ToArray();
        }

        var first = new FixedClassifier(Distribution(1));
        var second = new FixedClassifier(Distribution(2));
        var third = new FixedClassifier(Distribution(3));

        _two = new BlendTextClassifier(new[]
        {
            new TextClassifierLane(first, 0.7),
            new TextClassifierLane(second, 0.3),
        });

        _three = new BlendTextClassifier(new[]
        {
            new TextClassifierLane(first, 0.5),
            new TextClassifierLane(second, 0.3),
            new TextClassifierLane(third, 0.2),
        });

        _withCandidate = new BlendTextClassifier(
            new[]
            {
                new TextClassifierLane(first, 0.8),
                new TextClassifierLane(second, 0.2),
            },
            new[]
            {
                new CandidateLane(new FixedCandidateSource(new TextEvidence("PROJ", 1)), 0.5),
            });
    }

    [Benchmark(Baseline = true)]
    public int TwoLanes() => _two.Predict(_text, limit: 3).Count;

    [Benchmark]
    public int ThreeLanes() => _three.Predict(_text, limit: 3).Count;

    [Benchmark]
    public int TwoLanesWithCandidate() => _withCandidate.Predict(_text, limit: 3).Count;

    private sealed class FixedClassifier(ClassificationResult[] results) : ITextClassifier
    {
        public void Train(IEnumerable<SearchDocument> documents) { }

        public IReadOnlyList<ClassificationResult> Predict(
            string text, int limit = 3, IReadOnlySet<string>? excludedCategories = null) =>
            results.Take(limit).ToArray();

        public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null) =>
            results.Length > 0 ? results[0].Category : null;
    }

    private sealed class FixedCandidateSource(TextEvidence evidence) : ICandidateTextSource
    {
        public IReadOnlyList<TextEvidence> Candidates(string text) => new[] { evidence };
    }
}
