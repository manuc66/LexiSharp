using System;
using System.IO;
using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Indexing;

/// <summary>
/// A segment on disk: an index written once, reopened and searched without being rebuilt.
/// </summary>
/// <remarks>
/// <para>
/// This is the storage half that matters before memory mapping does: a corpus is analysed once, written
/// as bytes, and a later process reads those bytes instead of re-indexing — which on the corpora this
/// repository measures is the difference between starting in milliseconds and spending a minute in the
/// tokenizer.
/// </para>
/// <para>
/// <b>It reads the whole segment into memory.</b> That is what this type is and not what it will
/// become: a segment larger than the process's heap needs a source that hands out blocks as they are
/// asked for, which is the next increment and a change to the block reader rather than to this file.
/// Until then a segment is bounded by the memory its reader is given, exactly like the in-memory index
/// it was written from.
/// </para>
/// </remarks>
internal static class SegmentFile
{
    /// <summary>Writes <paramref name="index"/> to <paramref name="path"/>, replacing what is there.</summary>
    public static void Write(ITextIndex index, string path)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(path);

        File.WriteAllBytes(path, SegmentWriter.Write(index));
    }

    /// <summary>Opens the segment at <paramref name="path"/> as a searchable index.</summary>
    /// <param name="path">A file written by <see cref="Write"/>.</param>
    /// <param name="tokenizer">
    /// The tokenizer the segment was written with; the dictionary holds normalized terms, so a lookup is
    /// by the normalized form.
    /// </param>
    /// <exception cref="InvalidDataException">The file is not a segment this reader knows.</exception>
    public static SegmentTextIndex Open(string path, ITokenizer? tokenizer = null)
    {
        ArgumentNullException.ThrowIfNull(path);

        return new SegmentTextIndex(File.ReadAllBytes(path), tokenizer);
    }
}
