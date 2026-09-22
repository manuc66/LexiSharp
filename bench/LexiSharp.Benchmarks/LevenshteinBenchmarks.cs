using BenchmarkDotNet.Attributes;
using LexiSharp.Similarity;

namespace LexiSharp.Benchmarks;

[MemoryDiagnoser]
public class LevenshteinBenchmarks
{
    private static readonly string[] Vocabulary =
    {
        "search", "engine", "index", "query", "score", "rank", "document", "token",
        "term", "corpus", "collection", "language", "alpha", "bravo", "charlie",
        "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet", "kilo",
        "lima", "mike", "november", "oscar", "papa", "quebec", "romeo", "sierra",
        "tango", "uniform", "victor", "whiskey", "xray", "yankee", "zulu",
    };

    private const string BaseTerm = "searh";

    [Benchmark]
    public int DistanceAgainstVocabulary()
    {
        int sum = 0;
        foreach (var term in Vocabulary)
            sum += LevenshteinDistance.Distance(BaseTerm, term);
        return sum;
    }

    [Benchmark]
    public int DistanceSimilarPairs()
    {
        int sum = 0;
        for (int i = 0; i < Vocabulary.Length; i++)
            sum += LevenshteinDistance.Distance(Vocabulary[i], Vocabulary[(i + 1) % Vocabulary.Length]);
        return sum;
    }
}
