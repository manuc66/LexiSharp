using BenchmarkDotNet.Running;

namespace LexiSharp.Benchmarks;

public static class Program
{
    public static void Main(string[] args)
    {
        var switcher = new BenchmarkSwitcher(new[]
        {
            typeof(TokenizerBenchmarks),
            typeof(IndexBenchmarks),
            typeof(SearchBenchmarks),
            typeof(CandidateSearchBenchmarks),
            typeof(ScoringPathBenchmarks),
            typeof(LevenshteinBenchmarks),
            typeof(ClassificationBenchmarks),
        });

        switcher.Run(args);
    }
}