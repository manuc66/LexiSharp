using System.Buffers;
using System.Buffers.Binary;

namespace LexiSharp.Indexing;

/// <summary>
/// The block layout one term's postings are written in: ordinals and frequencies grouped into
/// document-aligned windows, delta- and varint-encoded, with the bound a pruned scoring pass needs
/// per window and a skip table to reach a window without reading the ones before it.
/// </summary>
/// <remarks>
/// <para>
/// Why a layout and not the two <c>int[]</c> the index holds today: a pruned pass has to compare the
/// bounds of several terms' current windows at once, and a memory-mapped segment has to read postings
/// where they lie instead of materializing them. The flat pair can do neither — it is a derived,
/// rebuilt, garbage-collected copy — so this is the shape both will read, and it is written once
/// rather than once per consumer.
/// </para>
/// <para>
/// A block covers the ordinal window <c>[w × <see cref="BlockSpan"/>, (w + 1) × <see cref="BlockSpan"/>)</c>
/// and holds only the entries that window contains. The windows are aligned across terms, which is
/// what makes their bounds summable; a term absent from a window simply has no block for it.
/// </para>
/// <para>
/// Layout of one term's region:
/// <code>
///   varint  blockCount
///   uint32  windowIndex of block 0, of block SkipEvery, …   (so a target window can be looked up)
///   uint32  offset of that block from the region start
///   blocks, in ascending window order:
///     varint windowIndex        ordinal window, = firstOrdinal / BlockSpan
///     varint count              entries in the block, 1 … BlockSpan
///     varint lastOrdinalDelta   last entry's ordinal − windowIndex × BlockSpan
///     varint maxFrequency       largest term frequency in the block
///     varint minDocumentLength  smallest document length in the block
///     count × ( varint ordinalDelta, varint frequency )
/// </code>
/// where each ordinal delta is from the ordinal before it, or from the window base for the first.
/// </para>
/// <para>
/// <see cref="BlockSpan"/> is 128 because a smaller window buys tighter bounds and pays for them in
/// skip-table bytes and block headers; 128 is the value a pruned pass is usually built around. That
/// it is right *here* is not measured.
/// </para>
/// </remarks>
internal static class PostingBlocks
{
    /// <summary>Ordinals per block: one block covers <c>[w × BlockSpan, (w + 1) × BlockSpan)</c>.</summary>
    public const int BlockSpan = 128;

    /// <summary>Number of blocks between two entries of the skip table.</summary>
    public const int SkipEvery = 8;

    /// <summary>Entries in the skip table for <paramref name="blockCount"/> blocks.</summary>
    public static int SkipCount(int blockCount) => (blockCount + SkipEvery - 1) / SkipEvery;

    /// <summary>Bytes one entry of the skip table takes: a window index and an offset.</summary>
    public const int SkipEntryBytes = 2 * sizeof(uint);

    /// <summary>Bytes <see cref="Write"/> would write for these postings.</summary>
    /// <remarks>
    /// <paramref name="ordinals"/> must be strictly ascending and <paramref name="lengths"/> indexed
    /// by ordinal, which is what the index's flat copies are.
    /// </remarks>
    public static int Measure(ReadOnlySpan<int> ordinals, ReadOnlySpan<int> frequencies, ReadOnlySpan<int> lengths)
    {
        int blockCount = CountBlocks(ordinals);
        int total = Varints.Size(blockCount) + (SkipCount(blockCount) * SkipEntryBytes);

        int index = 0;

        while (index < ordinals.Length)
        {
            int end = WindowEnd(ordinals, index);
            total += BlockSize(ordinals, frequencies, lengths, index, end);
            index = end;
        }

        return total;
    }

    /// <summary>
    /// Writes the region and returns how many bytes it took, which is <see cref="Measure"/> for the
    /// same postings.
    /// </summary>
    public static int Write(
        ReadOnlySpan<int> ordinals,
        ReadOnlySpan<int> frequencies,
        ReadOnlySpan<int> lengths,
        Span<byte> destination)
    {
        int blockCount = CountBlocks(ordinals);
        int skipCount = SkipCount(blockCount);
        int[] skipWindows = ArrayPool<int>.Shared.Rent(skipCount == 0 ? 1 : skipCount);
        int[] skipOffsets = ArrayPool<int>.Shared.Rent(skipCount == 0 ? 1 : skipCount);

        try
        {
            // The table comes before the blocks, so it is known before they are written: one walk to
            // record where each skip-table block starts, then one to write them.
            int header = Varints.Size(blockCount) + (skipCount * SkipEntryBytes);
            int position = header;
            int block = 0;
            int index = 0;

            while (index < ordinals.Length)
            {
                int end = WindowEnd(ordinals, index);

                if (block % SkipEvery == 0)
                {
                    skipWindows[block / SkipEvery] = ordinals[index] / BlockSpan;
                    skipOffsets[block / SkipEvery] = position;
                }

                position += BlockSize(ordinals, frequencies, lengths, index, end);
                index = end;
                block++;
            }

            int written = Varints.Write(blockCount, destination);

            for (int i = 0; i < skipCount; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(destination[written..], (uint)skipWindows[i]);
                BinaryPrimitives.WriteUInt32LittleEndian(destination[(written + sizeof(uint))..], (uint)skipOffsets[i]);
                written += SkipEntryBytes;
            }

            index = 0;

            while (index < ordinals.Length)
            {
                int end = WindowEnd(ordinals, index);
                written += WriteBlock(ordinals, frequencies, lengths, index, end, destination[written..]);
                index = end;
            }

            return written;
        }
        finally
        {
            if (skipCount > 0)
            {
                ArrayPool<int>.Shared.Return(skipWindows);
                ArrayPool<int>.Shared.Return(skipOffsets);
            }
        }
    }

    /// <summary>Number of distinct ordinal windows the postings occupy.</summary>
    private static int CountBlocks(ReadOnlySpan<int> ordinals)
    {
        int blocks = 0;
        int index = 0;

        while (index < ordinals.Length)
        {
            index = WindowEnd(ordinals, index);
            blocks++;
        }

        return blocks;
    }

    /// <summary>Exclusive end of the run of entries sharing the window the entry at <paramref name="start"/> is in.</summary>
    private static int WindowEnd(ReadOnlySpan<int> ordinals, int start)
    {
        int window = ordinals[start] / BlockSpan;
        int end = start + 1;

        while (end < ordinals.Length && ordinals[end] / BlockSpan == window)
            end++;

        return end;
    }

    private static int BlockSize(
        ReadOnlySpan<int> ordinals,
        ReadOnlySpan<int> frequencies,
        ReadOnlySpan<int> lengths,
        int start,
        int end)
    {
        int window = ordinals[start] / BlockSpan;
        (int maxFrequency, int minLength) = BlockBounds(ordinals, frequencies, lengths, start, end);

        int size = Varints.Size(window)
            + Varints.Size(end - start)
            + Varints.Size(ordinals[end - 1] - (window * BlockSpan))
            + Varints.Size(maxFrequency)
            + Varints.Size(minLength);

        int previous = window * BlockSpan;

        for (int i = start; i < end; i++)
        {
            size += Varints.Size(ordinals[i] - previous) + Varints.Size(frequencies[i]);
            previous = ordinals[i];
        }

        return size;
    }

    private static (int MaxFrequency, int MinLength) BlockBounds(
        ReadOnlySpan<int> ordinals,
        ReadOnlySpan<int> frequencies,
        ReadOnlySpan<int> lengths,
        int start,
        int end)
    {
        int maxFrequency = 0;
        int minLength = int.MaxValue;

        for (int i = start; i < end; i++)
        {
            if (frequencies[i] > maxFrequency)
                maxFrequency = frequencies[i];

            int length = lengths[ordinals[i]];

            if (length < minLength)
                minLength = length;
        }

        return (maxFrequency, minLength);
    }

    private static int WriteBlock(
        ReadOnlySpan<int> ordinals,
        ReadOnlySpan<int> frequencies,
        ReadOnlySpan<int> lengths,
        int start,
        int end,
        Span<byte> destination)
    {
        int window = ordinals[start] / BlockSpan;
        (int maxFrequency, int minLength) = BlockBounds(ordinals, frequencies, lengths, start, end);

        int written = Varints.Write(window, destination);
        written += Varints.Write(end - start, destination[written..]);
        written += Varints.Write(ordinals[end - 1] - (window * BlockSpan), destination[written..]);
        written += Varints.Write(maxFrequency, destination[written..]);
        written += Varints.Write(minLength, destination[written..]);

        int previous = window * BlockSpan;

        for (int i = start; i < end; i++)
        {
            written += Varints.Write(ordinals[i] - previous, destination[written..]);
            written += Varints.Write(frequencies[i], destination[written..]);
            previous = ordinals[i];
        }

        return written;
    }
}
