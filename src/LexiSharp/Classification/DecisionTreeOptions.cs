namespace LexiSharp.Classification;

/// <summary>
/// How a candidate split is scored when a decision tree chooses its next question.
/// </summary>
public enum SplitCriterion
{
    /// <summary>
    /// Entropy reduction (the ID3/C4.5 gain). The default: with binary feature questions there is
    /// no cardinality bias to correct, and the score reads as "how much this question tells us".
    /// </summary>
    InformationGain,

    /// <summary>
    /// Gain divided by the entropy of the split itself (C4.5's gain ratio). Penalizes unbalanced
    /// splits, which on binary questions means it can prefer a rare feature over a balanced one.
    /// </summary>
    GainRatio,

    /// <summary>Gini impurity reduction (CART).</summary>
    Gini,
}

/// <summary>
/// Tunable knobs for <see cref="DecisionTreeClassifier"/>; the defaults stop a tree before it
/// memorizes the training corpus.
/// </summary>
public sealed record DecisionTreeOptions
{
    /// <summary>Default options: information gain, depth 8, at least two documents per leaf.</summary>
    public static readonly DecisionTreeOptions Default = new();

    /// <summary>Maximum number of questions on a root-to-leaf path. <c>0</c> makes the root a leaf.</summary>
    public int MaxDepth { get; init; } = 8;

    /// <summary>Minimum number of training documents a split must leave on each side.</summary>
    public int MinDocumentsPerLeaf { get; init; } = 2;

    /// <summary>Minimum corpus-wide document frequency for a feature to be considered as a question.</summary>
    public int MinDocumentFrequency { get; init; } = 1;

    /// <summary>Minimum information gain a split must add; <c>0</c> refuses a gain of exactly zero.</summary>
    public double MinGain { get; init; }

    /// <summary>
    /// Maximum number of thresholds evaluated per numeric feature at one node. The thresholds are
    /// taken from the node's sorted values at an even stride, so the cap bounds the search without
    /// fixing it to a bucket boundary. Default: <c>32</c>.
    /// </summary>
    public int MaxThresholdsPerFeature { get; init; } = 32;

    /// <summary>
    /// The cost of asking any question about a feature, or null for a cost of <c>1</c>. The split
    /// score is divided by it, so a cheap question that is almost as discriminative wins over an
    /// expensive one. Costs must be finite and positive; a non-positive or non-finite value fails
    /// training. Default: <c>null</c>.
    /// </summary>
    public Func<string, double>? QuestionCost { get; init; }

    /// <summary>The split score to maximize. Default: <see cref="SplitCriterion.InformationGain"/>.</summary>
    public SplitCriterion Criterion { get; init; } = SplitCriterion.InformationGain;

    /// <summary>
    /// Additive smoothing applied to a leaf's class counts, so a class the leaf never saw keeps a
    /// small probability instead of exactly zero. Always positive. Default: <c>1</c>.
    /// </summary>
    public double Alpha { get; init; } = 1.0;

    internal bool IsValid =>
        MaxDepth >= 0
        && MinDocumentsPerLeaf >= 1
        && MinDocumentFrequency >= 1
        && MinGain >= 0
        && !double.IsNaN(MinGain)
        && !double.IsInfinity(MinGain)
        && MaxThresholdsPerFeature >= 1
        && Alpha > 0
        && !double.IsNaN(Alpha)
        && !double.IsInfinity(Alpha);
}
