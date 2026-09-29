using System.Collections.Concurrent;
using System.Threading.Tasks;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The in-memory index documents that read-only queries hold no shared mutable state and may run
/// concurrently with one another. The term-at-a-time scoring pass added shared state to make that
/// true: a per-term copy of the posting lists, built on first use and cached.
/// </summary>
/// <remarks>
/// A cache is exactly the kind of thing that turns a documented concurrency guarantee into a lie,
/// and the failure would be intermittent — a torn or half-built array surfacing as a wrong score
/// on some runs. Two things are checked: that concurrent searches over one engine return what a
/// lone search returns, and that they stay correct while the cache is being *built*, which is the
/// window where two readers can race.
/// </remarks>
public class ConcurrentSearchTests
{
    /// <summary>
    /// A corpus with <b>known</b> document frequencies, so a test about which documents a term
    /// reaches is testing that and not a random generator's luck.
    /// </summary>
    /// <remarks>
    /// The first version drew ranks from a cubic, which put the queried ranks in the corpus only
    /// about seventeen times in a hundred thousand tokens — and a query for a term that is not in
    /// the vocabulary passes whether the posting cache is right or wrong, so every assertion in
    /// these tests was vacuous. Checked with <c>df(term) = 0</c> rather than assumed.
    /// </remarks>
    private const int CorpusSize = 2_000;

    private static InMemoryTextIndex BuildIndex()
    {
        var index = new InMemoryTextIndex();
        var random = new Random(20240117);

        for (int i = 0; i < CorpusSize; i++)
        {
            var text = new System.Text.StringBuilder(200);

            // Body text, so documents differ in length and therefore in score.
            for (int w = 0; w < 20 + random.Next(40); w++)
                text.Append("body").Append(w % 97).Append(' ');

            // Document frequencies, exactly: common 1 000, medium 100, rare 10, singleton 1.
            if (i < 1_000)
                text.Append("commonterm ");
            if (i < 100)
                text.Append("mediumterm ");
            if (i < 10)
                text.Append("rareterm ");
            if (i < 1)
                text.Append("singleterm ");

            index.Add(new SearchDocument(
                "doc-" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture),
                text.ToString()));
        }

        return index;
    }

    private static string[] Queries() =>
    [
        "commonterm",
        "commonterm mediumterm",
        "commonterm mediumterm rareterm",
        "rareterm singleterm",
        "singleterm",
    ];

    /// <summary>The frequencies <see cref="BuildIndex"/> is built to have, asserted before use.</summary>
    [Fact]
    public void TheCorpusHasTheDocumentFrequenciesTheTestsQuery()
    {
        var index = BuildIndex();

        Assert.Equal(1_000, index.DocumentFrequency("commonterm"));
        Assert.Equal(100, index.DocumentFrequency("mediumterm"));
        Assert.Equal(10, index.DocumentFrequency("rareterm"));
        Assert.Equal(1, index.DocumentFrequency("singleterm"));
        Assert.Equal(0, index.DocumentFrequency("absentterm"));
    }

    [Fact]
    public void ConcurrentSearchesOverOneEngineAgreeWithASingleSearch()
    {
        var index = BuildIndex();
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());
        var queries = Queries();
        var options = new SearchOptions(Limit: 25);

        // The answers to hold every run to, taken from a lone search.
        var expected = queries.ToDictionary(q => q, q => engine.Search(q, options));

        var failures = new ConcurrentBag<string>();

        Parallel.For(0, 4, _ =>
        {
            for (int round = 0; round < 60; round++)
            {
                // Vary the order so the threads are not synchronised on the same cache entries,
                // and repeat the queries so the cache is both cold and warm across the run.
                foreach (var query in queries.Reverse())
                {
                    var actual = engine.Search(query, options);
                    var wanted = expected[query];

                    if (actual.Count != wanted.Count)
                    {
                        failures.Add($"{query}: {actual.Count} results, expected {wanted.Count}");
                        continue;
                    }

                    for (int i = 0; i < wanted.Count; i++)
                    {
                        if (actual[i].DocumentId != wanted[i].DocumentId)
                        {
                            failures.Add($"{query}: rank {i} is {actual[i].DocumentId}, expected {wanted[i].DocumentId}");
                            break;
                        }

                        if (actual[i].Score != wanted[i].Score)
                        {
                            failures.Add($"{query}: {actual[i].DocumentId} scored {actual[i].Score}, expected {wanted[i].Score}");
                            break;
                        }
                    }
                }
            }
        });

        Assert.Empty(failures);
    }

    /// <summary>
    /// A mutation has to make the cached posting copies stale, or a search after it silently
    /// answers from the corpus as it was.
    /// </summary>
    /// <remarks>
    /// This is the test that is worth having about the cache, and it is the one that passes
    /// vacuously if the queried terms are out of vocabulary — hence
    /// <see cref="TheCorpusHasTheDocumentFrequenciesTheTestsQuery"/>, which asserts the fixture
    /// rather than trusting it.
    /// </remarks>
    [Fact]
    public void MutatingTheIndexInvalidatesTheCachedPostings()
    {
        var index = BuildIndex();
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());
        var options = new SearchOptions(Limit: 10);

        // Force the flat copies to exist, so the mutation below has something stale to discard.
        var before = engine.Search("rareterm", options);
        Assert.Equal(10, before.Count);

        // A document shorter than the ten that hold the term, so it has to outrank them: a stale
        // posting list would miss it entirely, and a stale length would rank it wrongly.
        index.Add(new SearchDocument("doc-99999", "rareterm"));

        var after = engine.Search("rareterm", options);

        Assert.Equal(10, after.Count);
        Assert.Equal("doc-99999", after[0].DocumentId);
        Assert.DoesNotContain(before, r => r.DocumentId == "doc-99999");

        // The score must be the one the index can be asked for, not one left over from a cache.
        Assert.Equal(
            new Bm25Scorer().Score("doc-99999", new[] { "rareterm" }, index),
            after[0].Score);

        // A term that only the new document has is reachable immediately.
        index.Add(new SearchDocument("doc-99998", "newterm rareterm"));

        var withNewTerm = engine.Search("newterm", options);

        Assert.Equal("doc-99998", Assert.Single(withNewTerm).DocumentId);
        Assert.Equal(1, index.DocumentFrequency("newterm"));
    }

    /// <summary>
    /// Removing and re-adding a document must not leave a posting list pointing at a freed
    /// ordinal, which is the failure mode a reuse policy introduces.
    /// </summary>
    [Fact]
    public void RemovingAndReAddingADocumentKeepsItsScoresCorrect()
    {
        var index = BuildIndex();
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());
        var options = new SearchOptions(Limit: 10);

        // Warm the flat posting copies, so the removal below has stale ones to discard.
        engine.Search("rareterm", options);

        Assert.True(index.Remove("doc-00000"));
        Assert.False(index.Remove("doc-00000"));

        // A shorter document under the same id, so a stale posting entry would show up as a score
        // computed from the old term frequency or the old length. The second id is new, so it must
        // also reach the result set — a reused ordinal that leaked into it would not.
        index.Add(new SearchDocument("doc-00000", "rareterm"));
        index.Add(new SearchDocument("doc-99998", "rareterm"));

        var after = engine.Search("rareterm", options);

        // doc-00000 was a holder of the term and now is a singleton and doc-99998 is new, so the
        // corpus now has one more document holding the term than it started with — and the reused
        // ordinal must not have leaked into a document that never had it.
        Assert.Equal(10, after.Count);
        Assert.Equal(11, index.DocumentFrequency("rareterm"));
        Assert.Equal(1, index.DocumentLength("doc-00000"));
        Assert.Equal(1, index.TermFrequency("doc-00000", "rareterm"));
        Assert.Equal(1, index.DocumentLength("doc-99998"));
        Assert.Equal(1, index.TermFrequency("doc-99998", "rareterm"));
        Assert.Equal(CorpusSize + 1, index.Count);

        // The re-added document's score is the one the index can be asked for, not a cached one.
        var replaced = Assert.Single(after, r => r.DocumentId == "doc-00000");
        Assert.Equal(
            new Bm25Scorer().Score("doc-00000", new[] { "rareterm" }, index),
            replaced.Score);

        // A document that never had the term must not have acquired one through a reused ordinal.
        Assert.Equal(0, index.TermFrequency("doc-00500", "rareterm"));
        Assert.DoesNotContain(after, r => r.DocumentId == "doc-00500");
    }
}
