using LexiSharp.Hybrid;
using Xunit;

namespace LexiSharp.Tests;

// The centreing pair — Mean / Center / Normalize / CenterAndNormalize — is arithmetic a caller
// previously had to reimplement, so these pin the arithmetic rather than any benefit from it.
// Nothing here asserts that centreing improves retrieval: that depends on the embedding model
// and the corpus, and is not what a unit test can establish.
public class VectorSimilarityCenteringTests
{
    [Fact]
    public void Mean_AveragesEachDimension()
    {
        var mean = VectorSimilarity.Mean(new[]
        {
            new float[] { 1f, 3f, 5f },
            new float[] { 3f, 5f, 7f },
        });

        Assert.Equal(new[] { 2f, 4f, 6f }, mean);
    }

    [Fact]
    public void Mean_OfOneVector_IsThatVector()
    {
        var mean = VectorSimilarity.Mean(new[] { new float[] { 1f, -2f, 3f } });

        Assert.Equal(new[] { 1f, -2f, 3f }, mean);
    }

    [Fact]
    public void Mean_EmptySet_Throws()
    {
        // There is no mean of nothing, and silently returning zeros would put a wrong offset
        // under every subsequent Center call.
        Assert.Throws<ArgumentException>(() => VectorSimilarity.Mean(Array.Empty<ReadOnlyMemory<float>>()));
    }

    [Fact]
    public void Mean_MismatchedLengths_Throws()
    {
        Assert.Throws<ArgumentException>(() => VectorSimilarity.Mean(new[]
        {
            new float[] { 1f, 2f },
            new float[] { 1f, 2f, 3f },
        }));
    }

    [Fact]
    public void Mean_FloatRowsOverload_AgreesWithTheMemoryOne()
    {
        // Two shapes, one answer: a caller holding float[] rows and one holding
        // ReadOnlyMemory<float> must not get different means.
        var rows = new[]
        {
            new float[] { 1f, 3f, 5f },
            new float[] { 3f, 5f, 7f },
        };

        Assert.Equal(
            VectorSimilarity.Mean(rows.Select(v => (ReadOnlyMemory<float>)v)),
            VectorSimilarity.Mean(rows));
    }

    [Fact]
    public void Mean_EnumeratesExactlyOnce()
    {
        // Documented on the method, and a provider handing over a hundred thousand embeddings
        // would notice a second pass.
        var counted = new CountingEnumerable(new ReadOnlyMemory<float>[]
        {
            new float[] { 1f },
            new float[] { 3f },
        });

        var mean = VectorSimilarity.Mean(counted);

        Assert.Equal(1, counted.Enumerations);
        Assert.Equal(new[] { 2f }, mean);
    }

    [Fact]
    public void Center_SubtractsElementwise()
    {
        var destination = new float[3];

        VectorSimilarity.Center(new[] { 5f, 7f, 9f }, new[] { 1f, 2f, 3f }, destination);

        Assert.Equal(new[] { 4f, 5f, 6f }, destination);
    }

    [Fact]
    public void Center_InPlace_IsWellDefined()
    {
        // Documented as safe, so it must be: each element is read before it is written.
        float[] vector = [5f, 7f, 9f];

        VectorSimilarity.Center(vector, new[] { 1f, 2f, 3f }, vector);

        Assert.Equal(new[] { 4f, 5f, 6f }, vector);
    }

    [Fact]
    public void Center_LengthMismatch_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            VectorSimilarity.Center(new[] { 1f, 2f }, new[] { 1f }, new float[2]));
    }

    [Fact]
    public void CenteringACorpusByItsOwnMean_GivesZeroMean()
    {
        // The actual guarantee: removing the mean removes it. A caller centring both documents
        // and queries relies on this to place them in the same space.
        var corpus = new[]
        {
            new float[] { 1f, 3f, 5f },
            new float[] { 3f, 5f, 7f },
            new float[] { 5f, 7f, 9f },
        };

        float[] mean = VectorSimilarity.Mean(corpus);
        var centered = new List<ReadOnlyMemory<float>>(corpus.Length);

        foreach (float[] vector in corpus)
        {
            var copy = new float[vector.Length];
            VectorSimilarity.Center(vector, mean, copy);
            centered.Add(copy);
        }

        Assert.All(VectorSimilarity.Mean(centered), value => Assert.Equal(0f, value, 5));
    }

    [Fact]
    public void Normalize_ScalesToUnitLength()
    {
        var destination = new float[2];

        VectorSimilarity.Normalize(new[] { 3f, 4f }, destination);

        Assert.Equal(new[] { 0.6f, 0.8f }, destination);
        Assert.Equal(1f, VectorSimilarity.Norm(destination), 5);
    }

    [Fact]
    public void Normalize_InPlace_IsWellDefined()
    {
        float[] vector = [3f, 4f];

        VectorSimilarity.Normalize(vector, vector);

        Assert.Equal(1f, VectorSimilarity.Norm(vector), 5);
    }

    [Fact]
    public void Normalize_ZeroVector_CopiesThroughRatherThanNaN()
    {
        // CosineSimilarity reports 0 for a zero input; normalizing one to NaN would poison
        // every later comparison instead of carrying that convention.
        var destination = new float[] { 99f, 99f };

        VectorSimilarity.Normalize(new[] { 0f, 0f }, destination);

        Assert.Equal(new[] { 0f, 0f }, destination);
    }

    [Fact]
    public void Normalize_LengthMismatch_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            VectorSimilarity.Normalize(new[] { 1f, 2f }, new float[3]));
    }

    [Fact]
    public void CenterAndNormalize_AgreesWithDoingTheTwoSteps()
    {
        // The fused form exists to skip materializing the subtraction; it must not change it.
        float[] vector = [7f, 1f, 4f];
        float[] mean = [2f, 3f, 1f];

        var fused = new float[3];
        VectorSimilarity.CenterAndNormalize(vector, mean, fused);

        var twoSteps = new float[3];
        VectorSimilarity.Center(vector, mean, twoSteps);
        VectorSimilarity.Normalize(twoSteps, twoSteps);

        Assert.Equal(twoSteps, fused);
    }

    [Fact]
    public void CenterAndNormalize_ProducesUnitLength()
    {
        var destination = new float[3];

        VectorSimilarity.CenterAndNormalize(new[] { 7f, 1f, 4f }, new[] { 2f, 3f, 1f }, destination);

        Assert.Equal(1f, VectorSimilarity.Norm(destination), 5);
    }

    [Fact]
    public void CenterAndNormalize_VectorEqualingMean_ProducesZerosNotNaN()
    {
        // The one input where centering collapses to nothing. NaN here would reach cosine
        // comparison and infect every score that touched it.
        var destination = new float[] { 99f, 99f, 99f };

        VectorSimilarity.CenterAndNormalize(new[] { 2f, 3f, 1f }, new[] { 2f, 3f, 1f }, destination);

        Assert.Equal(new[] { 0f, 0f, 0f }, destination);
    }

    [Fact]
    public void CenterAndNormalize_LengthMismatch_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            VectorSimilarity.CenterAndNormalize(new[] { 1f, 2f }, new[] { 1f }, new float[2]));
    }

    private sealed class CountingEnumerable : IEnumerable<ReadOnlyMemory<float>>
    {
        private readonly IEnumerable<ReadOnlyMemory<float>> _inner;

        public CountingEnumerable(IEnumerable<ReadOnlyMemory<float>> inner) => _inner = inner;

        public int Enumerations { get; private set; }

        public IEnumerator<ReadOnlyMemory<float>> GetEnumerator()
        {
            Enumerations++;
            return _inner.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
