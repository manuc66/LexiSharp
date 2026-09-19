using LexiSharp.Core;
using LexiSharp.Hybrid;
using Xunit;

namespace LexiSharp.Tests;

public class MaxSimRerankerTests
{
    private static readonly SearchDocument Doc1 = new("1", "apple pie recipe");
    private static readonly SearchDocument Doc2 = new("2", "apple tart recipe");
    private static readonly SearchDocument Doc3 = new("3", "kubernetes cluster");

    private static SearchResult R(string id, SearchDocument doc, double score) => new(id, score, doc);

    private static ReadOnlyMemory<float> V(params float[] values) => values;

    private static IReadOnlyList<ReadOnlyMemory<float>> Tokens(params ReadOnlyMemory<float>[] vectors) => vectors;

    private sealed class StubTokenizer : ITokenEmbeddingProvider
    {
        private readonly Func<string, IReadOnlyList<ReadOnlyMemory<float>>> _lookup;

        public StubTokenizer(Func<string, IReadOnlyList<ReadOnlyMemory<float>>> lookup)
            => _lookup = lookup;

        public string Name => "stub";

        public IReadOnlyList<ReadOnlyMemory<float>> GetTokenEmbeddings(string text) => _lookup(text);
    }

    [Fact]
    public void Rerank_OrdersByColbertMaxSim()
    {
        // Query tokens: "red" → (1,0), "apple" → (0,1).
        // Doc "1": tokens (1,0) and (0.95,0.3) — "red" matches well, "apple" weakly.
        // Doc "2": tokens (0.8,0.6) and (0,1) — "red" OK, "apple" perfectly.
        var model = new StubTokenizer(q => q == "q"
            ? Tokens(V(1f, 0f), V(0f, 1f))
            : ThrowUnknown(q));

        // MaxSim(1d) ≈ 1 + 0.3 = 1.3 ; MaxSim(2d) ≈ 0.8 + 1 = 1.8
        var vectors = new Dictionary<string, IReadOnlyList<ReadOnlyMemory<float>>>
        {
            ["1"] = Tokens(V(1f, 0f), V(0.95f, 0.3f)),
            ["2"] = Tokens(V(0.8f, 0.6f), V(0f, 1f)),
        };

        var reranker = new MaxSimReranker(model, vectors);

        var results = reranker.Rerank("q", new[] { R("1", Doc1, 1.0), R("2", Doc2, 0.8) });

        Assert.Equal(new[] { "2", "1" }, results.Select(r => r.DocumentId).ToArray());
        Assert.Equal(1.8, results[0].Score, 3);
        Assert.Equal(1.3, results[1].Score, 2);
    }

    [Fact]
    public void Rerank_DropsCandidatesWithoutDocumentVectors()
    {
        var model = new StubTokenizer(_ => Tokens(V(1f, 0f)));
        var vectors = new Dictionary<string, IReadOnlyList<ReadOnlyMemory<float>>>
        {
            ["1"] = Tokens(V(1f, 0f)),
            // "3" has no entry
        };

        var reranker = new MaxSimReranker(model, vectors);

        var results = reranker.Rerank("q", new[] { R("1", Doc1, 1), R("3", Doc3, 1) });

        Assert.Single(results, r => r.DocumentId == "1");
    }

    [Fact]
    public void Rerank_EmptyQueryTokens_KeepIncomingOrder()
    {
        var model = new StubTokenizer(_ => Tokens());
        var vectors = new Dictionary<string, IReadOnlyList<ReadOnlyMemory<float>>> { ["1"] = Tokens(V(1f)) };

        var reranker = new MaxSimReranker(model, vectors);

        var results = reranker.Rerank("q", new[] { R("1", Doc1, 1.0), R("2", Doc2, 0.8) });

        Assert.Equal(new[] { "1", "2" }, results.Select(r => r.DocumentId).ToArray());
        Assert.Equal(1.0, results[0].Score); // untouched, since nothing was re-scored
        Assert.Equal(0.8, results[1].Score);
    }

    [Fact]
    public void Rerank_OrthogonalTokens_ScoredLowOrDropped()
    {
        // One query token (1,0); doc tokens all orthogonal (0,1) → cosine 0 → total 0 → dropped.
        var model = new StubTokenizer(_ => Tokens(V(1f, 0f)));
        var vectors = new Dictionary<string, IReadOnlyList<ReadOnlyMemory<float>>>
        {
            ["1"] = Tokens(V(0f, 1f)),
        };

        var reranker = new MaxSimReranker(model, vectors);

        Assert.Empty(reranker.Rerank("q", new[] { R("1", Doc1, 1.0) }));
    }

    [Fact]
    public void Rerank_LimitAndMinimumScore()
    {
        var model = new StubTokenizer(_ => Tokens(V(1f, 0f)));
        var vectors = new Dictionary<string, IReadOnlyList<ReadOnlyMemory<float>>>
        {
            ["1"] = Tokens(V(0.2f, 0.98f)),
            ["2"] = Tokens(V(0.9f, 0.44f)),
            ["3"] = Tokens(V(0.6f, 0.8f)),
        };

        var scored = new MaxSimReranker(model, vectors);
        var results = scored.Rerank("q", new[] { R("1", Doc1, 1), R("2", Doc2, 1), R("3", Doc3, 1) });
        Assert.Equal(new[] { "2", "3", "1" }, results.Select(r => r.DocumentId).ToArray());

        var limited = new MaxSimReranker(model, vectors, limit: 2);
        Assert.Equal(new[] { "2", "3" }, limited.Rerank("q", new[] { R("1", Doc1, 1), R("2", Doc2, 1), R("3", Doc3, 1) }).Select(r => r.DocumentId).ToArray());

        var minimum = new MaxSimReranker(model, vectors, minimumScore: 0.55);
        Assert.Equal(new[] { "2", "3" }, minimum.Rerank("q", new[] { R("1", Doc1, 1), R("2", Doc2, 1), R("3", Doc3, 1) }).Select(r => r.DocumentId).ToArray());
    }

    [Fact]
    public void Rerank_TiesKeepIncomingOrder()
    {
        var model = new StubTokenizer(_ => Tokens(V(1f, 0f)));
        var vectors = new Dictionary<string, IReadOnlyList<ReadOnlyMemory<float>>>
        {
            ["a"] = Tokens(V(1f, 0f)),
            ["b"] = Tokens(V(1f, 0f)),
            ["c"] = Tokens(V(1f, 0f)),
        };

        var reranker = new MaxSimReranker(model, vectors);

        var results = reranker.Rerank("q", new[] { R("a", Doc1, 1), R("b", Doc2, 1), R("c", Doc3, 1) });

        Assert.Equal(new[] { "a", "b", "c" }, results.Select(r => r.DocumentId).ToArray());
        Assert.All(results, r => Assert.Equal(1.0, r.Score, 3));
    }

    [Fact]
    public void Rerank_DoesNotMutateInput()
    {
        var model = new StubTokenizer(_ => Tokens(V(1f, 0f)));
        var vectors = new Dictionary<string, IReadOnlyList<ReadOnlyMemory<float>>>
        {
            ["1"] = Tokens(V(1f, 0f)),
            ["2"] = Tokens(V(0f, 1f)),
        };

        var candidates = new List<SearchResult> { R("1", Doc1, 1.0), R("2", Doc2, 0.8) };
        var reranker = new MaxSimReranker(model, vectors);

        reranker.Rerank("q", candidates);

        Assert.Equal(1.0, candidates[0].Score);
        Assert.Equal(0.8, candidates[1].Score);
    }

    [Fact]
    public void Ctor_ValidatesArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new MaxSimReranker(null!, new Dictionary<string, IReadOnlyList<ReadOnlyMemory<float>>>()));
        Assert.Throws<ArgumentNullException>(() => new MaxSimReranker(new StubTokenizer(_ => Tokens()), null!));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new MaxSimReranker(new StubTokenizer(_ => Tokens()), new Dictionary<string, IReadOnlyList<ReadOnlyMemory<float>>>(), limit: 0));
    }

    private static IReadOnlyList<ReadOnlyMemory<float>> ThrowUnknown(string text) =>
        throw new KeyNotFoundException($"No tokens configured for '{text}'.");
}