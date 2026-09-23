using LexiSharp.Benchmarking;
using LexiSharp.Core;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class BenchmarkingTests
{
    private static IReadOnlyList<SearchDocument> Corpus() =>
    [
        new SearchDocument("d1", "distributed systems architecture consensus replication fault tolerance"),
        new SearchDocument("d2", "web application architecture security performance scalability"),
        new SearchDocument("d3", "distributed database replication consistency partitioning"),
        new SearchDocument("d4", "food recipes cooking baking bread"),
        new SearchDocument("d5", "networking protocols tcp ip routing"),
    ];

    private static IReadOnlyList<BenchmarkQuery> Queries() =>
    [
        new BenchmarkQuery("q1", "distributed replication", ["d1", "d3"]),
        new BenchmarkQuery("q2", "web security performance", ["d2"]),
        new BenchmarkQuery("q3", "cooking bread", ["d4"]),
        new BenchmarkQuery("q4", "networking routing", ["d5"]),
    ];

    [Fact]
    public void Run_ReportsOneRowPerConfigWithJudgedQueries()
    {
        var configs = new[]
        {
            BenchmarkConfig.Bm25(),
            BenchmarkConfig.TfIdf(),
        };

        var results = CorpusBenchmark.Run(Corpus(), Queries(), configs);

        Assert.Equal(["BM25", "TF-IDF"], results.Select(result => result.Name));
        foreach (var result in results)
        {
            Assert.Equal(4, result.JudgedQueries);
            Assert.True(result.TotalMilliseconds >= 0);
            Assert.True(result.MillisecondsPerQuery >= 0);
            foreach (double metric in new[] { result.Metrics.NdcgAtK, result.Metrics.MapAtK, result.Metrics.MrrAtK, result.Metrics.RecallAtK, result.Metrics.PrecisionAtK, result.Metrics.F1AtK })
                Assert.InRange(metric, 0, 1);
        }
    }

    [Fact]
    public void Run_MeasuresDiscriminativeQuality()
    {
        var empty = new BenchmarkConfig("empty", (_, _, _, _) => new EmptyEngine());
        var configs = new[]
        {
            BenchmarkConfig.Bm25(),
            empty,
        };

        var results = CorpusBenchmark.Run(Corpus(), Queries(), configs);

        Assert.True(
            results[0].Metrics.RecallAtK > 0 && results[1].Metrics.RecallAtK == 0,
            "a config scoring zero must surface a recall of zero next to a scoring one");
        Assert.Equal(results[0].JudgedQueries, results[1].JudgedQueries);
    }

    [Fact]
    public void Run_RrfConfigCarriesSourceLabels()
    {
        var rrf = BenchmarkConfig.HybridRrf(new Bm25Scorer(), new TfIdfScorer());

        var result = Assert.Single(CorpusBenchmark.Run(Corpus(), Queries(), new[] { rrf }));

        Assert.Equal("RRF: BM25 + TF-IDF", result.Name);
        Assert.Equal(4, result.JudgedQueries);
    }

    [Fact]
    public void Run_Bm25TunedTakesTheLabeledQueriesIntoAccount()
    {
        var result = Assert.Single(CorpusBenchmark.Run(
            Corpus(),
            Queries(),
            new[] { BenchmarkConfig.Bm25Tuned() }));

        Assert.Equal("BM25 (tuned)", result.Name);
        Assert.Equal(4, result.JudgedQueries);
        Assert.InRange(result.Metrics.F1AtK, 0, 1);
    }

    [Fact]
    public void Run_SkipsQueriesWithoutRelevance()
    {
        var queries = Queries()
            .Append(new BenchmarkQuery("q-no-qrels", "fault tolerance", Array.Empty<string>()))
            .ToArray();

        var result = Assert.Single(CorpusBenchmark.Run(Corpus(), queries, new[] { BenchmarkConfig.Bm25() }));

        Assert.Equal(4, result.JudgedQueries);
    }

    [Fact]
    public void Run_IsDeterministic()
    {
        var configs = new[]
        {
            BenchmarkConfig.Bm25(),
            BenchmarkConfig.HybridRrf(new Bm25Scorer(), new TfIdfScorer()),
        };

        var first = CorpusBenchmark.Run(Corpus(), Queries(), configs);
        var second = CorpusBenchmark.Run(Corpus(), Queries(), configs);

        for (int i = 0; i < configs.Length; i++)
        {
            Assert.Equal(first[i].Metrics.NdcgAtK, second[i].Metrics.NdcgAtK, 12);
            Assert.Equal(first[i].Metrics.MrrAtK, second[i].Metrics.MrrAtK, 12);
        }
    }

    [Fact]
    public void Run_ValidatesInputs()
    {
        var corpus = Corpus();
        var queries = Queries();

        Assert.Throws<ArgumentException>(() => CorpusBenchmark.Run(corpus, Array.Empty<BenchmarkQuery>(), new[] { BenchmarkConfig.Bm25() }));
        Assert.Throws<ArgumentException>(() => CorpusBenchmark.Run(corpus, queries, Array.Empty<BenchmarkConfig>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => CorpusBenchmark.Run(corpus, queries, new[] { BenchmarkConfig.Bm25() }, new BenchmarkOptions { TopK = 0 }));
        Assert.Throws<ArgumentNullException>(() => CorpusBenchmark.Run(null!, queries, new[] { BenchmarkConfig.Bm25() }));
    }

    [Fact]
    public void Run_HonorsCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            CorpusBenchmark.Run(Corpus(), Queries(), new[] { BenchmarkConfig.Bm25() }, cancellationToken: cts.Token));
    }

    [Fact]
    public void HybridRrf_RequiresAtLeastOneScorer()
    {
        Assert.Throws<ArgumentException>(() => BenchmarkConfig.HybridRrf());
        Assert.Throws<ArgumentNullException>(() => BenchmarkConfig.HybridRrf(null!));
    }

    [Fact]
    public void Constructor_RejectsBlankNamesAndNullBuilders()
    {
        Assert.Throws<ArgumentException>(() => new BenchmarkConfig("", (_, _, _, _) => new EmptyEngine()));
        Assert.Throws<ArgumentNullException>(() => new BenchmarkConfig("custom", null!));
    }

    /// <summary>Engine that never matches anything; used to prove the runner reports signal.</summary>
    private sealed class EmptyEngine : ITextSearchEngine
    {
        public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null) =>
            Array.Empty<SearchResult>();

        public void Index(IEnumerable<SearchDocument> documents) => throw new NotSupportedException();

        public void Add(SearchDocument document) => throw new NotSupportedException();

        public void Remove(string documentId) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();
    }
}