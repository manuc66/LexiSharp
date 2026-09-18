using LexiSharp.Hybrid;
using Xunit;

namespace LexiSharp.Tests;

public class VectorSimilarityTests
{
    [Fact]
    public void CosineSimilarity_IdenticalVectors_IsOne() =>
        Assert.Equal(1f, VectorSimilarity.CosineSimilarity(new[] { 1f, 0f, 0f }, new[] { 1f, 0f, 0f }));

    [Fact]
    public void CosineSimilarity_OrthogonalVectors_IsZero() =>
        Assert.Equal(0f, VectorSimilarity.CosineSimilarity(new[] { 1f, 0f }, new[] { 0f, 1f }));

    [Fact]
    public void CosineSimilarity_OppositeVectors_IsNegativeOne() =>
        Assert.Equal(-1f, VectorSimilarity.CosineSimilarity(new[] { 1f, 0f }, new[] { -1f, 0f }), 5);

    [Fact]
    public void CosineSimilarity_ZeroVector_IsZero() =>
        Assert.Equal(0f, VectorSimilarity.CosineSimilarity(new[] { 0f, 0f }, new[] { 1f, 2f }));

    [Fact]
    public void CosineSimilarity_LengthMismatch_Throws() =>
        Assert.Throws<ArgumentException>(() => VectorSimilarity.CosineSimilarity(new[] { 1f }, new[] { 1f, 2f }));
}