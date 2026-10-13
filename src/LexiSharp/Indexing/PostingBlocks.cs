using System.Buffers;
using System.Buffers.Binary;

namespace LexiSharp.Indexing;

/// <summary>
/// The block layout one term's postings are written in: ordinals and frequencies grouped into
/// document-aligned windows, delta-encoded and packed into byte-aligned planes, with the bound a
/// pruned scoring pass needs per window and a skip table to reach a window without reading the ones
/// before it.
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
///     varint entriesBytes       bytes the two planes take — the block's own length
///     varint lastOrdinalDelta   last entry's ordinal − windowIndex × BlockSpan
///     varint maxFrequency       largest term frequency in the block
///     varint minDocumentLength  smallest document length in the block
///     varint planeForm          entry widths: bit0 frequency, bit1 ordinal, bit2 position (0 = u8)
///     count × ordinal delta     packed at 8 or 16 bits, from the ordinal before, or the window base
///     count × frequency         packed at 8 or 16 bits
///     varint positionsBytes     bytes the positions plane takes
///     Σ frequency × position    packed at 8 or 16 bits: each entry's first position, then deltas
/// </code>
/// where each ordinal delta is from the ordinal before it, or from the window base for the first. The
/// planes are byte-aligned and indexed, so decoding an entry is two aligned loads — no per-entry bit
/// shuffling, which is the point of the format. A block may hold values larger than 255 in a plane:
/// that plane widens to 16 bits for the whole block, which is the lossless floor of the trade.
/// </para>
/// <para>
/// <b>Positions.</b> The third plane holds, for every entry in order, that entry's term positions: the
/// first absolute within the document, each later one a delta from the position before it. A segment
/// that stores them answers <c>GetTermPositions</c> by decoding one entry instead of tokenizing the
/// document, which is what the phrase gate and the proximity reranker ask for per candidate. A reader
/// that does not want them never decodes the plane — the header says how many bytes it takes, so a
/// block's length is known without summing the frequencies.
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
    /// by ordinal, which is what the index's flat copies are. <paramref name="positions"/> holds, in
    /// order, <c>frequencies[i]</c> positions for entry <c>i</c>, ascending within the entry — so its
    /// length is the sum of <paramref name="frequencies"/>.
    /// </remarks>
    public static int Measure(
        ReadOnlySpan<int> ordinals,
        ReadOnlySpan<int> frequencies,
        ReadOnlySpan<int> lengths,
        ReadOnlySpan<int> positions)
    {
        int blockCount = CountBlocks(ordinals);
        int total = Varints.Size(blockCount) + (SkipCount(blockCount) * SkipEntryBytes);

        int index = 0;
        int positionStart = 0;

        while (index < ordinals.Length)
        {
            int end = WindowEnd(ordinals, index);
            total += BlockSize(ordinals, frequencies, lengths, positions, positionStart, index, end);
            positionStart += FrequencySum(frequencies, index, end);
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
        ReadOnlySpan<int> positions,
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
            int positionStart = 0;

            while (index < ordinals.Length)
            {
                int end = WindowEnd(ordinals, index);

                if (block % SkipEvery == 0)
                {
                    skipWindows[block / SkipEvery] = ordinals[index] / BlockSpan;
                    skipOffsets[block / SkipEvery] = position;
                }

                position += BlockSize(ordinals, frequencies, lengths, positions, positionStart, index, end);
                positionStart += FrequencySum(frequencies, index, end);
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
            positionStart = 0;

            while (index < ordinals.Length)
            {
                int end = WindowEnd(ordinals, index);
                written += WriteBlock(
                    ordinals, frequencies, lengths, positions, positionStart, index, end, destination[written..]);

                positionStart += FrequencySum(frequencies, index, end);
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
        ReadOnlySpan<int> positions,
        int positionStart,
        int start,
        int end)
    {
        int window = ordinals[start] / BlockSpan;
        (int maxFrequency, int minLength) = BlockBounds(ordinals, frequencies, lengths, start, end);
        (int entriesBytes, int form, int lastOrdinal) = EntryLayout(ordinals, frequencies, start, end);
        (int positionsBytes, int positionWidth) = PositionLayout(frequencies, positions, positionStart, start, end);

        form |= (positionWidth - 1) << 2;

        // The block carries its own length, which is what lets a reader reach the scoring entries
        // without first walking them to find where the block ends.
        return Varints.Size(window)
            + Varints.Size(end - start)
            + Varints.Size(entriesBytes)
            + Varints.Size(lastOrdinal - (window * BlockSpan))
            + Varints.Size(maxFrequency)
            + Varints.Size(minLength)
            + Varints.Size(form)
            + entriesBytes
            + Varints.Size(positionsBytes)
            + positionsBytes;
    }

    /// <summary>Total term frequency of the entries in <c>[start, end)</c> — the positions plane's value count.</summary>
    private static int FrequencySum(ReadOnlySpan<int> frequencies, int start, int end)
    {
        int total = 0;

        for (int i = start; i < end; i++)
            total += frequencies[i];

        return total;
    }

    /// <summary>
    /// Bytes the positions plane takes and the width it is packed at: for every entry in order, its
    /// first position absolute and each later one a delta from the position before it.
    /// </summary>
    private static (int Bytes, int Width) PositionLayout(
        ReadOnlySpan<int> frequencies,
        ReadOnlySpan<int> positions,
        int positionStart,
        int start,
        int end)
    {
        int count = 0;
        int max = 0;
        int at = positionStart;

        for (int i = start; i < end; i++)
        {
            int previous = 0;

            for (int k = 0; k < frequencies[i]; k++)
            {
                int value = positions[at++];
                int encoded = k == 0 ? value : value - previous;

                if (encoded > max)
                    max = encoded;

                previous = value;
                count++;
            }
        }

        int width = max <= byte.MaxValue ? 1 : 2;

        return (count * width, width);
    }

    /// <summary>
    /// Bytes the two entry planes take, how they are packed (<c>form</c>: 0 u8/u8, 1 u8/u16, 2 u16/u8,
    /// 3 u16/u16), and the last ordinal they reach.
    /// </summary>
    private static (int EntriesBytes, int Form, int LastOrdinal) EntryLayout(
        ReadOnlySpan<int> ordinals,
        ReadOnlySpan<int> frequencies,
        int start,
        int end)
    {
        int window = ordinals[start] / BlockSpan;
        int previous = window * BlockSpan;
        int maxDelta = 0;
        int maxFrequency = 0;

        for (int i = start; i < end; i++)
        {
            int delta = ordinals[i] - previous;

            if (delta > maxDelta)
                maxDelta = delta;

            if (frequencies[i] > maxFrequency)
                maxFrequency = frequencies[i];

            previous = ordinals[i];
        }

        int deltaBytes = maxDelta <= byte.MaxValue ? 1 : 2;
        int frequencyBytes = maxFrequency <= byte.MaxValue ? 1 : 2;
        int form = ((deltaBytes - 1) * 2) + (frequencyBytes - 1);

        return ((end - start) * (deltaBytes + frequencyBytes), form, previous);
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
        ReadOnlySpan<int> positions,
        int positionStart,
        int start,
        int end,
        Span<byte> destination)
    {
        int window = ordinals[start] / BlockSpan;
        (int maxFrequency, int minLength) = BlockBounds(ordinals, frequencies, lengths, start, end);
        (int entriesBytes, int form, int lastOrdinal) = EntryLayout(ordinals, frequencies, start, end);
        (int positionsBytes, int positionWidth) = PositionLayout(frequencies, positions, positionStart, start, end);

        form |= (positionWidth - 1) << 2;

        int written = Varints.Write(window, destination);
        written += Varints.Write(end - start, destination[written..]);
        written += Varints.Write(entriesBytes, destination[written..]);
        written += Varints.Write(lastOrdinal - (window * BlockSpan), destination[written..]);
        written += Varints.Write(maxFrequency, destination[written..]);
        written += Varints.Write(minLength, destination[written..]);
        written += Varints.Write(form, destination[written..]);
        written += Varints.Write(positionsBytes, destination[written..]);

        int deltaBytes = 1 + ((form >> 1) & 1);
        int frequencyBytes = 1 + (form & 1);
        int positionBytes = 1 + ((form >> 2) & 1);
        int previous = window * BlockSpan;

        for (int i = start; i < end; i++)
        {
            int delta = ordinals[i] - previous;

            if (deltaBytes == 1)
                destination[written++] = (byte)delta;
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(destination[written..], (ushort)delta);
                written += sizeof(ushort);
            }

            previous = ordinals[i];
        }

        for (int i = start; i < end; i++)
        {
            if (frequencyBytes == 1)
                destination[written++] = (byte)frequencies[i];
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(destination[written..], (ushort)frequencies[i]);
                written += sizeof(ushort);
            }
        }

        int at = positionStart;

        for (int i = start; i < end; i++)
        {
            int previousPosition = 0;

            for (int k = 0; k < frequencies[i]; k++)
            {
                int value = positions[at++];
                int encoded = k == 0 ? value : value - previousPosition;

                previousPosition = value;

                if (positionWidth == 1)
                    destination[written++] = (byte)encoded;
                else
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(destination[written..], (ushort)encoded);
                    written += sizeof(ushort);
                }
            }
        }

        return written;
    }
}
