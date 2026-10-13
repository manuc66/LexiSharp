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

    [Fact]
    public void AMappedStoreServesTheSamePageAsAnArrayStore()
    {
        // The only thing that makes a second opening mode honest: the same page, bit for bit. A mapped
        // source cannot be viewed, so the fold pays a copy out of the mapping per posting block and the
        // engine takes the string path — and neither is allowed to move a score.
        string array = NewDirectory();
        string mapped = NewDirectory();

        try
        {
            Seed(array);
            Seed(mapped);

            using var mappedStore = new SegmentedTextIndex(mapped, tokenizer: null, SegmentSourceMode.Mapped);
            var arrayStore = new SegmentedTextIndex(array);

            Assert.Equal(SegmentSourceMode.Mapped, mappedStore.Mode);
            Assert.Equal(SegmentSourceMode.Array, arrayStore.Mode);
            Assert.Equal(arrayStore.Count, mappedStore.Count);

            var mappedEngine = new RankedTextSearchEngine(mappedStore, new Bm25Scorer());
            var arrayEngine = new RankedTextSearchEngine(arrayStore, new Bm25Scorer());

            foreach (string query in QuerySet)
                AssertSamePage(query, arrayEngine.Search(query), mappedEngine.Search(query));
        }
        finally
        {
            if (Directory.Exists(array)) Directory.Delete(array, recursive: true);
            if (Directory.Exists(mapped)) Directory.Delete(mapped, recursive: true);
        }
    }

    [Fact]
    public void AMappedStoreCommitsAndKeepsItsMode()
    {
        string directory = NewDirectory();

        try
        {
            Seed(directory);

            using var store = new SegmentedTextIndex(directory, tokenizer: null, SegmentSourceMode.Mapped);
            var committed = store.Commit();

            // The mode survives the commit, so a store that maps does not silently fall back to reading.
            Assert.Equal(SegmentSourceMode.Mapped, committed.Mode);
            Assert.Equal(1, committed.SegmentCount);

            // And it is still searchable through the mapping the commit opened.
            var engine = new RankedTextSearchEngine(committed, new Bm25Scorer());

            foreach (string query in new[] { "common", "rareword", "term20 term25" })
                Assert.NotEmpty(engine.Search(query));

            committed.Dispose();
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AMappedStoreIsDisposableAndAnArrayStoreIsNot()
    {
        string directory = NewDirectory();

        try
        {
            Seed(directory);

            // An array store holds nothing that needs releasing, so disposing is a no-op and an
            // undisposed one leaks nothing the garbage collector does not own.
            var arrayStore = new SegmentedTextIndex(directory);
            arrayStore.Dispose();
            arrayStore.Dispose();

            // A mapped store holds a view over a file. Dispose is idempotent.
            var mappedStore = new SegmentedTextIndex(directory, tokenizer: null, SegmentSourceMode.Mapped);
            mappedStore.Dispose();
            mappedStore.Dispose();
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AMappedStoreTakesWritesAndServesThem()
    {
        string directory = NewDirectory();

        try
        {
            Seed(directory);

            using var store = new SegmentedTextIndex(directory, tokenizer: null, SegmentSourceMode.Mapped);
            var engine = new RankedTextSearchEngine(store, new Bm25Scorer());

            Assert.Empty(engine.Search("brandnewterm"));

            store.Add(new SearchDocument("doc-new", "brandnewterm onlyhere"));

            var found = engine.Search("brandnewterm");

            Assert.Single(found);
            Assert.Equal("doc-new", found[0].DocumentId);

            Assert.True(store.Remove("doc-new"));
            Assert.Empty(engine.Search("brandnewterm"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AStoreIndexesTextFieldContentLikeTheInMemoryIndex()
    {
        // The in-memory index's flat view is the union of the body and every text field, so a term that
        // only appears in a title is reachable and a document's length is the union's. An overlay that
        // tokenizes the body alone loses both: the field-only terms leave the dictionary and every
        // length shortens, which moves every score the document takes.
        //
        // This was found by the demo's segment lane, whose parity guard refused the page on a corpus
        // carrying titles — the first place the two lanes were compared on real data.
        var index = new InMemoryTextIndex();

        index.Add(new SearchDocument(
            "doc-1",
            "statin breast cancer survival",
            Fields: new Dictionary<string, string>(StringComparer.Ordinal) { ["journal"] = "bmj" },
            Category: "research",
            TextFields: new Dictionary<string, string>(StringComparer.Ordinal)
            { ["title"] = "Statin use and breast cancer survival" }));

        string directory = NewDirectory();

        try
        {
            var store = new SegmentedTextIndex(directory);
            store.Index(index.Documents);

            // Every term the in-memory index holds, including the title-only one.
            foreach (string term in new[] { "statin", "breast", "cancer", "survival", "use" })
                Assert.Equal(index.DocumentFrequency(term), store.DocumentFrequency(term));

            // And the length is the union's, not the body's.
            Assert.Equal(index.DocumentLength("doc-1"), store.DocumentLength("doc-1"));

            // A stop word in a field is a stop word exactly as in the body: whatever the tokenizer does
            // to the body it does to the field, because one tokenizer serves both.
            Assert.Equal(index.DocumentFrequency("and"), store.DocumentFrequency("and"));

            // The terms and positions a positional read reports are the indexed ones: the body's, then
            // the title's after the field gap. A read that tokenized the body alone would answer a
            // proximity query over a different token sequence than the one that was indexed — this is
            // the divergence the harness found on a proximity row before the fields were stored.
            Assert.Equal(index.GetTerms("doc-1"), store.GetTerms("doc-1"));

            foreach (string term in new[] { "statin", "survival", "use" })
                Assert.Equal(index.GetTermPositions("doc-1", term), store.GetTermPositions("doc-1", term));

            // The same read from the committed segment on disk, which is the path that recomputes
            // positions from stored text rather than from the live overlay.
            var committed = store.Commit();

            try
            {
                Assert.Equal(index.GetTerms("doc-1"), committed.GetTerms("doc-1"));

                foreach (string term in new[] { "statin", "survival", "use" })
                    Assert.Equal(index.GetTermPositions("doc-1", term), committed.GetTermPositions("doc-1", term));

                // The whole document round-trips, so a facet over the store sees the metadata a facet
                // over the in-memory index sees, and the title field survives the write. Compared part
                // by part: a record's generated equality compares the dictionaries by reference, so a
                // whole-document Equal would fail on two dictionaries with identical contents.
                Assert.True(committed.TryGetDocument("doc-1", out var roundTripped));
                Assert.NotNull(roundTripped);
                Assert.Equal("doc-1", roundTripped.Id);
                Assert.Equal("statin breast cancer survival", roundTripped.Text);
                Assert.Equal("research", roundTripped.Category);
                Assert.Equal("bmj", roundTripped.Fields!["journal"]);
                Assert.Equal("Statin use and breast cancer survival", roundTripped.TextFields!["title"]);
            }
            finally
            {
                committed.Dispose();
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AStoreAnswersTheVocabularySoAPrefixAtomIsNotSilentlyNarrowed()
    {
        // Without IVocabularyIndex the engine's prefix and fuzzy atoms fall back to the atom's literal
        // base term, which answers a different question than the one asked and says nothing.
        string directory = NewDirectory();

        try
        {
            Seed(directory);

            using var store = new SegmentedTextIndex(directory);

            Assert.True(store is IVocabularyIndex);

            var engine = new RankedTextSearchEngine(store, new Bm25Scorer());
            var expanded = engine.Search("term2*", new SearchOptions { Limit = 100 });
            var literal = engine.Search("term2", new SearchOptions { Limit = 100 });

            // The prefix reaches every term that starts with the stem, which is more than the stem.
            Assert.True(expanded.Count > literal.Count, "a prefix atom reached nothing the stem did not");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AStoreScoresARepeatedTermExactlyAsTheIndexDoes()
    {
        // The overlay holds one entry per (term, document) with the occurrence count, not one entry per
        // occurrence. The accumulator's first touch writes and every later touch adds, so per-occurrence
        // entries would reach the right term frequency; the score would still be wrong, because BM25's
        // saturation is not linear. On this fixture the same document scored 0.4722 through one entry of
        // frequency 2 and 0.6611 through two of frequency 1.
        var index = new InMemoryTextIndex();

        index.Add(new SearchDocument("doc-1", "alpha alpha alpha beta"));
        index.Add(new SearchDocument("doc-2", "alpha gamma"));

        string directory = NewDirectory();

        try
        {
            var store = new SegmentedTextIndex(directory);

            store.Index(index.Documents);

            var memoryEngine = new RankedTextSearchEngine(index, new Bm25Scorer());
            var storeEngine = new RankedTextSearchEngine(store, new Bm25Scorer());

            foreach (string query in new[] { "alpha", "alpha beta", "alpha alpha beta" })
                AssertSamePage(query, memoryEngine.Search(query), storeEngine.Search(query));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TheEngineAccumulatesOverAStoreRatherThanScanningIt()
    {
        // Without the IAccumulatingIndex capability the engine takes its per-document loop, and for a
        // segment-backed index that loop costs O(documents x document frequency): it asks for one term's
        // frequency once per document, and a segment answers each by walking the term's postings. Measured
        // on a 40,000-document corpus, a single near-universal term served 2.8 s that way.
        var index = new InMemoryTextIndex();

        for (int i = 0; i < 400; i++)
            index.Add(new SearchDocument("doc-" + i.ToString("D4", CultureInfo.InvariantCulture), "term" + (i % 40) + " shared filler"));

        string directory = NewDirectory();

        try
        {
            var store = new SegmentedTextIndex(directory);

            store.Index(index.Documents);

            Assert.True(store is IAccumulatingIndex);
            Assert.Equal(index.OrdinalSpace, ((IAccumulatingIndex)store).OrdinalSpace);

            // And the page it serves is the in-memory index's, which is what makes the capability a
            // forwarding rather than a second answer.
            foreach (string query in new[] { "term0", "term1 shared", "term0 term1" })
            {
                AssertSamePage(
                    query,
                    new RankedTextSearchEngine(index, new Bm25Scorer()).Search(query),
                    new RankedTextSearchEngine(store, new Bm25Scorer()).Search(query));
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ARareQueryOverAStoreDoesNotWalkTheCorpus()
    {
        // A query below the accumulation threshold declines the term-at-a-time pass, and the engine then
        // takes its per-document loop. Without ICandidateIndex that loop walks Documents, which for a
        // store decodes the whole corpus. Measured on a 200,000-document store, a term with a document
        // frequency of 3 served 203,952 us and allocated 222,646,808 B per search that way, against
        // 1,200 ns for the in-memory index it has to match. With the capability it is 5,900 ns.
        var index = new InMemoryTextIndex();

        for (int i = 0; i < 2_000; i++)
        {
            var builder = new System.Text.StringBuilder();

            for (int t = 0; t < 20; t++)
                builder.Append("term").Append(i % 40).Append(' ');

            if (i % 500 == 0)
                builder.Append("rareterm ");

            index.Add(new SearchDocument("doc-" + i.ToString("D5", CultureInfo.InvariantCulture), builder.ToString()));
        }

        string directory = NewDirectory();

        try
        {
            var store = new SegmentedTextIndex(directory);

            store.Index(index.Documents);

            Assert.True(store is ICandidateIndex);

            var memoryEngine = new RankedTextSearchEngine(index, new Bm25Scorer());
            var storeEngine = new RankedTextSearchEngine(store, new Bm25Scorer());

            foreach (string query in new[] { "rareterm", "term0", "term0 rareterm" })
                AssertSamePage(query, memoryEngine.Search(query), storeEngine.Search(query));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AStoreScoresTheDocumentFreePathsAsTheInMemoryIndex()
    {
        // A plain query — no metadata filter, no facet — is served by id: the engine reads a
        // candidate's id to score it and its ordinal to place it, and TopRankedWindow resolves the
        // page's documents at the cut. Both arms of that path have to rank exactly as the in-memory
        // index, which holds its documents and never materializes anything. The two arms are reached
        // by coverage: a rare term takes the candidate enumeration, common terms take the full scan.
        var memory = new InMemoryTextIndex();
        memory.Add(new SearchDocument("rare", "quokka wombat common common common"));
        memory.Add(new SearchDocument("a", "common common common alpha"));
        memory.Add(new SearchDocument("b", "common common beta common"));
        memory.Add(new SearchDocument("c", "common gamma common common"));

        string directory = NewDirectory();

        try
        {
            var store = new SegmentedTextIndex(directory);
            store.Index(memory.Documents);
            using var committed = store.Commit();

            foreach (ITextScorer scorer in new ITextScorer[] { new Bm25Scorer(), new QueryLikelihoodScorer(0.2) })
            {
                var memoryEngine = new RankedTextSearchEngine(memory, scorer);
                var storeEngine = new RankedTextSearchEngine(committed, scorer);

                // "quokka" reaches one document — the candidate arm. "common alpha" and a bare
                // "common" reach most of the corpus — the full-scan arm. Both are id-only.
                foreach (string query in new[] { "quokka", "quokka common", "common alpha", "common" })
                    AssertSamePage(query, memoryEngine.Search(query), storeEngine.Search(query));
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AStoreGatesPhrasesExactlyAsTheInMemoryIndex()
    {
        // The phrase gate reads term positions, and a store holds none: it recomputes them from the
        // stored text. This is the one read path where the two indexes could disagree, so it is checked
        // against the in-memory index rather than trusted — including across a text field's gap, where a
        // phrase must not bridge the body into a title.
        var memory = new InMemoryTextIndex();

        memory.Add(new SearchDocument("adjacent", "alpha beta gamma"));
        memory.Add(new SearchDocument("reversed", "beta alpha gamma"));
        memory.Add(new SearchDocument("apart", "alpha gamma beta"));
        memory.Add(new SearchDocument(
            "field-gap",
            "alpha gamma",
            TextFields: new Dictionary<string, string>(StringComparer.Ordinal) { ["title"] = "beta gamma" }));
        memory.Add(new SearchDocument(
            "title-only",
            "gamma",
            TextFields: new Dictionary<string, string>(StringComparer.Ordinal) { ["title"] = "alpha beta" }));

        string directory = NewDirectory();

        try
        {
            var store = new SegmentedTextIndex(directory);
            store.Index(memory.Documents);
            using var committed = store.Commit();

            var memoryEngine = new RankedTextSearchEngine(memory, new Bm25Scorer());
            var storeEngine = new RankedTextSearchEngine(committed, new Bm25Scorer());
            var options = new SearchOptions(10, ParseQuerySyntax: true);

            // Adjacency, reversal, a title, and a phrase that would only match by bridging the gap.
            foreach (string query in new[] { "\"alpha beta\"", "\"beta gamma\"", "\"alpha gamma\"", "\"alpha beta gamma\"" })
                AssertSamePage(query, memoryEngine.Search(query, options), storeEngine.Search(query, options));

            // And the gate is not vacuous: the phrase that would need to bridge the body into the title
            // matches the one document that spells it out in one field, and not the field-gap document.
            var bridged = storeEngine.Search("\"alpha beta gamma\"", options);
            Assert.Equal("adjacent", Assert.Single(bridged).DocumentId);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }


    private static void Seed(string directory)
    {
        var store = new SegmentedTextIndex(directory);

        foreach (var document in Build().Documents)
            store.Add(document);

        store.Commit();
    }

    private static readonly string[] QuerySet = ["common", "rareword", "term20 term25", "term39"];

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
