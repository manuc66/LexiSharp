using LexiSharp.Adaptive;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class PreferenceLearnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    private static ClassificationResult[] Candidates(params (string Category, double Probability)[] rows) =>
        rows.Select(row => new ClassificationResult(row.Category, row.Probability)).ToArray();

    private static PreferenceEvent Event(
        string text,
        string[] offered,
        string chosen,
        double? propensity = null)
    {
        double probability = 1.0 / offered.Length;

        return new PreferenceEvent(
            text,
            "ctx",
            offered.Select(category => new ClassificationResult(category, probability)).ToArray(),
            chosen,
            Now,
            propensity);
    }

    [Fact]
    public void WithoutTraining_ReRankIsTheIdentity()
    {
        var learner = new PreferenceLearner();
        ClassificationResult[] candidates = Candidates(("A", 0.6), ("B", 0.4));

        IReadOnlyList<ClassificationResult> result = learner.ReRank("alpha", "ctx", candidates);

        Assert.Equal(candidates.Select(candidate => candidate.Category), result.Select(candidate => candidate.Category));
        Assert.Equal(candidates.Select(candidate => candidate.Probability), result.Select(candidate => candidate.Probability));
    }

    [Fact]
    public void TrainingOnB_PrefersB()
    {
        var learner = new PreferenceLearner();

        learner.Train(Enumerable.Range(0, 20).Select(_ => Event("alpha", ["A", "B"], "B")));

        IReadOnlyList<ClassificationResult> result = learner.ReRank(
            "alpha",
            "ctx",
            Candidates(("A", 0.6), ("B", 0.4)));

        Assert.Equal("B", result[0].Category);
    }

    [Fact]
    public void ReRank_NormalizesAndKeepsTheCandidateSet()
    {
        var learner = new PreferenceLearner();
        learner.Train(Enumerable.Range(0, 5).Select(_ => Event("alpha", ["A", "B"], "B")));

        IReadOnlyList<ClassificationResult> result = learner.ReRank(
            "beta",
            "ctx",
            Candidates(("A", 0.7), ("B", 0.3)));

        Assert.Equal(2, result.Count);
        Assert.Equal(1.0, result.Sum(candidate => candidate.Probability), precision: 10);
        Assert.Equal(["A", "B"], result.Select(candidate => candidate.Category).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Training_IsDeterministic()
    {
        PreferenceEvent[] events = Enumerable.Range(0, 12)
            .Select(i => Event(i % 2 == 0 ? "alpha" : "beta", ["A", "B"], i % 2 == 0 ? "B" : "A"))
            .ToArray();

        var first = new PreferenceLearner();
        var second = new PreferenceLearner();
        first.Train(events);
        second.Train(events);

        var candidates = Candidates(("A", 0.5), ("B", 0.5));

        Assert.Equal(
            first.ReRank("alpha", "ctx", candidates).Select(candidate => candidate.Category),
            second.ReRank("alpha", "ctx", candidates).Select(candidate => candidate.Category));
    }

    [Fact]
    public void ChosenAbsent_IsRejected()
    {
        var learner = new PreferenceLearner();

        Assert.Throws<ArgumentException>(() =>
            learner.Train([Event("alpha", ["A", "B"], "C")]));
    }

    [Fact]
    public void Snapshot_RoundTrips()
    {
        var learner = new PreferenceLearner();
        learner.Train(Enumerable.Range(0, 10).Select(_ => Event("alpha", ["A", "B"], "B")));

        PreferenceSnapshot snapshot = learner.Save(Now);
        PreferenceLearner loaded = PreferenceLearner.Load(snapshot);

        var candidates = Candidates(("A", 0.6), ("B", 0.4));

        Assert.Equal(
            learner.ReRank("alpha", "ctx", candidates).Select(candidate => candidate.Category),
            loaded.ReRank("alpha", "ctx", candidates).Select(candidate => candidate.Category));
    }

    [Fact]
    public void Evaluator_ReportsAgreementMrrAndIps()
    {
        var policy = new FixedPolicy();

        PreferenceReport report = PreferenceEvaluator.Replay(
        [
            Event("alpha", ["A", "B"], "B", propensity: 0.5),
            Event("beta", ["A", "B"], "A", propensity: 0.25),
        ],
            policy);

        Assert.Equal(2, report.Events);
        Assert.Equal(2, report.WithPropensity);
        Assert.Equal(0.5, report.Top1Agreement, precision: 10);
        Assert.Equal(0.75, report.MeanReciprocalRank, precision: 10);
        Assert.Equal(1.0 / 3.0, report.IpsTop1!.Value, precision: 10);
        Assert.Equal(1.8, report.EffectiveSampleSize, precision: 10);
        Assert.Equal(2, report.ByLoggedRank.Count);
        Assert.Equal(64, report.OrderHash.Length);
    }

    [Fact]
    public void EmptyStream_ReturnsAnEmptyReport()
    {
        PreferenceReport report = PreferenceEvaluator.Replay(
            Array.Empty<PreferenceEvent>(),
            new FixedPolicy());

        Assert.Equal(0, report.Events);
        Assert.Null(report.IpsTop1);
        Assert.Empty(report.OrderHash);
    }

    private sealed class FixedPolicy : IPreferencePolicy
    {
        public IReadOnlyList<ClassificationResult> ReRank(
            string text,
            string contextKey,
            IReadOnlyList<ClassificationResult> candidates) =>
        [
            new("B", 0.8),
            new("A", 0.2),
        ];
    }
}
