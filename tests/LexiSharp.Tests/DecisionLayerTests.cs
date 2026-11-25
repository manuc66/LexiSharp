using LexiSharp.Adaptive;
using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class DecisionLayerTests
{
    private static ClassificationResult[] Distribution(params (string Category, double Probability)[] rows) =>
        rows.Select(row => new ClassificationResult(row.Category, row.Probability)).ToArray();

    private static DecisionCosts Costs(
        double ask = 1,
        double abstain = 2,
        double explore = 3,
        IMisclassificationCost? misclassification = null,
        double? voiThreshold = null,
        double residualAfterAsk = 0) =>
        new()
        {
            AskCost = ask,
            AbstainCost = abstain,
            ExploreCost = explore,
            Misclassification = misclassification ?? UniformMisclassificationCost.One,
            ValueOfInformationThreshold = voiThreshold,
            ResidualCostAfterAsk = residualAfterAsk,
        };

    [Fact]
    public void HighConfidence_AcceptsTheTopCategory()
    {
        var policy = new CostAwareDecisionPolicy();

        Decision decision = policy.Decide(
            Distribution(("A", 0.95), ("B", 0.05)),
            Costs());

        Assert.Equal(DecisionKind.Accept, decision.Kind);
        Assert.Equal("A", decision.Category);
        Assert.Equal(0.05, decision.ExpectedCost, precision: 10);
    }

    [Fact]
    public void LowConfidence_AsksWhenTheQuestionIsCheap()
    {
        var policy = new CostAwareDecisionPolicy();

        Decision decision = policy.Decide(
            Distribution(("A", 0.55), ("B", 0.45)),
            Costs(ask: 0.1));

        Assert.Equal(DecisionKind.Ask, decision.Kind);
        Assert.Equal(["A", "B"], decision.Candidates);
        Assert.Null(decision.Category);
    }

    [Fact]
    public void LowConfidence_AbstainsWhenAskingIsDisabled()
    {
        var policy = new CostAwareDecisionPolicy();

        Decision decision = policy.Decide(
            Distribution(("A", 0.55), ("B", 0.45)),
            Costs(ask: double.PositiveInfinity, abstain: 0.2));

        Assert.Equal(DecisionKind.Abstain, decision.Kind);
    }

    [Fact]
    public void CategoryCost_ChangesTheAcceptedCategory()
    {
        var policy = new CostAwareDecisionPolicy();
        var misclassification = new CategoryMisclassificationCost(
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["Urgent"] = 20,
                ["Normal"] = 1,
            });

        Decision decision = policy.Decide(
            Distribution(("Urgent", 0.4), ("Normal", 0.6)),
            Costs(ask: double.PositiveInfinity, abstain: double.PositiveInfinity, misclassification: misclassification));

        Assert.Equal(DecisionKind.Accept, decision.Kind);
        Assert.Equal("Urgent", decision.Category);
    }

    [Fact]
    public void AskCandidates_UseTheMassPrefix()
    {
        var policy = new CostAwareDecisionPolicy();

        Decision decision = policy.Decide(
            Distribution(("A", 0.8), ("B", 0.15), ("C", 0.05)),
            Costs(ask: 0.01));

        Assert.Equal(DecisionKind.Ask, decision.Kind);
        Assert.Equal(["A", "B"], decision.Candidates);
    }

    [Fact]
    public void ValueOfInformationThreshold_BlocksAsk()
    {
        var policy = new CostAwareDecisionPolicy();

        Decision decision = policy.Decide(
            Distribution(("A", 0.55), ("B", 0.45)),
            Costs(ask: 0.1, voiThreshold: 0.5));

        Assert.Equal(DecisionKind.Accept, decision.Kind);
        Assert.Equal(0.35, decision.ValueOfInformation!.Value, precision: 10);
    }

    [Fact]
    public void InfiniteAsk_IsNeverSelected()
    {
        var policy = new CostAwareDecisionPolicy();

        Decision decision = policy.Decide(
            Distribution(("A", 0.55), ("B", 0.45)),
            Costs(ask: double.PositiveInfinity));

        Assert.NotEqual(DecisionKind.Ask, decision.Kind);
        Assert.Contains(decision.Options, option => option.Kind == DecisionKind.Ask);
    }

    [Fact]
    public void Explore_RequiresAnExplicitPolicy()
    {
        var policy = new CostAwareDecisionPolicy();

        Decision decision = policy.Decide(
            Distribution(("A", 0.9), ("B", 0.1)),
            Costs(explore: 0.01));

        Assert.NotEqual(DecisionKind.Explore, decision.Kind);
    }

    [Fact]
    public void Explore_UsesThePolicyChoice()
    {
        var policy = new CostAwareDecisionPolicy(new DecisionOptions
        {
            AllowExplore = true,
            Exploration = new FixedExploration("B"),
        });

        Decision decision = policy.Decide(
            Distribution(("A", 0.6), ("B", 0.4)),
            Costs(ask: double.PositiveInfinity, abstain: double.PositiveInfinity, explore: 0.01));

        Assert.Equal(DecisionKind.Explore, decision.Kind);
        Assert.Equal("B", decision.Category);
        Assert.Equal(0.01, decision.ExpectedCost, precision: 10);
    }

    [Fact]
    public void EmptyDistribution_Abstains()
    {
        var policy = new CostAwareDecisionPolicy();

        Decision decision = policy.Decide([], Costs());

        Assert.Equal(DecisionKind.Abstain, decision.Kind);
    }

    [Fact]
    public void NonNormalizedDistribution_Throws()
    {
        var policy = new CostAwareDecisionPolicy();

        Assert.Throws<ArgumentException>(() =>
            policy.Decide(Distribution(("A", 0.6), ("B", 0.6)), Costs()));
    }

    [Fact]
    public void DuplicateCategories_Throw()
    {
        var policy = new CostAwareDecisionPolicy();

        Assert.Throws<ArgumentException>(() =>
            policy.Decide(Distribution(("A", 0.5), ("A", 0.5)), Costs()));
    }

    [Fact]
    public void EqualCosts_PreferAccept()
    {
        var policy = new CostAwareDecisionPolicy();

        Decision decision = policy.Decide(
            Distribution(("A", 0.5), ("B", 0.5)),
            Costs(ask: 0.5, abstain: 2));

        Assert.Equal(DecisionKind.Accept, decision.Kind);
    }

    [Fact]
    public void InvalidOptions_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CostAwareDecisionPolicy(new DecisionOptions { AskCandidateMass = 0 }));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CostAwareDecisionPolicy(new DecisionOptions { MinAskCandidates = 1 }));
    }

    [Fact]
    public void InvalidCost_IsRejected()
    {
        var policy = new CostAwareDecisionPolicy();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            policy.Decide(Distribution(("A", 0.9), ("B", 0.1)), Costs(ask: -1)));
    }

    private sealed class FixedExploration(string category) : IExplorationPolicy
    {
        public string? Choose(IReadOnlyList<ClassificationResult> calibrated, DecisionCosts costs) =>
            category;
    }
}
