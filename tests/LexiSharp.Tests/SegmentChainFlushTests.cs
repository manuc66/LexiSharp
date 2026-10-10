using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Writing a chain out as one segment: the flush, the commit, and what a merge does to the scores.
/// </summary>
/// <remarks>
/// <para>
/// A segment cannot hold a gap, so writing one from a chain is a compaction rather than a copy —
/// tombstoned documents drop out, the live ones are renumbered, every ordinal changes. That is what
/// makes this the one point in a chain's life at which every ordinal moves.
/// </para>
/// <para>
/// The property these tests pin is that a flush is invisible to the corpus except in the way it should
/// be. Writing the same live corpus out and reading it back must serve the same page, with one
/// exception stated and measured rather than assumed: the tallied statistics move at a merge, because
/// the new segment's document frequencies, average length and <c>N</c> are its own and no longer
/// include the deleted documents. That is the only point at which a score may change, and it is the
/// whole reason a merge is a scheduled batch operation rather than something a query triggers.
/// </para>
/// </remarks>
public class SegmentChainFlushTests
{
    private const int Documents = 300;
    private const int Tokens = 20;

    [Fact]
    public void AFlushServesTheSamePageAsTheChainItWasWrittenFrom()
    {
        var (memory, chain, engines) = Engines();

        // A chain with no writes is exactly its segments, so a flush of it must be its index.
        var flushed = chain.Flush();
        var flushedEngine = new RankedTextSearchEngine(flushed, new Bm25Scorer());

        foreach (string query in QuerySet)
            AssertSamePage(query, engines.Chain.Search(query), flushedEngine.Search(query));

        Assert.Equal(memory.Count, flushed.Count);
        Assert.Equal(memory.VocabularySize, flushed.VocabularySize);
        Assert.Equal(memory.CorpusTokenCount, flushed.CorpusTokenCount);
        Assert.Equal(memory.AverageDocumentLength, flushed.AverageDocumentLength);
    }

    [Fact]
    public void AFlushedChainDropsTheTombstones()
    {
        var (_, chain, _) = Engines();

        chain.Remove("doc-0007");
        chain.Remove("doc-0100");
        chain.Remove("doc-0299");

        Assert.Equal(3, chain.TombstoneCount);

        var flushed = chain.Flush();

        // The chain being written from is untouched: a flush is a pure function of the live corpus.
        Assert.Equal(3, chain.TombstoneCount);
        Assert.Equal(Documents - 3, chain.Count);
        Assert.Equal(Documents - 3, flushed.Count);
        Assert.False(flushed.Contains("doc-0007"));
        Assert.False(flushed.Contains("doc-0100"));
        Assert.False(flushed.Contains("doc-0299"));
        Assert.True(flushed.Contains("doc-0008"));
    }

    [Fact]
    public void AFlushCarriesTheOverlayIntoTheSegment()
    {
        var (_, chain, engines) = Engines();

        chain.Add(new SearchDocument("doc-new", "brandnewterm onlyhere"));

        Assert.Equal(1, chain.OverlayCount);

        var flushed = chain.Flush();

        // The added document is now in the segment, not in an overlay.
        Assert.True(flushed.Contains("doc-new"));
        Assert.True(flushed.TryGetDocument("doc-new", out var document));
        Assert.Equal("brandnewterm onlyhere", document!.Text);

        var flushedEngine = new RankedTextSearchEngine(flushed, new Bm25Scorer());
        var found = flushedEngine.Search("brandnewterm");

        Assert.Single(found);
        Assert.Equal("doc-new", found[0].DocumentId);
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(engines.Chain.Search("brandnewterm")[0].Score),
            BitConverter.DoubleToInt64Bits(found[0].Score));
    }

    [Fact]
    public void AFlushSurvivesRoundTrippingItsBytes()
    {
        var (_, chain, engines) = Engines();

        chain.Remove("doc-0007");
        chain.Add(new SearchDocument("doc-new", "brandnewterm onlyhere"));

        var once = chain.Flush();

        // A flush is a pure function of the live corpus, so writing the merged segment out again
        // serves the same page.
        var twice = new SegmentChain([once]).Flush();

        foreach (string query in QuerySet)
        {
            var expected = new RankedTextSearchEngine(once, new Bm25Scorer()).Search(query);
            var actual = new RankedTextSearchEngine(twice, new Bm25Scorer()).Search(query);

            AssertSamePage(query, expected, actual);
        }
    }

    [Fact]
    public void ASearchServedFromAFlushedChainIsUnchangedForSurvivors()
    {
        // The one thing a merge is allowed to change: the tallied statistics, because the new segment
        // no longer counts the deleted documents. Everything else — the ids, the order — survives, and
        // the scores of every surviving document move, because N and the average length moved with it.
        var (_, chain, engines) = Engines();

        chain.Remove("doc-0007");
        chain.Remove("doc-0100");

        var before = engines.Chain.Search("common", new SearchOptions { Limit = 200 });
        var flushed = chain.Flush();
        var after = new RankedTextSearchEngine(flushed, new Bm25Scorer()).Search("common", new SearchOptions { Limit = 200 });

        // The same documents, in the same order — only the scores move.
        Assert.Equal(
            before.Where(r => r.DocumentId is not "doc-0007" and not "doc-0100").Select(r => r.DocumentId),
            after.Select(r => r.DocumentId));

        // And every score that moved, moved for the reason above: the statistics the flush wrote are no
        // longer the frozen ones the chain served. This is asserted rather than assumed, because a
        // merge that quietly moved nothing would be a merge whose statistics were still frozen.
        int moved = after.Count(hit =>
        {
            var reference = before.First(r => r.DocumentId == hit.DocumentId);

            return BitConverter.DoubleToInt64Bits(reference.Score) != BitConverter.DoubleToInt64Bits(hit.Score);
        });

        Assert.True(moved > 0, "the flush moved no score, so the statistics did not move with it");
    }

    [Fact]
    public void TheFlushedStatisticsNoLongerCountTheDeletedDocuments()
    {
        var (memory, chain, _) = Engines();

        int count = chain.Count;
        int statistic = chain.StatisticDocumentCount;

        Assert.Equal(Documents, statistic);

        chain.Remove("doc-0007");

        // Frozen while the tombstone is live.
        Assert.Equal(Documents, chain.StatisticDocumentCount);
        Assert.Equal(count - 1, chain.Count);

        var flushed = chain.Flush();

        // And rewritten once the merge has happened, because the new segment is its own corpus: its
        // document frequencies no longer count the deleted document, for every term it carried.
        Assert.Equal(Documents - 1, flushed.Count);

        var deleted = memory.GetTerms("doc-0007").Distinct().ToList();

        Assert.NotEmpty(deleted);

        foreach (string term in deleted)
            Assert.Equal(memory.DocumentFrequency(term) - 1, flushed.DocumentFrequency(term));

        // And a term it did not carry is untouched.
        foreach (string term in ((IVocabularyIndex)memory).Vocabulary.Where(t => !deleted.Contains(t)))
            Assert.Equal(memory.DocumentFrequency(term), flushed.DocumentFrequency(term));
    }

    [Fact]
    public void AFlushIsReadableAsAnOrdinarySegment()
    {
        var (memory, chain, _) = Engines();

        chain.Remove("doc-0007");
        chain.Add(new SearchDocument("doc-new", "brandnewterm onlyhere"));

        var flushed = chain.Flush();

        // Every segment invariant, on the merged output.
        Assert.Equal(Documents - 1 + 1, flushed.Count);
        Assert.True(flushed is ICandidateIndex);
        Assert.Equal(Documents - 1 + 1, flushed.Count);

        // Every document frequencies identically to the index it was written from, minus the deleted.
        foreach (string term in ((IVocabularyIndex)memory).Vocabulary)
            Assert.True(memory.DocumentFrequency(term) >= flushed.DocumentFrequency(term));

        for (int ordinal = 0; ordinal < flushed.Count; ordinal++)
            Assert.NotNull(((IAccumulatingIndex)flushed).DocumentAt(ordinal));

        // The frequencies are the index's minus the documents the chain deleted — the flush is the
        // first point at which the deletion is tallied into them.
        var deleted = memory.GetTerms("doc-0007").Distinct().ToHashSet();

        foreach (string term in ((IVocabularyIndex)memory).Vocabulary)
        {
            int expected = memory.DocumentFrequency(term) - (deleted.Contains(term) ? 1 : 0);

            Assert.Equal(expected, flushed.DocumentFrequency(term));
        }
    }

    private static readonly string[] QuerySet = ["common", "rareword", "term0 term1", "term5"];

    private static (InMemoryTextIndex, SegmentChain, (RankedTextSearchEngine Memory, RankedTextSearchEngine Chain)) Engines()
    {
        var memory = Build();
        var chain = new SegmentChain(Split(memory, 2));

        return (
            memory,
            chain,
            (new RankedTextSearchEngine(memory, new Bm25Scorer()), new RankedTextSearchEngine(chain, new Bm25Scorer())));
    }

    private static List<SegmentTextIndex> Split(InMemoryTextIndex memory, int count)
    {
        var documents = memory.Documents.ToList();
        var segments = new List<SegmentTextIndex>();
        int per = documents.Count / count;

        for (int i = 0; i < count; i++)
        {
            int take = i == count - 1 ? documents.Count - (i * per) : per;
            var chunk = new InMemoryTextIndex();

            foreach (var document in documents.Skip(i * per).Take(take))
                chunk.Add(document);

            segments.Add(new SegmentTextIndex(SegmentWriter.Write(chunk)));
        }

        return segments;
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
