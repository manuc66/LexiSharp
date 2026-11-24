using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class SignificanceTestsTests
{
    [Fact]
    public void Wilcoxon_IdenticalSamples_ReturnsOne()
    {
        var a = new[] { 1.0, 2.0, 3.0 };
        var b = new[] { 1.0, 2.0, 3.0 };
        Assert.Equal(1.0, SignificanceTests.WilcoxonSignedRank(a, b), 3);
    }

    [Fact]
    public void Wilcoxon_DifferentSamples_ReturnsLowP()
    {
        var a = new[] { 10.0, 11.0, 12.0, 13.0, 14.0, 15.0 };
        var b = new[] { 1.0, 2.0, 3.0, 4.0, 5.0, 6.0 };
        Assert.True(SignificanceTests.WilcoxonSignedRank(a, b) < 0.05);
    }

    [Fact]
    public void PairedBootstrap_ConsistentImprovement_IsSignificant()
    {
        var a = new[] { 2.0, 3.0, 4.0, 5.0, 6.0, 7.0 };
        var b = new[] { 1.0, 2.0, 3.0, 4.0, 5.0, 6.0 };
        Assert.True(LexiSharp.Core.SignificanceTests.PairedBootstrap(a, b) < 0.05);
    }

    [Fact]
    public void PairedBootstrap_IdenticalSamples_ReturnsOne()
    {
        var a = new[] { 1.0, 2.0, 3.0 };
        var b = new[] { 1.0, 2.0, 3.0 };
        Assert.Equal(1.0, SignificanceTests.PairedBootstrap(a, b), 3);
    }
}
