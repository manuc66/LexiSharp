using LexiSharp.Core;
using LexiSharp.Hybrid;
using Xunit;

namespace LexiSharp.Tests;

public class MmrExplainTests
{
    [Fact]
    public void ExplainRerank_ReturnsDecompositionForSelectedDocument()
    {
        var vectors = new Dictionary<string, ReadOnlyMemory<float>>
        {
            ["d1"] = new float[] { 1f, 0f }.AsMemory(),
            ["d2"] = new float[] { 0f, 1f }.AsMemory(),
            ["d3"] = new float[] { 0.5f, 0.5f }.AsMemory(),
        };

        var candidates = new[]
        {
            new SearchResult("d1", 1.0, new SearchDocument("d1", "doc1")),
            new SearchResult("d2", 0.8, new SearchDocument("d2", "doc2")),
            new SearchResult("d3", 0.6, new SearchDocument("d3", "doc3")),
        };

        var reranker = new MaximalMarginalRelevanceReranker(vectors, lambda: 0.7);
        var reranked = reranker.Rerank("query", candidates);

        Assert.NotEmpty(reranked);

        var explanation = reranker.ExplainRerank("query", candidates, reranked[0].DocumentId);
        Assert.NotNull(explanation);
        Assert.Equal(reranked[0].DocumentId, explanation!.DocumentId);
        Assert.Equal(2, explanation.Signals.Count);
        Assert.Contains(explanation.Signals, s => s.Name == "relevance");
        Assert.Contains(explanation.Signals, s => s.Name == "redundancy");
        Assert.False(string.IsNullOrWhiteSpace(explanation.Summary));
    }

    [Fact]
    public void ExplainRerank_ReturnsNullForNonExplainableReranker()
    {
        var reranker = new MaximalMarginalRelevanceReranker(
            new Dictionary<string, ReadOnlyMemory<float>>(), lambda: 0.5);
        var candidates = Array.Empty<SearchResult>();
        var explanation = reranker.ExplainRerank("q", candidates, "d1");
        Assert.Null(explanation);
    }

    [Fact]
    public void ExplainRerank_ReturnsNullForUnselectedDocument()
    {
        var vectors = new Dictionary<string, ReadOnlyMemory<float>>
        {
            ["d1"] = new float[] { 1f, 0f }.AsMemory(),
        };
        var candidates = new[] { new SearchResult("d1", 1.0, new SearchDocument("d1", "doc1")) };
        var reranker = new MaximalMarginalRelevanceReranker(vectors, lambda: 0.7);
        reranker.Rerank("query", candidates);

        var explanation = reranker.ExplainRerank("query", candidates, "nonexistent");
        Assert.Null(explanation);
    }
}