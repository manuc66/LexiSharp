using LexiSharp.Core;
using LexiSharp.Hybrid;
using Xunit;

namespace LexiSharp.Tests;

public class MaxSimExplainTests
{
    private static ReadOnlyMemory<float> V(params float[] values) => values;

    private static ReadOnlyMemory<float>[] Tokens(params ReadOnlyMemory<float>[] vectors) => vectors;

    private sealed class StubTokenizer : ITokenEmbeddingProvider
    {
        private readonly Func<string, IReadOnlyList<ReadOnlyMemory<float>>> _lookup;

        public StubTokenizer(Func<string, IReadOnlyList<ReadOnlyMemory<float>>> lookup)
            => _lookup = lookup;

        public string Name => "stub";

        public IReadOnlyList<ReadOnlyMemory<float>> GetTokenEmbeddings(string text, EmbeddingUse use) => _lookup(text);
    }

    [Fact]
    public void ExplainRerank_ReturnsPerQueryTokenSignals()
    {
        var model = new StubTokenizer(q => q == "q"
            ? Tokens(V(1f, 0f), V(0f, 1f))
            : throw new InvalidOperationException());

        var vectors = new Dictionary<string, IReadOnlyList<ReadOnlyMemory<float>>>
        {
            ["1"] = Tokens(V(1f, 0f), V(0.95f, 0.3f)),
        };

        var reranker = new MaxSimReranker(model, vectors);
        var candidates = new[] { new SearchResult("1", 1.0, new SearchDocument("1", "doc1")) };

        var explanation = reranker.ExplainRerank("q", candidates, "1");

        Assert.NotNull(explanation);
        Assert.Equal("1", explanation!.DocumentId);
        Assert.Equal(2, explanation.Signals.Count);
        Assert.All(explanation.Signals, s => Assert.StartsWith("query-token-", s.Name));
        Assert.False(string.IsNullOrWhiteSpace(explanation.Summary));
    }

    [Fact]
    public void ExplainRerank_ReturnsNullForMissingDocument()
    {
        var model = new StubTokenizer(q => Tokens(V(1f, 0f)));
        var vectors = new Dictionary<string, IReadOnlyList<ReadOnlyMemory<float>>>();
        var reranker = new MaxSimReranker(model, vectors);
        var candidates = Array.Empty<SearchResult>();

        var explanation = reranker.ExplainRerank("q", candidates, "missing");
        Assert.Null(explanation);
    }
}