using LexiSharp.Core;

namespace LexiSharp.Classification;

/// <summary>The outcome of a <see cref="BlendTextClassifier"/> prediction with its evidence mass.</summary>
/// <param name="Results">The final predicted categories.</param>
/// <param name="EvidenceMass">
/// The fraction of the blend's lane weight that produced an answer, in <c>[0, 1]</c>.
/// A low value means few (or weak) lanes spoke.
/// </param>
/// <param name="Lanes">Per-lane contributions.</param>
public sealed record BlendPrediction(
    IReadOnlyList<ClassificationResult> Results,
    double EvidenceMass,
    IReadOnlyList<LaneContribution> Lanes);