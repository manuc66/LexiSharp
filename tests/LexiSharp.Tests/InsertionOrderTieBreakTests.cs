using System.Collections.Generic;
using System.IO;
using System.Linq;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// <see cref="SearchOptions.TieBreak"/> <see cref="Ranking.TieBreak.InsertionOrder"/>: what the order
/// a tied page comes back in is, and what it does not depend on.
/// </summary>
/// <remarks>
/// <para>
/// The defect these tests pin: the order used to be the position the engine <i>produced</i> a
/// candidate in, and the engine produces candidates in different orders on different paths. The
/// term-at-a-time pass walks the posting lists and records the first touch of each ordinal; the
/// per-document loop walks the union of the terms' candidates, or the whole corpus once that union
/// covers half of it. Which path served a query is a function of corpus size and term frequency, so
/// the tied order moved with the corpus — the same documents ranked differently at two sizes, with
/// nothing the caller did changing.
/// </para>
/// <para>
/// The order is the document's ordinal: the position it holds in the index's ordinal space, which is
/// where it was added. Every path supplies it, so the order is the same whichever path ran and at
/// whatever corpus size. An index that cannot enumerate its ordinals keeps the order it enumerates
/// its documents in, which is all such an index can say.
/// </para>
/// <para>
/// Every fixture here asserts the path it exercised before asserting anything about the page, the way
/// <see cref="AccumulateFilteredQueriesTests"/> does. <c>alpha</c> and <c>beta</c> each carry half the
/// tied documents, interleaved, so the document frequency behind the idf is equal, the term frequency
/// is one and the length is the same — which makes every candidate's score the same double. Ids
/// descend with the insertion order, so a tie-break that read ids would read as the reverse of the
/// expected page. Without the exact tie the whole file would pass on any order at all.
/// </para>
/// </remarks>
public class InsertionOrderTieBreakTests
{
    /// <summary>A query whose two terms between them match every tied document and nothing else.</summary>
    private const string Query = "alpha beta";

    /// <summary>
    /// The tied documents, in insertion order: half carry <c>alpha</c>, half <c>beta</c>, at
    /// alternating ordinals. Metadata fields are not indexed, so the tier entry leaves every score
    /// exactly where it was and a filter has something to select on.
    /// </summary>
    private static List<SearchDocument> TiedDocuments(int pairs)
    {
        var documents = new List<SearchDocument>(2 * pairs);

        for (int i = 0; i < 2 * pairs; i++)
        {
            documents.Add(new SearchDocument(
                "doc-" + (char)('z' - i),
                (i % 2 == 0 ? "alpha" : "beta") + " zulu",
                Fields: new Dictionary<string, string> { ["tier"] = "x" }));
        }

        return documents;
    }

    private static InMemoryTextIndex IndexWithTiedDocuments(int pairs, int filler)
    {
        var index = new InMemoryTextIndex();

        foreach (var document in TiedDocuments(pairs))
            index.Add(document);

        for (int f = 0; f < filler; f++)
            index.Add(new SearchDocument("filler-" + f.ToString("D2"), "unrelated words entirely"));

        return index;
    }

    private static SearchOptions TiedOptions() =>
        new(Limit: 50, TieBreak: TieBreak.InsertionOrder);

    private static IReadOnlyList<SearchResult> Search(
        InMemoryTextIndex index, SearchOptions? options = null) =>
        new RankedTextSearchEngine(index, new Bm25Scorer()).Search(Query, options);

    private static IReadOnlyList<string> IdsOf(IEnumerable<SearchResult> page) =>
        page.Select(result => result.DocumentId).ToList();

    /// <summary>
    /// The reach of <see cref="Query"/>: the sum of the distinct terms' document frequencies, which
    /// is the quantity the engine compares against
    /// <see cref="RankedTextSearchEngine.AccumulationThreshold"/>.
    /// </summary>
    private static long ReachOf(InMemoryTextIndex index) =>
        new Tokenizer().Tokenize(Query).Sum(term => (long)index.DocumentFrequency(term));

    private static void AssertFixtureReachesAccumulation(InMemoryTextIndex index)
    {
        long reachable = ReachOf(index);
        int threshold = RankedTextSearchEngine.AccumulationThreshold(index.OrdinalSpace);

        Assert.True(
            reachable >= threshold,
            $"fixture does not reach the term-at-a-time pass: {reachable} reachable documents, threshold {threshold}. These tests would pass vacuously.");
    }

    /// <summary>
    /// Asserts the per-document loop's candidate branch — below the accumulation floor, and with the
    /// terms covering less than half the corpus, which is the branch whose order is the union of the
    /// posting lists rather than the corpus.
    /// </summary>
    private static void AssertFixtureTakesTheCandidatePath(InMemoryTextIndex index)
    {
        long reachable = ReachOf(index);
        int threshold = RankedTextSearchEngine.AccumulationThreshold(index.OrdinalSpace);

        Assert.True(
            reachable < threshold,
            $"fixture does not stay under the accumulation floor: {reachable} reachable documents, threshold {threshold}.");
        Assert.True(
            reachable < index.Count / 2.0,
            $"fixture does not stay under the candidate branch: {reachable} reachable documents of {index.Count}.");
    }

    /// <summary>Asserts the per-document loop's full scan — the other side of the same boundary.</summary>
    private static void AssertFixtureTakesTheFullScan(InMemoryTextIndex index)
    {
        long reachable = ReachOf(index);
        int threshold = RankedTextSearchEngine.AccumulationThreshold(index.OrdinalSpace);

        Assert.True(
            reachable < threshold,
            $"fixture does not stay under the accumulation floor: {reachable} reachable documents, threshold {threshold}.");
        Assert.True(
            reachable >= index.Count / 2.0,
            $"fixture does not reach the full scan: {reachable} reachable documents of {index.Count}.");
    }

    /// <summary>
    /// The tie is what makes every assertion below a statement about the tie-break rather than about
    /// the scoring, so it is asserted rather than left to be true.
    /// </summary>
    private static void AssertEveryScoreTies(IReadOnlyList<SearchResult> page)
    {
        Assert.NotEmpty(page);
        Assert.All(page, result => Assert.Equal(page[0].Score, result.Score));
    }

    [Fact]
    public void TheTermAtATimePassOrdersTiedDocumentsByInsertionOrder()
    {
        var (index, expected) = Fixture(pairs: 4);

        AssertFixtureReachesAccumulation(index);

        var page = Search(index, TiedOptions());

        AssertEveryScoreTies(page);
        Assert.Equal(expected, IdsOf(page));
    }

    [Fact]
    public void TheCandidatePathOrdersTiedDocumentsByInsertionOrder()
    {
        var (index, expected) = Fixture(pairs: 3, filler: 10);

        AssertFixtureTakesTheCandidatePath(index);

        var page = Search(index, TiedOptions());

        AssertEveryScoreTies(page);
        Assert.Equal(expected, IdsOf(page));
    }

    [Fact]
    public void TheFullScanOrdersTiedDocumentsByInsertionOrder()
    {
        var (index, expected) = Fixture(pairs: 3, filler: 6);

        AssertFixtureTakesTheFullScan(index);

        var page = Search(index, TiedOptions());

        AssertEveryScoreTies(page);
        Assert.Equal(expected, IdsOf(page));
    }

    [Fact]
    public void TheTiedOrderIsTheSameAtEveryCorpusSize()
    {
        // Six tied documents and a growing corpus around them. The boundary of the candidate branch
        // sits between twelve documents and thirteen, so these sizes walk from the full scan onto
        // the candidate path and stay there — the case where the order used to change.
        foreach (int filler in new[] { 6, 7, 10, 13, 16, 19, 26 })
        {
            var (index, expected) = Fixture(pairs: 3, filler);

            Assert.Equal(expected, IdsOf(Search(index, TiedOptions())));
        }
    }

    [Fact]
    public void AFilteredQueryOrdersTiedDocumentsByInsertionOrder()
    {
        var (index, expected) = Fixture(pairs: 3, filler: 10);

        AssertFixtureTakesTheCandidatePath(index);

        IReadOnlyList<MetadataFilter> admitsEveryDocument =
            new[] { new MetadataFilter("tier", MetadataFilterOperator.Equal, "x") };

        var options = new SearchOptions(
            Limit: 50,
            Filters: admitsEveryDocument,
            TieBreak: TieBreak.InsertionOrder);

        var page = Search(index, options);

        AssertEveryScoreTies(page);
        Assert.Equal(expected, IdsOf(page));

        // The guard keeps a filtered query on the per-document loop under this option, and the loop
        // a caller would reach with AccumulateFilteredQueries turned off is the same one — so the
        // two answers are the same answer, on the same documents in the same order.
        var loop = Search(index, options with { AccumulateFilteredQueries = false });

        Assert.Equal(IdsOf(page), IdsOf(loop));
    }

    [Fact]
    public void AFacetedQueryOrdersTiedDocumentsByInsertionOrder()
    {
        var (index, expected) = Fixture(pairs: 3, filler: 10);

        AssertFixtureTakesTheCandidatePath(index);

        var faceted = new RankedTextSearchEngine(index, new Bm25Scorer())
            .SearchWithFacets(Query, TiedOptions(), new[] { "tier" });

        var page = faceted.Results;

        AssertEveryScoreTies(page);
        Assert.Equal(expected, IdsOf(page));

        // The facet counters saw every tied document exactly once, whichever order they arrived in.
        var bucket = Assert.Single(faceted.Buckets);
        var value = Assert.Single(bucket.Values);

        Assert.Equal("tier", bucket.Field);
        Assert.Equal("x", value.Value);
        Assert.Equal(expected.Count, value.Count);
    }

    [Fact]
    public void ARemovedDocumentFreesTheOrdinalTheNextOneIsAddedAt()
    {
        var (index, expected) = Fixture(pairs: 3, filler: 10);

        AssertFixtureTakesTheCandidatePath(index);

        string removed = expected[2];
        int freed = index.OrdinalOf(removed);
        Assert.True(index.Remove(removed));

        // A removal frees the slot rather than closing the corpus up, and the next document added
        // takes that slot — so "the order documents were added" is the ordinal order, with a
        // re-added document taking the position of the slot it was handed rather than the end.
        string reAdded = "inserted";
        index.Add(new SearchDocument(reAdded, "alpha zulu"));

        Assert.Equal(freed, index.OrdinalOf(reAdded));
        Assert.Equal(2, freed);

        var page = Search(index, TiedOptions());

        AssertEveryScoreTies(page);
        Assert.Equal(
            new[] { expected[0], expected[1], reAdded, expected[3], expected[4], expected[5] },
            IdsOf(page));
    }

    [Fact]
    public void AStoreOrdersTiedDocumentsByInsertionOrder()
    {
        string directory = Path.Combine(Path.GetTempPath(), "lexisharp-insertion-" + System.Guid.NewGuid().ToString("N"));

        try
        {
            var committed = TiedDocuments(4);
            var overlaid = TiedDocuments(4);

            using (var store = new SegmentedTextIndex(directory))
            {
                foreach (var document in committed)
                    store.Add(document);

                using var written = store.Commit();

                Assert.Equal(
                    committed.Select(document => document.Id),
                    IdsOf(new RankedTextSearchEngine(written, new Bm25Scorer()).Search(Query, TiedOptions())));
            }

            // The same documents in the overlay, before any commit: the overlay ordinals continue
            // the ones the segments end on, so the order is the same one.
            using (var store = new SegmentedTextIndex(directory))
            {
                foreach (var document in overlaid)
                    store.Add(document);

                Assert.Equal(
                    overlaid.Select(document => document.Id),
                    IdsOf(new RankedTextSearchEngine(store, new Bm25Scorer()).Search(Query, TiedOptions())));
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AnIndexWithoutAnOrdinalSpaceIsOrderedByTheOrderItEnumeratesDocumentsIn()
    {
        // A caller-defined index that offers the read surface and nothing else: no ordinal space, no
        // candidate capability, no id-by-ordinal. The only insertion order it has is the order it
        // enumerates its documents in, so this wrapper enumerates them rotated — neither insertion
        // order nor id order — to prove the page follows the enumeration.
        var index = IndexWithTiedDocuments(pairs: 4, filler: 0);
        var order = index.Documents.Skip(1).Concat(index.Documents.Take(1)).ToList();

        var engine = new RankedTextSearchEngine(new EnumeratedOrderIndex(index, order), new Bm25Scorer());

        var page = engine.Search(Query, TiedOptions());

        AssertEveryScoreTies(page);
        Assert.Equal(order.Select(document => document.Id), IdsOf(page));
    }

    private static (InMemoryTextIndex Index, IReadOnlyList<string> Expected) Fixture(int pairs, int filler = 0)
    {
        var documents = TiedDocuments(pairs);
        var index = IndexWithTiedDocuments(pairs, filler);

        return (index, documents.Select(document => document.Id).ToList());
    }

    /// <summary>
    /// The read surface, delegated, with the document enumeration deliberately its own — an index
    /// that cannot answer an ordinal with a document has no ordinal order to give.
    /// </summary>
    private sealed class EnumeratedOrderIndex(
        IReadOnlyTextIndex inner,
        IReadOnlyList<SearchDocument> order)
        : IReadOnlyTextIndex
    {
        public IReadOnlyCollection<SearchDocument> Documents { get; } = order.ToList();

        public int Count => inner.Count;

        public double AverageDocumentLength => inner.AverageDocumentLength;

        public int VocabularySize => inner.VocabularySize;

        public long CorpusTokenCount => inner.CorpusTokenCount;

        public bool Contains(string documentId) => inner.Contains(documentId);

        public IReadOnlyList<string> GetTerms(string documentId) => inner.GetTerms(documentId);

        public IReadOnlyList<int> GetTermPositions(string documentId, string term) =>
            inner.GetTermPositions(documentId, term);

        public int DocumentFrequency(string term) => inner.DocumentFrequency(term);

        public int CorpusFrequency(string term) => inner.CorpusFrequency(term);

        public int TermFrequency(string documentId, string term) => inner.TermFrequency(documentId, term);

        public int DocumentLength(string documentId) => inner.DocumentLength(documentId);

        public bool TryGetDocument(
            string documentId,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SearchDocument? document) =>
            inner.TryGetDocument(documentId, out document);
    }
}
