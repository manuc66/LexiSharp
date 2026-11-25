using LexiSharp.Adaptive;
using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class AdaptiveAutoTuneTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    private static NaiveBayesClassifier Model()
    {
        var model = new NaiveBayesClassifier();
        model.Train(
        [
            new SearchDocument("a", "gamma", Category: "A"),
            new SearchDocument("b", "gamma", Category: "B"),
        ]);

        return model;
    }

    private static FeedbackEvent<string> Correction(int minute) =>
        new(
            "gamma",
            "A",
            "B",
            "ctx",
            Now.AddMinutes(minute),
            FeedbackOutcome.Corrected);

    [Fact]
    public void AutoTune_HalvesTheHalfLifeOnASustainedDrop()
    {
        var memory = new FeedbackMemory<string>(
            new RecencyPolicy(TimeSpan.FromDays(20), TimeSpan.FromDays(365)),
            null,
            ContextCodecs.Raw<string>());
        var ranker = new AdaptiveFeedbackRanker<string>(
            Model(),
            memory,
            new AdaptiveOptions { AutoTuneHalfLife = true });

        for (int i = 0; i < 100; i++)
        {
            ranker.Observe(Correction(i));
        }

        Assert.True(memory.Policy.HalfLife < TimeSpan.FromDays(20));
        Assert.True(memory.Policy.HalfLife >= TimeSpan.FromDays(3));
    }

    [Fact]
    public void AutoTuneOff_LeavesThePolicyAlone()
    {
        var memory = new FeedbackMemory<string>(
            new RecencyPolicy(TimeSpan.FromDays(20), TimeSpan.FromDays(365)),
            null,
            ContextCodecs.Raw<string>());
        var ranker = new AdaptiveFeedbackRanker<string>(Model(), memory);

        for (int i = 0; i < 100; i++)
        {
            ranker.Observe(Correction(i));
        }

        Assert.Equal(TimeSpan.FromDays(20), memory.Policy.HalfLife);
    }

    [Fact]
    public void Snapshot_PreservesTheAutoTuneFlag()
    {
        var memory = new FeedbackMemory<string>(
            RecencyPolicy.None,
            null,
            ContextCodecs.Raw<string>());
        var ranker = new AdaptiveFeedbackRanker<string>(
            Model(),
            memory,
            new AdaptiveOptions { AutoTuneHalfLife = true });

        AdaptiveSnapshot snapshot = ranker.Save(Now, "model-v1");

        using var stream = new MemoryStream();
        AdaptiveSnapshotSerializer.Save(snapshot, stream);
        stream.Position = 0;

        AdaptiveSnapshot loaded = AdaptiveSnapshotSerializer.Load(stream);

        Assert.True(loaded.Options.AutoTuneHalfLife);
    }
}
