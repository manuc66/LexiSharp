namespace LexiSharp.Classification;

/// <summary>
/// Tunable knobs for <see cref="RandomForestClassifier{TItem}"/>: how many trees, how deep, and
/// how much of the feature space each tree is allowed to see.
/// </summary>
/// <remarks>
/// A forest is the accuracy answer to the single tree's weak spot on sparse text: one greedy tree
/// plateaus well below a linear model, while many decorrelated trees vote their way back. The
/// price is the path: a forest has no single <c>Explain</c> chain, so use
/// <see cref="DecisionTreeClassifier{TItem}"/> when the questions are the point and the forest when
/// the label is.
/// </remarks>
public sealed record RandomForestOptions
{
    /// <summary>Default options: 40 trees of depth 20, 30 % of the features each, Gini.</summary>
    public static readonly RandomForestOptions Default = new();

    /// <summary>Number of trees in the forest. Default: <c>40</c>.</summary>
    public int Trees { get; init; } = 40;

    /// <summary>Maximum depth of each tree. Default: <c>20</c>.</summary>
    public int MaxDepth { get; init; } = 20;

    /// <summary>Minimum number of documents a split must leave on each side. Default: <c>2</c>.</summary>
    public int MinDocumentsPerLeaf { get; init; } = 2;

    /// <summary>Minimum corpus-wide document frequency for a feature to be a question. Default: <c>2</c>.</summary>
    public int MinDocumentFrequency { get; init; } = 2;

    /// <summary>
    /// Share of the vocabulary each tree may use, drawn without replacement. Lower decorrelates
    /// more and weakens each tree; <c>1</c> gives every tree the whole space. Default: <c>0.3</c>.
    /// </summary>
    public double FeatureSubsetFraction { get; init; } = 0.3;

    /// <summary>The split score each tree maximizes. Default: <see cref="SplitCriterion.Gini"/>.</summary>
    public SplitCriterion Criterion { get; init; } = SplitCriterion.Gini;

    /// <summary>Seed for the bootstrap draws and the feature subsets. Default: <c>7</c>.</summary>
    public int Seed { get; init; } = 7;

    /// <summary>Additive smoothing applied to each tree's leaf counts. Default: <c>1</c>.</summary>
    public double Alpha { get; init; } = 1.0;

    internal bool IsValid =>
        Trees >= 1
        && MaxDepth >= 0
        && MinDocumentsPerLeaf >= 1
        && MinDocumentFrequency >= 1
        && FeatureSubsetFraction > 0
        && FeatureSubsetFraction <= 1
        && !double.IsNaN(FeatureSubsetFraction)
        && !double.IsInfinity(FeatureSubsetFraction)
        && Seed >= 0
        && Alpha > 0
        && !double.IsNaN(Alpha)
        && !double.IsInfinity(Alpha);
}
