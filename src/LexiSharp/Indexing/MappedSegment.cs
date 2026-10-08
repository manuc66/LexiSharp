using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using LexiSharp.Linguistics;

namespace LexiSharp.Indexing;

/// <summary>
/// A <see cref="SegmentSource"/> over a memory-mapped view of a file: every read is a copy out of the
/// mapping, never a span, which is what keeps <see cref="System.Runtime.CompilerServices.Unsafe"/> out
/// of the codebase.
/// </summary>
internal sealed class MappedSegmentSource : SegmentSource
{
    private readonly MemoryMappedViewAccessor _accessor;

    public MappedSegmentSource(MemoryMappedViewAccessor accessor)
    {
        _accessor = accessor;
    }

    public override int Length => (int)_accessor.Capacity;

    public override int ReadInt32(int offset) => _accessor.ReadInt32(offset);

    public override long ReadInt64(int offset) => _accessor.ReadInt64(offset);

    public override bool TryView(int offset, int length, out ReadOnlySpan<byte> view)
    {
        view = default;

        return false;
    }

    public override void Read(int offset, int length, byte[] destination) =>
        _accessor.ReadArray(offset, destination, 0, length);
}

/// <summary>
/// A segment held as a memory-mapped file, and the index that searches it.
/// </summary>
/// <remarks>
/// The map has a lifetime, and the index depends on it, so the two are disposed together — a
/// <see cref="SegmentTextIndex"/> alone cannot tell when it is safe to unmap the file underneath it.
/// Dispose is idempotent, and the index is unusable after it, which is a statement about the mapping
/// rather than about the search.
/// </remarks>
internal sealed class MappedSegment : IDisposable
{
    private MemoryMappedFile? _file;
    private MemoryMappedViewAccessor? _accessor;
    private readonly SegmentTextIndex _index;

    private MappedSegment(MemoryMappedFile file, MemoryMappedViewAccessor accessor, SegmentTextIndex index)
    {
        _file = file;
        _accessor = accessor;
        _index = index;
    }

    /// <summary>The index over the mapped segment, valid until <see cref="Dispose"/>.</summary>
    public SegmentTextIndex Index => _index;

    /// <summary>Maps the segment at <paramref name="path"/> and opens an index over it.</summary>
    public static MappedSegment Open(string path, ITokenizer? tokenizer = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        var file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        var accessor = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

        try
        {
            return new MappedSegment(file, accessor, new SegmentTextIndex(new MappedSegmentSource(accessor), tokenizer));
        }
        catch
        {
            accessor.Dispose();
            file.Dispose();
            throw;
        }
    }

    /// <summary>Unmaps the file. Idempotent; the <see cref="Index"/> is unusable afterwards.</summary>
    public void Dispose()
    {
        _accessor?.Dispose();
        _file?.Dispose();
        _accessor = null;
        _file = null;
    }
}