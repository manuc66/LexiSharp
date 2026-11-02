namespace LexiSharp.Core;

/// <summary>
/// The atomic publish point for the « snapshot swap » concurrency pattern on a classifier: one
/// thread trains, clones or otherwise builds a fresh model off-lock, then <see cref="Swap"/> makes
/// it visible to every concurrent reader in one atomic step.
/// </summary>
/// <remarks>
/// <para>
/// This is the classifier counterpart of <see cref="AtomicEngineReference"/> and the
/// frequent-retraining option beside <see cref="Classification.SynchronizedTextClassifier"/>: the
/// wrapper serializes a writer against readers, while this holder leaves the running model untouched
/// and pays nothing on the read side. It is the right shape when a retrain builds a new model
/// anyway; it is not a substitute for incremental feedback, where copying the model per write would
/// cost far more than the write itself.
/// </para>
/// <para>
/// A reader must capture the classifier <b>once per prediction</b> into a local
/// (<c>var model = reference.Current;</c>, then predict through that local): reading the holder
/// again mid-prediction could pick up a newer model and mix two views in one answer. The volatile
/// read is what makes that single capture safe.
/// </para>
/// <para>
/// No classifier is disposed here — neither the initial one nor the one <see cref="Swap"/>
/// displaces, which is returned to the caller: the caller owns every instance it passes in, and
/// nothing in this holder changes that. Do not mutate a displaced instance after the swap: a reader
/// may still be predicting through the reference it captured, and the snapshot contract is what
/// makes that safe.
/// </para>
/// </remarks>
public sealed class AtomicClassifierReference
{
    private ITextClassifier _classifier;

    /// <param name="classifier">The classifier initially visible to readers. Not owned by this holder.</param>
    /// <exception cref="ArgumentNullException"><paramref name="classifier"/> is null.</exception>
    public AtomicClassifierReference(ITextClassifier classifier)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        _classifier = classifier;
    }

    /// <summary>
    /// The currently published classifier. Each read is volatile, so a reader that needs one stable
    /// model per prediction should read this once into a local and predict through that.
    /// </summary>
    public ITextClassifier Current => Volatile.Read(ref _classifier);

    /// <summary>
    /// Atomically replaces the published classifier and returns the previous one, so a writer can
    /// retire the instance readers have stopped seeing. The new classifier must be fully trained
    /// before the call.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="classifier"/> is null.</exception>
    public ITextClassifier Swap(ITextClassifier classifier)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        return Interlocked.Exchange(ref _classifier, classifier);
    }
}
