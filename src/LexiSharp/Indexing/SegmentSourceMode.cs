using System;

namespace LexiSharp.Indexing;

/// <summary>How a store opens the segments already in its directory.</summary>
/// <remarks>
/// <para>
/// Two modes, and the choice between them is a measured trade rather than a matter of taste. Both were
/// measured on the same corpus and host, with the two variants alternated inside one loop, and two runs
/// agreeing:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Opening is at parity.</b> 40,000 documents: 6425 µs array against 6644 µs mapped, min of 60
/// rounds. 200,000 documents: 49307 µs array against 46991 µs mapped. Both differences are inside
/// noise, so a mapped source does <i>not</i> open slower — an earlier figure to the contrary was taken
/// on a loaded host and is withdrawn.
/// </description></item>
/// <item><description>
/// <b>A mapped source searches 1.10-1.14× slower</b>, because a source that cannot be viewed pays a
/// copy out of the mapping per posting block, and the engine's span fast path never engages.
/// </description></item>
/// <item><description>
/// <b>Mapping allocates about 4.8× less, and the saving scales with the segment size</b> — 12 MB at
/// 40,000 documents, 60 MB at 200,000, so roughly 500 MB at a million. The id to ordinal map is held
/// in memory either way, so what mapping saves is the segment's own bytes.
/// </description></item>
/// </list>
/// <para>
/// So <see cref="Array"/> is the faster search and the simpler lifetime, and <see cref="Mapped"/> is the
/// smaller heap. The default is <see cref="Array"/>: the tax is a constant ~1.11× on every query, and
/// the saving only becomes the deciding factor when a segment is large enough that its copy into memory
/// is what stops the store fitting. Which size that is depends on the machine, and it is not measured
/// here — a caller choosing <see cref="Mapped"/> is choosing the heap, not following a measured
/// optimum.
/// </para>
/// </remarks>
public enum SegmentSourceMode
{
    /// <summary>Read each segment into memory. The faster search; the store holds the bytes.</summary>
    Array = 0,

    /// <summary>Map each segment. The smaller heap; the store must be disposed.</summary>
    Mapped = 1,
}
