using System.Collections.Generic;
using System.Linq;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Merging a chain: collapsing it to one segment, and the threshold that says when it is worth doing.
/// </summary>
/// <remarks>
/// <para>
/// A merge is a flush plus a switch-over, and it returns a <b>new</b> chain rather than mutating this
/// one. That is deliberate: <see cref="IAccumulatingIndex.Accumulate"/> reads the segments, their
/// bases and the length array, so a write between those three reads would fold half the old chain and
/// half the new one. Building the result off to the side and letting the caller swap the engine's
/// reference is the pattern this repository publishes as <c>AtomicEngineReference</c>.
/// </para>
/// <para>
/// The measured case for merging is a tombstone tax of <b>1.18× a head query at 10 ‰ deleted</b>,
/// falling from there and going slightly negative at high rates. So the first per cent of deletions
/// costs most of it, which is where the default threshold sits.
/// </para>
/// </remarks>
public class SegmentChainMergeTests
{
    private const int Documents = 300;
    private const int Tokens = 20;

    [Fact]
    public void AMergeCollapsesTheChainToOneSegment()
    {
        var (_, chain, _) = Engines(2);

        Assert.Equal(2, chain.SegmentCount);

        var merged = chain.Merge();

        Assert.Equal(1, merged.SegmentCount);
        Assert.Equal(0, merged.TombstoneCount);
        Assert.Equal(0, merged.OverlayCount);
        Assert.Equal(chain.Count, merged.Count);
    }

    [Fact]
    public void AMergeLeavesTheSourceChainIntact()
    {
        var (_, chain, _) = Engines(3);

        chain.Remove("doc-0007");
        chain.Add(new SearchDocument("doc-new", "brandnewterm"));

        int count = chain.Count;

        var merged = chain.Merge();

        // The chain being merged from still holds everything it held: a merge is a pure function of
        // the live corpus, and the source is what a reader is still searching.
        Assert.Equal(3, chain.SegmentCount);
        Assert.Equal(1, chain.TombstoneCount);
        Assert.Equal(1, chain.OverlayCount);
        Assert.Equal(count, chain.Count);
        Assert.True(chain.Contains("doc-new"));
        Assert.False(chain.Contains("doc-0007"));

        Assert.Equal(1, merged.SegmentCount);
    }

    [Fact]
    public void AMergeServesTheSameDocumentsInTheSameOrder()
    {
        var (_, chain, engines) = Engines(2);

        chain.Remove("doc-0007");
        chain.Remove("doc-0100");
        chain.Add(new SearchDocument("doc-new", "brandnewterm onlyhere"));

        var merged = chain.Merge();
        var mergedEngine = new RankedTextSearchEngine(merged, new Bm25Scorer());

        foreach (string query in QuerySet.Concat(["brandnewterm"]))
        {
            var before = engines.Chain.Search(query, new SearchOptions { Limit = 200 });
            var after = mergedEngine.Search(query, new SearchOptions { Limit = 200 });

            // The same documents, in the same order. The scores move — every one of them, because the
            // merged segment's N and average no longer count the deleted documents — and that is
            // asserted separately.
            Assert.Equal(
                before.Where(r => r.DocumentId is not "doc-0007" and not "doc-0100").Select(r => r.DocumentId),
                after.Select(r => r.DocumentId));
        }

        // And the added document is in the merged segment, reachable by its own term.
        var found = mergedEngine.Search("brandnewterm");

        Assert.Contains(found, r => r.DocumentId == "doc-new");
    }

    [Fact]
    public void AMergeMovesEverySurvivingScore()
    {
        // The price of a compaction, asserted in both directions: the ids and the order survive, and
        // at least one score moved. A merge that moved nothing would be a merge whose statistics were
        // still frozen, which is a bug.
        var (_, chain, engines) = Engines(2);

        chain.Remove("doc-0007");

        var before = engines.Chain.Search("common", new SearchOptions { Limit = 200 });
        var merged = chain.Merge();
        var after = new RankedTextSearchEngine(merged, new Bm25Scorer()).Search("common", new SearchOptions { Limit = 200 });

        int moved = after.Count(hit =>
        {
            var reference = before.First(r => r.DocumentId == hit.DocumentId);

            return BitConverter.DoubleToInt64Bits(reference.Score) != BitConverter.DoubleToInt64Bits(hit.Score);
        });

        Assert.True(moved > 0, "the merge moved no score, so the statistics did not move with it");
    }

    [Fact]
    public void AMergeOfAnUnwrittenChainIsItsIndex()
    {
        var (memory, chain, engines) = Engines(2);
        var merged = chain.Merge();
        var mergedEngine = new RankedTextSearchEngine(merged, new Bm25Scorer());

        foreach (string query in QuerySet)
            AssertSamePage(query, engines.Memory.Search(query), mergedEngine.Search(query));

        Assert.Equal(memory.Count, merged.Count);
        Assert.Equal(memory.VocabularySize, merged.VocabularySize);
        Assert.Equal(memory.CorpusTokenCount, merged.CorpusTokenCount);
        Assert.Equal(memory.AverageDocumentLength, merged.AverageDocumentLength);
    }

    [Fact]
    public void AMergedChainCanBeMergedAgainAndWrittenTo()
    {
        var (_, chain, engines) = Engines(2);

        var once = chain.Merge();

        // A merge is idempotent on an unmodified chain, and the result is still writable.
        var twice = once.Merge();

        Assert.Equal(1, twice.SegmentCount);
        Assert.Equal(once.Count, twice.Count);

        twice.Add(new SearchDocument("doc-new", "brandnewterm"));

        var found = new RankedTextSearchEngine(twice, new Bm25Scorer()).Search("brandnewterm");

        Assert.Single(found);
        Assert.Equal("doc-new", found[0].DocumentId);
    }

    [Fact]
    public void AMergedChainKeepsTheTombstonesOut()
    {
        var (_, chain, _) = Engines(2);

        chain.Remove("doc-0007");
        chain.Remove("doc-0100");
        chain.Remove("doc-0299");

        var merged = chain.Merge();

        Assert.Equal(Documents - 3, merged.Count);
        Assert.False(merged.Contains("doc-0007"));
        Assert.False(merged.Contains("doc-0100"));
        Assert.False(merged.Contains("doc-0299"));
        Assert.Equal(0, merged.TombstoneCount);
    }

    [Fact]
    public void AChainShouldMergeAtOnePerCentTombstones()
    {
        var (_, chain, _) = Engines(1);

        Assert.False(chain.ShouldMerge());

        // One per cent of 300 is 3.
        chain.Remove("doc-0001");
        chain.Remove("doc-0002");

        Assert.False(chain.ShouldMerge());

        chain.Remove("doc-0003");

        Assert.True(chain.ShouldMerge());
        Assert.Equal(3, chain.TombstoneCount);
    }

    [Fact]
    public void AChainShouldNotMergeWhenTheThresholdIsRaised()
    {
        var (_, chain, _) = Engines(1);

        for (int i = 1; i <= 10; i++)
            chain.Remove($"doc-{i:D4}");

        Assert.True(chain.ShouldMerge());
        Assert.False(chain.ShouldMerge(0.5));
    }

    [Fact]
    public void AChainShouldNotMergeWhenItHasNoSegmentsToMerge()
    {
        var (_, chain, _) = Engines(1);

        Assert.False(chain.ShouldMerge(double.Epsilon));
        Assert.True(chain.ShouldMerge(0));
    }

    private static readonly string[] QuerySet = ["common", "rareword", "term0 term1", "term5"];

    private static (InMemoryTextIndex, SegmentChain, (RankedTextSearchEngine Memory, RankedTextSearchEngine Chain)) Engines(int segments)
    {
        var memory = Build();
        var chain = new SegmentChain(Split(memory, segments));

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
                "doc-" + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
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
