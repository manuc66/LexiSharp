using System.Buffers.Binary;
using System.IO;

namespace LexiSharp.Indexing;

/// <summary>
/// Reads one term's postings out of a region written by <see cref="PostingBlocks"/>: a forward
/// cursor over blocks, and within a block over its entries.
/// </summary>
/// <remarks>
/// <para>
/// A cursor rather than a decoded array, and that is the point of the format: a pruned pass reads the
/// bound of a block, decides, and often moves on without touching its entries, and a mapped segment
/// cannot hand out an array at all. Nothing here copies the region.
/// </para>
/// <para>
/// The cursor moves forward only — there is no going back to a block already passed, because the skip
/// table is what makes a forward jump cheap and a backward one impossible without rereading.
/// </para>
/// </remarks>
internal ref struct PostingBlockReader
{
    private readonly ReadOnlySpan<byte> _region;
    private readonly ReadOnlySpan<byte> _skip;
    private readonly int _skipCount;
    private readonly int _blockCount;
    private ReadOnlySpan<byte> _position;
    private ReadOnlySpan<byte> _entries;
    private int _blockIndex;
    private int _previousOrdinal;

    /// <summary>Positions the cursor before the first block of <paramref name="region"/>.</summary>
    /// <exception cref="InvalidDataException">The region does not hold a posting region.</exception>
    public PostingBlockReader(ReadOnlySpan<byte> region)
    {
        _region = region;

        if (!Varints.TryRead(ref region, out int blockCount) || blockCount < 0)
            throw new InvalidDataException("the posting region does not start with a block count");

        int skipCount = PostingBlocks.SkipCount(blockCount);
        int skipBytes = skipCount * PostingBlocks.SkipEntryBytes;

        if (region.Length < skipBytes)
            throw new InvalidDataException("the posting region ends inside its skip table");

        _blockCount = blockCount;
        _skipCount = skipCount;
        _skip = region[..skipBytes];
        _position = region[skipBytes..];
        _entries = default;
        _blockIndex = -1;
        _previousOrdinal = 0;
        LastOrdinal = int.MinValue;
    }

    /// <summary>Number of blocks the region holds.</summary>
    public int BlockCount => _blockCount;

    /// <summary>Index of the current block, or <c>-1</c> before the first <see cref="MoveNext"/>.</summary>
    public int BlockIndex => _blockIndex;

    /// <summary>Ordinal window of the current block, = <c>FirstOrdinal / <see cref="PostingBlocks.BlockSpan"/></c>.</summary>
    public int WindowIndex { get; private set; }

    /// <summary>Entries in the current block.</summary>
    public int Count { get; private set; }

    /// <summary>Largest term frequency in the current block.</summary>
    public int MaxFrequency { get; private set; }

    /// <summary>Smallest document length in the current block; with <see cref="MaxFrequency"/> it bounds the block.</summary>
    public int MinDocumentLength { get; private set; }

    /// <summary>Ordinal of the current block's last entry.</summary>
    public int LastOrdinal { get; private set; }

    /// <summary>Ordinal the current block's window starts at.</summary>
    public int FirstOrdinal => WindowIndex * PostingBlocks.BlockSpan;

    /// <summary>Moves to the next block. Returns <c>false</c> past the last one.</summary>
    public bool MoveNext()
    {
        if (_blockIndex + 1 >= _blockCount)
            return false;

        if (!TryDecodeBlock(_position, out int consumed))
            throw new InvalidDataException("the posting region ends inside a block");

        _position = _position[consumed..];
        _blockIndex++;

        return true;
    }

    /// <summary>
    /// Moves forward to the first block that can contain <paramref name="ordinal"/> — the first whose
    /// last entry is at or after it — using the skip table to get near before decoding.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the cursor is on such a block, <c>false</c> when every remaining block ends
    /// before <paramref name="ordinal"/>. The cursor lands on the block's first entry either way, so
    /// a caller walks <see cref="TryReadEntry"/> from there.
    /// </returns>
    public bool AdvanceTo(int ordinal)
    {
        if (_blockIndex >= 0 && LastOrdinal >= ordinal)
            return true;

        int targetWindow = ordinal / PostingBlocks.BlockSpan;

        // The skip table is keyed by window, not by block number: only populated windows have a
        // block, so the block whose window is the last at or below the target is where to start.
        int entry = LastSkipEntryAtOrBefore(targetWindow);
        int block = entry < 0 ? 0 : entry * PostingBlocks.SkipEvery;

        if (block > _blockIndex)
            Seek(block);

        while (MoveNext())
        {
            if (LastOrdinal >= ordinal)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Reads the next entry of the current block, in ascending ordinal order, and returns
    /// <c>false</c> once the block's entries are exhausted.
    /// </summary>
    public bool TryReadEntry(out int ordinal, out int frequency)
    {
        var entries = _entries;

        if (Varints.TryRead(ref entries, out int delta) && Varints.TryRead(ref entries, out frequency))
        {
            ordinal = _previousOrdinal + delta;
            _previousOrdinal = ordinal;
            _entries = entries;

            return true;
        }

        ordinal = 0;
        frequency = 0;

        return false;
    }

    /// <summary>Jumps the cursor to a block the skip table has an offset for.</summary>
    private void Seek(int blockIndex)
    {
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(
            _skip.Slice((blockIndex / PostingBlocks.SkipEvery * PostingBlocks.SkipEntryBytes) + sizeof(uint)));

        _position = _region[(int)offset..];
        _blockIndex = blockIndex - 1;
        _entries = default;
        _previousOrdinal = 0;
        LastOrdinal = int.MinValue;
    }

    /// <summary>
    /// Index of the last skip entry whose window is at or below <paramref name="window"/>, or
    /// <c>-1</c> when the target is before the first one. The entries ascend, so this is a binary
    /// search.
    /// </summary>
    private readonly int LastSkipEntryAtOrBefore(int window)
    {
        int low = 0;
        int high = _skipCount - 1;
        int found = -1;

        while (low <= high)
        {
            int middle = (low + high) / 2;
            uint candidate = BinaryPrimitives.ReadUInt32LittleEndian(
                _skip.Slice(middle * PostingBlocks.SkipEntryBytes));

            if (candidate <= (uint)window)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found;
    }

    private bool TryDecodeBlock(ReadOnlySpan<byte> block, out int consumed)
    {
        var remaining = block;

        if (!Varints.TryRead(ref remaining, out int window) ||
            !Varints.TryRead(ref remaining, out int count) ||
            !Varints.TryRead(ref remaining, out int lastDelta) ||
            !Varints.TryRead(ref remaining, out int maxFrequency) ||
            !Varints.TryRead(ref remaining, out int minLength) ||
            count <= 0 ||
            count > PostingBlocks.BlockSpan ||
            window < 0 ||
            lastDelta < 0)
        {
            consumed = 0;
            return false;
        }

        int headerBytes = block.Length - remaining.Length;
        var entries = remaining;
        int previous = window * PostingBlocks.BlockSpan;
        int last = previous;

        for (int i = 0; i < count; i++)
        {
            if (!Varints.TryRead(ref entries, out int delta) || !Varints.TryRead(ref entries, out _))
            {
                consumed = 0;
                return false;
            }

            last = previous + delta;
            previous = last;
        }

        int entryBytes = (block.Length - headerBytes) - entries.Length;

        if (last != (window * PostingBlocks.BlockSpan) + lastDelta)
        {
            // The stored last entry and the deltas disagree. Cheap to check here and it is the one
            // relationship a reader uses to skip a block without decoding it.
            consumed = 0;
            return false;
        }

        WindowIndex = window;
        Count = count;
        MaxFrequency = maxFrequency;
        MinDocumentLength = minLength;
        LastOrdinal = last;
        _entries = block.Slice(headerBytes, entryBytes);
        _previousOrdinal = window * PostingBlocks.BlockSpan;
        consumed = headerBytes + entryBytes;

        return true;
    }
}
