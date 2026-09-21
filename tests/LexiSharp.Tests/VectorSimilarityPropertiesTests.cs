using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using LexiSharp.Hybrid;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Property-based tests for <see cref="VectorSimilarity.CosineSimilarity"/>: the result must
/// stay in [-1, 1], be symmetric, and be invariant to positive rescaling of either input.
/// Both vectors share a length and their components are bounded so the float math cannot overflow.
/// </summary>
public class VectorSimilarityPropertiesTests
{
    private static readonly Gen<float> Component =
        Gen.Choose(-500, 500).Select(i => i / 10.0f);

    private static readonly Arbitrary<(float[] A, float[] B)> VectorPairs =
        Arb.From(
            from length in Gen.Choose(1, 16)
            from a in Gen.CollectToArray(Enumerable.Repeat(Component, length))
            from b in Gen.CollectToArray(Enumerable.Repeat(Component, length))
            select (a, b));

    [PropertyAttribute]
    public Property Cosine_LiesInUnitInterval() =>
        Prop.ForAll(VectorPairs, pair =>
        {
            float cosine = VectorSimilarity.CosineSimilarity(pair.A, pair.B);
            // A tiny tolerance absorbs float rounding of the accumulated dot product.
            return !float.IsNaN(cosine) && Math.Abs(cosine) <= 1.0f + 1e-5f;
        });

    [PropertyAttribute]
    public Property Cosine_IsSymmetric() =>
        Prop.ForAll(VectorPairs, pair =>
            Math.Abs(VectorSimilarity.CosineSimilarity(pair.A, pair.B)
                - VectorSimilarity.CosineSimilarity(pair.B, pair.A)) < 1e-6f);

    [PropertyAttribute]
    public Property Cosine_IsInvariantToPositiveScaling() =>
        Prop.ForAll(
            VectorPairs,
            Arb.From(Gen.Choose(1, 1000).Select(scale => (float)scale)),
            (pair, scale) =>
            {
                var scaled = pair.B.Select(component => component * scale).ToArray();
                return Math.Abs(VectorSimilarity.CosineSimilarity(pair.A, pair.B)
                    - VectorSimilarity.CosineSimilarity(pair.A, scaled)) < 1e-3f;
            });

    [PropertyAttribute]
    public Property Cosine_AgainstZeroVector_IsZero() =>
        Prop.ForAll(VectorPairs, pair =>
            VectorSimilarity.CosineSimilarity(pair.A, new float[pair.A.Length]) == 0f);

    [Fact]
    public void Cosine_EmptyVectors_IsZero() =>
        Assert.Equal(0f, VectorSimilarity.CosineSimilarity(ReadOnlySpan<float>.Empty, ReadOnlySpan<float>.Empty));
}