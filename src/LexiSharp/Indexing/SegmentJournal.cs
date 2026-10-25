using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using LexiSharp.Core;

namespace LexiSharp.Indexing;

/// <summary>One write the journal holds, in the order it was written.</summary>
internal readonly record struct JournalEntry(long Sequence, JournalOperation Operation, SearchDocument? Document, string? DocumentId);

/// <summary>What a journal entry asks the store to do when it is replayed.</summary>
internal enum JournalOperation : byte
{
    Add = 1,
    Remove = 2,
    Clear = 3,
}

/// <summary>
/// The write-ahead journal of one store: every write, appended in order, replayed over the newest
/// segment when the store opens.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a journal and not a commit.</b> A commit compacts the whole corpus, so its cost follows the
/// corpus and not the writes: measured at 40,000 documents it is about 1.4 s and 188 MB. An
/// application that commits often enough to make its loss window small therefore pays a corpus rewrite
/// per window. A journal's cost follows the writes — an append is 0.5 µs and a device flush is the
/// expensive part — which is the one property that lets durability and compaction be chosen
/// independently.
/// </para>
/// <para>
/// <b>What stops the replay.</b> A segment carries the last sequence it contains (offset 40 of its
/// header), and replay starts above it. That matters because applying a write twice is not harmless:
/// a write of an id that is already present tombstones the old ordinal and adds a new one, so a
/// replayed entry would change the corpus rather than reproduce it.
/// </para>
/// <para>
/// <b>Record format.</b> <c>[int32 payloadLength][payload][uint32 crc32 of payload]</c>, with the
/// payload being <c>[int64 sequence][byte operation][body]</c>. The length prefix is what tells a torn
/// tail — the bytes of a record a crash interrupted — from corruption: a partial record cannot present
/// a length that fits the bytes behind it, because the length only becomes readable when its own four
/// bytes are complete and the flush order is the file order. A record whose length fits but whose
/// checksum does not is a rewritten or damaged file, and the journal refuses to open rather than
/// replaying an operation it cannot vouch for.
/// </para>
/// <para>
/// <b>Not thread-safe</b>: one journal belongs to one store, and a store's mutations are single-writer
/// by contract.
/// </para>
/// </remarks>
internal sealed class SegmentJournal : IDisposable
{
    /// <summary>The journal's file name, beside the segments it belongs to.</summary>
    internal const string FileName = "journal.wal";

    /// <summary>Sequence plus operation: the shortest payload a record can have.</summary>
    private const int MinimumPayload = sizeof(long) + 1;

    /// <summary>A record longer than this is a length prefix that cannot be trusted.</summary>
    private const int MaximumPayload = 256 * 1024 * 1024;

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private readonly string _path;
    private readonly JournalOptions _options;
    private readonly FileStream _stream;
    private readonly byte[] _prefix = new byte[4];
    private readonly byte[] _checksum = new byte[4];

    private long _lastSequence;
    private int _unflushed;
    private long _lastFlushAt;

    private SegmentJournal(string path, JournalOptions options, FileStream stream, long lastSequence)
    {
        _path = path;
        _options = options;
        _stream = stream;
        _lastSequence = lastSequence;
        _lastFlushAt = Environment.TickCount64;
    }

    /// <summary>The last sequence this journal holds, or the sequence it was reset to.</summary>
    public long LastSequence => _lastSequence;

    /// <summary>
    /// How many bytes the open dropped as a torn tail, or zero when the file ended on a whole record.
    /// </summary>
    /// <remarks>
    /// A crash leaves a prefix of the journal, and the last record it was writing is the one that does
    /// not frame. Opening cuts it off so the next append lands on a record boundary; the count is here
    /// because a journal that dropped something is a fact a caller may want to know rather than a
    /// detail the file system swallows.
    /// </remarks>
    public long TruncatedBytes { get; private set; }

    /// <summary>
    /// Opens the journal beside the segments in <paramref name="directory"/>, validates every record it
    /// holds and drops a torn tail.
    /// </summary>
    /// <param name="directory">The store's directory. Created when absent.</param>
    /// <param name="options">The flush policy.</param>
    /// <exception cref="InvalidDataException">A record is intact by its framing and fails its checksum.</exception>
    public static SegmentJournal Open(string directory, JournalOptions options)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
            throw new ArgumentException("a disabled journal is not opened; a store with no journal never creates one", nameof(options));

        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, FileName);
        long lastSequence = 0;
        long validLength = 0;
        long fileLength = 0;

        if (File.Exists(path))
        {
            using (var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096))
            {
                fileLength = read.Length;

                foreach (var (sequence, length) in Scan(read))
                {
                    lastSequence = sequence;
                    validLength += length;
                }
            }

            // A torn tail is a record the crash interrupted. Appending after it would make the next
            // replay stop at the same torn record and lose everything written after it, so the file is
            // cut back to the last record that is whole before anything is appended.
            if (fileLength != validLength)
            {
                using var truncate = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
                truncate.SetLength(validLength);
            }
        }

        // Buffering is disabled on purpose: an append reaches the operating system when it is written,
        // which is what makes OnCommit survive a process crash. The device flush is separate and only a
        // policy asks for it.
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, bufferSize: 1);

        return new SegmentJournal(path, options, stream, lastSequence) { TruncatedBytes = fileLength - validLength };
    }

    /// <summary>
    /// Appends one operation and returns the sequence it was given.
    /// </summary>
    /// <remarks>
    /// The sequence is the journal's own counter, not a caller's: a caller that replayed a journal and
    /// then appended cannot hand out a sequence the replay already consumed.
    /// </remarks>
    public long Append(JournalOperation operation, SearchDocument? document = null, string? documentId = null)
    {
        long sequence = _lastSequence + 1;
        byte[] record = Encode(sequence, operation, document, documentId);

        _stream.Write(record);
        _lastSequence = sequence;
        _unflushed++;

        switch (_options.Flush)
        {
            case JournalFlushPolicy.PerWrite:
                FlushToDevice();
                break;

            case JournalFlushPolicy.Grouped:
                if (_unflushed >= _options.MaxRecords ||
                    Environment.TickCount64 - _lastFlushAt >= (long)_options.MaxDelay.TotalMilliseconds)
                {
                    FlushToDevice();
                }

                break;
        }

        return sequence;
    }

    /// <summary>
    /// The entries this journal holds above <paramref name="afterSequence"/>, in the order they were
    /// written.
    /// </summary>
    /// <remarks>
    /// The stream is opened for reading rather than shared with the appending handle, so the two
    /// directions do not depend on each other's position.
    /// </remarks>
    public IEnumerable<JournalEntry> ReadFrom(long afterSequence)
    {
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096);

        while (TryReadRecord(stream, out long sequence, out byte[] payload, out int length))
        {
            try
            {
                if (sequence > afterSequence)
                    yield return Decode(sequence, payload, length);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }
    }

    /// <summary>
    /// Forgets every entry and continues from <paramref name="sequence"/>: what a store does after a
    /// commit has written those entries into the segment.
    /// </summary>
    public void Reset(long sequence)
    {
        if (sequence < _lastSequence)
            throw new ArgumentOutOfRangeException(
                nameof(sequence),
                sequence,
                $"the journal holds entries up to {_lastSequence} and cannot forget them without losing writes");

        // Truncating to nothing is the whole reset: the commit contains every entry the journal had, so
        // there is no prefix to keep. A crash after this and before the next append replays nothing and
        // the segment is the corpus.
        _stream.SetLength(0);
        _stream.Flush();
        _lastSequence = sequence;
        _unflushed = 0;
        _lastFlushAt = Environment.TickCount64;
    }

    /// <summary>Forces every entry written so far to the device, whatever the policy.</summary>
    public void Flush()
    {
        if (_unflushed > 0)
            FlushToDevice();
    }

    public void Dispose()
    {
        // The last chance a grouped journal has to make its tail durable: a policy that flushes on a
        // cadence would otherwise leave the final entries to the next append, which may not come.
        if (_options.Flush != JournalFlushPolicy.OnCommit)
            Flush();

        _stream.Dispose();
    }

    private void FlushToDevice()
    {
        _stream.Flush(flushToDisk: true);
        _unflushed = 0;
        _lastFlushAt = Environment.TickCount64;
    }

    /// <summary>
    /// Walks the records without decoding any document, yielding each sequence and the record's framed
    /// length. Stops at the first record the framing cannot complete, which is a torn tail.
    /// </summary>
    private static IEnumerable<(long Sequence, int Length)> Scan(FileStream stream)
    {
        var prefix = new byte[4];
        var checksum = new byte[4];

        while (true)
        {
            if (ReadAtMost(stream, prefix, 4) < 4)
                yield break;

            int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);

            if (length < MinimumPayload || length > MaximumPayload)
                yield break;

            byte[] payload = ArrayPool<byte>.Shared.Rent(length);

            try
            {
                if (ReadAtMost(stream, payload, length) < length || ReadAtMost(stream, checksum, 4) < 4)
                    yield break;

                if (Crc32(payload.AsSpan(0, length)) != BinaryPrimitives.ReadUInt32LittleEndian(checksum))
                    throw new InvalidDataException($"the journal '{stream.Name}' holds a record whose checksum does not match its bytes");

                yield return (BinaryPrimitives.ReadInt64LittleEndian(payload), 4 + length + 4);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }
    }

    /// <summary>Reads one framed record, validating its checksum, and stops at a torn tail.</summary>
    private bool TryReadRecord(FileStream stream, out long sequence, out byte[] payload, out int length)
    {
        sequence = 0;
        payload = [];
        length = 0;

        if (ReadAtMost(stream, _prefix, 4) < 4)
            return false;

        length = BinaryPrimitives.ReadInt32LittleEndian(_prefix);

        if (length < MinimumPayload || length > MaximumPayload)
            return false;

        payload = ArrayPool<byte>.Shared.Rent(length);

        if (ReadAtMost(stream, payload, length) < length || ReadAtMost(stream, _checksum, 4) < 4)
        {
            ArrayPool<byte>.Shared.Return(payload);
            payload = [];
            return false;
        }

        if (Crc32(payload.AsSpan(0, length)) != BinaryPrimitives.ReadUInt32LittleEndian(_checksum))
        {
            ArrayPool<byte>.Shared.Return(payload);
            payload = [];
            throw new InvalidDataException($"the journal '{stream.Name}' holds a record whose checksum does not match its bytes");
        }

        sequence = BinaryPrimitives.ReadInt64LittleEndian(payload);
        return true;
    }

    private static byte[] Encode(long sequence, JournalOperation operation, SearchDocument? document, string? documentId)
    {
        using var payload = new MemoryStream();

        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(sequence);
            writer.Write((byte)operation);

            switch (operation)
            {
                case JournalOperation.Add:
                    WriteDocument(writer, document ?? throw new ArgumentNullException(nameof(document)));
                    break;

                case JournalOperation.Remove:
                    writer.Write(documentId ?? throw new ArgumentNullException(nameof(documentId)));
                    break;
            }
        }

        int length = (int)payload.Length;
        byte[] record = new byte[4 + length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(record, length);

        payload.GetBuffer().AsSpan(0, length).CopyTo(record.AsSpan(4));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4 + length), Crc32(record.AsSpan(4, length)));

        return record;
    }

    private static JournalEntry Decode(long sequence, byte[] payload, int length)
    {
        using var stream = new MemoryStream(payload, 0, length, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        long stored = reader.ReadInt64();

        if (stored != sequence)
            throw new InvalidDataException("a journal record does not carry the sequence its frame was read at");

        var operation = (JournalOperation)reader.ReadByte();

        return operation switch
        {
            JournalOperation.Add => new JournalEntry(sequence, operation, ReadDocument(reader), null),
            JournalOperation.Remove => new JournalEntry(sequence, operation, null, reader.ReadString()),
            JournalOperation.Clear => new JournalEntry(sequence, operation, null, null),
            _ => throw new InvalidDataException($"a journal record carries operation {(byte)operation}, which this reader does not know"),
        };
    }

    private static void WriteDocument(BinaryWriter writer, SearchDocument document)
    {
        writer.Write(document.Id);
        writer.Write(document.Text);
        WriteOptionalString(writer, document.Category);
        WriteOptionalFields(writer, document.Fields);
        WriteOptionalFields(writer, document.TextFields);
    }

    private static SearchDocument ReadDocument(BinaryReader reader)
    {
        string id = reader.ReadString();
        string text = reader.ReadString();
        string? category = ReadOptionalString(reader);

        return new SearchDocument(
            id,
            text,
            Fields: ReadOptionalFields(reader),
            Category: category,
            TextFields: ReadOptionalFields(reader));
    }

    private static void WriteOptionalString(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);

        if (value is not null)
            writer.Write(value);
    }

    private static string? ReadOptionalString(BinaryReader reader) =>
        reader.ReadBoolean() ? reader.ReadString() : null;

    private static void WriteOptionalFields(BinaryWriter writer, IReadOnlyDictionary<string, string>? fields)
    {
        writer.Write(fields is not null);

        if (fields is null)
            return;

        writer.Write(fields.Count);

        foreach (var (key, value) in fields)
        {
            writer.Write(key);
            writer.Write(value);
        }
    }

    private static IReadOnlyDictionary<string, string>? ReadOptionalFields(BinaryReader reader)
    {
        if (!reader.ReadBoolean())
            return null;

        int count = reader.ReadInt32();

        // Two strings need at least two bytes, so a count that cannot fit in what is left is a
        // malformed record rather than a large one. The check is here because the decoder is the one
        // place a field written and read out of order turns into an allocation instead of a failure.
        if (count < 0 || count > (reader.BaseStream.Length - reader.BaseStream.Position))
            throw new InvalidDataException($"a journal record declares {count} fields, which cannot fit in its payload");

        var fields = new Dictionary<string, string>(count, StringComparer.Ordinal);

        for (int i = 0; i < count; i++)
            fields.Add(reader.ReadString(), reader.ReadString());

        return fields;
    }

    private static int ReadAtMost(Stream stream, byte[] buffer, int count)
    {
        int total = 0;

        while (total < count)
        {
            int read = stream.Read(buffer, total, count - total);

            if (read == 0)
                return total;

            total += read;
        }

        return total;
    }

    /// <summary>The CRC-32 of a record's payload, IEEE 802.3, the checksum the format uses.</summary>
    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;

        foreach (byte value in data)
            crc = Crc32Table[(crc ^ value) & 0xFF] ^ (crc >> 8);

        return ~crc;
    }

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];

        for (uint i = 0; i < table.Length; i++)
        {
            uint value = i;

            for (int bit = 0; bit < 8; bit++)
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;

            table[i] = value;
        }

        return table;
    }
}
