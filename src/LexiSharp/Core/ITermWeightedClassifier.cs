namespace LexiSharp.Core;

/// <summary>
/// Optional capability of an <see cref="ITextClassifier"/> that can report the weight of a single
/// term for a category, used for term-level explainability.
/// </summary>
public interface ITermWeightedClassifier : ITextClassifier
{
    /// <summary>Returns the weight of <paramref name="term"/> for <paramref name="category"/>.</summary>
    double TermWeight(string category, string term);
}
