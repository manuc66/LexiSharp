using System;
using System.Buffers.Binary;
using System.IO;

namespace LexiSharp.Indexing;

/// <summary>
/// Reads one term's postings out of a region written by <see cref="PostingBlocks"/>: a forward cursor
/// over blocks, and within a block over its entries.
/// </summary>
/// <remarks>
/// <para>
/// A cursor rather than a decoded array, and that is the point of the format: a pruned pass reads the
/// bound of a block, decides, and often moves on without touching its entries, and a mapped segment
/// cannot hand out an array at all. Nothing here copies the region when it can be viewed, and nothing
/// holds a whole postings list when it cannot, a source-backed reader fetches a window into
/// a buffer its caller owns and feeds several blocks from it before fetching again.
/// </para>
/// <para>
/// The cursor moves forward only: there is no going back to a block already passed, because the skip
/// table is what makes a forward jump cheap and a backward one impossible without rereading.
/// </para>
/// <para>
/// A fetched block lands in the scratch buffer the caller passed, so <b>two source-backed readers over
/// the same buffer must not be interleaved</b>. The index creates one at a time and finishes with each
/// before it asks for the next, which is what makes one buffer per index safe.
/// </para>
/// </remarks>
internal ref struct PostingBlockReader
{
    private readonly ReadOnlySpan<byte> _region;
    private readonly SegmentSource? _source;
    private readonly int _regionOffset;
    private readonly int _regionLength;
    private readonly byte[]? _scratch;
    private readonly ReadOnlySpan<byte> _skip;
    private readonly int _skipCount;
    private readonly int _blockCount;
    private int _windowOffset;
    private int _windowLength;
    private ReadOnlySpan<byte> _entries;
    private int _position;
    private int _blockIndex;
    private int _previousOrdinal;

    /// <summary>Positions the cursor before the first block of a region held in memory.</summary>
    /// <exception cref="InvalidDataException">The region does not hold a postings list.</exception>
    public PostingBlockReader(ReadOnlySpan<byte> region)
    {
        _region = region;
        _source = null;
        _regionOffset = 0;
        _regionLength = region.Length;
        _scratch = null;

        var remaining = region;
        int countBytes = region.Length;

        if (!Varints.TryRead(ref remaining, out int blockCount) || blockCount < 0)
            throw new InvalidDataException("the posting region does not start with a block count");

        countBytes -= remaining.Length;

        int skipCount = PostingBlocks.SkipCount(blockCount);
        int skipBytes = skipCount * PostingBlocks.SkipEntryBytes;

        if (remaining.Length < skipBytes)
            throw new InvalidDataException("the posting region ends inside its skip table");

        _blockCount = blockCount;
        _skipCount = skipCount;
        _skip = remaining[..skipBytes];
        _position = countBytes + skipBytes;
        _entries = default;
        _blockIndex = -1;
        _previousOrdinal = 0;
        LastOrdinal = int.MinValue;
    }

    /// <summary>
    /// Positions the cursor before the first block of a region a source holds, fetching one block at a
    /// time into <paramref name="scratch"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The region does not hold a postings list.</exception>
    public PostingBlockReader(SegmentSource source, int offset, int length, byte[] scratch)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(scratch);

        if (scratch.Length < FetchWindow)
            throw new ArgumentException($"a source-backed reader needs at least {FetchWindow} bytes of scratch", nameof(scratch));

        _region = default;
        _source = source;
        _regionOffset = offset;
        _regionLength = length;
        _scratch = scratch;
        _skip = default;

        // The header is small and fixed: a window is enough to read the count and the table it announces.
        int window = Math.Min(scratch.Length, length);

        if (window <= 0)
            throw new InvalidDataException("the posting region is empty");

        source.Read(offset, window, scratch);

        ReadOnlySpan<byte> remaining = scratch.AsSpan(0, window);
        int countBytes = window;

        if (!Varints.TryRead(ref remaining, out int blockCount) || blockCount < 0)
            throw new InvalidDataException("the posting region does not start with a block count");

        countBytes -= remaining.Length;

        int skipCount = PostingBlocks.SkipCount(blockCount);
        int skipBytes = skipCount * PostingBlocks.SkipEntryBytes;

        if (remaining.Length < skipBytes)
            throw new InvalidDataException("the posting region ends inside its skip table");

        _blockCount = blockCount;
        _skipCount = skipCount;
        _position = countBytes + skipBytes;
        _entries = default;
        _blockIndex = -1;
        _previousOrdinal = 0;
        LastOrdinal = int.MinValue;
        _windowOffset = 0;
        _windowLength = window;
    }

    /// <summary>Bytes a source-backed reader fetches at a time; a block cannot exceed it.</summary>
    /// <remarks>
    /// A block holds at most 128 entries, and an entry is two varints of at most five bytes each, so a
    /// block is under 1.4 KB and a 2 KB window always holds one.
    /// </remarks>
    private const int FetchWindow = 2048;

    /// <summary>
    /// Largest block the format can write: its six header varints (at most five bytes each) and 128
    /// entries of two varints of at most five bytes. A window holds at least this much before a block
    /// starts, which is why a walk can feed several blocks from one window.
    /// </summary>
    private const int MaxBlockBytes = 1536;

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

        if (!TryDecodeBlock(Load(_position), out int consumed))
            throw new InvalidDataException("the posting region ends inside a block");

        _position += consumed;
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

    /// <summary>The bytes from <paramref name="position"/> on: the region when it can be viewed, a fetched window otherwise.</summary>
    /// <remarks>
    /// A fetched window feeds several blocks: the walk refetches only when a block could straddle the
    /// window's end (<see cref="MaxBlockBytes"/> bytes of scrap), instead of copying the same bytes
    /// again for every block — a head term of 313 blocks once paid 313 reads, each re-fetching the
    /// window. The window is the caller's scratch, so it must be valid as `_scratch` and is read as
    /// `_scratch`. A block that lands entirely inside the window is decoded from it; a block that
    /// would start in the scrap gets a fresh window. A returned span is valid until the next load.
    /// </remarks>
    private ReadOnlySpan<byte> Load(int position)
    {
        if (_source is null)
        {
            if (position < 0 || position > _region.Length)
                throw new InvalidDataException("a block starts outside the posting region");

            return _region[position..];
        }

        int available = _regionLength - position;

        if (available <= 0)
            throw new InvalidDataException("a block starts outside the posting region");

        int within = position - _windowOffset;

        // The walk holds the invariant that a block starts with at least MaxBlockBytes of its window
        // ahead of it, so a block never straddles the window's end; once that scrap is gone, fetch.
        if (_windowLength - within < MaxBlockBytes)
        {
            int window = Math.Min(_scratch!.Length, available);
            _source.Read(_regionOffset + position, window, _scratch);
            _windowOffset = position;
            _windowLength = window;
            within = 0;
        }

        return _scratch.AsSpan(within, _windowLength - within);
    }

    /// <summary>The window stored for skip entry <paramref name="entry"/>.</summary>
    private readonly int SkipWindow(int entry) =>
        _source is null
            ? (int)BinaryPrimitives.ReadUInt32LittleEndian(_skip.Slice(entry * PostingBlocks.SkipEntryBytes))
            : _source.ReadInt32(_regionOffset + (entry * PostingBlocks.SkipEntryBytes));

    /// <summary>The block offset stored for skip entry <paramref name="entry"/>, from the region's start.</summary>
    private readonly int SkipOffset(int entry) =>
        _source is null
            ? BinaryPrimitives.ReadInt32LittleEndian(_skip.Slice((entry * PostingBlocks.SkipEntryBytes) + sizeof(int)))
            : _source.ReadInt32(_regionOffset + (entry * PostingBlocks.SkipEntryBytes) + sizeof(int));

    /// <summary>Jumps the cursor to a block the skip table has an offset for.</summary>
    private void Seek(int blockIndex)
    {
        _position = SkipOffset(blockIndex / PostingBlocks.SkipEvery);
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

            if (SkipWindow(middle) <= window)
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
            !Varints.TryRead(ref remaining, out int entryBytes) ||
            !Varints.TryRead(ref remaining, out int lastDelta) ||
            !Varints.TryRead(ref remaining, out int maxFrequency) ||
            !Varints.TryRead(ref remaining, out int minLength) ||
            count <= 0 ||
            count > PostingBlocks.BlockSpan ||
            window < 0 ||
            lastDelta < 0 ||
            entryBytes < 2 ||
            entryBytes > PostingBlocks.BlockSpan * 10)
        {
            consumed = 0;
            return false;
        }

        int headerBytes = block.Length - remaining.Length;

        if (headerBytes + entryBytes > block.Length)
        {
            consumed = 0;
            return false;
        }

        // The block carries its own length, so its entries are reached without first walking them to
        // find where the block ends — which is what the reader once paid a whole extra decode for. The
        // writer's length is trusted; a corrupt one surfaces as an entry walk that ends early rather
        // than as an exception.
        WindowIndex = window;
        Count = count;
        MaxFrequency = maxFrequency;
        MinDocumentLength = minLength;
        LastOrdinal = (window * PostingBlocks.BlockSpan) + lastDelta;
        _entries = block.Slice(headerBytes, entryBytes);
        _previousOrdinal = window * PostingBlocks.BlockSpan;
        consumed = headerBytes + entryBytes;

        return true;
    }
}
