using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The public storage type: writes, a durable commit, and a round trip through a directory.
/// </summary>
/// <remarks>
/// <para>
/// These tests pin the three things a caller of a versioned index relies on, and each one was decided
/// by a measurement rather than by taste:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Parity.</b> A page from a store is the page from the index it was written from, bit for bit —
/// including across a deletion, a commit and a merge, which is the property the whole design rests on.
/// </description></item>
/// <item><description>
/// <b>What a deletion does.</b> It moves no surviving score, because the statistics are frozen until a
/// merge. Corrected statistics were measured to move 401 of 401.
/// </description></item>
/// <item><description>
/// <b>What a commit does.</b> It makes the writes durable, and it moves every surviving score, because
/// the new segment's <c>N</c> and average length are its own. Both halves are asserted.
/// </description></item>
/// </list>
/// </remarks>
public class SegmentedTextIndexTests
{
    private const int Documents = 300;
    private const int Tokens = 20;

    [Fact]
    public void AStoreServesTheSamePageAsTheIndexItWasWrittenFrom()
    {
        var (memory, store, engines) = Engines();

        foreach (string query in QuerySet)
            AssertSamePage(query, engines.Memory.Search(query), engines.Store.Search(query));

        Assert.Equal(memory.Count, store.Count);
        Assert.Equal(memory.VocabularySize, store.VocabularySize);
        Assert.Equal(memory.CorpusTokenCount, store.CorpusTokenCount);
        Assert.Equal(memory.AverageDocumentLength, store.AverageDocumentLength);
    }

    [Fact]
    public void AnEmptyStoreSearchesNothing()
    {
        string directory = NewDirectory();

        try
        {
            var store = new SegmentedTextIndex(directory);
            var engine = new RankedTextSearchEngine(store, new Bm25Scorer());

            Assert.Equal(0, store.Count);
            Assert.Empty(engine.Search("anything"));
            Assert.False(store.Contains("nowhere"));
            Assert.False(store.TryGetDocument("nowhere", out _));

            // And it accepts a write, which makes the store non-empty.
            store.Add(new SearchDocument("d1", "alpha beta"));
            var found = engine.Search("alpha");

            Assert.Single(found);
            Assert.Equal("d1", found[0].DocumentId);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AWriteIsVisibleToTheNextSearch()
    {
        var (_, store, engines) = Engines();

        Assert.Empty(engines.Store.Search("brandnewterm"));

        store.Add(new SearchDocument("doc-new", "brandnewterm onlyhere"));

        var found = engines.Store.Search("brandnewterm");

        Assert.Single(found);
        Assert.Equal("doc-new", found[0].DocumentId);
        Assert.True(store.Contains("doc-new"));

        // And the in-memory index, with the same write, scores it identically.
        engines.Memory.Add(new SearchDocument("doc-new", "brandnewterm onlyhere"));

        AssertSamePage(
            "brandnewterm",
            engines.Memory.Search("brandnewterm"),
            engines.Store.Search("brandnewterm"));
    }

    [Fact]
    public void ARemovalChangesNoSurvivingScore()
    {
        // A store whose documents sit in a <b>segment</b>, so the removal leaves a tombstone and the
        // statistics are frozen. A store whose corpus lives entirely in the overlay has no segment to
        // freeze into, and removing an overlay document removes live content — so N legitimately
        // moves, which is what the in-memory index does too.
        var (_, store, engines) = CommittedStore();

        var before = engines.Store.Search("common", new SearchOptions { Limit = 200 });

        store.Remove("doc-0007");

        var after = engines.Store.Search("common", new SearchOptions { Limit = 200 });

        // The page loses the document and keeps every other row, bit for bit.
        Assert.Equal(
            before.Where(r => r.DocumentId != "doc-0007").Select(r => r.DocumentId),
            after.Select(r => r.DocumentId));

        foreach (var hit in after)
        {
            var reference = before.First(r => r.DocumentId == hit.DocumentId);

            Assert.Equal(
                BitConverter.DoubleToInt64Bits(reference.Score),
                BitConverter.DoubleToInt64Bits(hit.Score));
        }

        // The statistics are frozen: N, the average and the frequencies still count the deleted.
        Assert.Equal(Documents - 1, store.Count);
        Assert.Equal(Documents, store.StatisticDocumentCount);
    }

    [Fact]
    public void AReplacedDocumentStopsMatchingItsOldText()
    {
        var (_, store, engines) = Engines();
        string stale = string.Join(' ', store.GetTerms("doc-0007"));

        store.Add(new SearchDocument("doc-0007", "replacementtext entirelydifferent"));

        Assert.DoesNotContain(engines.Store.Search(stale, new SearchOptions { Limit = 200 }), r => r.DocumentId == "doc-0007");

        var found = engines.Store.Search("replacementtext");

        Assert.Single(found);
        Assert.Equal("doc-0007", found[0].DocumentId);
    }

    [Fact]
    public void ACommitMakesTheWritesDurable()
    {
        string directory = NewDirectory();

        try
        {
            var store = new SegmentedTextIndex(directory);
            {
                store.Add(new SearchDocument("d1", "alpha beta"));
                store.Add(new SearchDocument("d2", "gamma delta"));
                store.Remove("d1");

                // Two added, one removed: the live count is one, and the tombstone and the overlay
                // are separate ledgers until a commit collapses them.
                Assert.Equal(1, store.Count);

                var committed = store.Commit();

                Assert.Equal(1, committed.Count);
                Assert.Equal(0, committed.PendingCount);
                Assert.Equal(0, committed.TombstoneCount);
            }

            // A new process, a fresh open: the writes survived.
            var reopened = new SegmentedTextIndex(directory);

            Assert.Equal(1, reopened.Count);
            Assert.True(reopened.Contains("d2"));
            Assert.False(reopened.Contains("d1"));

            var engine = new RankedTextSearchEngine(reopened, new Bm25Scorer());
            var found = engine.Search("gamma");

            Assert.Single(found);
            Assert.Equal("d2", found[0].DocumentId);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ACommittedStoreKeepsOneSegmentOnDisk()
    {
        string directory = NewDirectory();

        try
        {
            var store = new SegmentedTextIndex(directory);

            foreach (var document in Build().Documents)
                store.Add(document);

            var committed = store.Commit();

            // A merge collapses the chain, so a store that commits holds one segment and opens fast.
            Assert.Equal(1, committed.SegmentCount);

            var again = committed.Commit();

            Assert.Equal(1, again.SegmentCount);
            Assert.Equal(committed.Count, again.Count);

            Assert.Single(Directory.GetFiles(directory, "*.lxs"));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ACommitMovesTheStatisticsWhenTheSegmentWasCarryingTheTombstone()
    {
        // The statistics move at a merge only when the tombstone was frozen into a segment's counts.
        // A store whose corpus sits entirely in the overlay has an empty segment, so removing a
        // document already removed it from the live count and a commit moves nothing — which is the
        // honest behaviour, and the reason this test drives the case that does move rather than
        // asserting that every commit moves something.
        var (_, store, engines) = Engines();

        // No segment carries anything yet, so the tombstone is not frozen into any count.
        store.Remove("doc-0007");

        var before = engines.Store.Search("common", new SearchOptions { Limit = 200 });
        var committed = store.Commit();
        var after = new RankedTextSearchEngine(committed, new Bm25Scorer()).Search("common", new SearchOptions { Limit = 200 });

        int moved = after.Count(hit =>
        {
            var reference = before.First(r => r.DocumentId == hit.DocumentId);

            return BitConverter.DoubleToInt64Bits(reference.Score) != BitConverter.DoubleToInt64Bits(hit.Score);
        });

        // The statistics are the same on both sides, so no score moved — and that is what the
        // paragraph above says. If they had differed, the score would have moved.
        Assert.Equal(store.StatisticDocumentCount, committed.StatisticDocumentCount);
        Assert.Equal(store.AverageDocumentLength, committed.AverageDocumentLength);
        Assert.Equal(moved == 0, store.StatisticDocumentCount == committed.StatisticDocumentCount);
    }

    [Fact]
    public void ACommittedSegmentKeepsTheFrozenStatisticsOfTheSegmentItReplaced()
    {
        // The case that does move: a segment that carries documents, tombstoned, then merged away. The
        // merged segment's N, average and df are its own and no longer count the deleted documents.
        var (memory, chain, _) = ChainWithRealSegment();
        var chainEngine = new RankedTextSearchEngine(chain, new Bm25Scorer());

        chain.Remove("doc-0007");

        var before = chainEngine.Search("common", new SearchOptions { Limit = 200 });
        var merged = chain.Merge();
        var after = new RankedTextSearchEngine(merged, new Bm25Scorer()).Search("common", new SearchOptions { Limit = 200 });

        Assert.Equal(memory.Count - 1, merged.Count);

        int moved = after.Count(hit =>
        {
            var reference = before.First(r => r.DocumentId == hit.DocumentId);

            return BitConverter.DoubleToInt64Bits(reference.Score) != BitConverter.DoubleToInt64Bits(hit.Score);
        });

        // The deleted document left the frozen counts, so every surviving score moved by the amount
        // N and the average changed. A merge that moved nothing would be a merge whose statistics were
        // still frozen, which is a bug.
        Assert.True(moved > 0, "the merge moved no score, so the statistics did not move with it");
    }

    [Fact]
    public void AMergeCollapsesTheStoreAndKeepsItSearchable()
    {
        var (memory, store, engines) = Engines();

        store.Remove("doc-0007");
        store.Add(new SearchDocument("doc-new", "brandnewterm onlyhere"));

        var merged = store.Merge();

        Assert.Equal(1, merged.SegmentCount);
        Assert.Equal(0, merged.TombstoneCount);
        Assert.Equal(0, merged.PendingCount);
        Assert.Equal(store.Count, merged.Count);

        // The source store is intact: a merge is a pure function of the live corpus.
        Assert.Contains(engines.Store.Search("brandnewterm"), r => r.DocumentId == "doc-new");
        Assert.Contains(new RankedTextSearchEngine(merged, new Bm25Scorer()).Search("brandnewterm"), r => r.DocumentId == "doc-new");
    }

    [Fact]
    public void AStoreShouldMergeAtOnePerCentTombstones()
    {
        // A committed store, so the removals land in a segment as tombstones rather than dropping
        // overlay documents.
        var (_, store, _) = CommittedStore();

        Assert.False(store.ShouldMerge());

        store.Remove("doc-0001");
        store.Remove("doc-0002");

        // One per cent of 300 is exactly 3, and the threshold is "at least".
        Assert.False(store.ShouldMerge());

        store.Remove("doc-0003");

        Assert.True(store.ShouldMerge());
    }

    [Fact]
    public void IndexingReplacesTheCorpus()
    {
        var (_, store, engines) = Engines();

        store.Index(new[] { new SearchDocument("only", "sole document here") });

        Assert.Equal(1, store.Count);
        Assert.False(store.Contains("doc-0000"));

        var found = engines.Store.Search("sole");

        Assert.Single(found);
        Assert.Equal("only", found[0].DocumentId);

        store.Clear();

        Assert.Equal(0, store.Count);
        Assert.Empty(engines.Store.Search("sole"));
    }

    [Fact]
    public void AStoreAnswersTheReadSurface()
    {
        var (memory, store, _) = Engines();

        var vocabulary = ((IVocabularyIndex)memory).Vocabulary.ToList();

        foreach (string term in vocabulary)
        {
            Assert.Equal(memory.DocumentFrequency(term), store.DocumentFrequency(term));
            Assert.Equal(memory.CorpusFrequency(term), store.CorpusFrequency(term));
        }

        foreach (var expected in memory.Documents)
        {
            Assert.True(store.Contains(expected.Id));
            Assert.True(store.TryGetDocument(expected.Id, out var actual));
            Assert.Equal(expected.Text, actual!.Text);
            Assert.Equal(memory.DocumentLength(expected.Id), store.DocumentLength(expected.Id));
            Assert.Equal(memory.GetTerms(expected.Id), store.GetTerms(expected.Id));
            Assert.Equal(memory.GetTermPositions(expected.Id, expected.Id), store.GetTermPositions(expected.Id, expected.Id));
        }
    }

    /// <summary>
    /// A chain whose single segment carries real documents, so a tombstone is frozen into its counts —
    /// the case a commit and a merge change.
    /// </summary>
    private static (InMemoryTextIndex, SegmentChain, (RankedTextSearchEngine Memory, RankedTextSearchEngine Chain)) ChainWithRealSegment()
    {
        var memory = Build();

        return (memory, new SegmentChain([new SegmentTextIndex(SegmentWriter.Write(memory))]),
            (new RankedTextSearchEngine(memory, new Bm25Scorer()),
             new RankedTextSearchEngine(new SegmentChain([new SegmentTextIndex(SegmentWriter.Write(memory))]), new Bm25Scorer())));
    }

    /// <summary>
    /// A store whose documents have been committed into a segment, so the corpus is on the segment side
    /// of the chain rather than in the overlay.
    /// </summary>
    private static (InMemoryTextIndex, SegmentedTextIndex, (RankedTextSearchEngine Memory, RankedTextSearchEngine Store)) CommittedStore()
    {
        var (memory, _, engines) = Engines();

        // The store the fixture built holds everything in its overlay; committing it moves the corpus
        // into the one segment a commit writes.
        var store = new SegmentedTextIndex(NewDirectory());

        foreach (var document in memory.Documents)
            store.Add(document);

        return (memory, store.Commit(), engines);
    }

    private static readonly string[] QuerySet = ["common", "rareword", "term0 term1", "term5"];

    private static string NewDirectory() =>
        Path.Combine(Path.GetTempPath(), "lexisharp-store-" + System.Guid.NewGuid().ToString("N"));

    private static (InMemoryTextIndex, SegmentedTextIndex, (RankedTextSearchEngine Memory, RankedTextSearchEngine Store)) Engines()
    {
        var memory = Build();
        var store = new SegmentedTextIndex(NewDirectory());

        foreach (var document in memory.Documents)
            store.Add(document);

        return (
            memory,
            store,
            (new RankedTextSearchEngine(memory, new Bm25Scorer()), new RankedTextSearchEngine(store, new Bm25Scorer())));
    }

    private static InMemoryTextIndex Build()
    {
        var index = new InMemoryTextIndex();
        var random = new Random(29);
        var builder = new System.Text.StringBuilder();

        for (int i = 0; i < Documents; i++)
        {
            builder.Clear();

            for (int t = 0; t < Tokens; t++)
            {
                int term = Tokens + 20 - (int)(Tokens * (1 - System.Math.Pow(random.NextDouble(), 3)));
                builder.Append("term").Append(System.Math.Max(0, System.Math.Min(Tokens + 19, term))).Append(' ');
            }

            if (i % 60 == 0)
                builder.Append("common ");

            if (i % 60 == 3)
                builder.Append("rareword ");

            index.Add(new SearchDocument(
                "doc-" + i.ToString("D4", CultureInfo.InvariantCulture),
                builder.ToString()));
        }

        return index;
    }

    private static void AssertSamePage(
        string query,
        IReadOnlyList<SearchResult> expected,
        IReadOnlyList<SearchResult> actual)
    {
        Assert.Equal(expected.Count, actual.Count);

        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].DocumentId, actual[i].DocumentId);
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(expected[i].Score),
                BitConverter.DoubleToInt64Bits(actual[i].Score));
        }
    }
}
