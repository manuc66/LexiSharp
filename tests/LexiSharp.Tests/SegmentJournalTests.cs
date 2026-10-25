using System.Collections.Generic;
using System.IO;
using System.Linq;
using LexiSharp.Core;
using LexiSharp.Indexing;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The write-ahead journal: what it holds, what it does with a torn tail, and what it refuses.
/// </summary>
/// <remarks>
/// <para>
/// The journal is the half of durability that a commit cannot give cheaply — a commit rewrites the
/// corpus, a journal appends what changed — so what is pinned here is the property replay depends on:
/// the file frames its records, a crash leaves a prefix of them, and opening drops the one the crash
/// interrupted rather than replaying it or losing what follows.
/// </para>
/// <para>
/// The torn-tail test walks <b>every</b> byte cut, not one chosen length. A framing bug usually shows
/// at one offset out of a record's length, and one sample would miss it: `[int32 length][payload][crc]`
/// has three plausible partial states — inside the length, inside the payload, inside the checksum —
/// and each is a different branch of the reader.
/// </para>
/// </remarks>
public class SegmentJournalTests
{
    private static JournalOptions Options(JournalFlushPolicy flush = JournalFlushPolicy.Grouped) =>
        new() { Flush = flush };

    private static string NewDirectory() =>
        Path.Combine(Path.GetTempPath(), "lexisharp-journal-" + System.Guid.NewGuid().ToString("N"));

    private static SearchDocument Document(string id, string text) =>
        new(
            id,
            text,
            Fields: new Dictionary<string, string> { ["tier"] = "x", ["n"] = "1" },
            Category: "article",
            TextFields: new Dictionary<string, string> { ["title"] = "a title" });

    private static List<JournalEntry> ReadAll(string directory)
    {
        using var journal = SegmentJournal.Open(directory, Options());
        return journal.ReadFrom(0).ToList();
    }

    [Fact]
    public void EveryOperationRoundTripsThroughTheFile()
    {
        string directory = NewDirectory();

        try
        {
            using (var journal = SegmentJournal.Open(directory, Options()))
            {
                Assert.Equal(1, journal.Append(JournalOperation.Add, Document("a", "alpha beta")));
                Assert.Equal(2, journal.Append(JournalOperation.Remove, documentId: "b"));
                Assert.Equal(3, journal.Append(JournalOperation.Clear));
                Assert.Equal(4, journal.Append(JournalOperation.Add, Document("c", "gamma")));
            }

            var entries = ReadAll(directory);

            Assert.Equal(new long[] { 1, 2, 3, 4 }, entries.Select(entry => entry.Sequence));
            Assert.Equal(
                new[] { JournalOperation.Add, JournalOperation.Remove, JournalOperation.Clear, JournalOperation.Add },
                entries.Select(entry => entry.Operation));

            var first = entries[0].Document;

            Assert.NotNull(first);
            Assert.Equal("a", first!.Id);
            Assert.Equal("alpha beta", first.Text);
            Assert.Equal("article", first.Category);
            Assert.Equal("x", first.Fields!["tier"]);
            Assert.Equal("1", first.Fields["n"]);
            Assert.Equal("a title", first.TextFields!["title"]);

            Assert.Equal("b", entries[1].DocumentId);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TheChecksumIsTheStandardCrc32()
    {
        // The published check value for IEEE 802.3 CRC-32. A reader and a writer that shared a wrong
        // table would agree with each other and disagree with the format, which is the one thing a
        // round-trip test cannot catch.
        Assert.Equal(0xCBF43926u, SegmentJournal.Crc32("123456789"u8));
    }

    [Fact]
    public void ACrashLeavesAPrefixAndEveryCutReplaysWhatFits()
    {
        string directory = NewDirectory();

        try
        {
            using (var journal = SegmentJournal.Open(directory, Options()))
            {
                for (int i = 1; i <= 4; i++)
                    journal.Append(JournalOperation.Add, Document("doc" + i, "text " + i));
            }

            byte[] whole = File.ReadAllBytes(Path.Combine(directory, SegmentJournal.FileName));

            // Walk every cut, from an empty file to one byte short of the whole journal.
            for (int cut = 0; cut < whole.Length; cut++)
            {
                string truncated = NewDirectory();

                try
                {
                    Directory.CreateDirectory(truncated);
                    File.WriteAllBytes(Path.Combine(truncated, SegmentJournal.FileName), whole.AsSpan(0, cut).ToArray());

                    var expected = sequencesThatFit(whole, cut);
                    long boundary = boundaryThatFits(whole, cut);

                    using (var journal = SegmentJournal.Open(truncated, Options()))
                    {
                        // Opening is a read: it reports the tear and does not repair it.
                        Assert.Equal(cut - boundary, journal.TruncatedBytes);
                        Assert.Equal(expected, journal.ReadFrom(0).Select(entry => entry.Sequence));

                        // The first append is what cuts the torn tail off, and the record it writes is
                        // whole because it starts at the boundary.
                        Assert.Equal(expected.DefaultIfEmpty(0).Max() + 1, journal.Append(JournalOperation.Clear));
                    }

                    using (var reopened = SegmentJournal.Open(truncated, Options()))
                    {
                        Assert.Equal(
                            expected.Append(expected.DefaultIfEmpty(0).Max() + 1),
                            reopened.ReadFrom(0).Select(entry => entry.Sequence));
                        Assert.Equal(0, reopened.TruncatedBytes);
                    }
                }
                finally
                {
                    if (Directory.Exists(truncated)) Directory.Delete(truncated, recursive: true);
                }
            }

            // The sequences a cut leaves readable: every record whose frame — [int32][uint32][payload][uint32]
            // — is complete in the bytes available.
            static List<long> sequencesThatFit(byte[] bytes, int available)
            {
                var sequences = new List<long>();
                int at = 0;

                while (at + 8 <= available)
                {
                    int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));

                    if (at + 8 + length + 4 > available)
                        break;

                    sequences.Add(System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(at + 8)));
                    at += 8 + length + 4;
                }

                return sequences;
            }

            static long boundaryThatFits(byte[] bytes, int available)
            {
                int at = 0;

                while (at + 8 <= available)
                {
                    int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));

                    if (at + 8 + length + 4 > available)
                        break;

                    at += 8 + length + 4;
                }

                return at;
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void OpeningReadsAndWritesNothing()
    {
        string directory = NewDirectory();

        try
        {
            // No journal file was ever created here: opening must not create one, which is what lets a
            // read-only corpus replica be opened at all.
            SegmentJournal.Open(directory, Options()).Dispose();

            Assert.False(File.Exists(Path.Combine(directory, SegmentJournal.FileName)));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ACorruptedLengthIsRefusedRatherThanTruncated()
    {
        string directory = NewDirectory();

        try
        {
            using (var journal = SegmentJournal.Open(directory, Options()))
            {
                for (int i = 1; i <= 3; i++)
                    journal.Append(JournalOperation.Add, Document("doc" + i, "text " + i));
            }

            string path = Path.Combine(directory, SegmentJournal.FileName);
            byte[] bytes = File.ReadAllBytes(path);

            // A bit flip in the second record's length. On framing alone this reads as a record that
            // never finished, which would truncate the third record silently; the header's own checksum
            // is what tells the two apart.
            int first = 8 + System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes) + 4;
            bytes[first + 3] ^= 0x01;
            File.WriteAllBytes(path, bytes);

            Assert.Throws<InvalidDataException>(() => SegmentJournal.Open(directory, Options()));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ADamagedRecordIsRefusedRatherThanReplayed()
    {
        string directory = NewDirectory();

        try
        {
            using (var journal = SegmentJournal.Open(directory, Options()))
            {
                for (int i = 1; i <= 3; i++)
                    journal.Append(JournalOperation.Add, Document("doc" + i, "text " + i));
            }

            string path = Path.Combine(directory, SegmentJournal.FileName);
            byte[] bytes = File.ReadAllBytes(path);

            // A byte inside the second record's payload: the framing still says where the record ends,
            // so this is damage rather than a tear, and the journal must not guess which operation was
            // meant.
            int second = 8 + System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes) + 4;

            bytes[second + 8 + 12] ^= 0xFF;
            File.WriteAllBytes(path, bytes);

            Assert.Throws<InvalidDataException>(() => SegmentJournal.Open(directory, Options()));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ReplayStartsAboveTheSequenceItIsGiven()
    {
        string directory = NewDirectory();

        try
        {
            using (var journal = SegmentJournal.Open(directory, Options()))
            {
                for (int i = 1; i <= 5; i++)
                    journal.Append(JournalOperation.Add, Document("doc" + i, "text " + i));
            }

            using var reopened = SegmentJournal.Open(directory, Options());

            Assert.Equal(new long[] { 4, 5 }, reopened.ReadFrom(3).Select(entry => entry.Sequence));
            Assert.Empty(reopened.ReadFrom(5));
            Assert.Equal(5, reopened.LastSequence);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ARestartedJournalContinuesItsSequence()
    {
        string directory = NewDirectory();

        try
        {
            using (var journal = SegmentJournal.Open(directory, Options()))
                journal.Append(JournalOperation.Add, Document("a", "alpha"));

            using (var journal = SegmentJournal.Open(directory, Options()))
                Assert.Equal(2, journal.Append(JournalOperation.Remove, documentId: "a"));

            Assert.Equal(new long[] { 1, 2 }, ReadAll(directory).Select(entry => entry.Sequence));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ACommitForgetsEverythingAndContinuesFromItsSequence()
    {
        string directory = NewDirectory();

        try
        {
            using (var journal = SegmentJournal.Open(directory, Options()))
            {
                for (int i = 1; i <= 3; i++)
                    journal.Append(JournalOperation.Add, Document("doc" + i, "text " + i));

                journal.Reset(3);

                Assert.Equal(0, new FileInfo(Path.Combine(directory, SegmentJournal.FileName)).Length);
                Assert.Equal(4, journal.Append(JournalOperation.Add, Document("doc4", "text 4")));
            }

            var entries = ReadAll(directory);

            Assert.Equal(new long[] { 4 }, entries.Select(entry => entry.Sequence));
            Assert.Equal("doc4", entries[0].Document!.Id);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AResetCannotForgetAWritItHasNotWritten()
    {
        string directory = NewDirectory();

        try
        {
            using var journal = SegmentJournal.Open(directory, Options());
            journal.Append(JournalOperation.Add, Document("a", "alpha"));
            journal.Append(JournalOperation.Add, Document("b", "beta"));

            // The segment being replaced contains a prefix of what the journal holds, not more: a reset
            // below the tail would drop the writes it is meant to protect.
            Assert.Throws<ArgumentOutOfRangeException>(() => journal.Reset(1));
            Assert.Equal(2, journal.LastSequence);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(JournalFlushPolicy.PerWrite)]
    [InlineData(JournalFlushPolicy.Grouped)]
    [InlineData(JournalFlushPolicy.OnCommit)]
    public void EveryPolicyPersistsWhatItWrote(JournalFlushPolicy policy)
    {
        // The three policies differ in what a power loss reaches back through, which no unit test can
        // observe. What they must agree on is that a journal that closed cleanly holds everything —
        // the device-level difference is a stated property of the policy, not a silent one.
        string directory = NewDirectory();

        try
        {
            using (var journal = SegmentJournal.Open(directory, Options(policy)))
            {
                for (int i = 1; i <= 3; i++)
                    journal.Append(JournalOperation.Add, Document("doc" + i, "text " + i));
            }

            Assert.Equal(new long[] { 1, 2, 3 }, ReadAll(directory).Select(entry => entry.Sequence));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ADisabledJournalIsNotOpened()
    {
        string directory = NewDirectory();

        try
        {
            Assert.Throws<ArgumentException>(() => SegmentJournal.Open(directory, JournalOptions.Disabled));
            Assert.False(File.Exists(Path.Combine(directory, SegmentJournal.FileName)));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AGroupedJournalRefusesAnEmptyBatch()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new JournalOptions { MaxRecords = 0 }.Validated());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new JournalOptions { MaxDelay = TimeSpan.FromMilliseconds(-1) }.Validated());
    }
}
