using LexiSharp.Core;

namespace LexiSharp.Classification;

/// <summary>
/// A classifier that accepts request-scoped evidence in addition to the text.
/// </summary>
/// <remarks>
/// The evaluator uses this capability when an <see cref="OnlineEvaluationExample"/> carries a
/// <see cref="BlendContext"/>; a classifier that does not implement it ignores the context and is
/// scored on the text alone. The contract is otherwise the same as
/// <see cref="ITextClassifier.Predict(string, int, IReadOnlySet{string})"/>.
/// </remarks>
public interface IContextualTextClassifier : ITextClassifier
{
    /// <summary>Predicts with evidence supplied for this request only.</summary>
    /// <param name="text">The text being classified.</param>
    /// <param name="context">Request-scoped candidates and priors; never null.</param>
    /// <param name="limit">Maximum number of categories to return.</param>
    /// <param name="excludedCategories">Optional categories to hide.</param>
    IReadOnlyList<ClassificationResult> Predict(
        string text,
        BlendContext context,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null);
}
