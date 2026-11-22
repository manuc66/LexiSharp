namespace LexiSharp.Classification;

/// <summary>A split of a labelled stream into a training prefix and a test suffix.</summary>
/// <param name="Train">Examples used for training (earlier in time).</param>
/// <param name="Test">Examples used for evaluation (later in time).</param>
public sealed record TimeSeriesSplit(
    IReadOnlyList<OnlineEvaluationExample> Train,
    IReadOnlyList<OnlineEvaluationExample> Test);

/// <summary>Helpers for candidate-scoped, time-ordered evaluation.</summary>
public static class TimeSeriesSplitter
{
    /// <summary>Splits <paramref name="examples"/> so that the first <paramref name="trainFraction"/> (by block order) trains and the rest tests.</summary>
    public static TimeSeriesSplit Split(
        IReadOnlyList<OnlineEvaluationExample> examples,
        double trainFraction = 0.5)
    {
        ArgumentNullException.ThrowIfNull(examples);

        if (trainFraction < 0 || trainFraction > 1)
            throw new ArgumentOutOfRangeException(nameof(trainFraction));

        if (examples.Count == 0)
            return new TimeSeriesSplit(Array.Empty<OnlineEvaluationExample>(), Array.Empty<OnlineEvaluationExample>());

        int trainCount = (int)Math.Round(examples.Count * trainFraction);

        if (trainCount < 1) trainCount = 1;
        if (trainCount > examples.Count - 1 && examples.Count > 1) trainCount = examples.Count - 1;

        var ordered = examples.OrderBy(e => e.Block).ToList();
        var train = ordered.Take(trainCount).ToList();
        var test = ordered.Skip(trainCount).ToList();

        return new TimeSeriesSplit(train, test);
    }
}

/// <summary>Restricts a prediction to an explicit candidate set.</summary>
/// <param name="Candidates">Allowed categories; null means all learned categories.</param>
public sealed record PredictionScope(IReadOnlySet<string>? Candidates = null);