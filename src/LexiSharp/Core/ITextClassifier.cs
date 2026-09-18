namespace LexiSharp.Core;

/// <summary>
/// Supervised text classifier trained on labelled documents.
/// </summary>
public interface ITextClassifier
{
    /// <summary>Trains the classifier on documents carrying a non-null <see cref="SearchDocument.Category"/>.</summary>
    void Train(IEnumerable<SearchDocument> documents);

    /// <summary>
    /// Predicts the most likely categories for a raw piece of text, by decreasing probability.
    /// </summary>
    /// <param name="text">Raw text to classify; tokenized internally.</param>
    /// <param name="limit">Maximum number of categories to return.</param>
    IReadOnlyList<ClassificationResult> Predict(string text, int limit = 3);

    /// <summary>Returns the single most likely category, or null when nothing was trained.</summary>
    string? PredictBest(string text);
}