namespace LexiSharp.Core;

/// <summary>
/// A category with an unnormalized weight, produced by a source that is not a trained classifier.
/// </summary>
/// <param name="Category">The category the evidence points at; never empty.</param>
/// <param name="Score">A finite, non-negative weight. Higher means stronger evidence.</param>
public sealed record TextEvidence(string Category, double Score);

/// <summary>
/// Produces categories that no trained classifier has learned, from the text alone.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam for evidence that introduces a label: a key written in the text, an entity
/// extracted by a rule, a category named by a caller. It is deliberately not a
/// <see cref="ITextClassifier"/>: there is no corpus, no training pass and no per-class
/// distribution. A source returns one weight per candidate, and the blend normalizes them.
/// </para>
/// <para>
/// A source must not infer a label from the model's output; that is what
/// <see cref="ITextPriorSource"/> is for. A source that returns the same category twice has its
/// scores summed. An empty result means "no evidence", not "the text is unclassifiable".
/// </para>
/// <para>
/// Thread-safety is implementation-defined. A blend may call a source concurrently with another
/// prediction; sources that keep mutable state must synchronize it.
/// </para>
/// </remarks>
public interface ICandidateTextSource
{
    /// <summary>Returns the candidate categories the text carries, with their weights.</summary>
    /// <param name="text">The text being classified; never null.</param>
    IReadOnlyList<TextEvidence> Candidates(string text);
}
