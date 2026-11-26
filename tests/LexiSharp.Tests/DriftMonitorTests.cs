using LexiSharp.Adaptive;
using Xunit;

namespace LexiSharp.Tests;

public class DriftMonitorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    private static DriftMonitor Monitor(DriftOptions? options = null) =>
        new(options ?? new DriftOptions { MinObservations = 20, Threshold = 5 });

    [Fact]
    public void StationaryCorrectStream_DoesNotDrift()
    {
        DriftMonitor monitor = Monitor();

        for (int i = 0; i < 200; i++)
        {
            monitor.Observe(correct: true, Now.AddMinutes(i));
        }

        Assert.False(monitor.Drifted);
    }

    [Fact]
    public void StepToIncorrect_IsDetected()
    {
        DriftMonitor monitor = Monitor();

        for (int i = 0; i < 100; i++)
        {
            monitor.Observe(correct: true, Now.AddMinutes(i));
        }

        for (int i = 100; i < 200 && !monitor.Drifted; i++)
        {
            monitor.Observe(correct: false, Now.AddMinutes(i));
        }

        Assert.True(monitor.Drifted);
    }

    [Fact]
    public void Reset_ClearsTheDetection()
    {
        DriftMonitor monitor = Monitor();

        for (int i = 0; i < 100; i++)
        {
            monitor.Observe(correct: false, Now.AddMinutes(i));
        }

        Assert.True(monitor.Drifted);

        monitor.Reset();

        Assert.False(monitor.Drifted);
        Assert.Equal(0, monitor.Observations);
    }

    [Fact]
    public void SuggestedHalfLife_HalvesOnDriftAndIsIdempotent()
    {
        DriftMonitor monitor = Monitor();

        for (int i = 0; i < 100; i++)
        {
            monitor.Observe(correct: false, Now.AddMinutes(i));
        }

        var current = TimeSpan.FromDays(20);

        Assert.Equal(TimeSpan.FromDays(10), monitor.SuggestedHalfLife(current));
        Assert.Equal(TimeSpan.FromDays(10), monitor.SuggestedHalfLife(current));
    }

    [Fact]
    public void SuggestedHalfLife_GrowsAfterStability()
    {
        DriftMonitor monitor = Monitor(new DriftOptions
        {
            MinObservations = 20,
            StableObservations = 50,
        });

        for (int i = 0; i < 50; i++)
        {
            monitor.Observe(correct: true, Now.AddMinutes(i));
        }

        Assert.Equal(
            TimeSpan.FromDays(10) * 1.05,
            monitor.SuggestedHalfLife(TimeSpan.FromDays(10)));
    }

    [Fact]
    public void SuggestedHalfLife_IsClamped()
    {
        DriftMonitor monitor = Monitor(new DriftOptions
        {
            MinObservations = 1,
            Threshold = 0.1,
            MinHalfLife = TimeSpan.FromDays(5),
            MaxHalfLife = TimeSpan.FromDays(10),
        });

        monitor.Observe(correct: false, Now);

        Assert.Equal(TimeSpan.FromDays(5), monitor.SuggestedHalfLife(TimeSpan.FromDays(6)));
    }

    [Fact]
    public void OutOfOrderObservation_IsRejected()
    {
        DriftMonitor monitor = Monitor();
        monitor.Observe(correct: true, Now);

        Assert.Throws<ArgumentOutOfRangeException>(() => monitor.Observe(correct: true, Now.AddSeconds(-1)));
    }
}
