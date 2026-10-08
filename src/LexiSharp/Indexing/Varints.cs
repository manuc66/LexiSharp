namespace LexiSharp.Indexing;

/// <summary>
/// Base-128 variable-length integers: the encoding the block layout writes ordinals, frequencies and
/// counts in.
/// </summary>
/// <remarks>
/// Seven bits per byte, the high bit meaning "another byte follows". A posting entry inside a block
/// is two of these — a delta of a few units and a frequency of one or two — so an entry costs two or
/// three bytes against the eight an <c>int</c> pair costs, and the value is still exact.
/// <para>
/// Deliberately not <c>BinaryPrimitives</c> or a compression package: the format has to be readable
/// from a mapped file with nothing loaded beside it, and an encoding that is nine lines of arithmetic
/// is easier to hold to that than a codec whose stream needs a decoder state.
/// </para>
/// </remarks>
internal static class Varints
{
    /// <summary>Number of bytes <see cref="Write"/> would use for <paramref name="value"/>.</summary>
    public static int Size(int value)
    {
        // Two's complement of a negative number has all the high bits set, so the loop is the same
        // one either side: the encoding is a bit pattern, not a sign.
        uint remaining = (uint)value;
        int size = 1;

        while (remaining >= 0x80)
        {
            remaining >>= 7;
            size++;
        }

        return size;
    }

    /// <summary>
    /// Writes <paramref name="value"/> at the start of <paramref name="destination"/> and returns how
    /// many bytes it took, which is <see cref="Size"/>.
    /// </summary>
    public static int Write(int value, Span<byte> destination)
    {
        uint remaining = (uint)value;
        int written = 0;

        while (remaining >= 0x80)
        {
            destination[written++] = (byte)(remaining | 0x80);
            remaining >>= 7;
        }

        destination[written++] = (byte)remaining;

        return written;
    }

    /// <summary>
    /// Reads a value and advances <paramref name="source"/>, or returns <c>false</c> when the bytes
    /// end mid-value.
    /// </summary>
    public static bool TryRead(ref ReadOnlySpan<byte> source, out int value)
    {
        uint result = 0;
        int shift = 0;
        int read = 0;

        while (true)
        {
            if (read >= source.Length || shift > 28)
            {
                value = 0;
                return false;
            }

            byte current = source[read++];
            result |= (uint)(current & 0x7F) << shift;

            if ((current & 0x80) == 0)
                break;

            shift += 7;
        }

        source = source[read..];
        value = (int)result;

        return true;
    }
}
