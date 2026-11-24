using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class TemporalPriorTests
{
    private static readonly ObservedAssignment[] History =
    {
        new(DayOfWeek.Monday, 9, "A"),
        new(DayOfWeek.Monday, 9, "A"),
        new(DayOfWeek.Monday, 9, "A"),
        new(DayOfWeek.Monday, 10, "B"),
    };

    [Fact]
    public void UnderSupport_ReturnsEmpty()
    {
        var prior = new TemporalPrior(History);
        var probs = prior.Probabilities(DayOfWeek.Monday, 12);
        Assert.Empty(probs);
    }

    [Fact]
    public void Block_GroupsHours()
    {
        var prior = new TemporalPrior(History, blockHours: 3, minSupport: 1);
        var probs = prior.Probabilities(DayOfWeek.Monday, 10);
        Assert.Contains("B", probs.Keys);
    }

    [Fact]
    public void Dirichlet_AvoidsProbabilityOne()
    {
        var prior = new TemporalPrior(History, blockHours: 3, minSupport: 1, alpha: 0.5);
        var probs = prior.Probabilities(DayOfWeek.Monday, 9);
        Assert.True(probs["A"] < 1.0);
    }

    [Fact]
    public void Prior_WithoutClock_WallClockPathIsNeutral()
    {
        var prior = new TemporalPrior(History, blockHours: 3, minSupport: 1);
        Assert.IsAssignableFrom<ITemporalPriorSource>(prior);

        ITextPriorSource textPrior = prior;
        Assert.Empty(textPrior.Prior("text"));
    }

    [Fact]
    public void Prior_WithExplicitClock_UsesIt()
    {
        var instant = new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);
        var prior = new TemporalPrior(History, instant, blockHours: 3, minSupport: 1);
        ITextPriorSource textPrior = prior;
        Assert.NotEmpty(textPrior.Prior("text"));
    }

    [Fact]
    public void TemporalPrior_Prior_UsesInstant()
    {
        var prior = new TemporalPrior(History, blockHours: 3, minSupport: 1);
        var evidence = ((ITemporalPriorSource)prior).Prior(new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero));
        Assert.NotEmpty(evidence);
    }
}
