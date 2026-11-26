using LexiSharp.Adaptive;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class AdaptiveEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    private static FeedbackEvent<string> Event(
        FeedbackOutcome outcome,
        string? offered,
        string? accepted,
        int minute,
        double? propensity = null,
        string context = "ctx") =>
        new("text", offered, accepted, context, Now.AddMinutes(minute), outcome, propensity);

    private static AdaptiveEvaluationOptions Options() =>
        new() { BootstrapResamples = 0, RecoveryHorizon = 10, RepetitionHorizon = 10 };

    [Fact]
    public void Replay_CountsOutcomesAndScoresLabeledEvents()
    {
        var policy = new ScriptedPolicy("A");

        AdaptiveReport report = AdaptiveEvaluator.Replay(
        [
            Event(FeedbackOutcome.Accepted, "A", "A", 0),
            Event(FeedbackOutcome.Corrected, "A", "B", 1),
            Event(FeedbackOutcome.Accepted, "B", "B", 2),
            Event(FeedbackOutcome.Rejected, "A", null, 3),
            Event(FeedbackOutcome.Ignored, "B", null, 4),
        ],
            policy,
            Options());

        Assert.Equal(5, report.Events);
        Assert.Equal(3, report.Labeled);
        Assert.Equal(2, report.Accepted);
        Assert.Equal(1, report.Corrected);
        Assert.Equal(1, report.Rejected);
        Assert.Equal(1, report.Ignored);
        Assert.Equal(2.0 / 3.0, report.LabeledAccuracy, precision: 10);
        Assert.Equal(0.4, report.AcceptanceRate, precision: 10);
        Assert.Equal(0.6, report.LabeledShare, precision: 10);
        Assert.Equal(1.0 / 7.5, report.Ece, precision: 10);
        Assert.Equal(0.48, report.Brier, precision: 10);
        Assert.Equal(0.68524, report.LogLoss, precision: 4);
        Assert.Null(report.EmpiricalCoverage);
        Assert.Equal(64, report.OrderHash.Length);
    }

    [Fact]
    public void Replay_MeasuresRecoveryAndRepetition()
    {
        var policy = new ScriptedPolicy("A");

        AdaptiveReport report = AdaptiveEvaluator.Replay(
        [
            Event(FeedbackOutcome.Corrected, "A", "B", 0),
            Event(FeedbackOutcome.Accepted, "B", "B", 1),
            Event(FeedbackOutcome.Rejected, "A", null, 2),
        ],
            policy,
            Options());

        Assert.Equal(1, report.Recovery.Episodes);
        Assert.Equal(1, report.Recovery.Recovered);
        Assert.Equal(1, report.Recovery.Mean, precision: 10);
        Assert.Equal(1, report.Repetition.Episodes);
        Assert.Equal(0, report.Repetition.RepeatedOffers);
    }

    [Fact]
    public void Replay_CountsRepeatedBadOffers()
    {
        var policy = new SequencePolicy("A", "A", "A", "B");

        AdaptiveReport report = AdaptiveEvaluator.Replay(
        [
            Event(FeedbackOutcome.Corrected, "A", "B", 0),
            Event(FeedbackOutcome.Rejected, "A", null, 1),
            Event(FeedbackOutcome.Rejected, "A", null, 2),
            Event(FeedbackOutcome.Accepted, "B", "B", 3),
        ],
            policy,
            Options());

        Assert.Equal(1, report.Repetition.Episodes);
        Assert.Equal(2, report.Repetition.RepeatedOffers);
        Assert.Equal(2, report.Repetition.Mean, precision: 10);
    }

    [Fact]
    public void Replay_ComputesIpsAndEss()
    {
        var policy = new ScriptedPolicy("A");

        AdaptiveReport report = AdaptiveEvaluator.Replay(
        [
            Event(FeedbackOutcome.Accepted, "A", "A", 0, propensity: 0.5),
            Event(FeedbackOutcome.Accepted, "A", "A", 1, propensity: 0.25),
        ],
            policy,
            Options());

        Assert.NotNull(report.Importance);
        Assert.Equal(1.0, report.Importance!.Value, precision: 10);
        Assert.Equal(2, report.Importance.Events);
        Assert.True(report.Importance.EffectiveSampleSize > 0);
    }

    [Fact]
    public void EmptyStream_ReturnsAnEmptyReport()
    {
        AdaptiveReport report = AdaptiveEvaluator.Replay(
            Array.Empty<FeedbackEvent<string>>(),
            new ScriptedPolicy("A"),
            Options());

        Assert.Equal(0, report.Events);
        Assert.Equal(0, report.Labeled);
        Assert.Null(report.Importance);
        Assert.Empty(report.OrderHash);
    }

    [Fact]
    public void Replay_CountsOneEpisodePerContextAndBadOffer()
    {
        var policy = new SequencePolicy("A", "A", "A", "B");

        AdaptiveReport report = AdaptiveEvaluator.Replay(
        [
            Event(FeedbackOutcome.Corrected, "A", "B", 0),
            Event(FeedbackOutcome.Corrected, "A", "B", 1),
            Event(FeedbackOutcome.Rejected, "A", null, 2),
            Event(FeedbackOutcome.Accepted, "B", "B", 3),
        ],
            policy,
            Options());

        Assert.Equal(1, report.Repetition.Episodes);
        Assert.Equal(2, report.Repetition.RepeatedOffers);
        Assert.Equal(2, report.Repetition.Mean, precision: 10);
    }

    [Fact]
    public void Replay_HonorsTopKBeyondThePolicyDefault()
    {
        var report = AdaptiveEvaluator.Replay(
        [
            Event(FeedbackOutcome.Corrected, "C1", "C8", 0),
        ],
            new WidePolicy(),
            new AdaptiveEvaluationOptions { TopK = 10, BootstrapResamples = 0 });

        Assert.Equal(1.0, report.LabeledAccuracy, precision: 10);
    }

    private sealed class WidePolicy : IAdaptiveFeedbackPolicy<string>
    {
        public AdaptiveRanking Predict(string text, string context, DateTimeOffset now, int limit = 6)
        {
            var results = new List<AdaptiveScore>();

            for (int i = 0; i < Math.Min(10, Math.Max(1, limit)); i++)
            {
                results.Add(new AdaptiveScore($"C{i + 1}", 0.1, 0, 0, 1, 0.1, false, false));
            }

            return new AdaptiveRanking(results, results, null, false, false, 0, now);
        }

        public void Observe(FeedbackEvent<string> feedback)
        {
        }
    }

    private sealed class SequencePolicy(params string[] tops) : IAdaptiveFeedbackPolicy<string>
    {
        private int _index;

        public AdaptiveRanking Predict(string text, string context, DateTimeOffset now, int limit = 6)
        {
            string top = tops[Math.Min(_index, tops.Length - 1)];
            string other = top == "A" ? "B" : "A";

            _index++;

            var results = new[]
            {
                new AdaptiveScore(top, 0.8, 0, 0, 1, 0.8, false, false),
                new AdaptiveScore(other, 0.2, 0, 0, 1, 0.2, false, false),
            };

            return new AdaptiveRanking(results, results, null, false, false, 0, now);
        }

        public void Observe(FeedbackEvent<string> feedback)
        {
        }
    }

    private sealed class ScriptedPolicy(string initial) : IAdaptiveFeedbackPolicy<string>
    {
        private readonly Dictionary<string, string> _top = new(StringComparer.Ordinal);

        public AdaptiveRanking Predict(string text, string context, DateTimeOffset now, int limit = 6)
        {
            string top = _top.TryGetValue(context, out string? chosen) ? chosen : initial;
            string other = top == "A" ? "B" : "A";

            var results = new[]
            {
                new AdaptiveScore(top, 0.8, 0, 0, 1, 0.8, false, false),
                new AdaptiveScore(other, 0.2, 0, 0, 1, 0.2, false, false),
            };

            return new AdaptiveRanking(results, results, null, false, false, 0, now);
        }

        public void Observe(FeedbackEvent<string> feedback)
        {
            if (feedback.Outcome == FeedbackOutcome.Corrected && feedback.AcceptedCategory is not null)
            {
                _top[feedback.Context] = feedback.AcceptedCategory;
            }
        }
    }
}
