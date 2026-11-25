using LexiSharp.Adaptive;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class PreferenceSnapshotSerializerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Snapshot_RoundTripsThroughLXPR()
    {
        var learner = new PreferenceLearner();
        learner.Train(Enumerable.Range(0, 10).Select(_ => Event("alpha", "B")));

        PreferenceSnapshot snapshot = learner.Save(Now);

        using var stream = new MemoryStream();
        PreferenceSnapshotSerializer.Save(snapshot, stream);
        stream.Position = 0;

        PreferenceSnapshot loadedSnapshot = PreferenceSnapshotSerializer.Load(stream);
        PreferenceLearner loaded = PreferenceLearner.Load(loadedSnapshot);

        var candidates = new[]
        {
            new ClassificationResult("A", 0.6),
            new ClassificationResult("B", 0.4),
        };

        Assert.Equal(
            learner.ReRank("alpha", "ctx", candidates).Select(candidate => candidate.Category),
            loaded.ReRank("alpha", "ctx", candidates).Select(candidate => candidate.Category));
    }

    [Fact]
    public void TamperedSnapshot_IsRejected()
    {
        var learner = new PreferenceLearner();
        learner.Train(Enumerable.Range(0, 5).Select(_ => Event("alpha", "B")));

        using var stream = new MemoryStream();
        PreferenceSnapshotSerializer.Save(learner.Save(Now), stream);
        byte[] bytes = stream.ToArray();
        bytes[^1] ^= 0xFF;

        using var tampered = new MemoryStream(bytes);

        Assert.Throws<InvalidDataException>(() => PreferenceSnapshotSerializer.Load(tampered));
    }

    private static PreferenceEvent Event(string text, string chosen) =>
        new(
            text,
            "ctx",
            [
                new ClassificationResult("A", 0.5),
                new ClassificationResult("B", 0.5),
            ],
            chosen,
            Now);
}
