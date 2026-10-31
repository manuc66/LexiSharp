namespace LexiSharp.Classification;

/// <summary>
/// Tunable knobs for <see cref="LinearClassifier{TItem}"/>: the AdaGrad pass, the vocabulary
/// floor, and the abstention threshold.
/// </summary>
public sealed record LinearClassifierOptions
{
    /// <summary>Default options: 20 epochs, learning rate 0.1, L2 1e-4, min df 2.</summary>
    public static readonly LinearClassifierOptions Default = new();

    /// <summary>Number of passes over the training corpus. Default: <c>20</c>.</summary>
    public int Epochs { get; init; } = 20;

    /// <summary>AdaGrad's base step size. Always positive. Default: <c>0.1</c>.</summary>
    public double LearningRate { get; init; } = 0.1;

    /// <summary>L2 penalty applied to the feature weights, not the biases. Default: <c>1e-4</c>.</summary>
    public double L2 { get; init; } = 1e-4;

    /// <summary>Minimum corpus-wide document frequency for a feature to enter the model. Default: <c>2</c>.</summary>
    public int MinDocumentFrequency { get; init; } = 2;

    /// <summary>
    /// When the winning probability is below this, prediction returns nothing instead of an
    /// answer. <c>0</c> never abstains. Default: <c>0</c>.
    /// </summary>
    public double MinProbability { get; init; }

    /// <summary>Seed for the per-epoch shuffle. Default: <c>7</c>.</summary>
    public int Seed { get; init; } = 7;

    /// <summary>
    /// A multiplier applied to every document's sample weight, keyed by category. A value of
    /// <c>0</c> removes the category; values must be finite and non-negative. Default: <c>null</c>.
    /// </summary>
    public IReadOnlyDictionary<string, double>? ClassWeights { get; init; }

    internal bool IsValid =>
        Epochs >= 1
        && LearningRate > 0
        && !double.IsNaN(LearningRate)
        && !double.IsInfinity(LearningRate)
        && L2 >= 0
        && !double.IsNaN(L2)
        && !double.IsInfinity(L2)
        && MinDocumentFrequency >= 1
        && MinProbability >= 0
        && MinProbability <= 1
        && !double.IsNaN(MinProbability)
        && !double.IsInfinity(MinProbability)
        && Seed >= 0;
}
