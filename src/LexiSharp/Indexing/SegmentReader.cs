using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LexiSharp.Core;

namespace LexiSharp.Indexing;

/// <summary>
/// Reads one segment written by <see cref="SegmentWriter"/>: its documents, their token counts, and
/// every term's document frequency and postings.
/// </summary>
/// <remarks>
/// Every lookup lands on an offset the header or a table holds, and every offset is validated where it
/// is read — a segment whose tables point outside itself is refused rather than read as something. A
/// term is found by binary search over the dictionary's UTF-8 bytes, so a lookup never materializes the
/// query term as a string; only <see cref="Terms"/> does, and only when a caller asks for it.
/// </remarks>
internal sealed class SegmentReader
{
    private readonly byte[] _bytes;
    private readonly int _documentsTableOffset;
    private readonly int _lengthsOffset;
    private readonly int _termsTableOffset;

    /// <summary>Reads the segment in <paramref name="bytes"/>.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a segment this reader knows.</exception>
    public SegmentReader(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length < SegmentWriter.HeaderBytes)
            throw new InvalidDataException("the segment is shorter than its header");

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != SegmentWriter.Magic)
            throw new InvalidDataException("the segment does not start with its magic bytes");

        int version = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));

        if (version != SegmentWriter.Version)
            throw new InvalidDataException($"the segment declares version {version}, and this reader reads {SegmentWriter.Version}");

        _bytes = bytes;
        DocumentCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        TermCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));

        if (DocumentCount < 0 || TermCount < 0)
            throw new InvalidDataException("the segment declares a negative count");

        _documentsTableOffset = TableOffset(bytes, 16, DocumentCount, sizeof(int), "documents");
        _lengthsOffset = TableOffset(bytes, 24, DocumentCount, sizeof(int), "lengths");
        _termsTableOffset = TableOffset(bytes, 32, TermCount, sizeof(int), "terms");
    }

    /// <summary>Documents the segment holds.</summary>
    public int DocumentCount { get; }

    /// <summary>Terms the segment holds.</summary>
    public int TermCount { get; }

    /// <summary>Every term in the segment, in dictionary order.</summary>
    public IEnumerable<string> Terms
    {
        get
        {
            for (int i = 0; i < TermCount; i++)
                yield return ReadTerm(i).Term;
        }
    }

    /// <summary>The token count of the document at <paramref name="ordinal"/>.</summary>
    public int DocumentLength(int ordinal)
    {
        ArgumentOutOfRange(ordinal);
        return ReadInt32(_lengthsOffset + (ordinal * sizeof(int)), "length");
    }

    /// <summary>The id of the document at <paramref name="ordinal"/>, without decoding its text.</summary>
    /// <remarks>
    /// Separate from <see cref="Document"/> because a reader opening a segment builds an id to ordinal
    /// map, and doing that through <see cref="Document"/> would decode every text in the corpus at open.
    /// </remarks>
    public string DocumentId(int ordinal)
    {
        ArgumentOutOfRange(ordinal);

        int entry = ReadInt32(_documentsTableOffset + (ordinal * sizeof(int)), "document offset");
        int idLength = ReadInt32(entry, "id length");

        return Encoding.UTF8.GetString(Slice(entry + sizeof(int), idLength, "id"));
    }

    /// <summary>Reads the document at <paramref name="ordinal"/>, without its fields or category.</summary>
    public SearchDocument Document(int ordinal)
    {
        ArgumentOutOfRange(ordinal);

        int entry = ReadInt32(_documentsTableOffset + (ordinal * sizeof(int)), "document offset");

        string id = ReadLengthPrefixed(entry, out int afterId);
        string text = ReadLengthPrefixed(afterId, out _);

        return new SearchDocument(id, text);
    }

    /// <summary>
    /// Finds <paramref name="term"/> in the dictionary and reports its document frequency and the
    /// cursor over its postings, or <c>false</c> when the segment does not know it.
    /// </summary>
    public bool TryFindTerm(ReadOnlySpan<char> term, out int documentFrequency, out PostingBlockReader postings)
    {
        Span<byte> encoded = term.Length <= 256 ? stackalloc byte[term.Length * 4] : new byte[term.Length * 4];
        int length = Encoding.UTF8.GetBytes(term, encoded);
        var wanted = encoded[..length];

        int low = 0;
        int high = TermCount - 1;

        while (low <= high)
        {
            int middle = (low + high) / 2;
            var entry = ReadTermEntry(middle, wanted, out int comparison);

            if (comparison == 0)
            {
                documentFrequency = entry.DocumentFrequency;
                postings = new PostingBlockReader(_bytes.AsSpan(entry.PostingsOffset, entry.PostingsLength));

                return true;
            }

            if (comparison < 0)
                low = middle + 1;
            else
                high = middle - 1;
        }

        documentFrequency = 0;
        postings = default;

        return false;
    }

    private (string Term, int DocumentFrequency, int PostingsOffset, int PostingsLength) ReadTerm(int index) =>
        ReadTermEntry(index, default, out _);

    /// <summary>
    /// Reads the entry at <paramref name="index"/>, comparing its term against <paramref name="wanted"/>
    /// when one is given. Both the string and the comparison come out of one read of the entry.
    /// </summary>
    private (string Term, int DocumentFrequency, int PostingsOffset, int PostingsLength) ReadTermEntry(
        int index,
        ReadOnlySpan<byte> wanted,
        out int comparison)
    {
        int entry = ReadInt32(_termsTableOffset + (index * sizeof(int)), "term offset");
        int termLength = ReadInt32(entry, "term length");

        var term = _bytes.AsSpan(entry + sizeof(int), termLength);
        comparison = wanted.IsEmpty ? 0 : term.SequenceCompareTo(wanted);

        int frequency = ReadInt32(entry + sizeof(int) + termLength, "document frequency");
        int postingsOffset = ReadInt32(entry + (2 * sizeof(int)) + termLength, "postings offset");
        int postingsLength = ReadInt32(entry + (3 * sizeof(int)) + termLength, "postings length");

        return (Encoding.UTF8.GetString(term), frequency, postingsOffset, postingsLength);
    }

    private string ReadLengthPrefixed(int at, out int after)
    {
        int length = ReadInt32(at, "string length");
        after = at + sizeof(int) + length;

        return Encoding.UTF8.GetString(Slice(at + sizeof(int), length, "string"));
    }

    /// <summary>A span into the segment, refused when it would run past the end.</summary>
    private ReadOnlySpan<byte> Slice(int at, int length, string what)
    {
        if (at < SegmentWriter.HeaderBytes || length < 0 || at + length > _bytes.Length)
            throw new InvalidDataException($"a {what} runs outside the segment");

        return _bytes.AsSpan(at, length);
    }

    private int ReadInt32(int at, string what)
    {
        if (at < SegmentWriter.HeaderBytes || at + sizeof(int) > _bytes.Length)
            throw new InvalidDataException($"a {what} points outside the segment");

        return BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(at));
    }

    private void ArgumentOutOfRange(int ordinal)
    {
        if (ordinal < 0 || ordinal >= DocumentCount)
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, $"the segment holds {DocumentCount} document(s)");
    }

    private static int TableOffset(byte[] bytes, int at, int count, int width, string what)
    {
        long offset = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at));

        if (offset < SegmentWriter.HeaderBytes || (count > 0 && offset + ((long)count * width) > bytes.Length))
            throw new InvalidDataException($"the {what} table does not fit the segment");

        return (int)offset;
    }
}
