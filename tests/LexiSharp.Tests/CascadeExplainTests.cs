using LexiSharp.Core;
using LexiSharp.Hybrid;
using Xunit;

namespace LexiSharp.Tests;

public class CascadeExplainTests
{
    private sealed class StubReranker : IReranker, IRerankerExplainer
    {
        private readonly Func<string, IReadOnlyList<SearchResult>, IReadOnlyList<SearchResult>> _rerank;
        private readonly Func<string, IReadOnlyList<SearchResult>, string, RerankExplanation?> _explain;

        public StubReranker(
            Func<string, IReadOnlyList<SearchResult>, IReadOnlyList<SearchResult>> rerank,
            Func<string, IReadOnlyList<SearchResult>, string, RerankExplanation?> explain)
        {
            _rerank = rerank;
            _explain = explain;
        }

        public string Name => "stub";

        public IReadOnlyList<SearchResult> Rerank(string query, IReadOnlyList<SearchResult> candidates) => _rerank(query, candidates);

        public RerankExplanation? ExplainRerank(string query, IReadOnlyList<SearchResult> candidates, string documentId) => _explain(query, candidates, documentId);
    }

    [Fact]
    public void ExplainRerank_PropagatesStageScores()
    {
        var stage1 = new StubReranker(
            (q, c) => c.Select(r => r with { Score = r.Score * 2 }).ToList(),
            (q, c, id) => new RerankExplanation(id, c.First(r => r.DocumentId == id).Score * 2, new[] { new RerankSignal("stage1", 1.0, 0.5, 1.0) }, "stage1"));

        var stage2 = new StubReranker(
            (q, c) => c,
            (q, c, id) => new RerankExplanation(id, c.First(r => r.DocumentId == id).Score, new[] { new RerankSignal("stage2", 1.0, 0.3, 0.3) }, "stage2"));

        var pipeline = new CascadeRerankPipeline(new[] { stage1, stage2 });

        var candidates = new[]
        {
            new SearchResult("d1", 1.0, new SearchDocument("d1", "doc1")),
        };

        var explanation = pipeline.ExplainRerank("q", candidates, "d1");

        Assert.NotNull(explanation);
        Assert.Equal("d1", explanation!.DocumentId);
        Assert.Equal(3, explanation.Signals.Count); // input + stage1 + stage2
        Assert.Contains("Cascade:", explanation.Summary);
    }

    [Fact]
    public void ExplainRerank_ReturnsNullForAbsentCandidate()
    {
        var stage = new StubReranker(
            (q, c) => c,
            (q, c, id) => null);

        var pipeline = new CascadeRerankPipeline(new[] { stage });

        var candidates = new[] { new SearchResult("d1", 1.0, new SearchDocument("d1", "doc1")) };

        var explanation = pipeline.ExplainRerank("q", candidates, "missing");
        Assert.Null(explanation);
    }
}