using LexiSharp.Core;
using LexiSharp.Hybrid;
using Xunit;

namespace LexiSharp.Tests;

public class MaximalMarginalRelevanceRerankerTests
{
    private static readonly SearchDocument Doc1 = new("1", "apple pie recipe");
    private static readonly SearchDocument Doc2 = new("2", "apple tart recipe");
    private static readonly SearchDocument Doc3 = new("3", "kubernetes cluster");

    private static SearchResult R(string id, SearchDocument doc, double score) => new(id, score, doc);

    private static IReadOnlyDictionary<string, ReadOnlyMemory<float>> DefaultVectors => new Dictionary<string, ReadOnlyMemory<float>>
    {
        // "1" and "2" point in (almost) the same direction; "3" is orthogonal.
        ["1"] = new float[] { 1f, 0f },
        ["2"] = new float[] { 0.99f, 0.1f },
        ["3"] = new float[] { 0f, 1f },
    };

    [Fact]
    public void Rerank_TradesRedundancyForDiversity()
    {
        // λ = 0.7: "2" is nearly a duplicate of "1" (cos ≈ 0.995) — diversity pushes it behind
        // the orthogonal "3" even though it is the more relevant of the two.
        var reranker = new MaximalMarginalRelevanceReranker(DefaultVectors, lambda: 0.7);

        var results = reranker.Rerank("q", new[]
        {
            R("1", Doc1, 1.0), R("2", Doc2, 0.9), R("3", Doc3, 0.8),
        });

        Assert.Equal(new[] { "1", "3", "2" }, results.Select(r => r.DocumentId).ToArray());
    }

    [Fact]
    public void Rerank_LambdaOne_IsPureRelevance()
    {
        var reranker = new MaximalMarginalRelevanceReranker(DefaultVectors, lambda: 1.0);

        var results = reranker.Rerank("q", new[]
        {
            R("1", Doc1, 1.0), R("2", Doc2, 0.9), R("3", Doc3, 0.8),
        });

        Assert.Equal(new[] { "1", "2", "3" }, results.Select(r => r.DocumentId).ToArray());
    }

    [Fact]
    public void Rerank_KeepsOriginalScores()
    {
        var reranker = new MaximalMarginalRelevanceReranker(DefaultVectors, lambda: 0.7);

        var results = reranker.Rerank("q", new[]
        {
            R("1", Doc1, 1.0), R("2", Doc2, 0.9), R("3", Doc3, 0.8),
        });

        // Order-only strategy: the scores travel with the documents untouched.
        Assert.Equal(1.0, results[0].Score);
        Assert.Equal(0.8, results[1].Score);
        Assert.Equal(0.9, results[2].Score);
    }

    [Fact]
    public void Rerank_CandidatesWithoutVector_AreNeverPenalized()
    {
        var vectors = new Dictionary<string, ReadOnlyMemory<float>>
        {
            ["1"] = new float[] { 1f, 0f },
            ["2"] = new float[] { 0.99f, 0.1f },
        };
        var reranker = new MaximalMarginalRelevanceReranker(vectors, lambda: 0.0);

        var results = reranker.Rerank("q", new[]
        {
            R("1", Doc1, 1.0), R("2", Doc2, 1.0), R("3", Doc3, 1.0),
        });

        // λ = 0 is pure diversity. "2" is nearly a duplicate of "1" (cos ≈ 0.995) and is pushed
        // to the end; "3" has no vector at all, so it is never punished for redundancy and
        // outranks "2".
        Assert.Equal(new[] { "1", "3", "2" }, results.Select(r => r.DocumentId).ToArray());
    }

    [Fact]
    public void Rerank_TiesKeepIncomingOrder()
    {
        var reranker = new MaximalMarginalRelevanceReranker(DefaultVectors, lambda: 1.0);

        var results = reranker.Rerank("q", new[]
        {
            R("a", Doc1, 1.0), R("b", Doc2, 1.0), R("c", Doc3, 1.0),
        });

        Assert.Equal(new[] { "a", "b", "c" }, results.Select(r => r.DocumentId).ToArray());
    }

    [Fact]
    public void Rerank_LimitTrimsToTheMostDiversePrefix()
    {
        var reranker = new MaximalMarginalRelevanceReranker(DefaultVectors, lambda: 0.7, limit: 2);

        var results = reranker.Rerank("q", new[]
        {
            R("1", Doc1, 1.0), R("2", Doc2, 0.9), R("3", Doc3, 0.8),
        });

        Assert.Equal(new[] { "1", "3" }, results.Select(r => r.DocumentId).ToArray());
    }

    [Fact]
    public void Rerank_DropsBrokenScores_AndDoesNotMutateInput()
    {
        var candidates = new List<SearchResult>
        {
            R("1", Doc1, double.NaN),
            R("2", Doc2, 0.9),
            R("3", Doc3, double.PositiveInfinity),
        };
        var reranker = new MaximalMarginalRelevanceReranker(DefaultVectors);

        var results = reranker.Rerank("q", candidates);

        Assert.Single(results, r => r.DocumentId == "2");
        Assert.Equal(3, candidates.Count); // input untouched
    }

    [Fact]
    public void Rerank_EmptyCandidates_YieldEmpty()
    {
        var reranker = new MaximalMarginalRelevanceReranker(DefaultVectors);

        Assert.Empty(reranker.Rerank("q", Array.Empty<SearchResult>()));
    }

    [Fact]
    public void Ctor_ValidatesArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new MaximalMarginalRelevanceReranker(null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MaximalMarginalRelevanceReranker(DefaultVectors, lambda: -0.1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MaximalMarginalRelevanceReranker(DefaultVectors, lambda: 1.1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MaximalMarginalRelevanceReranker(DefaultVectors, limit: 0));
    }
}
