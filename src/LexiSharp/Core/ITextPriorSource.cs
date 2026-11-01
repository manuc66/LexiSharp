namespace LexiSharp.Core;

/// <summary>
/// Produces additive log-prior evidence for categories a trained model already holds.
/// </summary>
/// <remarks>
/// <para>
/// The score is a <b>log</b> multiplier: the blend applies <c>p(c) *= exp(weight · bonus(c))</c>.
/// A positive bonus pulls a category up, a negative one pushes it down, and zero leaves the
/// blended probability unchanged. Keeping it additive in log space means two sources compose by
/// adding their bonuses, which is what a recency term and a neighbour-similarity term should do.
/// </para>
/// <para>
/// A prior never introduces a category: if the text points at a label no lane produced, use
/// <see cref="ICandidateTextSource"/>. A prior for a category outside the trained lanes is
/// ignored rather than treated as an error, because a source does not necessarily know the
/// model's label set; that includes a category a candidate source injected, which belongs to the
/// evidence lane and not to the model's prior.
/// </para>
/// <para>
/// Thread-safety is implementation-defined; see <see cref="ICandidateTextSource"/>.
/// </para>
/// </remarks>
public interface ITextPriorSource
{
    /// <summary>Returns the log-prior bonus for the categories the text should favor.</summary>
    /// <param name="text">The text being classified; never null.</param>
    IReadOnlyList<TextEvidence> Prior(string text);
}
