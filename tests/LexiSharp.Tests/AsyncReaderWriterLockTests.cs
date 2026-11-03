using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The async-capable gate behind <see cref="Classification.SynchronizedTextClassifier"/>: readers
/// overlap, one writer excludes them, waiters are served FIFO with a writer already queued taking
/// precedence over readers that arrive after it, and the lock can be released on a different thread
/// from the one that acquired it.
/// </summary>
public class AsyncReaderWriterLockTests
{
    [Fact]
    public async Task ReadersOverlap()
    {
        using var gate = new AsyncReaderWriterLock();

        await gate.EnterReadLockAsync();
        Assert.True(gate.EnterReadLockAsync().IsCompletedSuccessfully);

        gate.ExitReadLock();
        gate.ExitReadLock();
    }

    [Fact]
    public async Task AWriterWaitsForReadersAlreadyInFlight()
    {
        using var gate = new AsyncReaderWriterLock();

        gate.EnterReadLock();
        ValueTask writer = gate.EnterWriteLockAsync();

        Assert.False(writer.IsCompleted);

        gate.ExitReadLock();
        await writer;

        gate.ExitWriteLock();
    }

    [Fact]
    public async Task AReaderWaitsForAWriter()
    {
        using var gate = new AsyncReaderWriterLock();

        await gate.EnterWriteLockAsync();
        ValueTask reader = gate.EnterReadLockAsync();

        Assert.False(reader.IsCompleted);

        gate.ExitWriteLock();
        await reader;

        gate.ExitReadLock();
    }

    [Fact]
    public async Task AQueuedWriterRunsBeforeAQueuedReader()
    {
        using var gate = new AsyncReaderWriterLock();

        gate.EnterReadLock();
        ValueTask writer = gate.EnterWriteLockAsync();
        ValueTask reader = gate.EnterReadLockAsync();

        Assert.False(writer.IsCompleted);
        Assert.False(reader.IsCompleted);

        gate.ExitReadLock();
        await writer;

        // The writer is now inside; the queued reader must still be waiting.
        Assert.False(reader.IsCompleted);

        gate.ExitWriteLock();
        await reader;

        gate.ExitReadLock();
    }

    [Fact]
    public async Task ACancelledReaderLeavesTheQueue()
    {
        using var gate = new AsyncReaderWriterLock();
        await gate.EnterWriteLockAsync();

        using var cancellation = new CancellationTokenSource();
        ValueTask reader = gate.EnterReadLockAsync(cancellation.Token);
        Assert.False(reader.IsCompleted);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader);

        gate.ExitWriteLock();

        // The cancelled waiter must not block the queue for the next caller.
        await gate.EnterReadLockAsync();
        gate.ExitReadLock();
    }

    [Fact]
    public async Task CancellingTheHeadWaiterLetsTheNextOneRun()
    {
        using var gate = new AsyncReaderWriterLock();
        gate.EnterReadLock();

        using var cancellation = new CancellationTokenSource();
        ValueTask writer = gate.EnterWriteLockAsync(cancellation.Token);
        ValueTask reader = gate.EnterReadLockAsync();

        Assert.False(writer.IsCompleted);
        Assert.False(reader.IsCompleted);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await writer);

        gate.ExitReadLock();
        await reader;

        gate.ExitReadLock();
    }

    [Fact]
    public async Task AnAlreadyCancelledTokenDoesNotEnterTheQueue()
    {
        using var gate = new AsyncReaderWriterLock();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await gate.EnterReadLockAsync(cancellation.Token));

        await gate.EnterReadLockAsync();
        gate.ExitReadLock();
    }

    [Fact]
    public async Task DisposeFaultsQueuedWaiters()
    {
        var gate = new AsyncReaderWriterLock();

        gate.EnterReadLock();
        ValueTask writer = gate.EnterWriteLockAsync();

        gate.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await writer);

        // A lock already held may still be released.
        gate.ExitReadLock();
    }

    [Fact]
    public void ExitWithoutHoldingTheLockThrows()
    {
        using var gate = new AsyncReaderWriterLock();

        Assert.Throws<SynchronizationLockException>(gate.ExitReadLock);
        Assert.Throws<SynchronizationLockException>(gate.ExitWriteLock);
    }
}
