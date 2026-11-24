using LexiSharp.Classification;
using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Expansion;

/// <summary>
/// Obsolete alias for <see cref="CorrectingTextClassifier"/>.
/// </summary>
/// <remarks>
/// Kept for binary compatibility with the 1.2.0 preview. New code should use
/// <see cref="CorrectingTextClassifier"/> directly with an <see cref="ISpanTokenizer"/>.
/// </remarks>
[Obsolete("Use LexiSharp.Classification.CorrectingTextClassifier with an ISpanTokenizer.")]
public sealed class CorrectingSubjectRanker : ITextClassifier
{
    private readonly CorrectingTextClassifier _inner;

    /// <summary>Wraps <paramref name="inner"/> with correction over <paramref name="vocabulary"/>.</summary>
    public CorrectingSubjectRanker(
        ITextClassifier inner,
        ITermCorrector corrector,
        IEnumerable<string> vocabulary,
        ITokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        if (tokenizer is not ISpanTokenizer spanTokenizer)
        {
            throw new ArgumentException("The tokenizer must implement ISpanTokenizer.", nameof(tokenizer));
        }

        _inner = new CorrectingTextClassifier(inner, corrector, vocabulary, spanTokenizer);
    }

    /// <inheritdoc />
    public void Train(IEnumerable<SearchDocument> documents) => _inner.Train(documents);

    /// <inheritdoc />
    public IReadOnlyList<ClassificationResult> Predict(
        string text,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null) =>
        _inner.Predict(text, limit, excludedCategories);

    /// <inheritdoc />
    public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null) =>
        _inner.PredictBest(text, excludedCategories);
}
