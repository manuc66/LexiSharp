using System.Globalization;
using System.Text.Json;

using LexiSharp.Adaptive;
using LexiSharp.Classification;
using LexiSharp.Core;

namespace LexiSharp.Eval;

/// <summary>One variant's figures in an adaptive-cost run.</summary>
internal sealed record AdaptiveCostVariant(
    string Name,
    int Events,
    int Accepted,
    int Asked,
    int Abstained,
    double Precision,
    double CostPer100,
    int Recovered,
    double RecoveryMean);

/// <summary>The pinned outcome of an adaptive-cost run.</summary>
internal sealed record AdaptiveCostPin(
    int Seed,
    int Events,
    int DriftAt,
    double AskCost,
    double AbstainCost,
    string OrderHash,
    IReadOnlyList<AdaptiveCostVariant> Variants);

/// <summary>
/// The <c>adaptive-cost</c> verb: a synthetic context-preference drift replayed through a static
/// model, the feedback memory and the cost-sensitive decision layer.
/// </summary>
/// <remarks>
/// The corpus and the log are synthetic and deterministic: an ambiguous two-category model, a
/// preference that flips at <c>--drift-at</c>, and a user who accepts a correct offer or corrects a
/// wrong one. The numbers isolate the mechanism; they are not a production claim.
/// </remarks>
internal static class AdaptiveCostCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static int Run(string[] args)
    {
        int events = 100;
        int driftAt = 50;
        int seed = 7;
        double askCost = 0.1;
        double abstainCost = 2;
        string? write = null;
        string? verify = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--events" when i + 1 < args.Length
                    && CommandParsing.TryPositiveInt(args[i + 1], out int parsedEvents):
                    events = parsedEvents;
                    i++;
                    break;

                case "--drift-at" when i + 1 < args.Length
                    && CommandParsing.TryNonNegativeInt(args[i + 1], out int parsedDrift):
                    driftAt = parsedDrift;
                    i++;
                    break;

                case "--seed" when i + 1 < args.Length
                    && CommandParsing.TryNonNegativeInt(args[i + 1], out int parsedSeed):
                    seed = parsedSeed;
                    i++;
                    break;

                case "--ask-cost" when i + 1 < args.Length
                    && CommandParsing.TryFiniteDouble(args[i + 1], out double parsedAsk):
                    askCost = parsedAsk;
                    i++;
                    break;

                case "--abstain-cost" when i + 1 < args.Length
                    && CommandParsing.TryFiniteDouble(args[i + 1], out double parsedAbstain):
                    abstainCost = parsedAbstain;
                    i++;
                    break;

                case "--write" when i + 1 < args.Length:
                    write = args[i + 1];
                    i++;
                    break;

                case "--verify" when i + 1 < args.Length:
                    verify = args[i + 1];
                    i++;
                    break;

                default:
                    Console.Error.WriteLine($"adaptive-cost: unknown or incomplete argument '{args[i]}'.");
                    return 2;
            }
        }

        if (events < 2 || driftAt <= 0 || driftAt >= events)
        {
            Console.Error.WriteLine("adaptive-cost: require 2 <= events and 0 < drift-at < events.");
            return 2;
        }

        if (askCost < 0 || abstainCost < 0)
        {
            Console.Error.WriteLine("adaptive-cost: costs must be non-negative.");
            return 2;
        }

        AdaptiveCostPin pin = AdaptiveCostScenario.Run(events, driftAt, askCost, abstainCost, seed);

        Print(pin);

        if (write is not null)
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(write));

            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(write, JsonSerializer.Serialize(pin, JsonOptions) + Environment.NewLine);
            Console.Error.WriteLine($"wrote {write}");
        }

        if (verify is not null)
        {
            return Verify(pin, verify);
        }

        return 0;
    }

    private static void Print(AdaptiveCostPin pin)
    {
        Console.WriteLine(
            $"adaptive-cost: events={pin.Events} drift-at={pin.DriftAt} " +
            $"ask={pin.AskCost} abstain={pin.AbstainCost} seed={pin.Seed}");
        Console.WriteLine($"order-hash {pin.OrderHash}");
        Console.WriteLine();
        Console.WriteLine("variant      accepted  asked  abstained  precision  cost/100  recovered  mean-recovery");

        foreach (AdaptiveCostVariant variant in pin.Variants)
        {
            Console.WriteLine(
                $"{variant.Name,-12} {variant.Accepted,8} {variant.Asked,6} {variant.Abstained,10} " +
                $"{variant.Precision,10:0.0000} {variant.CostPer100,9:0.0000} " +
                $"{variant.Recovered,9} {variant.RecoveryMean,13:0.0000}");
        }
    }

    private static int Verify(AdaptiveCostPin actual, string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"adaptive-cost: pin not found: {path}");
            return 2;
        }

        AdaptiveCostPin? expected = JsonSerializer.Deserialize<AdaptiveCostPin>(
            File.ReadAllText(path),
            JsonOptions);

        if (expected is null)
        {
            Console.Error.WriteLine("adaptive-cost: the pin is empty.");
            return 2;
        }

        var problems = new List<string>();

        if (expected.Seed != actual.Seed
            || expected.Events != actual.Events
            || expected.DriftAt != actual.DriftAt
            || Math.Abs(expected.AskCost - actual.AskCost) > 1e-12
            || Math.Abs(expected.AbstainCost - actual.AbstainCost) > 1e-12)
        {
            problems.Add("the scenario parameters moved");
        }

        if (!string.Equals(expected.OrderHash, actual.OrderHash, StringComparison.Ordinal))
        {
            problems.Add($"order hash {expected.OrderHash} -> {actual.OrderHash}");
        }

        if (expected.Variants.Count != actual.Variants.Count)
        {
            problems.Add($"variant count {expected.Variants.Count} -> {actual.Variants.Count}");
        }
        else
        {
            for (int i = 0; i < expected.Variants.Count; i++)
            {
                AdaptiveCostVariant left = expected.Variants[i];
                AdaptiveCostVariant right = actual.Variants[i];

                if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal)
                    || left.Events != right.Events
                    || left.Accepted != right.Accepted
                    || left.Asked != right.Asked
                    || left.Abstained != right.Abstained
                    || left.Recovered != right.Recovered
                    || Math.Abs(left.Precision - right.Precision) > 1e-9
                    || Math.Abs(left.CostPer100 - right.CostPer100) > 1e-9
                    || Math.Abs(left.RecoveryMean - right.RecoveryMean) > 1e-9)
                {
                    problems.Add($"variant '{left.Name}' moved");
                }
            }
        }

        if (problems.Count == 0)
        {
            Console.Error.WriteLine("adaptive-cost: pin verified.");
            return 0;
        }

        foreach (string problem in problems)
        {
            Console.Error.WriteLine($"adaptive-cost: {problem}");
        }

        return 1;
    }
}

/// <summary>The deterministic synthetic scenario behind <c>adaptive-cost</c>.</summary>
internal static class AdaptiveCostScenario
{
    public static AdaptiveCostPin Run(
        int events,
        int driftAt,
        double askCost,
        double abstainCost,
        int seed)
    {
        NaiveBayesClassifier model = Model();
        FeedbackEvent<string>[] log = BuildLog(model, events, driftAt);

        var options = new AdaptiveEvaluationOptions
        {
            BootstrapResamples = 0,
            RecoveryHorizon = 10,
            RepetitionHorizon = 10,
        };

        AdaptiveReport staticReport = AdaptiveEvaluator.Replay(log, new StaticPolicy(model), options);

        var memory = new FeedbackMemory<string>(
            RecencyPolicy.None,
            new FeedbackMemoryOptions { VetoAfter = 10 },
            ContextCodecs.Raw<string>());
        var adaptive = new AdaptiveFeedbackRanker<string>(model, memory);
        AdaptiveReport adaptiveReport = AdaptiveEvaluator.Replay(log, adaptive, options);

        DecisionFigures decision = RunDecision(log, model, askCost, abstainCost);

        var variants = new List<AdaptiveCostVariant>
        {
            new(
                "static",
                staticReport.Events,
                staticReport.Labeled,
                0,
                0,
                staticReport.LabeledAccuracy,
                (1 - staticReport.LabeledAccuracy) * 100,
                staticReport.Recovery.Recovered,
                staticReport.Recovery.Mean),
            new(
                "adaptive",
                adaptiveReport.Events,
                adaptiveReport.Labeled,
                0,
                0,
                adaptiveReport.LabeledAccuracy,
                (1 - adaptiveReport.LabeledAccuracy) * 100,
                adaptiveReport.Recovery.Recovered,
                adaptiveReport.Recovery.Mean),
            new(
                "decision",
                log.Length,
                decision.Accepted,
                decision.Asked,
                decision.Abstained,
                decision.Precision,
                decision.CostPer100,
                0,
                0),
        };

        return new AdaptiveCostPin(
            seed,
            events,
            driftAt,
            askCost,
            abstainCost,
            adaptiveReport.OrderHash,
            variants);
    }

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

    private static FeedbackEvent<string>[] BuildLog(
        ITextClassifier model,
        int events,
        int driftAt)
    {
        ClassificationResult[] offered = model.Predict("gamma", limit: int.MaxValue)
            .ToArray();
        string top = offered[0].Category;
        var log = new List<FeedbackEvent<string>>(events);

        for (int i = 0; i < events; i++)
        {
            string truth = i < driftAt ? "A" : "B";
            FeedbackOutcome outcome = string.Equals(top, truth, StringComparison.Ordinal)
                ? FeedbackOutcome.Accepted
                : FeedbackOutcome.Corrected;

            log.Add(new FeedbackEvent<string>(
                "gamma",
                top,
                truth,
                "ctx",
                DateTimeOffset.UnixEpoch.AddMinutes(i),
                outcome,
                Offered: offered));
        }

        return log.ToArray();
    }

    private static DecisionFigures RunDecision(
        IReadOnlyList<FeedbackEvent<string>> log,
        ITextClassifier model,
        double askCost,
        double abstainCost)
    {
        var memory = new FeedbackMemory<string>(
            RecencyPolicy.None,
            new FeedbackMemoryOptions { VetoAfter = 10 },
            ContextCodecs.Raw<string>());
        var ranker = new AdaptiveFeedbackRanker<string>(model, memory);
        var policy = new CostAwareDecisionPolicy();
        var costs = new DecisionCosts
        {
            AskCost = askCost,
            AbstainCost = abstainCost,
            ExploreCost = double.PositiveInfinity,
        };

        int accepted = 0;
        int asked = 0;
        int abstained = 0;
        int correct = 0;
        double totalCost = 0;

        foreach (FeedbackEvent<string> feedback in log)
        {
            AdaptiveRanking ranking = ranker.Predict(
                feedback.Text,
                feedback.Context,
                feedback.At);

            var distribution = new List<ClassificationResult>(ranking.AllScores.Count);

            foreach (AdaptiveScore score in ranking.AllScores)
            {
                distribution.Add(new ClassificationResult(score.Category, score.FinalProbability));
            }

            Decision decision = policy.Decide(distribution, costs);
            string truth = feedback.AcceptedCategory!;

            switch (decision.Kind)
            {
                case DecisionKind.Accept:
                    accepted++;
                    bool hit = string.Equals(decision.Category, truth, StringComparison.Ordinal);
                    correct += hit ? 1 : 0;
                    totalCost += hit ? 0 : 1;
                    break;

                case DecisionKind.Ask:
                    asked++;
                    totalCost += askCost;
                    break;

                case DecisionKind.Abstain:
                    abstained++;
                    totalCost += abstainCost;
                    break;

                case DecisionKind.Explore:
                    totalCost += double.PositiveInfinity;
                    break;
            }

            ranker.Observe(feedback);
        }

        return new DecisionFigures(
            accepted,
            asked,
            abstained,
            accepted > 0 ? correct / (double)accepted : 0,
            totalCost / log.Count * 100);
    }

    private sealed record DecisionFigures(
        int Accepted,
        int Asked,
        int Abstained,
        double Precision,
        double CostPer100);

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
