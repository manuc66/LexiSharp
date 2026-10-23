using LexiSharp.Core;
using LexiSharp.Embeddings;
using LexiSharp.Indexing;
using Xunit;

namespace LexiSharp.Tests;

public class VectorQueryEmbeddingCacheTests
{
    [Fact]
    public void RepeatedSearch_DoesNotReEmbedQuery()
    {
        var provider = new CountingEmbeddingProvider(8);
        var engine = new InMemoryVectorSearchEngine(provider);
        engine.Add(new SearchDocument("d", "hello world"));

        engine.Search("q");
        engine.Search("q");

        Assert.Equal(1, provider.QueryEmbeddingCalls);
    }

    [Fact]
    public void Mutation_InvalidatesQueryCache()
    {
        var provider = new CountingEmbeddingProvider(8);
        var engine = new InMemoryVectorSearchEngine(provider);
        engine.Add(new SearchDocument("d", "hello world"));

        engine.Search("q");
        engine.Add(new SearchDocument("e", "another doc"));
        engine.Search("q");

        Assert.Equal(2, provider.QueryEmbeddingCalls);
    }

    private sealed class CountingEmbeddingProvider : IEmbeddingProvider
    {
        private readonly HashingEmbeddingProvider _inner;
        public int QueryEmbeddingCalls;
        public CountingEmbeddingProvider(int dimension) => _inner = new HashingEmbeddingProvider(dimension);
        public int Dimension => _inner.Dimension;
        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(string text, EmbeddingUse use, CancellationToken cancellationToken = default)
        {
            if (use == EmbeddingUse.Query) Interlocked.Increment(ref QueryEmbeddingCalls);
            return _inner.GetTextEmbeddingAsync(text, use, cancellationToken);
        }
    }
}