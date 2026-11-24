namespace LexiSharp.Core;

/// <summary>
/// A set of categories produced by a conformal (or otherwise calibrated) classifier: the
/// smallest set of labels whose combined confidence reaches the requested coverage level.
/// An empty set means the classifier abstains.
/// </summary>
/// <param name="Categories">The labels in the set, in decreasing confidence order.</param>
/// <param name="Results">The corresponding <see cref="ClassificationResult"/> entries.</param>
public sealed record PredictionSet(
    IReadOnlyList<string> Categories,
    IReadOnlyList<ClassificationResult> Results)
{
    /// <summary>Alias for <see cref="Categories"/>.</summary>
    public IReadOnlyList<string> Labels => Categories;

    /// <summary>Alias for <see cref="Categories"/>.</summary>
    public IReadOnlyList<string> Items => Categories;

    /// <summary>Alias for <see cref="Categories"/>.</summary>
    public IReadOnlyList<string> Members => Categories;

    /// <summary>Number of labels in the set.</summary>
    public int Count => Categories.Count;

    /// <summary>Whether the classifier abstained (empty set).</summary>
    public bool IsEmpty => Categories.Count == 0;
}
