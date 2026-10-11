using System.Linq;
using LexiSharp.Core;
using LexiSharp.Demo;
using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The demo's segment lane: the same page as the lexical lane it sits beside, on a real corpus.
/// </summary>
/// <remarks>
/// The segment lane exists to make one claim visible — a store serves the page the in-memory index
/// serves, bit for bit — and that claim has only ever been asserted against synthetic corpora. This
/// checks it on the corpus the demo actually runs, which is also the only place a wrong answer is
/// visible to a reader.
///
/// The failure this guards against is a wiring error rather than an arithmetic one: the store must be
/// built with the same tokenizer the lexical lane tokenizes with, because the dictionary holds
/// normalized terms and a lookup is by the normalized form. Build it with any other tokenizer and the
/// two lanes diverge in a way nothing else in the suite would notice, because both would be internally
/// consistent.
/// </remarks>
public sealed class DemoSegmentLaneTests
{
    [Fact]
    public void TheSegmentLaneServesTheLexicalLanesPage()
    {
        DemoCorpusSet corpus = DemoCorpusCatalog.Load(DemoCorpusCatalog.BuiltIn, null, "/nonexistent");
        var service = new DemoSearchService(corpus, WordSegmentation.UnicodeWordBoundaries);

        foreach (string query in corpus.SampleQueries)
        {
            var comparison = service.Compare(query, limit: 10);
            var segment = comparison.Lanes.Single(lane => lane.Key == "segment");
            var lexical = comparison.Lanes.Single(lane => lane.Key == "lexical");

            Assert.Equal(lexical.Hits.Count, segment.Hits.Count);

            for (int i = 0; i < lexical.Hits.Count; i++)
            {
                Assert.Equal(lexical.Hits[i].Id, segment.Hits[i].Id);

                // Bit for bit, not within a tolerance: the two lanes are the same corpus under the same
                // scorer, and the store's whole claim is that being on disk changes none of it.
                Assert.Equal(
                    BitConverter.DoubleToInt64Bits(lexical.Hits[i].Score),
                    BitConverter.DoubleToInt64Bits(segment.Hits[i].Score));
            }
        }
    }

    [Fact]
    public void TheSegmentLaneIsBuiltFromTheWholeCorpus()
    {
        DemoCorpusSet corpus = DemoCorpusCatalog.Load(DemoCorpusCatalog.BuiltIn, null, "/nonexistent");
        var service = new DemoSearchService(corpus, WordSegmentation.UnicodeWordBoundaries);

        // Every document, and one segment on disk: a commit collapses the chain, which is what keeps a
        // store's open cost proportional to one segment rather than to its history.
        Assert.Equal(corpus.Documents.Count, service.SegmentCost.Documents);
        Assert.Equal(1, service.SegmentCost.Segments);

        // And the cost is reported at all, because a reader comparing the segment lane's latency with
        // the lexical lane's has no way to judge the trade without it.
        Assert.True(service.SegmentCost.IndexMs > 0);
        Assert.True(service.SegmentCost.CommitMs >= 0);
        Assert.True(service.SegmentCost.OpenMs > 0);
    }

    [Fact]
    public void TheSegmentLaneServesTheSamePageForAQueryTheCorpusCannotMatch()
    {
        // A query matching nothing is the cheapest case and the one most likely to diverge, because the
        // accumulating pass is declined for it and the store falls back to its candidate path — the
        // path that took 36.6 ms against 10.8 us for a single rare term. An empty page from both lanes
        // is the parity claim at its thinnest.
        DemoCorpusSet corpus = DemoCorpusCatalog.Load(DemoCorpusCatalog.BuiltIn, null, "/nonexistent");
        var service = new DemoSearchService(corpus, WordSegmentation.UnicodeWordBoundaries);

        var comparison = service.Compare("zzzznotavocabularyterm", limit: 10);
        var segment = comparison.Lanes.Single(lane => lane.Key == "segment");
        var lexical = comparison.Lanes.Single(lane => lane.Key == "lexical");

        Assert.Empty(lexical.Hits);
        Assert.Empty(segment.Hits);
    }
}
