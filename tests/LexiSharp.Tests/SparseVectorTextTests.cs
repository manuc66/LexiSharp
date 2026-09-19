using LexiSharp.Postgres;
using Xunit;

namespace LexiSharp.Tests;

public class SparseVectorTextTests
{
    [Fact]
    public void Format_RendersOneBasedCoordinatesAndDimension()
    {
        string literal = SparseVectorText.Format(
            new[] { (2, 1.5f), (1, -2.25f) },
            dimension: 5);

        Assert.Equal("{2:1.5,1:-2.25}/5", literal);
    }

    [Fact]
    public void Format_SkipsZeroNonFiniteAndOutOfRangeWeights()
    {
        string literal = SparseVectorText.Format(
            new[]
            {
                (1, float.NaN),
                (2, 0f),
                (3, float.PositiveInfinity),
                (4, 2f),
            },
            dimension: 5);

        Assert.Equal("{4:2}/5", literal);
    }

    [Fact]
    public void Format_EmptyCoordinates_YieldsEmptySparseVec()
    {
        Assert.Equal("{}/3", SparseVectorText.Format(Array.Empty<(int, float)>(), 3));
    }

    [Fact]
    public void Format_CoordinateOutsideDimension_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SparseVectorText.Format(new[] { (6, 1f) }, dimension: 5));
    }

    [Fact]
    public void Parse_ReadsCoordinatesAndDimension()
    {
        var (dimension, coordinates) = SparseVectorText.Parse("{2:1.5,1:-2.25}/5");

        Assert.Equal(5, dimension);
        Assert.Equal(1.5f, coordinates[2]);
        Assert.Equal(-2.25f, coordinates[1]);
    }

    [Fact]
    public void Parse_EmptyBody_ReadsDimensionOnly()
    {
        var (dimension, coordinates) = SparseVectorText.Parse("{}/3");

        Assert.Equal(3, dimension);
        Assert.Empty(coordinates);
    }

    [Fact]
    public void RoundTrip_IsLossless()
    {
        var pairs = new[]
        {
            (1, 0.5f),
            (4, 3.14f),
            (7, -1.1f),
        };

        var literal = SparseVectorText.Format(pairs, 8);
        var (dimension, coordinates) = SparseVectorText.Parse(literal);

        Assert.Equal(8, dimension);
        Assert.Equal(pairs.ToDictionary(p => p.Item1, p => p.Item2), coordinates);
    }
}