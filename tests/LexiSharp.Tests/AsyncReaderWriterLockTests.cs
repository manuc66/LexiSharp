using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The async-capable gate behind <see cref="Classification.SynchronizedTextClassifier"/>: readers
/// overlap, one writer excludes them, writers are preferred, and the lock can be released on a
/// different thread from the one that acquired it.
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
