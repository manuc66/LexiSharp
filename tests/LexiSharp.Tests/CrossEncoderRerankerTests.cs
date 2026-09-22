using LexiSharp.Core;
using LexiSharp.Hybrid;
using Xunit;

namespace LexiSharp.Tests;

public class CrossEncoderRerankerTests
{
    private static readonly SearchDocument Doc1 = new("1", "apple pie recipe");
    private static readonly SearchDocument Doc2 = new("2", "apple tart recipe");
    private static readonly SearchDocument Doc3 = new("3", "kubernetes cluster");
    private static readonly string[] ExpectedIds321 = new[] { "3", "2", "1" };
    private static readonly string[] ExpectedIds32 = new[] { "3", "2" };
    private static readonly string[] ExpectedIdsAbc = new[] { "a", "b", "c" };

    private static SearchResult R(string id, SearchDocument doc, double score) => new(id, score, doc);

    private sealed class StubScorer : ICrossEncoderScorer
    {
        private readonly Func<SearchDocument, double> _score;

        public StubScorer(Func<SearchDocument, double> score) => _score = score;
        public string Name => "stub";

        public double Score(string query, SearchDocument document) => _score(document);
    }

    [Fact]
    public void Rerank_ReordersByModelScore_AndReplacesScores()
    {
        // The retrieval ranked "1" first, but the cross-encoder clearly prefers "3".
        var reranker = new CrossEncoderReranker(new StubScorer(d => d.Id switch
        {
            "1" => 0.3,
            "2" => 0.6,
            "3" => 0.9,
            _ => 0,
        }));

        var results = reranker.Rerank("q", new[] { R("1", Doc1, 1.0), R("2", Doc2, 0.8), R("3", Doc3, 0.5) });

        Assert.Equal(ExpectedIds321, results.Select(r => r.DocumentId).ToArray());
        Assert.Equal(0.9, results[0].Score);
        Assert.Equal(0.6, results[1].Score);
        Assert.Equal(0.3, results[2].Score);
    }

    [Fact]
    public void Rerank_DropsZeroNaNInfinityScores()
    {
        var reranker = new CrossEncoderReranker(new StubScorer(d => d.Id switch
        {
            "1" => 0.5,
            "2" => double.NaN,
            "3" => double.PositiveInfinity,
            "4" => 0,
            _ => 0,
        }));

        var results = reranker.Rerank("q", new[] { R("1", Doc1, 1), R("2", Doc2, 1), R("3", Doc3, 1), R("4", Doc1, 1) });

        Assert.Single(results, r => r.DocumentId == "1");
    }

    [Fact]
    public void Rerank_MinimumScoreDropsWeakHits()
    {
        var reranker = new CrossEncoderReranker(new StubScorer(d => d switch
        {
            _ when d.Id == "1" => 0.9,
            _ => 0.4,
        }), minimumScore: 0.5);

        var results = reranker.Rerank("q", new[] { R("1", Doc1, 1), R("2", Doc2, 1) });

        Assert.Single(results, r => r.DocumentId == "1");
    }

    [Fact]
    public void Rerank_LimitTrims()
    {
        var reranker = new CrossEncoderReranker(new StubScorer(d => d.Id switch
        {
            "1" => 0.3,
            "2" => 0.6,
            "3" => 0.9,
            _ => 0,
        }), limit: 2);

        var results = reranker.Rerank("q", new[] { R("1", Doc1, 1), R("2", Doc2, 1), R("3", Doc3, 1) });

        Assert.Equal(ExpectedIds32, results.Select(r => r.DocumentId).ToArray());
    }

    [Fact]
    public void Rerank_TiesKeepIncomingOrder()
    {
        var reranker = new CrossEncoderReranker(new StubScorer(_ => 0.7));

        var results = reranker.Rerank("q", new[] { R("a", Doc1, 1), R("b", Doc2, 1), R("c", Doc3, 1) });

        Assert.Equal(ExpectedIdsAbc, results.Select(r => r.DocumentId).ToArray());
    }

    [Fact]
    public void Rerank_DoesNotMutateInput()
    {
        var candidates = new List<SearchResult> { R("1", Doc1, 1.0), R("2", Doc2, 0.8) };
        var reranker = new CrossEncoderReranker(new StubScorer(_ => 0.5));

        reranker.Rerank("q", candidates);

        Assert.Equal(1.0, candidates[0].Score);
        Assert.Equal(0.8, candidates[1].Score);
    }

    [Fact]
    public void Rerank_EmptyCandidates_YieldEmpty()
    {
        var reranker = new CrossEncoderReranker(new StubScorer(_ => 0.5));

        Assert.Empty(reranker.Rerank("q", Array.Empty<SearchResult>()));
    }

    [Fact]
    public void Ctor_ValidatesArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new CrossEncoderReranker(null!));
    }

    [Fact]
    public void Ctor_ValidatesLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CrossEncoderReranker(new StubScorer(_ => 0.5), limit: -1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new CrossEncoderReranker(new StubScorer(_ => 0.5), limit: 0));
    }
}