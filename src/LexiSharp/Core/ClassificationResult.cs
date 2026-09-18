namespace LexiSharp.Core;

/// <summary>
/// A category predicted for a piece of text, with its estimated probability.
/// </summary>
/// <param name="Category">The predicted category label.</param>
/// <param name="Probability">Estimated posterior probability in the <c>[0, 1]</c> range.</param>
public sealed record ClassificationResult(
    string Category,
    double Probability);