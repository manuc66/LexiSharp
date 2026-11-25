using LexiSharp.Adaptive;
using LexiSharp.Classification;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class AdaptiveBenchmarkTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AdaptiveMemory_RecoversFromAContextPreferenceDrift()
    {
        NaiveBayesClassifier model = AmbiguousModel();
        FeedbackEvent<string>[] events = BuildLog(model);

        var staticPolicy = new StaticPolicy(model);

        var memory = new FeedbackMemory<string>(
            RecencyPolicy.None,
            new FeedbackMemoryOptions { VetoAfter = 10 },
            ContextCodecs.Raw<string>());
        var adaptive = new AdaptiveFeedbackRanker<string>(model, memory);

        var options = new AdaptiveEvaluationOptions
        {
            BootstrapResamples = 0,
            RecoveryHorizon = 10,
            RepetitionHorizon = 10,
        };

        AdaptiveReport staticReport = AdaptiveEvaluator.Replay(events, staticPolicy, options);
        AdaptiveReport adaptiveReport = AdaptiveEvaluator.Replay(events, adaptive, options);

        Assert.Equal(100, adaptiveReport.Labeled);
        Assert.Equal(100, staticReport.Labeled);
        Assert.True(
            adaptiveReport.LabeledAccuracy > staticReport.LabeledAccuracy,
            $"adaptive {adaptiveReport.LabeledAccuracy} should beat static {staticReport.LabeledAccuracy}");
        Assert.True(adaptiveReport.Recovery.Recovered > 0);
    }

    [Fact]
    public void Replay_IsDeterministic()
    {
        NaiveBayesClassifier model = AmbiguousModel();
        FeedbackEvent<string>[] events = BuildLog(model);

        AdaptiveReport First()
        {
            var memory = new FeedbackMemory<string>(
                RecencyPolicy.None,
                null,
                ContextCodecs.Raw<string>());
            var ranker = new AdaptiveFeedbackRanker<string>(model, memory);

            return AdaptiveEvaluator.Replay(
                events,
                ranker,
                new AdaptiveEvaluationOptions { BootstrapResamples = 0 });
        }

        AdaptiveReport first = First();
        AdaptiveReport second = First();

        Assert.Equal(first.LabeledAccuracy, second.LabeledAccuracy, precision: 12);
        Assert.Equal(first.OrderHash, second.OrderHash);
        Assert.Equal(first.Recovery.Recovered, second.Recovery.Recovered);
    }

    private static NaiveBayesClassifier AmbiguousModel()
    {
        var model = new NaiveBayesClassifier();
        model.Train(
        [
            new SearchDocument("a", "gamma", Category: "A"),
            new SearchDocument("b", "gamma", Category: "B"),
        ]);

        return model;
    }

    private static FeedbackEvent<string>[] BuildLog(ITextClassifier model)
    {
        var events = new List<FeedbackEvent<string>>(100);
        ClassificationResult[] offered = model.Predict("gamma", limit: int.MaxValue)
            .ToArray();
        string top = offered[0].Category;

        for (int i = 0; i < 100; i++)
        {
            string truth = i < 50 ? "A" : "B";
            FeedbackOutcome outcome = string.Equals(top, truth, StringComparison.Ordinal)
                ? FeedbackOutcome.Accepted
                : FeedbackOutcome.Corrected;

            events.Add(new FeedbackEvent<string>(
                "gamma",
                top,
                truth,
                "ctx",
                Now.AddMinutes(i),
                outcome,
                Offered: offered));
        }

        return events.ToArray();
    }

    private sealed class StaticPolicy(ITextClassifier model) : IAdaptiveFeedbackPolicy<string>
    {
        public AdaptiveRanking Predict(string text, string context, DateTimeOffset now, int limit = 6)
        {
            var scores = new List<AdaptiveScore>();

            foreach (ClassificationResult result in model.Predict(text, limit: int.MaxValue))
            {
                scores.Add(new AdaptiveScore(
                    result.Category,
                    result.Probability,
                    0,
                    0,
                    1,
                    result.Probability,
                    false,
                    false));
            }

            return new AdaptiveRanking(scores, scores, null, false, false, 0, now);
        }

        public void Observe(FeedbackEvent<string> feedback)
        {
        }
    }
}
