using LexiSharp.Core;
using LexiSharp.Indexing;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The decoded-postings cache must be invisible to the search result: a page served through a warm
/// cache is the plain page, bit for bit, and the budget keeps the cache where it is told to be.
/// </summary>
public class DecodedPostingsCacheTests
{
    [Fact]
    public void ACachedPageEqualsThePlainPageBitForBit()
    {
        var index = InMemory();
        var bytes = SegmentWriter.Write(index);

        var plain = new RankedSearchWrapper(new SegmentTextIndex(bytes, tokenizer: null));
        var cached = new RankedSearchWrapper(
            new SegmentTextIndex(new ArraySegmentSource(bytes), tokenizer: null, new DecodedPostingsCache(10_000_000)));

        foreach (string query in new[] { "needleterm body42", "needleterm scoring3", "absent term47" })
        {
            // Fill the cache on the first pass, hit it on the second and third.
            for (int pass = 0; pass < 3; pass++)
            {
                double[] expect = plain.Search(query);
                double[] actual = cached.Search(query);

                Assert.Equal(expect.Length, actual.Length);

                for (int i = 0; i < expect.Length; i++)
                    Assert.Equal(expect[i], actual[i]);
            }
        }
    }

    [Fact]
    public void TheBudgetEvictsTheLeastRecentlyUsed()
    {
        var index = InMemory();
        var bytes = SegmentWriter.Write(index);

        // A roomy cache holds both folded terms, and reports exactly their arrays.
        var cache = new DecodedPostingsCache(10_000_000);
        var engine = new RankedSearchWrapper(
            new SegmentTextIndex(new ArraySegmentSource(bytes), tokenizer: null, cache));

        engine.Search("needleterm body42");

        Assert.True(cache.TryGet("needleterm", out var needle, out _));
        Assert.True(cache.TryGet("body42", out var body, out _));
        Assert.Equal(needle.Length + body.Length, cache.EntryCount);

        // A budget that holds the first term only makes the second fold evict it.
        var tight = new DecodedPostingsCache(needle.Length);
        var tightEngine = new RankedSearchWrapper(
            new SegmentTextIndex(new ArraySegmentSource(bytes), tokenizer: null, tight));

        tightEngine.Search("needleterm body42");

        Assert.False(tight.TryGet("needleterm", out _, out _), "the oldest term was evicted");
        Assert.True(tight.TryGet("body42", out _, out _), "the newest term stayed");

        // Reuse of a held term keeps it: a third fold touches both, and the one used last survives.
        var recency = new DecodedPostingsCache(needle.Length + body.Length);
        var recencyEngine = new RankedSearchWrapper(
            new SegmentTextIndex(new ArraySegmentSource(bytes), tokenizer: null, recency));

        recencyEngine.Search("body42 needleterm");
        recencyEngine.Search("needleterm body42");

        Assert.True(recency.TryGet("needleterm", out _, out _));
        Assert.True(recency.TryGet("body42", out _, out _));
    }

    private static InMemoryTextIndex InMemory()
    {
        var index = new InMemoryTextIndex();
        var builder = new System.Text.StringBuilder();

        for (int i = 0; i < 5_000; i++)
        {
            builder.Clear();

            for (int w = 0; w < 50; w++)
                builder.Append("body").Append(w % 97).Append(' ');

            builder.Append("needleterm scoring").Append(i % 5).Append(' ');

            index.Add(new SearchDocument(
                "doc-" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture),
                builder.ToString()));
        }

        return index;
    }

    /// <summary>Just enough of an engine to fold a query through the index and read the scores back.</summary>
    private sealed class RankedSearchWrapper(SegmentTextIndex index)
    {
        private readonly LexiSharp.Ranking.RankedTextSearchEngine _engine =
            new(index, new LexiSharp.Ranking.Bm25Scorer());

        public double[] Search(string query) =>
            _engine.Search(query, new LexiSharp.Core.SearchOptions(Limit: 10))
                .Select(r => r.Score)
                .ToArray();
    }
}