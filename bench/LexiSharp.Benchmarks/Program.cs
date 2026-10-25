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

        // One index per corpus size in a plain process, for sizes BenchmarkDotNet cannot hold.
        // See ScaleLane.
        if (args.Length > 0 && args[0] == "scale")
        {
            ScaleLane.Run(args[1..]);
            return;
        }

        // One shared index under 1/2/4/8 callers, raw and synchronized. See ConcurrencyLane.
        if (args.Length > 0 && args[0] == "concurrency")
        {
            ConcurrencyLane.Run(args[1..]);
            return;
        }

        var switcher = new BenchmarkSwitcher(new[]
        {
            typeof(TokenizerBenchmarks),
            typeof(IndexBenchmarks),
            typeof(SearchBenchmarks),
            typeof(HybridTraceBenchmarks),
            typeof(QueryAllocationBreakdownBenchmarks),
            typeof(CandidateSearchBenchmarks),
            typeof(ScoringPathBenchmarks),
            typeof(RerankBenchmarks),
            typeof(TextRankBenchmarks),
            typeof(LevenshteinBenchmarks),
            typeof(ClassificationBenchmarks),
            typeof(StoreWriteBenchmarks),
        });

        switcher.Run(args);
    }
}