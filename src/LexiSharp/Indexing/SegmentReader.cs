using System;
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
/// <para>
/// Every lookup lands on an offset the header or a table holds, and every offset is validated where it
/// is read — a segment whose tables point outside itself is refused rather than read as something. A
/// term is found by binary search over the dictionary's UTF-8 bytes, so a lookup never materializes the
/// query term as a string; only <see cref="Terms"/> does, and only when a caller asks for it.
/// </para>
/// <para>
/// The bytes come from a <see cref="SegmentSource"/>, which is what lets the same reader work over a
/// byte array (where every read is a slice) and over a memory-mapped file (where every read is a copy
/// into the scratch buffer this reader keeps). A span returned by a read is valid until the next one.
/// </para>
/// </remarks>
internal sealed class SegmentReader
{
    /// <summary>Bytes fetched when a source cannot be viewed; several posting blocks, so a walk refetches rarely.</summary>
    private const int FetchWindow = 8192;

    private readonly SegmentSource _source;
    private readonly int _documentsTableOffset;
    private readonly int _lengthsOffset;
    private readonly int _termsTableOffset;
    private byte[] _scratch;

    /// <summary>Reads the segment in <paramref name="bytes"/>.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a segment this reader knows.</exception>
    public SegmentReader(byte[] bytes)
        : this(new ArraySegmentSource(bytes))
    {
    }

    /// <summary>Reads the segment a source holds.</summary>
    /// <exception cref="InvalidDataException">The bytes are not a segment this reader knows.</exception>
    public SegmentReader(SegmentSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.Length < SegmentWriter.HeaderBytes)
            throw new InvalidDataException("the segment is shorter than its header");

        if ((uint)source.ReadInt32(0) != SegmentWriter.Magic)
            throw new InvalidDataException("the segment does not start with its magic bytes");

        int version = source.ReadInt32(4);

        if (version != SegmentWriter.Version)
            throw new InvalidDataException($"the segment declares version {version}, and this reader reads {SegmentWriter.Version}");

        _source = source;
        _scratch = new byte[FetchWindow];

        DocumentCount = source.ReadInt32(8);
        TermCount = source.ReadInt32(12);

        if (DocumentCount < 0 || TermCount < 0)
            throw new InvalidDataException("the segment declares a negative count");

        _documentsTableOffset = TableOffset(16, DocumentCount, sizeof(int), "documents");
        _lengthsOffset = TableOffset(24, DocumentCount, sizeof(int), "lengths");
        _termsTableOffset = TableOffset(32, TermCount, sizeof(int), "terms");
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

        int entry = EntryOffset(_documentsTableOffset, ordinal);
        int idLength = ReadInt32(entry, "id length");

        return Encoding.UTF8.GetString(Slice(entry + sizeof(int), idLength, "id"));
    }

    /// <summary>Reads the document at <paramref name="ordinal"/>, without its fields or category.</summary>
    public SearchDocument Document(int ordinal)
    {
        ArgumentOutOfRange(ordinal);

        int entry = EntryOffset(_documentsTableOffset, ordinal);

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
                postings = _source.TryView(entry.PostingsOffset, entry.PostingsLength, out var view)
                    ? new PostingBlockReader(view)
                    : new PostingBlockReader(_source, entry.PostingsOffset, entry.PostingsLength, _scratch);

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
        int entry = EntryOffset(_termsTableOffset, index);
        int termLength = ReadInt32(entry, "term length");

        var term = Slice(entry + sizeof(int), termLength, "term");
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

    /// <summary>
    /// The bytes at <c>[at, at + length)</c>, viewed when the source can and copied into the scratch
    /// when it cannot. <b>Valid until the next read.</b>
    /// </summary>
    private ReadOnlySpan<byte> Slice(int at, int length, string what)
    {
        if (at < SegmentWriter.HeaderBytes || length < 0 || at + length > _source.Length)
            throw new InvalidDataException($"a {what} runs outside the segment");

        if (_source.TryView(at, length, out var view))
            return view;

        if (_scratch.Length < length)
            _scratch = new byte[Math.Max(length, _scratch.Length * 2)];

        _source.Read(at, length, _scratch);

        return _scratch.AsSpan(0, length);
    }

    private int EntryOffset(int tableOffset, int index) =>
        ReadInt32(tableOffset + (index * sizeof(int)), "table entry");

    private int ReadInt32(int at, string what)
    {
        if (at < SegmentWriter.HeaderBytes || at + sizeof(int) > _source.Length)
            throw new InvalidDataException($"a {what} points outside the segment");

        return _source.ReadInt32(at);
    }

    private void ArgumentOutOfRange(int ordinal)
    {
        if (ordinal < 0 || ordinal >= DocumentCount)
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, $"the segment holds {DocumentCount} document(s)");
    }

    private int TableOffset(int at, int count, int width, string what)
    {
        long offset = _source.ReadInt64(at);

        if (offset < SegmentWriter.HeaderBytes || (count > 0 && offset + ((long)count * width) > _source.Length))
            throw new InvalidDataException($"the {what} table does not fit the segment");

        return (int)offset;
    }
}
