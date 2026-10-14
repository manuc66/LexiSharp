using BenchmarkDotNet.Running;

namespace LexiSharp.Benchmarks;

public static class Program
{
    public static void Main(string[] args)
    {
        // A host with no harness in it, for a sampler. See ProfileHost.
        if (args.Length > 0 && args[0] == "profile")
        {
            ProfileHost.Run(args.Length > 1 && int.TryParse(args[1], out int n) ? n : 4);
            return;
        }

        var switcher = new BenchmarkSwitcher(new[]
        {
            typeof(TokenizerBenchmarks),
            typeof(IndexBenchmarks),
            typeof(SearchBenchmarks),
            typeof(QueryAllocationBreakdownBenchmarks),
            typeof(CandidateSearchBenchmarks),
            typeof(ScoringPathBenchmarks),
            typeof(LevenshteinBenchmarks),
            typeof(ClassificationBenchmarks),
            typeof(StoreWriteBenchmarks),
        });

        switcher.Run(args);
    }
}