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

    // DotProduct / Norm / NormSquared are public because a caller holding cached norms should not
    // have to recompute them, so they are pinned here directly rather than left covered only
    // incidentally through the dense engine's scan.
    [Fact]
    public void DotProduct_SameVector_IsItsSquaredNorm()
    {
        float[] vector = [3f, 4f];

        Assert.Equal(25f, VectorSimilarity.DotProduct(vector, vector));
    }

    [Fact]
    public void DotProduct_IsSymmetric()
    {
        float[] left = [1f, 2f, 3f];
        float[] right = [4f, 5f, 6f];

        Assert.Equal(
            VectorSimilarity.DotProduct(left, right),
            VectorSimilarity.DotProduct(right, left));
    }

    [Fact]
    public void DotProduct_Empty_IsZero() =>
        Assert.Equal(0f, VectorSimilarity.DotProduct(ReadOnlySpan<float>.Empty, ReadOnlySpan<float>.Empty));

    [Fact]
    public void DotProduct_LengthMismatch_Throws() =>
        Assert.Throws<ArgumentException>(() => VectorSimilarity.DotProduct(new[] { 1f }, new[] { 1f, 2f }));

    [Fact]
    public void NormSquared_IsDotProductWithItself()
    {
        float[] vector = [1f, -2f, 2f, -1f];

        Assert.Equal(VectorSimilarity.DotProduct(vector, vector), VectorSimilarity.NormSquared(vector));
    }

    [Fact]
    public void Norm_IsSqrtOfNormSquared()
    {
        float[] vector = [1f, 2f, 2f];

        Assert.Equal(MathF.Sqrt(VectorSimilarity.NormSquared(vector)), VectorSimilarity.Norm(vector));
    }

    [Fact]
    public void Norm_ZeroVector_IsZero() =>
        Assert.Equal(0f, VectorSimilarity.Norm(new float[5]));

    [Fact]
    public void Norm_Empty_IsZero() =>
        Assert.Equal(0f, VectorSimilarity.Norm(ReadOnlySpan<float>.Empty));

    // The kernels accumulate in Vector<float>-wide lanes, so a length that is not a multiple of
    // the hardware width runs the scalar remainder loop as well. These pin that the remainder is
    // not dropped: a vectorized body that ignored its tail would still pass the aligned lengths.
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(16)]
    [InlineData(17)]
    public void DotProduct_MatchesScalarReference_AtEveryLengthRemainder(int length)
    {
        var left = new float[length];
        var right = new float[length];

        for (int i = 0; i < length; i++)
        {
            left[i] = i + 1;
            right[i] = (i % 3) - 1;
        }

        float expected = 0;
        for (int i = 0; i < length; i++)
            expected += left[i] * right[i];

        Assert.Equal(expected, VectorSimilarity.DotProduct(left, right));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(17)]
    public void NormSquared_MatchesScalarReference_AtEveryLengthRemainder(int length)
    {
        var vector = new float[length];
        for (int i = 0; i < length; i++)
            vector[i] = i - 4;

        float expected = 0;
        for (int i = 0; i < length; i++)
            expected += vector[i] * vector[i];

        Assert.Equal(expected, VectorSimilarity.NormSquared(vector));
    }
}
