using LexiSharp.Adaptive;
using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class AdaptiveSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    private static NaiveBayesClassifier Model()
    {
        var model = new NaiveBayesClassifier();
        model.Train(
        [
            new SearchDocument("a1", "alpha", Category: "A"),
            new SearchDocument("b1", "beta", Category: "B"),
        ]);

        return model;
    }

    [Fact]
    public void Snapshot_RoundTripsMemoryCalibrationAndOptions()
    {
        NaiveBayesClassifier model = Model();
        var memory = new FeedbackMemory<string>(
            RecencyPolicy.None,
            new FeedbackMemoryOptions(),
            ContextCodecs.Raw<string>());
        var calibrator = new WeightedConformalCalibrator(0.2, RecencyPolicy.None, minSamples: 1);
        var ranker = new AdaptiveFeedbackRanker<string>(model, memory, calibrator: calibrator);

        ranker.Observe(new FeedbackEvent<string>(
            "alpha",
            "A",
            "A",
            "ctx",
            Now,
            FeedbackOutcome.Accepted,
            Offered: model.Predict("alpha", limit: int.MaxValue)));

        AdaptiveSnapshot snapshot = ranker.Save(Now, "model-v1");

        using var stream = new MemoryStream();
        AdaptiveSnapshotSerializer.Save(snapshot, stream);
        stream.Position = 0;

        AdaptiveSnapshot loadedSnapshot = AdaptiveSnapshotSerializer.Load(stream);
        AdaptiveFeedbackRanker<string> loaded = AdaptiveFeedbackRanker<string>.Load(
            loadedSnapshot,
            model,
            ContextCodecs.Raw<string>());

        Assert.Equal("model-v1", loadedSnapshot.ModelSignature);
        Assert.False(string.IsNullOrEmpty(loadedSnapshot.Signature));
        Assert.Equal(
            ranker.Predict("alpha", "ctx", Now).Results.Select(score => score.Category),
            loaded.Predict("alpha", "ctx", Now).Results.Select(score => score.Category));
    }

    [Fact]
    public void TamperedSnapshot_IsRejected()
    {
        NaiveBayesClassifier model = Model();
        var memory = new FeedbackMemory<string>(
            RecencyPolicy.None,
            null,
            ContextCodecs.Raw<string>());
        var ranker = new AdaptiveFeedbackRanker<string>(model, memory);

        AdaptiveSnapshot snapshot = ranker.Save(Now, "model-v1");

        using var stream = new MemoryStream();
        AdaptiveSnapshotSerializer.Save(snapshot, stream);
        byte[] bytes = stream.ToArray();
        bytes[^1] ^= 0xFF;

        using var tampered = new MemoryStream(bytes);

        Assert.Throws<InvalidDataException>(() => AdaptiveSnapshotSerializer.Load(tampered));
    }
}
