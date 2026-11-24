using LexiSharp.Classification;
using LexiSharp.Core;
using LexiSharp.Indexing;
using Xunit;

namespace LexiSharp.Tests;

public class RiskyEdgeTests
{
    // --- NaiveBayes ---
    [Fact]
    public void NaiveBayes_SingleClass_PredictsIt()
    {
        var nb = new NaiveBayesClassifier();
        nb.Train(new[] { new SearchDocument("1", "hello", Category: "A") });
        Assert.Equal("A", nb.PredictBest("hello"));
    }

    [Fact]
    public void NaiveBayes_EmptyText_DoesNotCrash()
    {
        var nb = new NaiveBayesClassifier();
        nb.Train(new[] { new SearchDocument("1", "hello", Category: "A") });
        var results = nb.Predict("", limit: 3);
        Assert.NotNull(results);
    }

    // --- NearestNeighbor ---
    [Fact]
    public void NearestNeighbor_KGreaterThanN_DoesNotCrash()
    {
        var provider = new FakeEmbeddings();
        var nn = new NearestNeighborClassifier<Learned>(e => e.Vector, e => e.Label, new NearestNeighborOptions { K = 100 });
        nn.Train(new[] { new Learned("a", new float[] { 1f, 0f }), new Learned("b", new float[] { 0f, 1f }) });
        var results = nn.Predict(new ReadOnlyMemory<float>(new float[] { 1f, 0f }), limit: 3);
        Assert.NotNull(results);
    }

    // --- Blend ---
    [Fact]
    public void Blend_SingleZeroWeightLane_DoesNotDivideByZero()
    {
        var inner = new NaiveBayesClassifier();
        inner.Train(new[] { new SearchDocument("1", "hello", Category: "A") });
        var blend = new BlendTextClassifier(new[] { new TextClassifierLane(inner, 0.0) });
        var results = blend.Predict("hello", limit: 3);
        Assert.NotNull(results);
    }

    // --- Sparse engine ---
    [Fact]
    public void Sparse_RemoveUnknown_ReturnsFalse()
    {
        var engine = new SparseTextSearchEngine(new StubSparseProvider(_ => new Dictionary<string, float>()));
        Assert.False(engine.Remove("nope"));
    }

    [Fact]
    public void Sparse_ClearThenSearch_Empty()
    {
        var engine = new SparseTextSearchEngine(new StubSparseProvider(t => t == "q"
            ? new Dictionary<string, float> { ["x"] = 1f }
            : new Dictionary<string, float> { ["x"] = 1f }));
        engine.Add(new SearchDocument("1", "doc"));
        engine.Clear();
        Assert.Empty(engine.Search("q"));
    }

    private sealed record Learned(string Label, float[] Vector);
    private sealed class FakeEmbeddings : IEmbeddingProvider
    {
        public int Dimension => 2;
        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(string text, EmbeddingUse use, CancellationToken cancellationToken = default) =>
            Task.FromResult<ReadOnlyMemory<float>>(new float[] { 1f, 0f });
    }

    private sealed class StubSparseProvider : ISparseEmbeddingProvider
    {
        private readonly Func<string, IReadOnlyDictionary<string, float>> _lookup;
        public StubSparseProvider(Func<string, IReadOnlyDictionary<string, float>> lookup) => _lookup = lookup;
        public Task<IReadOnlyDictionary<string, float>> GetSparseEmbeddingAsync(string text, EmbeddingUse use, CancellationToken cancellationToken = default) =>
            Task.FromResult(_lookup(text));
    }
}
