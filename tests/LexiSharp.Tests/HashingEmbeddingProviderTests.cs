using LexiSharp.Core;
using LexiSharp.Embeddings;
using LexiSharp.Hybrid;
using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

public class HashingEmbeddingProviderTests
{
    private static float[] Floats(ReadOnlyMemory<float> vector) => vector.ToArray();

    [Fact]
    public void SameText_ProducesIdenticalVector_AcrossInstances()
    {
        var first = new HashingEmbeddingProvider(64);
        var second = new HashingEmbeddingProvider(64);

        Assert.Equal(Floats(first.GetEmbedding("refresh token session")), Floats(second.GetEmbedding("refresh token session")));
    }

    [Fact]
    public void Dimension_IsRespectedEverywhere()
    {
        var provider = new HashingEmbeddingProvider(128);

        Assert.Equal(128, provider.Dimension);
        Assert.Equal(128, provider.GetEmbedding("hello world").Length);
        Assert.Equal(64, new HashingEmbeddingProvider(64).GetEmbedding("hello").Length);

        foreach (var token in provider.GetTokenEmbeddings("hello world", EmbeddingUse.Passage))
            Assert.Equal(128, token.Length);
    }

    [Fact]
    public void NonEmptyText_IsUnitNormalized()
    {
        var provider = new HashingEmbeddingProvider(256);
        var vector = provider.GetEmbedding("token refresh session expiry").Span;

        double norm = 0;

        for (int i = 0; i < vector.Length; i++)
            norm += vector[i] * vector[i];

        Assert.Equal(1.0, Math.Sqrt(norm), 3);
    }

    [Fact]
    public void EmptyOrWhitespaceText_YieldsZeroVector()
    {
        var provider = new HashingEmbeddingProvider(32);

        Assert.All(Floats(provider.GetEmbedding("")), value => Assert.Equal(0f, value));
        Assert.All(Floats(provider.GetEmbedding("   ")), value => Assert.Equal(0f, value));
        Assert.Empty(provider.GetTokenEmbeddings("", EmbeddingUse.Passage));
    }

    [Fact]
    public void SharedTokens_HavePositiveCosine()
    {
        var provider = new HashingEmbeddingProvider(256);

        var query = provider.GetEmbedding("oauth access token");
        var document = provider.GetEmbedding("the access token expires quietly");

        Assert.True(VectorSimilarity.CosineSimilarity(query.Span, document.Span) > 0);
    }

    [Fact]
    public async Task QueryAndPassageRoles_AreSymmetric()
    {
        var provider = new HashingEmbeddingProvider(128);

        var asQuery = await provider.GetTextEmbeddingAsync("refresh token", EmbeddingUse.Query);
        var asPassage = await provider.GetTextEmbeddingAsync("refresh token", EmbeddingUse.Passage);

        Assert.Equal(Floats(asQuery), Floats(asPassage));
    }

    [Fact]
    public void TokenEmbeddings_ReturnOneUnitVectorPerToken()
    {
        var provider = new HashingEmbeddingProvider(256);
        var tokens = provider.GetTokenEmbeddings("alpha beta gamma", EmbeddingUse.Query);

        Assert.Equal(3, tokens.Count);

        foreach (var token in tokens)
        {
            double norm = 0;
            var span = token.Span;

            for (int i = 0; i < span.Length; i++)
                norm += span[i] * span[i];

            Assert.Equal(1.0, Math.Sqrt(norm), 3);
        }
    }

    [Fact]
    public void Name_ReportsItsDimension()
    {
        Assert.Equal("Hashing(512)", new HashingEmbeddingProvider(512).Name);
    }

    [Fact]
    public void NonPositiveDimension_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HashingEmbeddingProvider(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new HashingEmbeddingProvider(-8));
    }

    [Fact]
    public async Task NullArguments_Throw()
    {
        var provider = new HashingEmbeddingProvider(32);

        Assert.Throws<ArgumentNullException>(() => provider.GetEmbedding(null!));
        Assert.Throws<ArgumentNullException>(() => provider.GetTokenEmbeddings(null!, EmbeddingUse.Query));
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.GetTextEmbeddingAsync(null!, EmbeddingUse.Query));
    }

    [Fact]
    public void SignHashing_ProducesBothPositiveAndNegativeContributions()
    {
        var provider = new HashingEmbeddingProvider(512);
        var vector = provider.GetEmbedding(
            "alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu "
            + "nu xi omicron pi rho sigma tau upsilon phi chi psi omega")
            .ToArray();

        Assert.Contains(vector, value => value > 0);
        Assert.Contains(vector, value => value < 0);
    }

    [Fact]
    public void CustomTokenizer_IsUsed()
    {
        // A tokenizer that maps everything to a single token makes any two texts identical.
        var provider = new HashingEmbeddingProvider(64, new SingleTokenTokenizer());

        Assert.Equal(Floats(provider.GetEmbedding("anything at all")), Floats(provider.GetEmbedding("completely different")));
    }

    private sealed class SingleTokenTokenizer : ITokenizer
    {
        public IReadOnlyList<string> Tokenize(string text) =>
            text.Length == 0 ? System.Array.Empty<string>() : new[] { "token" };
    }
}
