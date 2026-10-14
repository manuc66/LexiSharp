using System;
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
/// known once they are written. The table's own position therefore goes in the header, and the
/// entries' positions are what the table holds.
/// </para>
/// <para>
/// Terms are ordered by <b>code point</b>, which is the order of their UTF-8 bytes — that is what lets
/// a reader compare a query's UTF-8 bytes against the stored ones and land on the right entry without
/// materializing a string. Ordinal string comparison would not do: it compares UTF-16 code units, which
/// orders a surrogate pair before <c>U+FFFD</c> where UTF-8 orders it after.
/// </para>
/// <para>
/// The segment's length is known before its first byte is written. <see cref="Measure"/> walks the same
/// corpus and writes nothing; <see cref="Write"/> then allocates one array of exactly that size and
/// <see cref="Build"/> assembles the body into it at <see cref="HeaderBytes"/>, patching the header at
/// zero once the offsets exist. The two passes must agree exactly — one byte more and the body overruns
/// the array, one fewer and it leaves it partly uninitialized — so the agreement is asserted on every
/// write, and asserted again for each postings region, which is sized by <see cref="PostingBlocks"/>'s
/// own measure. A body that disagrees with its measure fails the write; it does not corrupt a file.
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

    /// <summary>
    /// The largest segment this format can hold: every offset in it is an <c>int32</c>, so a corpus
    /// whose segment would pass this is refused rather than wrapped into a reader that follows garbage.
    /// </summary>
    private const long MaxSegmentBytes = int.MaxValue;

    /// <summary>Writes <paramref name="index"/> as a new segment and returns its bytes.</summary>
    /// <remarks>
    /// One allocation of exactly the segment's length, and no copy: the body is assembled in the array
    /// that is returned. Before the measure, the body was assembled in a growing buffer and copied out
    /// of it, which was a second array of the segment's size on every write — 1,203 MB allocated for a
    /// commit whose segment is 267 MB, of which this copy was the larger half.
    /// </remarks>
    public static byte[] Write(ITextIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);

        int total = Measure(index);

        var segment = new byte[total];

        int written = Build(index, segment);

        // Two directions of failure, and neither is silent. An overrun is stopped by the span's own
        // bounds check inside Build, which has a destination the exact size of the measure. An
        // underrun is what this catches: a body shorter than its measure would leave the tail of the
        // array uninitialized and the segment one byte shorter than its own header claims, which is a
        // file nobody can diagnose.
        if (written != total)
        {
            throw new InvalidOperationException(
                $"the segment was measured at {total} bytes and built as {written}: the corpus does not encode to a stable size");
        }

        return segment;
    }

    /// <summary>
    /// The exact length of the segment <see cref="Write"/> returns for <paramref name="index"/>, summed
    /// without writing a byte.
    /// </summary>
    /// <remarks>
    /// Every region's size is taken from the same primitives the write takes it from, rather than from
    /// a second hand-written encoding: a string's size is <see cref="Encoding.UTF8"/>'s byte count,
    /// which is what sizes the span <see cref="Body.WriteLengthPrefixed"/> encodes into; a postings
    /// region is <see cref="PostingBlocks.Measure"/>, which is what sizes the span
    /// <see cref="PostingBlocks.Write"/> encodes into. The tables are fixed width.
    /// </remarks>
    internal static int Measure(ITextIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);

        ISpanAccumulatingIndex source = Requires(index);
        long total = HeaderBytes;

        // Documents: each record, then the two int-per-ordinal tables that follow them. The records'
        // sizes do not depend on the order the documents are walked in, which is why this pass does
        // not reproduce the walk.
        for (int ordinal = 0; ordinal < source.OrdinalSpace; ordinal++)
        {
            var document = DocumentOf(source, ordinal);

            total += LengthPrefixedSize(document.Id)
                + LengthPrefixedSize(document.Text)
                + DictionarySize(document.Fields)
                + OptionalSize(document.Category)
                + DictionarySize(document.TextFields);
        }

        total += 2L * sizeof(int) * source.OrdinalSpace;

        // Postings, one region per term, and the term entry that indexes it; then the terms table.
        var vocabulary = (IVocabularyIndex)index;
        int terms = 0;

        foreach (string term in vocabulary.Vocabulary)
        {
            var view = Resolve(source, term);
            total += TermEntrySize(term)
                + PostingBlocks.Measure(view.Ordinals, view.Frequencies, view.Lengths, view.Positions);
            terms++;
        }

        total += (long)sizeof(int) * terms;

        if (total is > MaxSegmentBytes or < HeaderBytes)
        {
            throw new NotSupportedException(
                $"a segment's offsets are thirty-two bit, so it holds at most {MaxSegmentBytes} bytes; this corpus measures {total}");
        }

        return (int)total;
    }

    /// <summary>
    /// Assembles <paramref name="segment"/> — the whole array, header included — and returns the number
    /// of bytes it wrote, which is <see cref="Measure"/> for the same index.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The index cannot be read through the capabilities a segment needs, or it holds a gap where a
    /// document was removed.
    /// </exception>
    private static int Build(ITextIndex index, Span<byte> segment)
    {
        ISpanAccumulatingIndex source = Requires(index);
        var vocabulary = (IVocabularyIndex)index;

        var terms = new List<string>();

        foreach (string term in vocabulary.Vocabulary)
            terms.Add(term);

        terms.Sort(CompareByCodePoint);

        var body = new Body(segment.Slice(HeaderBytes));

        // Documents: entries first, then the table that says where they are.
        var documentOffsets = new int[source.OrdinalSpace];

        for (int ordinal = 0; ordinal < source.OrdinalSpace; ordinal++)
        {
            var document = DocumentOf(source, ordinal);

            documentOffsets[ordinal] = HeaderBytes + body.Position;
            body.WriteLengthPrefixed(document.Id);
            body.WriteLengthPrefixed(document.Text);

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
            body.WriteDictionary(document.Fields);
            body.WriteOptional(document.Category);
            body.WriteDictionary(document.TextFields);
        }

        long documentsTableOffset = HeaderBytes + body.Position;

        foreach (int offset in documentOffsets)
            body.WriteInt32(offset);

        // Lengths: one int per ordinal, in ordinal order.
        long lengthsOffset = HeaderBytes + body.Position;

        for (int ordinal = 0; ordinal < source.OrdinalSpace; ordinal++)
        {
            var document = source.DocumentAt(ordinal)!;
            body.WriteInt32(source.DocumentLength(document.Id));
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

            postingsOffsets[i] = HeaderBytes + body.Position;
            postingsLengths[i] = size;
            frequencies[i] = view.Count;

            int written = PostingBlocks.Write(
                view.Ordinals, view.Frequencies, view.Lengths, view.Positions, body.Span(size));

            // The same measure that sizes the destination also bounds what is written into it: a region
            // that disagrees is a defect in the layout, not in this corpus, and it is caught here
            // rather than by a reader that decodes the wrong number of entries.
            if (written != size)
            {
                throw new InvalidOperationException(
                    $"the postings of '{terms[i]}' were measured at {size} bytes and written as {written}");
            }

            body.Position += written;
        }

        // Terms: entries first, then their table.
        var termOffsets = new int[terms.Count];

        for (int i = 0; i < terms.Count; i++)
        {
            termOffsets[i] = HeaderBytes + body.Position;
            body.WriteTermEntry(terms[i], frequencies[i], postingsOffsets[i], postingsLengths[i]);
        }

        long termsTableOffset = HeaderBytes + body.Position;

        foreach (int offset in termOffsets)
            body.WriteInt32(offset);

        // The header is written last and in place: its offsets are the ones the body measured, and it
        // is the only part of the file that is known once the rest of it exists.
        Span<byte> header = segment.Slice(0, HeaderBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], source.OrdinalSpace);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], terms.Count);
        BinaryPrimitives.WriteInt64LittleEndian(header[16..], documentsTableOffset);
        BinaryPrimitives.WriteInt64LittleEndian(header[24..], lengthsOffset);
        BinaryPrimitives.WriteInt64LittleEndian(header[32..], termsTableOffset);

        return HeaderBytes + body.Position;
    }

    /// <summary>The capabilities a segment needs, and nothing more.</summary>
    private static ISpanAccumulatingIndex Requires(ITextIndex index)
    {
        // One capability carries all three needs: the ordinal space and the document at an ordinal
        // (IAccumulatingIndex's view), the token count and the postings (the read view's).
        if (index is not ISpanAccumulatingIndex source || index is not IVocabularyIndex)
        {
            throw new NotSupportedException(
                "a segment is written from an index that can enumerate its vocabulary, resolve a term's postings and report its documents by ordinal");
        }

        return source;
    }

    /// <summary>A term the index lists in its vocabulary but cannot resolve is an index defect, not a segment one.</summary>
    private static PostingView Resolve(ISpanAccumulatingIndex index, string term) =>
        index.TryResolvePostings(term, withPositions: true, out var view)
            ? view
            : throw new NotSupportedException($"the index lists the term '{term}' in its vocabulary but cannot resolve its postings");

    /// <summary>The document at an ordinal, or the refusal a segment makes of a corpus with a gap in it.</summary>
    private static SearchDocument DocumentOf(ISpanAccumulatingIndex source, int ordinal) =>
        source.DocumentAt(ordinal)
            ?? throw new NotSupportedException("a segment holds no gaps: compact the corpus before writing it");

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

    /// <summary>Bytes <see cref="Body.WriteLengthPrefixed"/> writes for <paramref name="value"/>.</summary>
    private static int LengthPrefixedSize(string value) => sizeof(int) + Encoding.UTF8.GetByteCount(value);

    /// <summary>Bytes <see cref="Body.WriteDictionary"/> writes for <paramref name="values"/>.</summary>
    /// <remarks>
    /// Independent of the order the entries are written in: the same sum over the same pairs, which is
    /// why the measure does not sort the keys to arrive at it.
    /// </remarks>
    private static int DictionarySize(IReadOnlyDictionary<string, string>? values)
    {
        if (values is null)
            return sizeof(int);

        int size = sizeof(int);

        foreach (var (key, value) in values)
            size += LengthPrefixedSize(key) + LengthPrefixedSize(value);

        return size;
    }

    /// <summary>Bytes <see cref="Body.WriteOptional"/> writes for <paramref name="value"/>.</summary>
    private static int OptionalSize(string? value) =>
        value is null ? sizeof(int) : LengthPrefixedSize(value);

    /// <summary>Bytes <see cref="Body.WriteTermEntry"/> writes for <paramref name="term"/>.</summary>
    private static int TermEntrySize(string term) => LengthPrefixedSize(term) + (3 * sizeof(int));

    /// <summary>
    /// A cursor over the segment's body: where the next write lands, and the writes a segment's regions
    /// are made of.
    /// </summary>
    /// <remarks>
    /// The destination is the array the write returns, sliced past the header, so every write here is
    /// into the segment itself rather than into a buffer that is copied into one. That is the reason
    /// <see cref="Span"/> exists rather than a length-prefixed writer over a growable buffer: the
    /// destination has no room to grow, so the size of each region is known before it is written.
    /// </remarks>
    private ref struct Body
    {
        private readonly Span<byte> _destination;
        private int _position;

        public Body(Span<byte> destination) => _destination = destination;

        /// <summary>Where the next byte lands, counted from the body's own start.</summary>
        public int Position
        {
            readonly get => _position;
            set => _position = value;
        }

        /// <summary>
        /// The next <paramref name="size"/> bytes, for a caller that has already measured them and
        /// advances <see cref="Position"/> by what it wrote.
        /// </summary>
        public readonly Span<byte> Span(int size) => _destination.Slice(_position, size);

        /// <summary>An <c>int32</c> in the segment's little-endian order.</summary>
        public void WriteInt32(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_destination.Slice(_position, sizeof(int)), value);
            _position += sizeof(int);
        }

        /// <summary>A string's UTF-8 bytes, with the byte count in front of them.</summary>
        /// <remarks>
        /// The count is taken first and the destination is sized by it, which is the same call
        /// <see cref="LengthPrefixedSize"/> makes — so the two cannot disagree about how many bytes a
        /// string takes. The return value is checked against it because an encoder that wrote fewer
        /// bytes than it counted would desynchronise every region after this one.
        /// </remarks>
        public void WriteLengthPrefixed(string value)
        {
            int size = Encoding.UTF8.GetByteCount(value);

            WriteInt32(size);

            int written = Encoding.UTF8.GetBytes(value, Span(size));

            if (written != size)
            {
                throw new InvalidOperationException(
                    $"UTF-8 counted {size} bytes for a string and wrote {written}");
            }

            _position += written;
        }

        /// <summary>
        /// A dictionary, or <c>-1</c> when there is none. Entries are written in key order, so the same
        /// document written twice is the same bytes — a byte comparison of two segments is a check this
        /// repository actually makes.
        /// </summary>
        public void WriteDictionary(IReadOnlyDictionary<string, string>? values)
        {
            if (values is null)
            {
                WriteInt32(-1);
                return;
            }

            WriteInt32(values.Count);

            foreach (var (key, value) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                WriteLengthPrefixed(key);
                WriteLengthPrefixed(value);
            }
        }

        /// <summary>A string, or <c>-1</c> when there is none.</summary>
        public void WriteOptional(string? value)
        {
            if (value is null)
            {
                WriteInt32(-1);
                return;
            }

            WriteLengthPrefixed(value);
        }

        /// <summary>One term of the dictionary: its bytes, its document frequency, and where its postings lie.</summary>
        public void WriteTermEntry(string term, int frequency, int postingsOffset, int postingsLength)
        {
            WriteLengthPrefixed(term);
            WriteInt32(frequency);
            WriteInt32(postingsOffset);
            WriteInt32(postingsLength);
        }
    }
}
