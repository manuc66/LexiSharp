namespace LexiSharp.Core;

/// <summary>
/// Optional capability of an <see cref="ITextClassifier"/> that can explain a prediction:
/// the predicted categories, the contribution of each underlying lane (for blended models)
/// and the terms that drove the decision.
/// </summary>
public interface IExplainableTextClassifier : ITextClassifier
{
    /// <summary>
    /// Explains the prediction for <paramref name="text"/>, or returns <c>null</c> when the
    /// model is untrained or cannot produce an explanation.
    /// </summary>
    ClassificationExplanation? Explain(
        string text,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null);
}

/// <summary>The contribution of one underlying lane (classifier) to a blended prediction.</summary>
/// <param name="Lane">Name of the lane (e.g. <c>NaiveBayes</c>, <c>NearestNeighbor</c>).</param>
/// <param name="Weight">The lane's blend weight in <c>[0, 1]</c>.</param>
/// <param name="Results">The lane's own prediction.</param>
public sealed record LaneContribution(
    string Lane,
    double Weight,
    IReadOnlyList<ClassificationResult> Results);

/// <summary>A transparent breakdown of a classification prediction.</summary>
/// <param name="Results">The final predicted categories.</param>
/// <param name="Lanes">Per-lane contributions (one entry for a single classifier).</param>
/// <param name="Terms">The (normalized) terms that drove the decision, with their aggregate weight.</param>
public sealed record ClassificationExplanation(
    IReadOnlyList<ClassificationResult> Results,
    IReadOnlyList<LaneContribution> Lanes,
    IReadOnlyList<TermContribution> Terms);
