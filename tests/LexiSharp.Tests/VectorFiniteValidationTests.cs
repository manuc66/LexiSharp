using LexiSharp.Core;
using LexiSharp.Embeddings;
using LexiSharp.Indexing;
using Xunit;

namespace LexiSharp.Tests;

public class VectorFiniteValidationTests
{
    [Fact]
    public void Add_NonFiniteEmbedding_Throws()
    {
        var provider = new ConstantEmbeddingProvider(new float[] { 1f, float.NaN });
        var engine = new InMemoryVectorSearchEngine(provider);

        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Add(new SearchDocument("d", "text")));
    }

    [Fact]
    public void Add_InfiniteEmbedding_Throws()
    {
        var provider = new ConstantEmbeddingProvider(new float[] { 1f, float.PositiveInfinity });
        var engine = new InMemoryVectorSearchEngine(provider);

        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Add(new SearchDocument("d", "text")));
    }

    [Fact]
    public void Search_NonFiniteQueryEmbedding_Throws()
    {
        var provider = new QueryDependentEmbeddingProvider(
            documentVector: new float[] { 1f, 0f },
            queryVector: new float[] { 1f, float.NaN });
        var engine = new InMemoryVectorSearchEngine(provider);
        engine.Add(new SearchDocument("d", "text"));

        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Search("q"));
    }

    [Fact]
    public void Import_NonFiniteVector_Throws()
    {
        var provider = new HashingEmbeddingProvider(4);
        var engine = new InMemoryVectorSearchEngine(provider);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            engine.Import(new[] { new VectorIndexEntry(new SearchDocument("d", "t"), new float[] { 1f, float.NaN, 0f, 0f }) }));
    }

    // Helpers
    private sealed class ConstantEmbeddingProvider : IEmbeddingProvider
    {
        private readonly float[] _vector;
        public ConstantEmbeddingProvider(float[] vector) => _vector = vector;
        public int Dimension => _vector.Length;
        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(string text, EmbeddingUse use, CancellationToken cancellationToken = default)
            => Task.FromResult<ReadOnlyMemory<float>>(_vector);
    }

    private sealed class QueryDependentEmbeddingProvider : IEmbeddingProvider
    {
        private readonly float[] _documentVector;
        private readonly float[] _queryVector;
        public QueryDependentEmbeddingProvider(float[] documentVector, float[] queryVector)
        {
            _documentVector = documentVector;
            _queryVector = queryVector;
        }
        public int Dimension => _documentVector.Length;
        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(string text, EmbeddingUse use, CancellationToken cancellationToken = default)
            => Task.FromResult<ReadOnlyMemory<float>>(use == EmbeddingUse.Query ? _queryVector : _documentVector);
    }
}