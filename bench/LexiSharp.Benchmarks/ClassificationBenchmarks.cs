using BenchmarkDotNet.Attributes;
using LexiSharp.Classification;
using LexiSharp.Core;

namespace LexiSharp.Benchmarks;

[MemoryDiagnoser]
public class ClassificationBenchmarks
{
    private const int TrainingSetSize = 10_000;
    private const int DivisionSetSize = 2_000;

    private NaiveBayesClassifier _classifier = null!;
    private DecisionTreeClassifier _decisionTree = null!;
    private CorpusDivider _divider = null!;
    private SearchDocument[] _training = Array.Empty<SearchDocument>();
    private SearchDocument[] _divisionCorpus = Array.Empty<SearchDocument>();
    private string[] _predictions = Array.Empty<string>();

    [GlobalSetup]
    public void Setup()
    {
        var categories = new[] { "Sports", "Technology", "Cooking", "Travel", "Finance" };
        var random = new Random(7); // NOSONAR:S2245 (fixed-seed synthetic corpus for reproducible benchmarks)
        _training = new SearchDocument[TrainingSetSize];

        for (int i = 0; i < _training.Length; i++)
        {
            _training[i] = new SearchDocument(
                "doc-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                CorpusFactory.CreateDocuments(1, 30)[0].Text,
                Category: categories[random.Next(categories.Length)]);
        }

        _divisionCorpus = _training[..DivisionSetSize];

        _classifier = new NaiveBayesClassifier();
        _classifier.Train(_training);

        _decisionTree = new DecisionTreeClassifier(
            options: new DecisionTreeOptions { MaxDepth = 6, MinDocumentsPerLeaf = 2, MinDocumentFrequency = 2 });
        _decisionTree.Train(_training);

        _divider = new CorpusDivider(options: new CorpusDividerOptions
        {
            MaxDepth = 3,
            MinDocumentsPerLeaf = 2,
            MinDocumentFrequency = 2,
            MaxCandidateTerms = 64,
            MaxVocabularyTerms = 2_000,
        });

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

    [Benchmark]
    public int TrainDecisionTree()
    {
        var classifier = new DecisionTreeClassifier(
            options: new DecisionTreeOptions { MaxDepth = 6, MinDocumentsPerLeaf = 2, MinDocumentFrequency = 2 });
        classifier.Train(_training);
        return 1;
    }

    [Benchmark]
    public string? PredictDecisionTree()
    {
        string? last = null;
        foreach (var text in _predictions)
            last = _decisionTree.PredictBest(text);
        return last;
    }

    [Benchmark]
    public int DivideCorpus() => _divider.Divide(_divisionCorpus).Clusters.Count;
}
