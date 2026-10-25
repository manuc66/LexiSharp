using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace LexiSharp.Indexing;

/// <summary>How long a write-ahead journal entry may sit unflushed.</summary>
/// <remarks>
/// The choice is a trade of throughput for the window a power loss can reach back through, and the
/// three options are priced rather than described: on one NVMe host, appending a 1 KiB entry costs
/// <b>0.5 µs</b> without a flush, <b>3.1 ms</b> with one, and <b>33 µs</b> when one flush covers a
/// hundred entries. Those are one host's figures and a device's flush latency is not a constant, so
/// the ordering is what the choice rests on rather than the numbers.
/// </remarks>
public enum JournalFlushPolicy
{
    /// <summary>
    /// Flush when <see cref="JournalOptions.MaxRecords"/> entries have accumulated, when
    /// <see cref="JournalOptions.MaxDelay"/> has passed since the last flush, or on <c>Commit</c>.
    /// The default: a power loss reaches back at most that many entries, and the flush is amortized over
    /// all of them.
    /// </summary>
    Grouped = 0,

    /// <summary>Flush every entry before the call that wrote it returns. The strictest window, at the
    /// price of a device flush per write.</summary>
    PerWrite = 1,

    /// <summary>
    /// Never flush outside <c>Commit</c>. Entries reach the operating system as they are written, so a
    /// process that crashes loses nothing and a power loss loses whatever the kernel had not written
    /// back — a window the journal cannot bound.
    /// </summary>
    OnCommit = 2,
}

/// <summary>
/// What a store's write-ahead journal does: whether it exists, when it flushes, and how much it may let
/// accumulate.
/// </summary>
/// <remarks>
/// <para>
/// A store journals every write so that the overlay survives a crash: the segment on disk is the corpus
/// as of the last commit, and the journal carries what was written since. Opening the store replays the
/// entries the segment does not already contain.
/// </para>
/// <para>
/// A store created with <see cref="Disabled"/> keeps today's behaviour — writes are durable at
/// <c>Commit</c> and lost before it. That is the right choice for a corpus assembled as one batch and
/// committed at the end, and the wrong one for a server that writes while it serves.
/// </para>
/// </remarks>
public sealed record JournalOptions
{
    /// <summary>Grouped flushing, a hundred entries or fifty milliseconds, whichever comes first.</summary>
    public static readonly JournalOptions Default = new();

    /// <summary>No journal: writes are durable at <c>Commit</c> and lost on a crash before it.</summary>
    public static readonly JournalOptions Disabled = new() { Enabled = false };

    /// <summary>Whether a journal is written at all. Default <c>true</c>.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>When an entry is forced to the device. Default <see cref="JournalFlushPolicy.Grouped"/>.</summary>
    public JournalFlushPolicy Flush { get; init; } = JournalFlushPolicy.Grouped;

    /// <summary>
    /// How many entries may accumulate before a grouped flush, and before a power loss could take them.
    /// Default 100. Ignored unless <see cref="Flush"/> is <see cref="JournalFlushPolicy.Grouped"/>.
    /// </summary>
    public int MaxRecords { get; init; } = 100;

    /// <summary>
    /// How long a grouped journal may wait before flushing. Default 50 ms. The bound is checked when an
    /// entry is appended and when the journal is disposed, so a writer that stops stops with its last
    /// flushed entry: this bounds the window of a stream of writes, not of a quiet one. Call
    /// <c>FlushJournal</c> to close the window at a cadence of your own.
    /// </summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// The options a store should use, and the one place an invalid combination is refused.
    /// </summary>
    internal JournalOptions Validated()
    {
        if (MaxRecords < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxRecords), MaxRecords, "a grouped journal needs at least one entry per flush");

        if (MaxDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(MaxDelay), MaxDelay, "a flush delay cannot be negative");

        return this;
    }
}
