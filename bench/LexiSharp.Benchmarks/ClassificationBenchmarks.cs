using BenchmarkDotNet.Attributes;
using LexiSharp.Classification;
using LexiSharp.Core;

namespace LexiSharp.Benchmarks;

[MemoryDiagnoser]
public class ClassificationBenchmarks
{
    private const int TrainingSetSize = 10_000;

    private NaiveBayesClassifier _classifier = null!;
    private string[] _predictions = Array.Empty<string>();

    [GlobalSetup]
    public void Setup()
    {
        var categories = new[] { "Sports", "Technology", "Cooking", "Travel", "Finance" };
        var random = new Random(7);
        var training = new SearchDocument[TrainingSetSize];

        for (int i = 0; i < training.Length; i++)
        {
            training[i] = new SearchDocument(
                "doc-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                CorpusFactory.CreateDocuments(1, 30)[0].Text,
                Category: categories[random.Next(categories.Length)]);
        }

        _classifier = new NaiveBayesClassifier();
        _classifier.Train(training);

        _predictions = new[]
        {
            "the club won the match at the stadium last night",
            "a new processor for laptop computers",
            "the carbonara pasta recipe is simple",
            "visiting rome and tuscany in one week",
            "stock market shares rose this quarter",
        };
    }

    [Benchmark]
    public int TrainClassifier()
    {
        var classifier = new NaiveBayesClassifier();
        classifier.Train(Enumerable.Range(0, TrainingSetSize / 5).Select(i =>
            new SearchDocument(i.ToString(), "alpha beta gamma delta", Category: "A")));
        return 1;
    }

    [Benchmark]
    public string? PredictText()
    {
        string? last = null;
        foreach (var text in _predictions)
            last = _classifier.PredictBest(text);
        return last;
    }
}