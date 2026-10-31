namespace LexiSharp.Classification;

/// <summary>
/// Tunable knobs for <see cref="CorpusDivider"/>; the defaults keep the tree shallow enough to
/// read and bound the vocabulary the split search compares.
/// </summary>
public sealed record CorpusDividerOptions
{
    /// <summary>Default options: depth 4, at least two items per cluster, top 64 candidate features.</summary>
    public static readonly CorpusDividerOptions Default = new();

    /// <summary>Maximum number of questions on a root-to-leaf path. <c>0</c> returns one cluster.</summary>
    public int MaxDepth { get; init; } = 4;

    /// <summary>Minimum number of documents a split must leave on each side.</summary>
    public int MinDocumentsPerLeaf { get; init; } = 2;

    /// <summary>Minimum document frequency in the region being split for a feature to be a question.</summary>
    public int MinDocumentFrequency { get; init; } = 2;

    /// <summary>
    /// Maximum number of candidate features scored at one node, taken by decreasing document
    /// frequency. The split search is <c>candidates × vocabulary</c>; this is the cap that keeps it
    /// linear in the corpus rather than quadratic in the vocabulary.
    /// </summary>
    public int MaxCandidateTerms { get; init; } = 64;

    /// <summary>
    /// Maximum number of features the two child profiles are compared over, taken by decreasing
    /// document frequency. Features outside the cap still live in the clusters; they are only not
    /// part of the separation score.
    /// </summary>
    public int MaxVocabularyTerms { get; init; } = 2000;

    /// <summary>
    /// Minimum separation a split must reach. The score is a balance-weighted mean squared
    /// difference of the two child feature profiles, in <c>[0, 0.25]</c>; <c>0</c> accepts any split
    /// that is not exactly flat.
    /// </summary>
    public double MinSeparation { get; init; } = 0.01;

    /// <summary>How many representative features a cluster reports. Default: <c>5</c>.</summary>
    public int TopFeaturesPerCluster { get; init; } = 5;

    internal bool IsValid =>
        MaxDepth >= 0
        && MinDocumentsPerLeaf >= 1
        && MinDocumentFrequency >= 1
        && MaxCandidateTerms >= 1
        && MaxVocabularyTerms >= 1
        && MinSeparation >= 0
        && !double.IsNaN(MinSeparation)
        && !double.IsInfinity(MinSeparation)
        && TopFeaturesPerCluster >= 0;
}
