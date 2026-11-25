using LexiSharp.Adaptive;
using Xunit;

namespace LexiSharp.Tests;

public class RecencyPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Weight_HalvesAtHalfLife()
    {
        var policy = new RecencyPolicy(TimeSpan.FromDays(10), TimeSpan.FromDays(30));

        Assert.Equal(1.0, policy.Weight(Now, Now), precision: 12);
        Assert.Equal(0.5, policy.Weight(Now, Now.AddDays(-10)), precision: 12);
        Assert.Equal(0.25, policy.Weight(Now, Now.AddDays(-20)), precision: 12);
    }

    [Fact]
    public void Weight_ExpiresAtMaxAge()
    {
        var policy = new RecencyPolicy(TimeSpan.FromDays(10), TimeSpan.FromDays(30));

        Assert.False(policy.IsExpired(Now, Now.AddDays(-30)));
        Assert.True(policy.IsExpired(Now, Now.AddDays(-30).AddTicks(-1)));
        Assert.Equal(0.0, policy.Weight(Now, Now.AddDays(-31)));
    }

    [Fact]
    public void Weight_DampsRepeatedOccurrences()
    {
        var policy = new RecencyPolicy(TimeSpan.FromDays(10), TimeSpan.FromDays(30), frequencySaturation: 7);

        Assert.Equal(1.0, policy.Weight(Now, Now, occurrences: 1), precision: 12);
        Assert.Equal(0.5, policy.Weight(Now, Now, occurrences: 8), precision: 12);
        Assert.Equal(0.1, policy.Weight(Now, Now, occurrences: 64), precision: 12);
    }

    [Fact]
    public void None_IsTheIdentity()
    {
        Assert.Equal(1.0, RecencyPolicy.None.Weight(Now, Now.AddYears(-50)), precision: 12);
        Assert.False(RecencyPolicy.None.IsExpired(Now, Now.AddYears(-50)));
    }

    [Fact]
    public void FutureSample_Throws()
    {
        var policy = new RecencyPolicy(TimeSpan.FromDays(10), TimeSpan.FromDays(30));

        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Weight(Now, Now.AddSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.IsExpired(Now, Now.AddSeconds(1)));
    }

    [Fact]
    public void Copies_QuantizesTheWeight()
    {
        var policy = new RecencyPolicy(
            TimeSpan.FromDays(10),
            TimeSpan.FromDays(30),
            mode: SampleWeightMode.Quantized,
            maxCopies: 4);

        Assert.Equal(4, policy.Copies(Now, Now));
        Assert.Equal(2, policy.Copies(Now, Now.AddDays(-10)));
        Assert.Equal(1, policy.Copies(Now, Now.AddDays(-20)));
        Assert.Equal(0, policy.Copies(Now, Now.AddDays(-31)));
    }

    [Fact]
    public void Copies_InExactMode_Throws()
    {
        var policy = new RecencyPolicy(TimeSpan.FromDays(10), TimeSpan.FromDays(30));

        Assert.Throws<InvalidOperationException>(() => policy.Copies(Now, Now));
    }

    [Fact]
    public void InvalidParameters_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecencyPolicy(TimeSpan.Zero, TimeSpan.FromDays(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecencyPolicy(TimeSpan.FromDays(1), TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecencyPolicy(TimeSpan.FromDays(1), TimeSpan.FromDays(2), frequencySaturation: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecencyPolicy(TimeSpan.FromDays(1), TimeSpan.FromDays(2), maxCopies: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecencyPolicy(TimeSpan.FromDays(1), TimeSpan.FromDays(2), mode: (SampleWeightMode)42));
    }

    [Fact]
    public void OccurrencesBelowOne_AreRejected()
    {
        var policy = new RecencyPolicy(TimeSpan.FromDays(10), TimeSpan.FromDays(30));

        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Weight(Now, Now, occurrences: 0));
    }
}
