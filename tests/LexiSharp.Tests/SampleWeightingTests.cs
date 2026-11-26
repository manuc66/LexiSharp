using LexiSharp.Adaptive;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class SampleWeightingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Expand_ProducesTheQuantizedCopyCountInOrder()
    {
        var first = new SearchDocument("1", "first");
        var second = new SearchDocument("2", "second");
        var policy = new RecencyPolicy(
            TimeSpan.FromDays(10),
            TimeSpan.FromDays(30),
            mode: SampleWeightMode.Quantized,
            maxCopies: 4);

        var expanded = SampleWeighting.Expand(
            [
                new TimedDocument(first, Now),
                new TimedDocument(second, Now.AddDays(-10)),
                new TimedDocument(first, Now.AddDays(-20)),
                new TimedDocument(second, Now.AddDays(-31)),
            ],
            policy,
            Now).ToList();

        Assert.Equal(
            ["1", "1", "1", "1", "2", "2", "1"],
            expanded.Select(document => document.Id));
    }

    [Fact]
    public void Expand_InExactMode_Throws()
    {
        var policy = new RecencyPolicy(TimeSpan.FromDays(10), TimeSpan.FromDays(30));

        Assert.Throws<InvalidOperationException>(() =>
            SampleWeighting.Expand(
                [new TimedDocument(new SearchDocument("1", "text"), Now)],
                policy,
                Now).ToList());
    }

    [Fact]
    public void Expand_RejectsADefaultTimedDocument()
    {
        var policy = new RecencyPolicy(
            TimeSpan.FromDays(10),
            TimeSpan.FromDays(30),
            mode: SampleWeightMode.Quantized);

        Assert.Throws<ArgumentException>(() =>
            SampleWeighting.Expand([default(TimedDocument)], policy, Now).ToList());
    }

    [Fact]
    public void Expand_RejectsAFutureSample()
    {
        var policy = new RecencyPolicy(
            TimeSpan.FromDays(10),
            TimeSpan.FromDays(30),
            mode: SampleWeightMode.Quantized);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SampleWeighting.Expand(
                [new TimedDocument(new SearchDocument("1", "text"), Now.AddSeconds(1))],
                policy,
                Now).ToList());
    }
}
