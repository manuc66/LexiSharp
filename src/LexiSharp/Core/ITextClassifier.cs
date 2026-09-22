namespace LexiSharp.Core;

/// <summary>
/// Supervised text classifier trained on labelled documents.
/// </summary>
/// <remarks>
/// Thread-safety: concurrent <see cref="Predict"/> calls are supported, but <see cref="Train"/>
/// must not run concurrently with any <see cref="Predict"/> or another <see cref="Train"/>.
/// Synchronize externally (e.g. train off-lock on a snapshot, then atomically swap the
/// classifier instance) when train and predict can overlap.
/// </remarks>
public interface ITextClassifier
{
    /// <summary>Trains the classifier on documents carrying a non-null <see cref="SearchDocument.Category"/>.</summary>
    void Train(IEnumerable<SearchDocument> documents);

    /// <summary>
    /// Predicts the most likely categories for a raw piece of text, by decreasing probability.
    /// </summary>
    /// <param name="text">Raw text to classify; tokenized internally.</param>
    /// <param name="limit">Maximum number of categories to return.</param>
    /// <param name="excludedCategories">Optional set of categories to hide without retraining; probabilities are renormalized over the remaining categories.</param>
    IReadOnlyList<ClassificationResult> Predict(
        string text,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null);

    /// <summary>Returns the single most likely category, or null when nothing was trained.</summary>
    string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null);
}