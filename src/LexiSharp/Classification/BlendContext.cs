using LexiSharp.Core;

namespace LexiSharp.Classification;

/// <summary>
/// Evidence supplied for one prediction, instead of being configured as a source on the blend.
/// </summary>
/// <remarks>
/// <para>
/// A request context is read once and never stored on the classifier: two calls with different
/// contexts cannot leak evidence into each other, and a caller that resolves candidates from a
/// query does not have to rebuild the whole <see cref="BlendTextClassifier"/>.
/// </para>
/// <para>
/// <see cref="Candidates"/> may introduce a category no trained lane produced.
/// <see cref="Priors"/> only shifts a category a trained lane produced, exactly like a configured
/// <see cref="ITextPriorSource"/>. The weights are applied to the context's evidence only; the
/// configured lanes and sources keep their own weights.
/// </para>
/// <para>
/// Candidate scores are <b>normalized</b> within the context before <see cref="CandidateWeight"/>
/// is applied, so the absolute amount of evidence is discarded: two neighbours that agree do not
/// outweigh one unless the caller folds the agreement into <see cref="CandidateWeight"/> (for
/// example, scale it by the sum of the evidence). A fixed weight treats every positive candidate
/// as equally meaningful; a sum-scaled weight lets several agreeing candidates outvote one. That
/// is a validation choice, and a wrong one can cost several points.
/// </para>
/// </remarks>
public sealed record BlendContext
{
    /// <summary>A context with no candidates and no priors.</summary>
    public static readonly BlendContext Empty = new();

    /// <summary>Categories the request may introduce, with their unnormalized weights.</summary>
    public IReadOnlyList<TextEvidence>? Candidates { get; init; }

    /// <summary>Log-prior bonuses the request may apply to categories a trained lane produced.</summary>
    public IReadOnlyList<TextEvidence>? Priors { get; init; }

    /// <summary>Weight of <see cref="Candidates"/> in the blend. Default: <c>1</c>.</summary>
    public double CandidateWeight { get; init; } = 1.0;

    /// <summary>Weight of <see cref="Priors"/> in the blend. Default: <c>1</c>.</summary>
    public double PriorWeight { get; init; } = 1.0;
}
