namespace LexiSharp.Core;

/// <summary>
/// A reader/writer gate that can be acquired synchronously or asynchronously, so a classifier can
/// hold one lock across an async provider call without tying the lock to the thread that started
/// it. Waiters are served in arrival order: a run of readers at the front of the queue is admitted
/// together, and a writer waits until the active readers release.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ReaderWriterLockSlim"/> is thread-affine, so it cannot be held across an
/// <c>await</c>: the continuation may run on another thread, and releasing the lock there throws.
/// This gate keeps its state under a short <c>lock</c> and hands out completion sources for async
/// waiters, so the thread that releases a read or write lock need not be the one that acquired it.
/// </para>
/// <para>
/// Synchronous waiters block on <see cref="Monitor.Wait(object)"/> rather than on a task, so a
/// contended synchronous prediction does not allocate a completion source, but it still occupies
/// the thread that blocks. Do not mix a capped thread pool, blocking <c>Predict</c> calls and an
/// async writer that awaits I/O: the writer's continuation needs a pool thread to release the
/// lock, and a saturated pool turns that into a latency spike or a deadlock. Use the async surface
/// end-to-end when that shape is possible. Async waiters use a
/// <see cref="TaskCompletionSource{TResult}"/> created with
/// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>, so no continuation runs while
/// the state lock is held. The uncontended path is one monitor acquisition and two field updates.
/// </para>
/// <para>
/// The queue is fair rather than writer-preferring: a stream of writers cannot starve readers, and
/// a writer already queued is admitted before readers that arrive after it. That is the ordering a
/// mixed prediction/feedback workload needs; the alternative — always admitting a waiting writer —
/// measured as near-reader starvation under the stress test.
/// </para>
/// <para>
/// Async acquisition accepts a cancellation token. A cancelled waiter is removed from the queue
/// under the state lock and its task is completed as cancelled, so a request behind a long writer
/// can abandon the wait without waiting for the writer to finish. Synchronous acquisition has no
/// token: the synchronous API has nowhere to report cancellation before the lock is held.
/// </para>
/// <para>
/// It does not detect reentrancy. The wrapper's locked calls go straight to the wrapped instance
/// and never re-enter it; a callback that does call back into the same wrapper can deadlock instead
/// of failing loudly, which is the trade for not paying a per-thread recursion table on every
/// prediction.
/// </para>
/// </remarks>
internal sealed class AsyncReaderWriterLock : IDisposable
{
    private readonly object _sync = new();
    private readonly LinkedList<Waiter> _waiters = new();
    private int _readers;
    private bool _writer;
    private bool _disposed;

    /// <summary>Acquires the read lock, blocking until it is available.</summary>
    public void EnterReadLock()
    {
        lock (_sync)
        {
            ThrowIfDisposed();

            if (!_writer && _waiters.Count == 0)
            {
                _readers++;
                return;
            }

            var waiter = new Waiter(isWriter: false);
            waiter.Node = _waiters.AddLast(waiter);

            while (!waiter.Granted)
            {
                Monitor.Wait(_sync);

                if (!waiter.Granted)
                    ThrowIfDisposed();
            }
        }
    }

    /// <summary>Acquires the read lock, waiting asynchronously when it is contended.</summary>
    public ValueTask EnterReadLockAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled(cancellationToken);

        lock (_sync)
        {
            ThrowIfDisposed();

            if (!_writer && _waiters.Count == 0)
            {
                _readers++;
                return ValueTask.CompletedTask;
            }

            return EnqueueAsyncWaiter(isWriter: false, cancellationToken);
        }
    }

    /// <summary>Releases one read lock.</summary>
    public void ExitReadLock()
    {
        lock (_sync)
        {
            if (_readers <= 0)
                throw new SynchronizationLockException("The read lock is not held.");

            _readers--;

            if (_readers == 0)
                GrantNext();
        }
    }

    /// <summary>Acquires the write lock, blocking until it is available.</summary>
    public void EnterWriteLock()
    {
        lock (_sync)
        {
            ThrowIfDisposed();

            if (!_writer && _readers == 0 && _waiters.Count == 0)
            {
                _writer = true;
                return;
            }

            var waiter = new Waiter(isWriter: true);
            waiter.Node = _waiters.AddLast(waiter);

            while (!waiter.Granted)
            {
                Monitor.Wait(_sync);

                if (!waiter.Granted)
                    ThrowIfDisposed();
            }
        }
    }

    /// <summary>Acquires the write lock, waiting asynchronously when it is contended.</summary>
    public ValueTask EnterWriteLockAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled(cancellationToken);

        lock (_sync)
        {
            ThrowIfDisposed();

            if (!_writer && _readers == 0 && _waiters.Count == 0)
            {
                _writer = true;
                return ValueTask.CompletedTask;
            }

            return EnqueueAsyncWaiter(isWriter: true, cancellationToken);
        }
    }

    /// <summary>Releases the write lock.</summary>
    public void ExitWriteLock()
    {
        lock (_sync)
        {
            if (!_writer)
                throw new SynchronizationLockException("The write lock is not held.");

            _writer = false;
            GrantNext();
        }
    }

    /// <summary>
    /// Refuses new acquisitions and wakes every waiter with <see cref="ObjectDisposedException"/>.
    /// Locks already held may still be released; the wrapper's dispose contract is that the next
    /// call fails, not that an in-flight call is torn down.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;

            while (_waiters.First is { } node)
            {
                _waiters.RemoveFirst();
                Waiter waiter = node.Value;
                waiter.Cancelled = true;
                waiter.Node = null;
                waiter.Registration.Unregister();

                if (waiter.Async is { } async)
                    async.TrySetException(new ObjectDisposedException(nameof(AsyncReaderWriterLock)));
            }

            // Synchronous waiters re-check the state after waking, so a pulse is enough.
            Monitor.PulseAll(_sync);
        }
    }

    private ValueTask EnqueueAsyncWaiter(bool isWriter, CancellationToken cancellationToken)
    {
        var waiter = new Waiter(isWriter)
        {
            Async = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        waiter.Node = _waiters.AddLast(waiter);

        if (cancellationToken.CanBeCanceled)
        {
            waiter.Registration = cancellationToken.Register(
                () => CancelWaiter(waiter, cancellationToken));
        }

        return new ValueTask(waiter.Async.Task);
    }

    private void CancelWaiter(Waiter waiter, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (waiter.Granted || waiter.Cancelled || waiter.Async is null)
                return;

            waiter.Cancelled = true;

            if (waiter.Node is { } node)
            {
                _waiters.Remove(node);
                waiter.Node = null;
            }

            waiter.Async.TrySetCanceled(cancellationToken);

            // If the cancelled waiter was at the front, the next one may now be grantable.
            GrantNext();
        }
    }

    private void GrantNext()
    {
        if (_writer || _disposed || _waiters.First is null)
            return;

        if (_waiters.First.Value.IsWriter)
        {
            if (_readers > 0)
                return;

            LinkedListNode<Waiter> node = _waiters.First;
            _waiters.RemoveFirst();
            _writer = true;
            Grant(node.Value);
            return;
        }

        while (_waiters.First is { } node && !node.Value.IsWriter)
        {
            _waiters.RemoveFirst();
            _readers++;
            Grant(node.Value);
        }
    }

    private void Grant(Waiter waiter)
    {
        waiter.Granted = true;
        waiter.Node = null;
        waiter.Registration.Unregister();

        if (waiter.Async is { } async)
            async.TrySetResult(true);
        else
            Monitor.PulseAll(_sync);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AsyncReaderWriterLock));
    }

    private sealed class Waiter(bool isWriter)
    {
        public bool IsWriter { get; } = isWriter;

        public bool Granted { get; set; }

        public bool Cancelled { get; set; }

        public TaskCompletionSource<bool>? Async { get; init; }

        public LinkedListNode<Waiter>? Node { get; set; }

        public CancellationTokenRegistration Registration { get; set; }
    }
}
