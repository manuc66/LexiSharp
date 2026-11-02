namespace LexiSharp.Core;

/// <summary>
/// Optional capability of an <see cref="ITextClassifier"/>: the same training and prediction
/// operations, with provider calls that can complete asynchronously.
/// </summary>
/// <remarks>
/// <para>
/// The synchronous surface stays the contract for callers that do not need I/O; this interface
/// exists because the embedding-backed classifiers already expose async overloads and a caller that
/// uses them would otherwise bypass <see cref="Classification.SynchronizedTextClassifier"/>.
/// </para>
/// <para>
/// Thread-safety is the same as <see cref="ITextClassifier"/>: concurrent
/// <see cref="PredictAsync"/> calls are supported, while <see cref="TrainAsync"/> must not overlap a
/// prediction or another training. The embedding provider behind an implementation must itself
/// support concurrent calls, because the wrapper's read lock admits several predictions at once.
/// <see cref="Classification.SynchronizedTextClassifier"/> wraps a classifier to provide that
/// separation for both the sync and the async members.
/// </para>
/// </remarks>
public interface IAsyncTextClassifier : ITextClassifier
{
    /// <summary>Trains the classifier asynchronously, replacing any previous model.</summary>
    /// <param name="documents">The labelled documents; those without a category are ignored.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task TrainAsync(IEnumerable<SearchDocument> documents, CancellationToken cancellationToken = default);

    /// <summary>Predicts the most likely categories for a text, asynchronously.</summary>
    /// <param name="text">Raw text to classify.</param>
    /// <param name="limit">Maximum number of categories to return.</param>
    /// <param name="excludedCategories">Optional categories to hide.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<ClassificationResult>> PredictAsync(
        string text,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional capability of an <see cref="IAsyncTextClassifier"/>: add or retract one labelled
/// document without a retraining pass, asynchronously.
/// </summary>
/// <remarks>
/// The parity contract is the same as <see cref="IIncrementalTextClassifier"/>: a model built one
/// document at a time is indistinguishable from one trained on the whole corpus. The thread-safety
/// contract is also the same — the mutating members must not overlap a prediction or another
/// mutation; <see cref="Classification.SynchronizedTextClassifier"/> provides that separation.
/// </remarks>
public interface IIncrementalAsyncTextClassifier : IAsyncTextClassifier
{
    /// <summary>Adds one labelled document asynchronously.</summary>
    /// <param name="document">The labelled document to learn.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task LearnAsync(SearchDocument document, CancellationToken cancellationToken = default);

    /// <summary>Removes one document's contribution asynchronously.</summary>
    /// <param name="document">The document to retract.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UnlearnAsync(SearchDocument document, CancellationToken cancellationToken = default);
}
