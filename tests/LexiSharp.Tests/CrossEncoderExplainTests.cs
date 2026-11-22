using LexiSharp.Core;
using LexiSharp.Hybrid;
using Xunit;

namespace LexiSharp.Tests;

public class CrossEncoderExplainTests
{
    private sealed class StubScorer : ICrossEncoderScorer
    {
        private readonly Func<string, SearchDocument, double> _score;
        public StubScorer(Func<string, SearchDocument, double> score) => _score = score;
        public string Name => "stub";
        public double Score(string query, SearchDocument document) => _score(query, document);
    }

    [Fact]
    public void ExplainRerank_ReturnsScoreAndRankChange()
    {
        var scorer = new StubScorer((q, d) => d.Id == "d2" ? 0.9 : 0.5);
        var reranker = new CrossEncoderReranker(scorer);

        var candidates = new[]
        {
            new SearchResult("d1", 1.0, new SearchDocument("d1", "doc1")),
            new SearchResult("d2", 0.5, new SearchDocument("d2", "doc2")),
        };

        var explanation = reranker.ExplainRerank("q", candidates, "d2");

        Assert.NotNull(explanation);
        Assert.Equal("d2", explanation!.DocumentId);
        Assert.Equal(0.9, explanation.FinalScore, 3);
        Assert.Single(explanation.Signals);
        Assert.Contains("rank 2 → 1", explanation.Summary);
    }

    [Fact]
    public void ExplainRerank_ReturnsNullForAbsentCandidate()
    {
        var scorer = new StubScorer((q, d) => 0.5);
        var reranker = new CrossEncoderReranker(scorer);

        var candidates = new[] { new SearchResult("d1", 1.0, new SearchDocument("d1", "doc1")) };

        var explanation = reranker.ExplainRerank("q", candidates, "missing");
        Assert.Null(explanation);
    }
}