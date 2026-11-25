namespace LexiSharp.Adaptive;

/// <summary>The cost of predicting one category when the truth is another.</summary>
public interface IMisclassificationCost
{
    /// <summary>Returns the cost; it must be <c>0</c> when the two categories are equal.</summary>
    /// <param name="actual">The true category.</param>
    /// <param name="predicted">The category the policy would accept.</param>
    double Cost(string actual, string predicted);
}

/// <summary>A single cost for every wrong prediction.</summary>
public sealed class UniformMisclassificationCost : IMisclassificationCost
{
    /// <summary>A uniform cost of <c>1</c>.</summary>
    public static UniformMisclassificationCost One { get; } = new(1);

    private readonly double _cost;

    /// <summary>Creates a uniform cost.</summary>
    /// <param name="cost">The cost of any wrong prediction. Must be finite and non-negative.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cost"/> is invalid.</exception>
    public UniformMisclassificationCost(double cost)
    {
        if (!double.IsFinite(cost) || cost < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cost), cost, "Must be finite and non-negative.");
        }

        _cost = cost;
    }

    /// <inheritdoc />
    public double Cost(string actual, string predicted) =>
        string.Equals(actual, predicted, StringComparison.Ordinal) ? 0 : _cost;
}

/// <summary>A cost keyed by the true category, with one fallback for the others.</summary>
public sealed class CategoryMisclassificationCost : IMisclassificationCost
{
    private readonly IReadOnlyDictionary<string, double> _costs;
    private readonly double _fallback;

    /// <summary>Creates a category-keyed cost.</summary>
    /// <param name="costs">The cost of getting each true category wrong. Must be finite and non-negative.</param>
    /// <param name="fallback">The cost for a category absent from <paramref name="costs"/>. Default: <c>1</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="costs"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A cost is invalid.</exception>
    public CategoryMisclassificationCost(
        IReadOnlyDictionary<string, double> costs,
        double fallback = 1.0)
    {
        ArgumentNullException.ThrowIfNull(costs);

        if (!double.IsFinite(fallback) || fallback < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(fallback), fallback, "Must be finite and non-negative.");
        }

        var copy = new Dictionary<string, double>(costs.Count, StringComparer.Ordinal);

        foreach ((string category, double cost) in costs)
        {
            if (!double.IsFinite(cost) || cost < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(costs), cost, "Costs must be finite and non-negative.");
            }

            copy[category] = cost;
        }

        _costs = copy;
        _fallback = fallback;
    }

    /// <inheritdoc />
    public double Cost(string actual, string predicted)
    {
        if (string.Equals(actual, predicted, StringComparison.Ordinal))
        {
            return 0;
        }

        return _costs.TryGetValue(actual, out double cost) ? cost : _fallback;
    }
}

/// <summary>The costs the decision layer minimizes.</summary>
public sealed record DecisionCosts
{
    /// <summary>The cost of asking a question; <see cref="double.PositiveInfinity"/> disables it. Default: <c>1</c>.</summary>
    public double AskCost { get; init; } = 1.0;

    /// <summary>The cost of abstaining; <see cref="double.PositiveInfinity"/> disables it. Default: <c>2</c>.</summary>
    public double AbstainCost { get; init; } = 2.0;

    /// <summary>The expected cost of an exploration step; <see cref="double.PositiveInfinity"/> disables it. Default: <c>3</c>.</summary>
    public double ExploreCost { get; init; } = 3.0;

    /// <summary>An extra cost after a question, for an imperfect answer. Default: <c>0</c>.</summary>
    public double ResidualCostAfterAsk { get; init; }

    /// <summary>The cost matrix; the default charges <c>1</c> for every wrong prediction.</summary>
    public IMisclassificationCost Misclassification { get; init; } = UniformMisclassificationCost.One;

    /// <summary>Minimum value of information for asking; null asks whenever it is cheaper. Default: <c>null</c>.</summary>
    public double? ValueOfInformationThreshold { get; init; }
}

/// <summary>The action the decision layer recommends.</summary>
public enum DecisionKind
{
    /// <summary>Accept a category.</summary>
    Accept,

    /// <summary>Ask the user to disambiguate a set of candidates.</summary>
    Ask,

    /// <summary>Do not offer an answer.</summary>
    Abstain,

    /// <summary>Try a category to learn from the response.</summary>
    Explore,
}

/// <summary>One considered action and its expected cost.</summary>
/// <param name="Kind">The action.</param>
/// <param name="Category">The accepted or explored category; null otherwise.</param>
/// <param name="Candidates">The set to ask about; empty otherwise.</param>
/// <param name="ExpectedCost">The expected cost; <see cref="double.PositiveInfinity"/> means disabled.</param>
/// <param name="ValueOfInformation">The value of asking, when it was evaluated.</param>
public sealed record DecisionOption(
    DecisionKind Kind,
    string? Category,
    IReadOnlyList<string> Candidates,
    double ExpectedCost,
    double? ValueOfInformation);

/// <summary>The recommended action and the options that produced it.</summary>
/// <param name="Kind">The chosen action.</param>
/// <param name="Category">The accepted or explored category; null otherwise.</param>
/// <param name="Candidates">The set to ask about; empty otherwise.</param>
/// <param name="ExpectedCost">The chosen action's expected cost.</param>
/// <param name="ValueOfInformation">The value of asking, when it was evaluated.</param>
/// <param name="Options">Every considered action, in Accept, Ask, Abstain, Explore order.</param>
public sealed record Decision(
    DecisionKind Kind,
    string? Category,
    IReadOnlyList<string> Candidates,
    double ExpectedCost,
    double? ValueOfInformation,
    IReadOnlyList<DecisionOption> Options);

/// <summary>Chooses a category to try when the caller allows exploration.</summary>
public interface IExplorationPolicy
{
    /// <summary>Returns the category to try, or null to skip exploration.</summary>
    /// <param name="calibrated">The calibrated distribution.</param>
    /// <param name="costs">The costs in force.</param>
    string? Choose(IReadOnlyList<Core.ClassificationResult> calibrated, DecisionCosts costs);
}

/// <summary>Options of the cost-aware decision policy.</summary>
public sealed record DecisionOptions
{
    /// <summary>Mass the ask set must reach, in <c>(0, 1]</c>. Default: <c>0.9</c>.</summary>
    public double AskCandidateMass { get; init; } = 0.9;

    /// <summary>Minimum candidates in an ask; at least <c>2</c>. Default: <c>2</c>.</summary>
    public int MinAskCandidates { get; init; } = 2;

    /// <summary>Maximum candidates in an ask, at least <see cref="MinAskCandidates"/>. Default: <c>3</c>.</summary>
    public int MaxAskCandidates { get; init; } = 3;

    /// <summary>Whether <see cref="Exploration"/> may return an action. Default: <c>false</c>.</summary>
    public bool AllowExplore { get; init; }

    /// <summary>The exploration policy; null disables exploration even when <see cref="AllowExplore"/> is true.</summary>
    public IExplorationPolicy? Exploration { get; init; }
}

/// <summary>A policy that turns a calibrated distribution and costs into an action.</summary>
public interface IDecisionPolicy
{
    /// <summary>Chooses the action with the lowest expected cost.</summary>
    /// <param name="calibrated">The calibrated distribution; its probabilities must sum to 1.</param>
    /// <param name="costs">The costs to minimize.</param>
    Decision Decide(IReadOnlyList<Core.ClassificationResult> calibrated, DecisionCosts costs);
}

/// <summary>
/// The default cost-sensitive policy: accepts the category with the lowest expected misclassification
/// cost, asks when the value of information pays for the question, abstains when deferring is cheaper,
/// and explores only when the caller supplies an <see cref="IExplorationPolicy"/>.
/// </summary>
public sealed class CostAwareDecisionPolicy : IDecisionPolicy
{
    private readonly DecisionOptions _options;

    /// <summary>Creates the policy.</summary>
    /// <param name="options">The ask set and exploration settings; null uses the defaults.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its contract.</exception>
    public CostAwareDecisionPolicy(DecisionOptions? options = null)
    {
        options ??= new DecisionOptions();
        ValidateOptions(options);
        _options = options;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="calibrated"/> or <paramref name="costs"/> is null.</exception>
    /// <exception cref="ArgumentException">The distribution is not normalized or has duplicate categories.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A cost is negative, NaN or malformed.</exception>
    public Decision Decide(
        IReadOnlyList<Core.ClassificationResult> calibrated,
        DecisionCosts costs)
    {
        ArgumentNullException.ThrowIfNull(calibrated);
        ArgumentNullException.ThrowIfNull(costs);

        ValidateCosts(costs);

        if (calibrated.Count == 0)
        {
            return AbstainOnly(costs);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        double sum = 0;

        foreach (Core.ClassificationResult result in calibrated)
        {
            ArgumentNullException.ThrowIfNull(result);

            if (!double.IsFinite(result.Probability) || result.Probability is < 0 or > 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(calibrated),
                    result.Probability,
                    "Probabilities must be in [0, 1].");
            }

            if (!seen.Add(result.Category))
            {
                throw new ArgumentException(
                    $"The distribution contains '{result.Category}' more than once.",
                    nameof(calibrated));
            }

            sum += result.Probability;
        }

        if (Math.Abs(sum - 1.0) > 1e-6)
        {
            throw new ArgumentException(
                $"The distribution must sum to 1; it sums to {sum}.",
                nameof(calibrated));
        }

        (string bestCategory, double bestAccept) = BestAccept(calibrated, costs);

        double askCost = double.PositiveInfinity;
        double? askVoi = null;
        IReadOnlyList<string> askCandidates = Array.Empty<string>();

        if (double.IsFinite(costs.AskCost) && calibrated.Count >= _options.MinAskCandidates)
        {
            askCandidates = AskCandidates(calibrated);

            if (askCandidates.Count >= _options.MinAskCandidates)
            {
                double candidateCost = costs.AskCost + costs.ResidualCostAfterAsk;

                if (double.IsFinite(candidateCost))
                {
                    askVoi = Math.Min(bestAccept, costs.AbstainCost) - candidateCost;

                    bool aboveThreshold = costs.ValueOfInformationThreshold is not { } threshold
                        || askVoi >= threshold - 1e-12;

                    if (aboveThreshold)
                    {
                        askCost = candidateCost;
                    }
                }
            }
        }

        string? exploreCategory = null;
        double exploreCost = double.PositiveInfinity;

        if (_options.AllowExplore
            && _options.Exploration is { } exploration
            && double.IsFinite(costs.ExploreCost))
        {
            string? candidate = exploration.Choose(calibrated, costs);

            if (candidate is not null)
            {
                if (!seen.Contains(candidate))
                {
                    throw new InvalidOperationException(
                        $"The exploration policy returned '{candidate}', which is absent from the distribution.");
                }

                exploreCategory = candidate;
                exploreCost = costs.ExploreCost;
            }
        }

        var options = new List<DecisionOption>(4)
        {
            new(DecisionKind.Accept, bestCategory, Array.Empty<string>(), bestAccept, null),
            new(DecisionKind.Ask, null, askCandidates, askCost, askVoi),
            new(DecisionKind.Abstain, null, Array.Empty<string>(), costs.AbstainCost, null),
            new(DecisionKind.Explore, exploreCategory, Array.Empty<string>(), exploreCost, null),
        };

        DecisionOption chosen = options[0];

        foreach (DecisionOption option in options.Skip(1))
        {
            if (option.ExpectedCost < chosen.ExpectedCost - 1e-12)
            {
                chosen = option;
            }
        }

        return new Decision(
            chosen.Kind,
            chosen.Category,
            chosen.Candidates,
            chosen.ExpectedCost,
            askVoi,
            options);
    }

    private static (string Category, double Cost) BestAccept(
        IReadOnlyList<Core.ClassificationResult> calibrated,
        DecisionCosts costs)
    {
        string? bestCategory = null;
        double bestCost = double.PositiveInfinity;
        double bestProbability = -1;

        foreach (Core.ClassificationResult candidate in calibrated)
        {
            double expected = 0;

            foreach (Core.ClassificationResult actual in calibrated)
            {
                if (string.Equals(actual.Category, candidate.Category, StringComparison.Ordinal))
                {
                    continue;
                }

                double cost = costs.Misclassification.Cost(actual.Category, candidate.Category);

                if (!double.IsFinite(cost) || cost < 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(costs),
                        cost,
                        "Misclassification costs must be finite and non-negative.");
                }

                expected += actual.Probability * cost;
            }

            bool better = expected < bestCost - 1e-12
                || (Math.Abs(expected - bestCost) <= 1e-12
                    && (candidate.Probability > bestProbability + 1e-12
                        || (Math.Abs(candidate.Probability - bestProbability) <= 1e-12
                            && (bestCategory is null
                                || string.CompareOrdinal(candidate.Category, bestCategory) < 0))));

            if (better)
            {
                bestCategory = candidate.Category;
                bestCost = expected;
                bestProbability = candidate.Probability;
            }
        }

        return (bestCategory!, bestCost);
    }

    private IReadOnlyList<string> AskCandidates(IReadOnlyList<Core.ClassificationResult> calibrated)
    {
        var sorted = calibrated
            .OrderByDescending(result => result.Probability)
            .ThenBy(result => result.Category, StringComparer.Ordinal)
            .ToList();

        var candidates = new List<string>(_options.MaxAskCandidates);
        double mass = 0;

        while (candidates.Count < sorted.Count
               && (mass < _options.AskCandidateMass
                   || candidates.Count < _options.MinAskCandidates))
        {
            mass += sorted[candidates.Count].Probability;
            candidates.Add(sorted[candidates.Count].Category);
        }

        if (candidates.Count > _options.MaxAskCandidates)
        {
            candidates.RemoveRange(
                _options.MaxAskCandidates,
                candidates.Count - _options.MaxAskCandidates);
        }

        return candidates;
    }

    private static Decision AbstainOnly(DecisionCosts costs)
    {
        if (!double.IsFinite(costs.AbstainCost))
        {
            throw new ArgumentException(
                "An empty distribution has no acceptable action: abstention is disabled.",
                nameof(costs));
        }

        var option = new DecisionOption(
            DecisionKind.Abstain,
            null,
            Array.Empty<string>(),
            costs.AbstainCost,
            null);

        return new Decision(
            DecisionKind.Abstain,
            null,
            Array.Empty<string>(),
            costs.AbstainCost,
            null,
            [option]);
    }

    private static void ValidateOptions(DecisionOptions options)
    {
        if (!double.IsFinite(options.AskCandidateMass) || options.AskCandidateMass is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.AskCandidateMass,
                "AskCandidateMass must be finite and in (0, 1].");
        }

        if (options.MinAskCandidates < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MinAskCandidates,
                "MinAskCandidates must be at least 2.");
        }

        if (options.MaxAskCandidates < options.MinAskCandidates)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxAskCandidates,
                "MaxAskCandidates must be at least MinAskCandidates.");
        }
    }

    private static void ValidateCosts(DecisionCosts costs)
    {
        RequireNonNegative(costs.AskCost, nameof(costs));
        RequireNonNegative(costs.AbstainCost, nameof(costs));
        RequireNonNegative(costs.ExploreCost, nameof(costs));
        RequireNonNegative(costs.ResidualCostAfterAsk, nameof(costs));

        if (costs.Misclassification is null)
        {
            throw new ArgumentException("Misclassification costs cannot be null.", nameof(costs));
        }

        if (costs.ValueOfInformationThreshold is { } threshold
            && (double.IsNaN(threshold) || threshold < 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(costs),
                threshold,
                "ValueOfInformationThreshold must be non-negative.");
        }
    }

    private static void RequireNonNegative(double cost, string parameterName)
    {
        if (double.IsNaN(cost) || cost < 0 || double.IsNegativeInfinity(cost))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                cost,
                "Costs must be non-negative and not NaN.");
        }
    }
}
