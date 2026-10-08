using System;
using System.Buffers.Binary;

namespace LexiSharp.Indexing;

/// <summary>
/// Where a segment's bytes come from: a byte array today, a memory-mapped view next.
/// </summary>
/// <remarks>
/// <para>
/// The reader needs three things from its bytes and no more: a length, a 32-bit word at an offset, and a
/// range of bytes — either viewed (when the source can hand one out) or copied into a buffer the caller
/// owns. That is the whole seam: a source that can view is as fast as the reader ever was, and a source
/// that cannot still serves every read with a copy.
/// </para>
/// <para>
/// <b>A source-backed reader must not be interleaved with another reader over the same source.</b> A
/// block reader parses its current block out of the caller's scratch buffer, and a second reader would
/// overwrite it under the first. The index creates its readers one at a time and finishes with each
/// before it asks for the next, which is what makes one scratch buffer per index safe — and it is a
/// property of the callers, not something this type can enforce.
/// </para>
/// </remarks>
internal abstract class SegmentSource
{
    /// <summary>Bytes the segment holds.</summary>
    public abstract int Length { get; }

    /// <summary>The little-endian 32-bit word at <paramref name="offset"/>.</summary>
    public abstract int ReadInt32(int offset);

    /// <summary>The little-endian 64-bit word at <paramref name="offset"/>.</summary>
    public abstract long ReadInt64(int offset);

    /// <summary>
    /// The bytes at <c>[offset, offset + length)</c> as a span, when this source can hand one out
    /// without copying. A source backed by a mapped file returns <c>false</c>.
    /// </summary>
    public abstract bool TryView(int offset, int length, out ReadOnlySpan<byte> view);

    /// <summary>Copies <c>[offset, offset + length)</c> into <paramref name="destination"/>.</summary>
    public abstract void Read(int offset, int length, byte[] destination);
}

/// <summary>A segment held in a byte array: every read is a slice of it.</summary>
internal sealed class ArraySegmentSource(byte[] bytes) : SegmentSource
{
    public override int Length => bytes.Length;

    public override int ReadInt32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));

    public override long ReadInt64(int offset) => BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset));

    public override bool TryView(int offset, int length, out ReadOnlySpan<byte> view)
    {
        view = bytes.AsSpan(offset, length);

        return true;
    }

    public override void Read(int offset, int length, byte[] destination) =>
        bytes.AsSpan(offset, length).CopyTo(destination);
}
