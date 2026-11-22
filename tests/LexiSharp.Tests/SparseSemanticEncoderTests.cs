using LexiSharp.Core;
using LexiSharp.Hybrid;
using Xunit;

namespace LexiSharp.Tests;

public class SparseSemanticEncoderTests
{
    [Fact]
    public async Task Encode_ProducesUnigramsAndNgrams()
    {
        var encoder = new SparseSemanticEncoder(maxNgram: 3, maxTerms: 64);

        var result = await encoder.GetSparseEmbeddingAsync("machine learning algorithms", EmbeddingUse.Passage);

        Assert.True(result.Count > 0);
        Assert.True(result.ContainsKey("machine"));
        Assert.True(result.ContainsKey("machine learning"));
        Assert.True(result.ContainsKey("machine learning algorithms"));
        // N-grams should have higher weight than unigrams.
        Assert.True(result["machine learning"] > result["machine"]);
    }

    [Fact]
    public async Task Encode_EmptyText_ReturnsEmpty()
    {
        var encoder = new SparseSemanticEncoder();

        var result = await encoder.GetSparseEmbeddingAsync("", EmbeddingUse.Passage);

        Assert.Empty(result);
    }

    [Fact]
    public async Task Encode_PruneToMaxTerms()
    {
        var encoder = new SparseSemanticEncoder(maxNgram: 2, maxTerms: 3);

        var result = await encoder.GetSparseEmbeddingAsync("a b c d e f g h", EmbeddingUse.Passage);

        Assert.True(result.Count <= 3);
    }

    [Fact]
    public async Task Encode_IsSymmetric()
    {
        var encoder = new SparseSemanticEncoder();

        var q = await encoder.GetSparseEmbeddingAsync("hello world", EmbeddingUse.Query);
        var p = await encoder.GetSparseEmbeddingAsync("hello world", EmbeddingUse.Passage);

        Assert.Equal(q.Count, p.Count);
        foreach (var key in q.Keys)
            Assert.Equal(q[key], p[key]);
    }

    [Fact]
    public void Constructor_ThrowsOnInvalidMaxNgram()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SparseSemanticEncoder(maxNgram: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SparseSemanticEncoder(maxNgram: 5));
    }
}