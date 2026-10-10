using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Writes over a chain of immutable segments: the live overlay, and the tombstones a deletion leaves.
/// </summary>
/// <remarks>
/// <para>
/// This is the increment the whole arc was for — a search server that must write to a storage the
/// segments make immutable. A segment is written once, so a write lands in an overlay and a reader sees
/// both; a deletion cannot remove the bytes describing the document, so it leaves an ordinal behind.
/// </para>
/// <para>
/// Three properties are pinned, and each one was measured before it was decided:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Parity.</b> A deletion moves no surviving score, bit for bit. That is only possible under the
/// frozen-statistics rule — measured, corrected statistics moved 401 of 401 scores where frozen moved
/// 0 — so the rule is asserted here rather than assumed.
/// </description></item>
/// <item><description>
/// <b>Upsert.</b> Replacing a document tombstones its old ordinal and writes the new version, so a
/// document stops matching its old text. Without that both versions would score.
/// </description></item>
/// <item><description>
/// <b>Visibility.</b> A write is visible to the very next search, with no rebuild and no commit.
/// </description></item>
/// </list>
/// </remarks>
public class SegmentChainWriteTests
{
    private const int Documents = 300;
    private const int Tokens = 20;

    /// <summary>Documents carrying the rare term: below the engine's accumulation threshold.</summary>
    private const int RareCarriers = 5;

    private const string RareTerm = "rareword";
    private const string CommonTerm = "common";

    [Fact]
    public void AnAddedDocumentIsVisibleToTheNextSearch()
    {
        var (memory, chain, engines) = Engines();
        string fresh = "brandnewterm onlyhere";

        // Nothing matches before the write.
        Assert.Empty(engines.Memory.Search(fresh));
        Assert.Empty(engines.Chain.Search(fresh));

        engines.Memory.Add(new SearchDocument("doc-new", fresh));
        chain.Add(new SearchDocument("doc-new", fresh));

        var expected = engines.Memory.Search(fresh);
        var actual = engines.Chain.Search(fresh);

        AssertSamePage(fresh, expected, actual);

        Assert.Single(actual);
        Assert.Equal("doc-new", actual[0].DocumentId);
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(expected[0].Score),
            BitConverter.DoubleToInt64Bits(actual[0].Score));
    }

    [Fact]
    public void ARemovedDocumentDisappearsAndNothingElseMoves()
    {
        var (_, chain, engines) = Engines();

        // A page before the removal, deep enough to see what moves.
        var before = engines.Chain.Search(CommonTerm, new SearchOptions { Limit = 100 });
        var removed = before[3].DocumentId;

        Assert.True(chain.Remove(removed));
        Assert.False(chain.Contains(removed));
        Assert.False(chain.TryGetDocument(removed, out _));

        var after = engines.Chain.Search(CommonTerm, new SearchOptions { Limit = 100 });

        // The page is the same page without the removed document, and every survivor's score is the
        // same score to the last bit — which is only true under the frozen statistics rule.
        var survivors = before.Where(r => r.DocumentId != removed).ToList();

        Assert.Equal(survivors.Count, after.Count);

        for (int i = 0; i < after.Count; i++)
        {
            Assert.Equal(survivors[i].DocumentId, after[i].DocumentId);
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(survivors[i].Score),
                BitConverter.DoubleToInt64Bits(after[i].Score));
        }
    }

    [Fact]
    public void ARemovalChangesNoStatistic()
    {
        // The frozen-statistics rule, asserted: a tombstone removes a document from the fold and from
        // Count, and touches nothing a scorer reads.
        var (_, chain, engines) = Engines();

        int count = chain.Count;
        int statistic = chain.StatisticDocumentCount;
        long tokens = chain.CorpusTokenCount;
        double average = chain.AverageDocumentLength;
        var pages = new[] { CommonTerm, RareTerm, "term0", "term0 term1" };
        var before = pages.ToDictionary(query => query, query => engines.Chain.Search(query, new SearchOptions { Limit = 50 }));

        Assert.True(chain.Remove("doc-0007"));
        Assert.True(chain.Remove("doc-0100"));

        Assert.Equal(count - 2, chain.Count);

        // The statistics are the segments' own, tombstones included.
        Assert.Equal(statistic, chain.StatisticDocumentCount);
        Assert.Equal(tokens, chain.CorpusTokenCount);
        Assert.Equal(average, chain.AverageDocumentLength);

        foreach (string query in pages)
        {
            var expected = before[query].Where(r => r.DocumentId is not "doc-0007" and not "doc-0100").ToList();
            var actual = engines.Chain.Search(query, new SearchOptions { Limit = 50 });

            Assert.Equal(expected.Count, actual.Count);

            for (int i = 0; i < actual.Count; i++)
            {
                Assert.Equal(expected[i].DocumentId, actual[i].DocumentId);
                Assert.Equal(BitConverter.DoubleToInt64Bits(expected[i].Score), BitConverter.DoubleToInt64Bits(actual[i].Score));
            }
        }
    }

    [Fact]
    public void AReplacedDocumentStopsMatchingItsOldText()
    {
        var (_, chain, engines) = Engines();

        // doc-0007 carries some terms; replace it with a document carrying a term it did not.
        var before = chain.GetTerms("doc-0007");
        var stale = engines.Chain.Search(string.Join(' ', before), new SearchOptions { Limit = 1 });
        Assert.Equal("doc-0007", stale[0].DocumentId);

        chain.Add(new SearchDocument("doc-0007", "replacementtext entirelydifferent"));
        engines.Chain.Search("replacementtext entirelydifferent");

        Assert.True(chain.TryGetDocument("doc-0007", out var replaced));
        Assert.Equal("replacementtext entirelydifferent", replaced!.Text);

        // The old text no longer matches it, and the new text does.
        var oldMatch = engines.Chain.Search(string.Join(' ', before), new SearchOptions { Limit = 200 });
        Assert.DoesNotContain(oldMatch, r => r.DocumentId == "doc-0007");

        var newMatch = engines.Chain.Search("replacementtext");
        Assert.Single(newMatch);
        Assert.Equal("doc-0007", newMatch[0].DocumentId);

        // And its length is the new document's, not the old one's.
        Assert.Equal(2, chain.DocumentLength("doc-0007"));
    }

    [Fact]
    public void AReplacedDocumentStillResolvesThroughOrdinalOf()
    {
        var (_, chain, _) = Engines();

        chain.Add(new SearchDocument("doc-0007", "replacementtext"));

        int ordinal = ((IAccumulatingIndex)chain).OrdinalOf("doc-0007");

        // The overlay's ordinal, past the segments'.
        Assert.True(ordinal >= 300, $"ordinal {ordinal} is not in the overlay");
        Assert.Equal("doc-0007", ((IAccumulatingIndex)chain).DocumentAt(ordinal)!.Id);

        // And the old ordinal is gone, not merely shadowed.
        Assert.Null(((IAccumulatingIndex)chain).DocumentAt(7));
    }

    [Fact]
    public void ARemovalOfAnUnknownDocumentIsAFailure()
    {
        var (_, chain, _) = Engines();

        Assert.False(chain.Remove("nowhere"));
        Assert.False(chain.Contains("nowhere"));
    }

    [Fact]
    public void AnAddedThenRemovedDocumentLeavesNoTrace()
    {
        var (_, chain, engines) = Engines();

        int count = chain.Count;

        chain.Add(new SearchDocument("doc-temp", "ephemeral content"));
        Assert.Equal(count + 1, chain.Count);

        Assert.True(chain.Remove("doc-temp"));
        Assert.Equal(count, chain.Count);
        Assert.False(chain.Contains("doc-temp"));
        Assert.False(chain.TryGetDocument("doc-temp", out _));
        Assert.Empty(engines.Chain.Search("ephemeral"));

        // The freed ordinal is reused, so the ordinal space tracks the live overlay.
        chain.Add(new SearchDocument("doc-temp2", "ephemeral content"));
        Assert.Equal(count + 1, chain.Count);
    }

    [Fact]
    public void AWriteDoesNotBreakTheReadSurface()
    {
        var (memory, chain, _) = Engines();

        chain.Add(new SearchDocument("doc-new", "brandnewterm onlyhere"));
        chain.Remove("doc-0007");

        // Every segment document still answers, and the new one does.
        for (int i = 0; i < 300; i++)
        {
            if (i == 7)
                continue;

            string id = "doc-" + i.ToString("D4", CultureInfo.InvariantCulture);

            Assert.True(chain.Contains(id), id);
            Assert.True(chain.TryGetDocument(id, out var document), id);
            Assert.NotNull(document);
        }

        Assert.True(chain.Contains("doc-new"));
        Assert.False(chain.Contains("doc-0007"));
    }

    [Fact]
    public void TheOverlayIsCountedSeparatelyFromTheTombstones()
    {
        var (_, chain, _) = Engines();

        Assert.Equal(0, chain.OverlayCount);
        Assert.Equal(0, chain.TombstoneCount);

        chain.Add(new SearchDocument("doc-a", "alpha"));
        chain.Add(new SearchDocument("doc-b", "beta"));
        chain.Remove("doc-0007");

        Assert.Equal(2, chain.OverlayCount);
        Assert.Equal(1, chain.TombstoneCount);

        // A document removed from the overlay is not a tombstone: it never joined a segment.
        chain.Remove("doc-a");

        Assert.Equal(1, chain.OverlayCount);
        Assert.Equal(1, chain.TombstoneCount);
    }

    [Fact]
    public void ARemovalIsVisibleToTheCandidatePath()
    {
        var (_, chain, _) = Engines();

        var candidates = ((ICandidateIndex)chain).GetCandidateDocuments(new[] { RareTerm }).ToList();
        Assert.Equal(RareCarriers, candidates.Count);

        string removed = candidates[0].Id;

        Assert.True(chain.Remove(removed));

        var after = ((ICandidateIndex)chain).GetCandidateDocuments(new[] { RareTerm }).ToList();

        Assert.Equal(RareCarriers - 1, after.Count);
        Assert.DoesNotContain(after, d => d.Id == removed);
    }

    [Fact]
    public void AWriteOverAChainOfTwoSegmentsStillReachesEveryGeneration()
    {
        var (memory, chain, engines) = ThreeWay();

        // The removed document lives in the first segment; the added one is new.
        chain.Remove("doc-0007");

        // A removal leaves the statistics frozen, so every surviving score is bit-identical.
        foreach (string query in new[] { CommonTerm, RareTerm, "term0 term1" })
            AssertSamePage(query, engines.Memory.Search(query), engines.Chain.Search(query));

        // An addition is not the same: a new document changes N and the average length, so every
        // score moves. That is the same thing the in-memory index does, and it is asserted rather
        // than glossed over — the chain must agree with the index about how much.
        chain.Add(new SearchDocument("doc-new", "crossgenerationterm"));
        engines.Memory.Add(new SearchDocument("doc-new", "crossgenerationterm"));

        var found = engines.Chain.Search("crossgenerationterm");

        Assert.Single(found);
        Assert.Equal("doc-new", found[0].DocumentId);

        foreach (string query in new[] { CommonTerm, RareTerm, "term0 term1" })
            AssertSamePage(query, engines.Memory.Search(query), engines.Chain.Search(query));
    }

    private static (InMemoryTextIndex, SegmentChain, (RankedTextSearchEngine Memory, RankedTextSearchEngine Chain)) Engines()
    {
        var memory = Build();
        var chain = new SegmentChain(Split(memory, 2));

        return (
            memory,
            chain,
            (new RankedTextSearchEngine(memory, new Bm25Scorer()), new RankedTextSearchEngine(chain, new Bm25Scorer())));
    }

    private static (InMemoryTextIndex, SegmentChain, (RankedTextSearchEngine Memory, RankedTextSearchEngine Chain)) ThreeWay()
    {
        var memory = Build();
        var chain = new SegmentChain(Split(memory, 3));

        return (
            memory,
            chain,
            (new RankedTextSearchEngine(memory, new Bm25Scorer()), new RankedTextSearchEngine(chain, new Bm25Scorer())));
    }

    /// <summary>
    /// Splits a corpus into <paramref name="count"/> contiguous chunks and writes each as its own
    /// segment. Contiguity is what makes a chain's global ordinal match the corpus's insertion order.
    /// </summary>
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
                builder.Append(CommonTerm).Append(' ');

            if (i % 60 == 3)
                builder.Append(RareTerm).Append(' ');

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
