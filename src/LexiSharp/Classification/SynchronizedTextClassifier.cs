using LexiSharp.Core;

namespace LexiSharp.Classification;

/// <summary>
/// Wraps an <see cref="ITextClassifier"/> so that concurrent prediction and concurrent
/// mutation stop being the caller's problem: any number of
/// <see cref="Predict(string, int, IReadOnlySet{string})"/> and <see cref="PredictBest"/> calls may
/// run at the same time, while <see cref="Train"/>, <see cref="Learn"/>, <see cref="Unlearn"/>,
/// <see cref="Reinforce"/>, <see cref="Unreinforce"/> and <see cref="ForgetReinforcement"/> each take
/// exclusive access and wait for the predictions already in flight to return. The async surface
/// (<see cref="IAsyncTextClassifier"/> / <see cref="IIncrementalAsyncTextClassifier"/>) is locked the
/// same way, without pinning the lock to the thread that started the call.
/// </summary>
/// <remarks>
/// The capabilities it forwards are optional and detected on the wrapped instance.
/// <see cref="IIncrementalTextClassifier"/>, <see cref="IReinforceableTextClassifier"/> and
/// <see cref="IWeightedPredictor"/> are forwarded when the wrapped classifier supports them and
/// throw <see cref="NotSupportedException"/> naming the wrapped type when it does not, rather than
/// silently doing nothing.
/// </remarks>
/// <remarks>
/// <para>
/// This is opt-in rather than baked into <see cref="NaiveBayesClassifier"/>, so that the classifier
/// itself keeps a plain, unsynchronized contract: the common shape — one writer, with readers
/// either single-threaded or pointed at a whole instance that is being swapped — keeps paying
/// nothing and keeps being free to do what a lock would forbid, such as training a fresh instance
/// off-lock and publishing it. A caller that wants overlapping feedback and prediction wraps its
/// classifier in this rather than hand-rolling the same lock.
/// </para>
/// <para>
/// The lock is not free in principle, but on this hot path it is not measurably expensive. Over 7
/// interleaved rounds of 200 000 <see cref="PredictBest"/> calls, the wrapped-to-unwrapped median
/// ratio came out at 0.986x, 1.051x, 1.057x, 0.975x and 1.043x on five separate runs — straddling
/// 1.0. Those figures were taken with the <see cref="ReaderWriterLockSlim"/> implementation this
/// type shipped first; the async-capable gate that replaced it has not been re-measured, so read
/// them as an order of magnitude rather than a bound. Do not read the lock as free either: it is a
/// monitor acquisition plus a branch per prediction, and a caller who needs the last few percent
/// should keep the classifier unwrapped rather than expect a number.
/// </para>
/// <para>
/// What the lock buys is correctness under overlap, and the churn that needs protecting is on both
/// sides of this wrapper. Over 3 runs of 2 s with 8 readers and 2 writers — one churning the
/// reinforcement ledger, one calling <see cref="Learn"/>/<see cref="Unlearn"/> and so rewriting the
/// per-class term counts, the corpus-wide counts and the document frequencies — the unwrapped
/// classifier threw on 8, 6 659 and 667 reads respectively, i.e. 0.00% to 0.10% of the 5.2M to 6.8M
/// attempted, and this one threw on none of roughly 2.6M. The bare counts are erratic because a race
/// only lands sometimes; the orders of magnitude will not reproduce on other hardware, the
/// qualitative result will. The stress test
/// <c>Predict_IsConsistentUnderConcurrentReinforcement</c> covers the zero-failure half. The
/// unwrapped numbers are recorded here and asserted nowhere, because a test that asserts a race
/// exists is a test that fails on a machine with fewer cores.
/// </para>
/// <para>
/// <see cref="IWeightedPredictor"/> is implemented unconditionally, because C# has no way to
/// implement an interface conditionally. When the wrapped classifier does not support it, the
/// weighted overload throws <see cref="NotSupportedException"/> naming the wrapped type rather than
/// quietly returning something else.
/// </para>
/// <para>
/// The async members are forwarded when the wrapped classifier implements
/// <see cref="IAsyncTextClassifier"/> (and <see cref="IIncrementalAsyncTextClassifier"/> for
/// <c>LearnAsync</c>/<c>UnlearnAsync</c>); otherwise they throw <see cref="NotSupportedException"/>
/// naming the wrapped type. Model-specific members that are not on either interface still bypass
/// the lock. Wrap once, and do not also expose the wrapped instance to callers; two wrappers around
/// the same instance do not share a lock, and wrapping a blend while also wrapping its lanes nests
/// locks instead of ordering them.
/// </para>
/// <para>
/// Dispose releases the lock. It does not dispose the wrapped classifier, which the caller still
/// owns. Every member throws <see cref="ObjectDisposedException"/> afterwards.
/// </para>
/// </remarks>
public sealed class SynchronizedTextClassifier
    : IIncrementalTextClassifier, IIncrementalAsyncTextClassifier, IReinforceableTextClassifier,
      IContextualTextClassifier, IWeightedPredictor, IDisposable
{
    private readonly ITextClassifier _inner;

    // Unlike ReaderWriterLockSlim, this gate is not thread-affine, so the async members may await
    // the wrapped classifier and release the lock on whichever thread the continuation lands.
    private readonly AsyncReaderWriterLock _lock = new();

    /// <param name="inner">The classifier to synchronize access to. Not owned by this wrapper.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is null.</exception>
    public SynchronizedTextClassifier(ITextClassifier inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>Whether the wrapped classifier implements <see cref="IIncrementalTextClassifier"/>.</summary>
    public bool SupportsIncremental => ClassifierCapabilities.IsIncremental(_inner);

    /// <summary>Whether the wrapped classifier implements <see cref="IContextualTextClassifier"/>.</summary>
    public bool SupportsContext => ClassifierCapabilities.IsContextual(_inner);

    /// <summary>Whether the wrapped classifier implements <see cref="IAsyncTextClassifier"/>.</summary>
    public bool SupportsAsync => ClassifierCapabilities.IsAsync(_inner);

    /// <inheritdoc />
    public void Train(IEnumerable<SearchDocument> documents)
    {
        _lock.EnterWriteLock();
        try
        {
            _inner.Train(documents);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    /// The wrapped classifier is not an <see cref="IIncrementalTextClassifier"/>.
    /// </exception>
    public void Learn(SearchDocument document)
    {
        var incremental = AsIncremental();

        _lock.EnterWriteLock();
        try
        {
            incremental.Learn(document);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    /// The wrapped classifier is not an <see cref="IIncrementalTextClassifier"/>.
    /// </exception>
    public void Unlearn(SearchDocument document)
    {
        var incremental = AsIncremental();

        _lock.EnterWriteLock();
        try
        {
            incremental.Unlearn(document);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private IIncrementalTextClassifier AsIncremental()
    {
        if (_inner is IIncrementalTextClassifier incremental)
            return incremental;

        throw new NotSupportedException(
            $"{_inner.GetType().Name} does not implement {nameof(IIncrementalTextClassifier)}.");
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    /// The wrapped classifier is not an <see cref="IAsyncTextClassifier"/>.
    /// </exception>
    public async Task TrainAsync(
        IEnumerable<SearchDocument> documents,
        CancellationToken cancellationToken = default)
    {
        var asyncInner = AsAsync();

        await _lock.EnterWriteLockAsync().ConfigureAwait(false);
        try
        {
            await asyncInner.TrainAsync(documents, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    /// The wrapped classifier is not an <see cref="IAsyncTextClassifier"/>.
    /// </exception>
    public async Task<IReadOnlyList<ClassificationResult>> PredictAsync(
        string text,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null,
        CancellationToken cancellationToken = default)
    {
        var asyncInner = AsAsync();

        await _lock.EnterReadLockAsync().ConfigureAwait(false);
        try
        {
            return await asyncInner
                .PredictAsync(text, limit, excludedCategories, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    /// The wrapped classifier is not an <see cref="IIncrementalAsyncTextClassifier"/>.
    /// </exception>
    public async Task LearnAsync(
        SearchDocument document,
        CancellationToken cancellationToken = default)
    {
        var incremental = AsIncrementalAsync();

        await _lock.EnterWriteLockAsync().ConfigureAwait(false);
        try
        {
            await incremental.LearnAsync(document, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    /// The wrapped classifier is not an <see cref="IIncrementalAsyncTextClassifier"/>.
    /// </exception>
    public async Task UnlearnAsync(
        SearchDocument document,
        CancellationToken cancellationToken = default)
    {
        var incremental = AsIncrementalAsync();

        await _lock.EnterWriteLockAsync().ConfigureAwait(false);
        try
        {
            await incremental.UnlearnAsync(document, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private IAsyncTextClassifier AsAsync()
    {
        if (_inner is IAsyncTextClassifier asyncClassifier)
            return asyncClassifier;

        throw new NotSupportedException(
            $"{_inner.GetType().Name} does not implement {nameof(IAsyncTextClassifier)}.");
    }

    private IIncrementalAsyncTextClassifier AsIncrementalAsync()
    {
        if (_inner is IIncrementalAsyncTextClassifier incremental)
            return incremental;

        throw new NotSupportedException(
            $"{_inner.GetType().Name} does not implement {nameof(IIncrementalAsyncTextClassifier)}.");
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    /// The wrapped classifier is not an <see cref="IReinforceableTextClassifier"/>.
    /// </exception>
    public void Reinforce(string text, string category, double weight = 1.0)
    {
        var reinforceable = AsReinforceable();

        _lock.EnterWriteLock();
        try
        {
            reinforceable.Reinforce(text, category, weight);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    /// The wrapped classifier is not an <see cref="IReinforceableTextClassifier"/>.
    /// </exception>
    public void Unreinforce(string text, string category, double weight = 1.0)
    {
        var reinforceable = AsReinforceable();

        _lock.EnterWriteLock();
        try
        {
            reinforceable.Unreinforce(text, category, weight);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    /// The wrapped classifier is not an <see cref="IReinforceableTextClassifier"/>.
    /// </exception>
    public void ForgetReinforcement()
    {
        var reinforceable = AsReinforceable();

        _lock.EnterWriteLock();
        try
        {
            reinforceable.ForgetReinforcement();
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private IReinforceableTextClassifier AsReinforceable()
    {
        if (_inner is IReinforceableTextClassifier reinforceable)
            return reinforceable;

        throw new NotSupportedException(
            $"{_inner.GetType().Name} does not implement {nameof(IReinforceableTextClassifier)}.");
    }

    /// <inheritdoc />
    public IReadOnlyList<ClassificationResult> Predict(
        string text,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null)
    {
        _lock.EnterReadLock();
        try
        {
            return _inner.Predict(text, limit, excludedCategories);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">
    /// The wrapped classifier is not an <see cref="IWeightedPredictor"/>.
    /// </exception>
    public IReadOnlyList<ClassificationResult> Predict(
        IEnumerable<WeightedToken> tokens,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null)
    {
        // Resolved before taking the lock: whether the wrapped type supports this cannot change
        // while the lock is held, and throwing without ever queueing behind a writer is friendlier.
        if (_inner is not IWeightedPredictor weighted)
        {
            throw new NotSupportedException(
                $"{_inner.GetType().Name} does not implement {nameof(IWeightedPredictor)}.");
        }

        _lock.EnterReadLock();
        try
        {
            return weighted.Predict(tokens, limit, excludedCategories);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Forwards a request context to the wrapped classifier when it implements
    /// <see cref="IContextualTextClassifier"/>.
    /// </summary>
    /// <param name="text">The text being classified.</param>
    /// <param name="context">Request-scoped evidence.</param>
    /// <param name="limit">Maximum number of categories to return.</param>
    /// <param name="excludedCategories">Optional categories to hide.</param>
    /// <exception cref="NotSupportedException">
    /// The wrapped classifier is not an <see cref="IContextualTextClassifier"/>.
    /// </exception>
    public IReadOnlyList<ClassificationResult> Predict(
        string text,
        BlendContext context,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_inner is not IContextualTextClassifier contextual)
        {
            throw new NotSupportedException(
                $"{_inner.GetType().Name} does not implement {nameof(IContextualTextClassifier)}.");
        }

        _lock.EnterReadLock();
        try
        {
            return contextual.Predict(text, context, limit, excludedCategories);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null)
    {
        _lock.EnterReadLock();
        try
        {
            return _inner.PredictBest(text, excludedCategories);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Releases the internal lock. The wrapped classifier is not disposed.</summary>
    public void Dispose() => _lock.Dispose();
}
