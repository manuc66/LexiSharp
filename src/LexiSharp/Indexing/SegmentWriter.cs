using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using LexiSharp.Core;

namespace LexiSharp.Indexing;

/// <summary>
/// Writes an index out as one immutable segment: every document, its token count, and every term's
/// document frequency and postings.
/// </summary>
/// <remarks>
/// <para>
/// A segment is written once and read many times, so every lookup lands on an offset a reader can
/// compute. Everything is fixed width except the strings and the postings themselves, and every offset
/// stored is <b>absolute within the file</b> — which costs four bytes where a varint would cost one,
/// and buys a reader with no offset arithmetic to get wrong. Compression is a later pass; this one is
/// about being correct.
/// </para>
/// <para>
/// Each lookup table is written <i>after</i> the entries it indexes, because their offsets are only
/// known once they are written and an <see cref="IBufferWriter{T}"/> cannot be patched. The table's own
/// position therefore goes in the header, and the entries' positions are what the table holds.
/// </para>
/// <para>
/// Terms are ordered by <b>code point</b>, which is the order of their UTF-8 bytes — that is what lets
/// a reader compare a query's UTF-8 bytes against the stored ones and land on the right entry without
/// materializing a string. Ordinal string comparison would not do: it compares UTF-16 code units, which
/// orders a surrogate pair before <c>U+FFFD</c> where UTF-8 orders it after.
/// </para>
/// <para>
/// Layout (little-endian, all offsets absolute):
/// <code>
///   header (40 bytes): magic "LXS1", int32 version, int32 documentCount, int32 termCount,
///                      int64 documentsTableOffset, int64 lengthsOffset, int64 termsTableOffset
///   document entries:  int32 idLength, id UTF-8, int32 textLength, text UTF-8
///   documents table:   documentCount × int32 — where each entry starts
///   lengths:           documentCount × int32 — token count
///   postings:          per term, in dictionary order — the layout of <see cref="PostingBlocks"/>
///   term entries:      int32 termLength, term UTF-8, int32 documentFrequency,
///                      int32 postingsOffset, int32 postingsLength
///   terms table:       termCount × int32 — where each entry starts
/// </code>
/// </para>
/// <para>
/// Not carried, and what that costs: no positions (a phrase query cannot be served from a segment), no
/// field or category metadata (a document reads back as its id and its text), and no generation,
/// deletion or merge story. Those are limitations, not oversights.
/// </para>
/// </remarks>
internal static class SegmentWriter
{
    /// <summary>Magic bytes at the head of every segment: <c>LXS1</c>, read as a little-endian word.</summary>
    internal const uint Magic = 0x3153584C;

    /// <summary>Format version. A reader refuses anything else rather than guessing.</summary>
    internal const int Version = 4;

    /// <summary>Fixed header size: everything else in the file is reached through it.</summary>
    internal const int HeaderBytes = 4 + 4 + 4 + 4 + (3 * 8);

    /// <summary>Writes <paramref name="index"/> as a new segment and returns its bytes.</summary>
    public static byte[] Write(ITextIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);

        // The header is forty bytes and only known once the body is assembled, so it is built on the
        // stack and prepended to the body in the one array that is returned. Before this, the segment
        // was materialized three times — the body buffer, a destination buffer, and the array copied out
        // of the destination — and measured at 1.35 GB of allocation for a 270 MB segment.
        Span<byte> header = stackalloc byte[HeaderBytes];
        var body = new ArrayBufferWriter<byte>(1 << 16);

        Build(index, body, header);

        var segment = new byte[HeaderBytes + body.WrittenCount];
        header.CopyTo(segment);
        body.WrittenSpan.CopyTo(segment.AsSpan(HeaderBytes));

        return segment;
    }

    /// <summary>
    /// Assembles <paramref name="index"/>'s body into <paramref name="body"/> and its forty-byte header
    /// into <paramref name="header"/>.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The index cannot be read through the capabilities a segment needs, or it holds a gap where a
    /// document was removed.
    /// </exception>
    private static void Build(ITextIndex index, ArrayBufferWriter<byte> body, Span<byte> header)
    {
        ArgumentNullException.ThrowIfNull(index);

        // One capability carries all three needs: the ordinal space and the document at an ordinal
        // (IAccumulatingIndex's view), the token count and the postings (the read view's).
        if (index is not ISpanAccumulatingIndex source || index is not IVocabularyIndex vocabulary)
        {
            throw new NotSupportedException(
                "a segment is written from an index that can enumerate its vocabulary, resolve a term's postings and report its documents by ordinal");
        }

        var terms = new List<string>();

        foreach (string term in vocabulary.Vocabulary)
            terms.Add(term);

        terms.Sort(CompareByCodePoint);

        // Documents: entries first, then the table that says where they are.
        var documentOffsets = new int[source.OrdinalSpace];

        for (int ordinal = 0; ordinal < source.OrdinalSpace; ordinal++)
        {
            var document = source.DocumentAt(ordinal)
                ?? throw new NotSupportedException("a segment holds no gaps: compact the corpus before writing it");

            documentOffsets[ordinal] = HeaderBytes + body.WrittenCount;
            WriteLengthPrefixed(document.Id, body);
            WriteLengthPrefixed(document.Text, body);

            // The whole document travels with it, not just its id and body. Each of the three is
            // read back through a different path, and dropping any of them makes a segment answer a
            // different question than the index it was written from:
            //  - TextFields: a positional read recomputes a document's term positions from what it
            //    stores, and the in-memory index places field tokens after the body with a gap
            //    between runs, so a segment without them answers a proximity query over a different
            //    token sequence than the one it indexed.
            //  - Fields: the facet collector counts on document metadata, so a segment without it
            //    returns empty buckets.
            //  - Category: part of the document, and the one place a segment would otherwise be a
            //    lossy copy of its source.
            WriteDictionary(document.Fields, body);
            WriteOptional(document.Category, body);
            WriteDictionary(document.TextFields, body);
        }

        long documentsTableOffset = HeaderBytes + body.WrittenCount;

        foreach (int offset in documentOffsets)
            WriteInt32(offset, body);

        // Lengths: one int per ordinal, in ordinal order.
        long lengthsOffset = HeaderBytes + body.WrittenCount;

        for (int ordinal = 0; ordinal < source.OrdinalSpace; ordinal++)
        {
            var document = source.DocumentAt(ordinal)!;
            WriteInt32(source.DocumentLength(document.Id), body);
        }

        // Postings: one region per term, in dictionary order.
        var postingsOffsets = new int[terms.Count];
        var postingsLengths = new int[terms.Count];
        var frequencies = new int[terms.Count];

        for (int i = 0; i < terms.Count; i++)
        {
            var view = Resolve(source, terms[i]);

            // The three views are passed as they are: both consumers take ReadOnlySpan<int>, and copying
            // them into arrays bought nothing but memory. It was not a small amount — the lengths view is
            // the whole corpus length table, and copying it once per term cost 11.8 GB of the 12.5 GB a
            // 200,000-document commit allocated, which was 94% of it. The spans point into arrays the
            // source holds for the duration of the write, so nothing is copied and nothing outlives its
            // source.
            int size = PostingBlocks.Measure(view.Ordinals, view.Frequencies, view.Lengths, view.Positions);

            postingsOffsets[i] = HeaderBytes + body.WrittenCount;
            postingsLengths[i] = size;
            frequencies[i] = view.Count;

            int written = PostingBlocks.Write(view.Ordinals, view.Frequencies, view.Lengths, view.Positions, body.GetSpan(size));
            body.Advance(written);
        }

        // Terms: entries first, then their table.
        var termOffsets = new int[terms.Count];

        for (int i = 0; i < terms.Count; i++)
        {
            termOffsets[i] = HeaderBytes + body.WrittenCount;
            WriteTermEntry(terms[i], frequencies[i], postingsOffsets[i], postingsLengths[i], body);
        }

        long termsTableOffset = HeaderBytes + body.WrittenCount;

        foreach (int offset in termOffsets)
            WriteInt32(offset, body);

        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], source.OrdinalSpace);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], terms.Count);
        BinaryPrimitives.WriteInt64LittleEndian(header[16..], documentsTableOffset);
        BinaryPrimitives.WriteInt64LittleEndian(header[24..], lengthsOffset);
        BinaryPrimitives.WriteInt64LittleEndian(header[32..], termsTableOffset);

    }

    private static void WriteTermEntry(string term, int frequency, int postingsOffset, int postingsLength, ArrayBufferWriter<byte> body)
    {
        int termSize = Encoding.UTF8.GetByteCount(term);

        WriteInt32(termSize, body);
        Encoding.UTF8.GetBytes(term, body.GetSpan(termSize));
        body.Advance(termSize);
        WriteInt32(frequency, body);
        WriteInt32(postingsOffset, body);
        WriteInt32(postingsLength, body);
    }

    /// <summary>
    /// Writes a string as its UTF-8 bytes with a length prefix, encoding straight into the destination.
    /// </summary>
    /// <remarks>
    /// The array-returning <c>Encoding.UTF8.GetBytes</c> materialises a <c>byte[]</c> per call and then
    /// copies it, which measured at 496 MB for one 100,000-document commit — every id and every
    /// document body. The span-writing overload encodes into the buffer the destination already owns,
    /// so nothing is allocated between the string and the segment. The length is written first because
    /// writing it may grow the buffer and move the span.
    /// </remarks>
    private static void WriteLengthPrefixed(string value, ArrayBufferWriter<byte> body)
    {
        int length = Encoding.UTF8.GetByteCount(value);

        WriteInt32(length, body);
        Encoding.UTF8.GetBytes(value, body.GetSpan(length));
        body.Advance(length);
    }

    /// <summary>
    /// A dictionary, or <c>-1</c> when there is none. Entries are written in key order, so the same
    /// document written twice is the same bytes — a byte comparison of two segments is a check this
    /// repository actually makes.
    /// </summary>
    private static void WriteDictionary(IReadOnlyDictionary<string, string>? values, ArrayBufferWriter<byte> body)
    {
        if (values is null)
        {
            WriteInt32(-1, body);
            return;
        }

        WriteInt32(values.Count, body);

        foreach (var (key, value) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            WriteLengthPrefixed(key, body);
            WriteLengthPrefixed(value, body);
        }
    }

    /// <summary>A string, or <c>-1</c> when there is none.</summary>
    private static void WriteOptional(string? value, ArrayBufferWriter<byte> body)
    {
        if (value is null)
        {
            WriteInt32(-1, body);
            return;
        }

        WriteLengthPrefixed(value, body);
    }

    private static void WriteInt32(int value, ArrayBufferWriter<byte> body)
    {
        Span<byte> buffer = body.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        body.Advance(sizeof(int));
    }

    private static void WriteBytes(ReadOnlySpan<byte> value, ArrayBufferWriter<byte> body)
    {
        value.CopyTo(body.GetSpan(value.Length));
        body.Advance(value.Length);
    }

    /// <summary>A term the index lists in its vocabulary but cannot resolve is an index defect, not a segment one.</summary>
    private static PostingView Resolve(ISpanAccumulatingIndex index, string term) =>
        index.TryResolvePostings(term, withPositions: true, out var view)
            ? view
            : throw new NotSupportedException($"the index lists the term '{term}' in its vocabulary but cannot resolve its postings");

    /// <summary>Orders two terms by code point — the order their UTF-8 bytes take, which is the order a reader searches in.</summary>
    private static int CompareByCodePoint(string left, string right)
    {
        var leftSpan = left.AsSpan();
        var rightSpan = right.AsSpan();

        while (!leftSpan.IsEmpty && !rightSpan.IsEmpty)
        {
            Rune.DecodeFromUtf16(leftSpan, out var leftRune, out int leftLength);
            Rune.DecodeFromUtf16(rightSpan, out var rightRune, out int rightLength);

            if (leftRune.Value != rightRune.Value)
                return leftRune.Value < rightRune.Value ? -1 : 1;

            leftSpan = leftSpan[leftLength..];
            rightSpan = rightSpan[rightLength..];
        }

        return leftSpan.Length.CompareTo(rightSpan.Length);
    }
}
