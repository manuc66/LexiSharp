using LexiSharp.Core;
using LexiSharp.Hybrid;
using Xunit;

namespace LexiSharp.Tests;

public class AsyncRerankerTests
{
    private sealed class ConstantScorer : ICrossEncoderScorer
    {
        public string Name => "constant";
        public double Score(string query, SearchDocument document) => document.Id == "a" ? 10.0 : 1.0;
    }

    private sealed class AsyncConstantScorer : IAsyncCrossEncoderScorer
    {
        public string Name => "async-constant";
        public Task<double> ScoreAsync(string query, SearchDocument document, CancellationToken cancellationToken = default) =>
            Task.FromResult(document.Id == "a" ? 10.0 : 1.0);
    }

    [Fact]
    public async Task Adapter_WrapsSyncScorer()
    {
        var adapter = new SyncCrossEncoderScorerAdapter(new ConstantScorer());
        var doc = new SearchDocument("a", "text");

        double score = await adapter.ScoreAsync("q", doc);

        Assert.Equal(10.0, score);
    }

    [Fact]
    public async Task AsyncReranker_ReordersCandidates()
    {
        var reranker = new AsyncCrossEncoderReranker(new AsyncConstantScorer());
        var candidates = new[]
        {
            new SearchResult("b", 5.0, new SearchDocument("b", "text b")),
            new SearchResult("a", 1.0, new SearchDocument("a", "text a")),
        };

        var result = await reranker.RerankAsync("q", candidates);

        Assert.Equal("a", result[0].DocumentId);
    }

    [Fact]
    public async Task SyncRerankerAdapter_WrapsSyncReranker()
    {
        var sync = new CrossEncoderReranker(new ConstantScorer());
        var adapter = new SyncRerankerAdapter(sync);
        var candidates = new[]
        {
            new SearchResult("b", 5.0, new SearchDocument("b", "text b")),
            new SearchResult("a", 1.0, new SearchDocument("a", "text a")),
        };

        var result = await adapter.RerankAsync("q", candidates);

        Assert.Equal("a", result[0].DocumentId);
    }

    [Fact]
    public void AsyncReranker_NameIsNotNull()
    {
        var reranker = new AsyncCrossEncoderReranker(new AsyncConstantScorer());
        Assert.False(string.IsNullOrWhiteSpace(reranker.Name));
    }
}
