using System.Collections.Generic;
using System.IO;
using System.Linq;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// What a store's journal makes durable, and what it refuses: a write survives a reopen, a commit stops
/// the replay, and a torn or damaged journal is handled by telling the two apart.
/// </summary>
/// <remarks>
/// <para>
/// Every test here opens the store a second time. That is the only way to exercise the property the
/// journal exists for — a process that ends without a commit and a store that starts again — and it is
/// also the test that a replay reproduces the corpus rather than duplicating it: a marker one entry too
/// low shows up as a doubled document, and one too high as a missing one.
/// </para>
/// <para>
/// The tests share one contract with the store's docs: a directory holds one journal and one writer. A
/// store replaced by <c>Commit</c> or <c>Merge</c> therefore refuses writes, and two of the tests below
/// pin that refusal rather than leaving it implied.
/// </para>
/// </remarks>
public class SegmentJournalReplayTests
{
    private static string NewDirectory() =>
        Path.Combine(Path.GetTempPath(), "lexisharp-journal-store-" + System.Guid.NewGuid().ToString("N"));

    private static SearchDocument Document(string id, string text) =>
        new(id, text, Fields: new Dictionary<string, string> { ["tier"] = "x" });

    private static IReadOnlyList<SearchResult> Search(SegmentedTextIndex store, string query) =>
        new RankedTextSearchEngine(store, new Bm25Scorer()).Search(query);

    private static string[] Ids(IEnumerable<SearchResult> results) =>
        results.Select(result => result.DocumentId).OrderBy(id => id, System.StringComparer.Ordinal).ToArray();

    private static void WithDirectory(System.Action<string> test)
    {
        string directory = NewDirectory();

        try
        {
            test(directory);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void APendingWriteSurvivesAReopen() => WithDirectory(directory =>
    {
        using (var store = new SegmentedTextIndex(directory))
        {
            store.Add(Document("a", "alpha beta"));
            store.Add(Document("b", "beta gamma"));
        }

        // No Commit: the corpus on disk is still empty and these two documents exist only in the
        // journal.
        using var reopened = new SegmentedTextIndex(directory);

        Assert.Equal(2, reopened.Count);
        Assert.Equal(new[] { "a", "b" }, Ids(Search(reopened, "beta")));

        Assert.True(reopened.TryGetDocument("a", out var read));
        Assert.Equal("x", read!.Fields!["tier"]);
    });

    [Fact]
    public void ARemovalSurvivesAReopen() => WithDirectory(directory =>
    {
        using (var store = new SegmentedTextIndex(directory))
        {
            store.Add(Document("a", "alpha"));
            store.Add(Document("b", "alpha"));
            store.Add(Document("c", "alpha"));
            using var committed = store.Commit();

            Assert.True(committed.Remove("b"));
        }

        using var reopened = new SegmentedTextIndex(directory);

        Assert.Equal(2, reopened.Count);
        Assert.Equal(new[] { "a", "c" }, Ids(Search(reopened, "alpha")));
    });

    [Fact]
    public void AClearSurvivesAReopen() => WithDirectory(directory =>
    {
        using (var store = new SegmentedTextIndex(directory))
        {
            store.Add(Document("a", "alpha"));
            store.Clear();
            store.Add(Document("b", "beta"));
        }

        using var reopened = new SegmentedTextIndex(directory);

        Assert.Equal(1, reopened.Count);
        Assert.Equal(new[] { "b" }, Ids(Search(reopened, "beta")));
    });

    [Fact]
    public void ACommitStopsTheReplayAtItsOwnSegment() => WithDirectory(directory =>
    {
        using (var store = new SegmentedTextIndex(directory))
        {
            store.Add(Document("a", "alpha"));
            store.Add(Document("b", "alpha"));

            using var committed = store.Commit();

            // A write after the commit belongs to the journal again, and the replay has to stop
            // between the two: too early duplicates a and b, too late loses c.
            committed.Add(Document("c", "alpha"));
        }

        using var reopened = new SegmentedTextIndex(directory);

        Assert.Equal(3, reopened.Count);
        Assert.Equal(new[] { "a", "b", "c" }, Ids(Search(reopened, "alpha")));

        // And a second reopen after a second commit still holds three, not six.
        using var recommitted = reopened.Commit();
        Assert.Equal(3, recommitted.Count);
    });

    [Fact]
    public void TheReplayedPageIsThePageTheStoreServed() => WithDirectory(directory =>
    {
        IReadOnlyList<SearchResult> before;

        using (var store = new SegmentedTextIndex(directory))
        {
            store.Add(Document("a", "search engine indexing"));
            store.Add(Document("b", "search engine ranking"));
            store.Add(Document("c", "engine ranking"));

            before = Search(store, "search engine");
        }

        using var reopened = new SegmentedTextIndex(directory);

        var after = Search(reopened, "search engine");

        Assert.Equal(before.Select(result => result.DocumentId), after.Select(result => result.DocumentId));
        Assert.Equal(
            before.Select(result => System.BitConverter.DoubleToInt64Bits(result.Score)),
            after.Select(result => System.BitConverter.DoubleToInt64Bits(result.Score)));
    });

    [Fact]
    public void ADisabledJournalCreatesNoFileAndLosesTheOverlay() => WithDirectory(directory =>
    {
        using (var store = new SegmentedTextIndex(directory, journal: JournalOptions.Disabled))
            store.Add(Document("a", "alpha"));

        Assert.False(File.Exists(Path.Combine(directory, SegmentJournal.FileName)));

        using var reopened = new SegmentedTextIndex(directory, journal: JournalOptions.Disabled);
        Assert.Equal(0, reopened.Count);
    });

    [Fact]
    public void AStoreReplacedByCommitRefusesToWrite() => WithDirectory(directory =>
    {
        using var store = new SegmentedTextIndex(directory);
        store.Add(Document("a", "alpha"));

        using var committed = store.Commit();

        var refusal = Assert.Throws<InvalidOperationException>(() => store.Add(Document("b", "beta")));
        Assert.Contains("replaced by Commit or Merge", refusal.Message);

        // The store the commit returned is the one that writes.
        committed.Add(Document("b", "beta"));
        Assert.Equal(2, committed.Count);
    });

    [Fact]
    public void AWriteThroughTheMergedStoreIsReplayed() => WithDirectory(directory =>
    {
        using (var store = new SegmentedTextIndex(directory))
        {
            store.Add(Document("a", "alpha"));
            store.Add(Document("b", "beta"));

            using var merged = store.Merge();

            Assert.Throws<InvalidOperationException>(() => store.Add(Document("c", "gamma")));

            // The journal travels with the store that replaces this one, so a write through it is
            // replayed after a reopen.
            merged.Add(Document("c", "gamma"));
            Assert.Equal(3, merged.Count);
        }

        using var reopened = new SegmentedTextIndex(directory);

        Assert.Equal(3, reopened.Count);
        Assert.Equal(new[] { "a", "b", "c" }, Ids(Search(reopened, "alpha beta gamma")));
    });

    [Fact]
    public void ATornJournalTailStillOpensTheStore() => WithDirectory(directory =>
    {
        using (var store = new SegmentedTextIndex(directory))
        {
            for (int i = 0; i < 5; i++)
                store.Add(Document("doc" + i, "alpha " + i));
        }

        string journal = Path.Combine(directory, SegmentJournal.FileName);
        byte[] bytes = File.ReadAllBytes(journal);
        File.WriteAllBytes(journal, bytes.AsSpan(0, bytes.Length - 7).ToArray());

        // The last record was interrupted; the four before it are the corpus.
        using var reopened = new SegmentedTextIndex(directory);

        Assert.Equal(4, reopened.Count);
        Assert.DoesNotContain("doc4", Ids(Search(reopened, "alpha")));
    });

    [Fact]
    public void ADamagedJournalRefusesToOpen() => WithDirectory(directory =>
    {
        using (var store = new SegmentedTextIndex(directory))
        {
            for (int i = 0; i < 3; i++)
                store.Add(Document("doc" + i, "alpha " + i));
        }

        string journal = Path.Combine(directory, SegmentJournal.FileName);
        byte[] bytes = File.ReadAllBytes(journal);

        // The second record's payload, with its framing intact: damage, not a tear.
        int second = 8 + System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes) + 4;
        bytes[second + 8 + 12] ^= 0xFF;
        File.WriteAllBytes(journal, bytes);

        Assert.Throws<InvalidDataException>(() => new SegmentedTextIndex(directory));
    });

    [Fact]
    public void TheTruncatedTailIsReportedOnTheStore() => WithDirectory(directory =>
    {
        using (var store = new SegmentedTextIndex(directory))
        {
            for (int i = 0; i < 3; i++)
                store.Add(Document("doc" + i, "alpha " + i));
        }

        string journal = Path.Combine(directory, SegmentJournal.FileName);
        long whole = new FileInfo(journal).Length;
        File.WriteAllBytes(journal, File.ReadAllBytes(journal).AsSpan(0, (int)whole - 5).ToArray());

        using var reopened = new SegmentedTextIndex(directory);

        Assert.Equal(2, reopened.Count);
        Assert.True(reopened.TruncatedJournalBytes > 0, "a torn tail has to be reportable, or the store is missing writes with no signal");

        // Opening reads and does not repair, so the tail is still there until a write cuts it back.
        reopened.Add(Document("late", "alpha late"));

        using var again = new SegmentedTextIndex(directory);

        Assert.Equal(0, again.TruncatedJournalBytes);
        Assert.Equal(3, again.Count);
    });

    [Fact]
    public void ADisabledJournalRefusesToIgnoreEntriesItCannotReplay() => WithDirectory(directory =>
    {
        // A durable write from a run that ended without a commit.
        using (var store = new SegmentedTextIndex(directory))
            store.Add(Document("a", "alpha"));

        var refusal = Assert.Throws<InvalidDataException>(
            () => new SegmentedTextIndex(directory, journal: JournalOptions.Disabled));

        Assert.Contains("journal entries", refusal.Message);
    });

    [Fact]
    public void ADisabledJournalOpensACommittedStore() => WithDirectory(directory =>
    {
        using (var store = new SegmentedTextIndex(directory))
        {
            store.Add(Document("a", "alpha"));
            store.Commit().Dispose();
        }

        // The journal was truncated by the commit, so disabling the journal has nothing to ignore.
        using var plain = new SegmentedTextIndex(directory, journal: JournalOptions.Disabled);

        Assert.Equal(1, plain.Count);
    });

    [Fact]
    public void AReadOnlyReplicaOpensServesAndRefusesTheFirstWrite()
    {
        // A read-only replica is a Unix permission question; the guard keeps the suite runnable on
        // Windows, where the equivalent needs an ACL and a different test.
        if (!OperatingSystem.IsWindows())
            ReadsAReadOnlyReplica();
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void ReadsAReadOnlyReplica() =>
        WithDirectory(built =>
        {
            using (var store = new SegmentedTextIndex(built))
            {
                store.Add(Document("a", "alpha beta"));
                store.Commit().Dispose();
            }

            string replica = NewDirectory();

            try
            {
                Directory.CreateDirectory(replica);

                foreach (string path in Directory.GetFiles(built))
                    File.Copy(path, Path.Combine(replica, Path.GetFileName(path)));

                File.SetUnixFileMode(Path.Combine(replica, SegmentJournal.FileName), UnixFileMode.UserRead);
                File.SetUnixFileMode(replica, UnixFileMode.UserRead | UnixFileMode.UserExecute);

                // Opening a replica is a read: it replays and serves without write access.
                using var opened = new SegmentedTextIndex(replica);

                Assert.Equal(1, opened.Count);
                Assert.Equal(new[] { "a" }, Ids(Search(opened, "alpha")));

                // The first write is where write access is needed, and it fails there rather than at open.
                Assert.Throws<UnauthorizedAccessException>(() => opened.Add(Document("b", "beta")));
            }
            finally
            {
                File.SetUnixFileMode(replica, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

                foreach (string path in Directory.GetFiles(replica))
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

                if (Directory.Exists(replica)) Directory.Delete(replica, recursive: true);
            }
        });

    [Fact]
    public void AnInterruptedFirstCommitIsRebuiltFromTheJournal() => WithDirectory(directory =>
    {
        // The shape a crash before the very first rename leaves: a temporary segment, no readable
        // generation, and a journal holding the writes the commit was compacting.
        Directory.CreateDirectory(directory);

        using (var journal = SegmentJournal.Open(directory, JournalOptions.Default))
            journal.Append(JournalOperation.Add, Document("a", "alpha"));

        File.WriteAllBytes(Path.Combine(directory, "segment-00000000.lxs.tmp"), new byte[64]);

        using var store = new SegmentedTextIndex(directory);

        Assert.Equal(1, store.Count);
        Assert.Equal(new[] { "a" }, Ids(Search(store, "alpha")));
    });

    [Fact]
    public void ATemporarySegmentWithNoJournalIsStillRefused() => WithDirectory(directory =>
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "segment-00000000.lxs.tmp"), new byte[64]);

        // The refusal is the one the store had before the journal: without entries to rebuild from, a
        // corpus that is on disk must not open as an empty one.
        Assert.Throws<InvalidDataException>(() => new SegmentedTextIndex(directory, journal: JournalOptions.Disabled));
    });
}
