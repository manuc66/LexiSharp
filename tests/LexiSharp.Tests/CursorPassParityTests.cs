using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The document-at-a-time cursor pass must produce the page the term-at-a-time accumulator produces:
/// the same documents, in the same order, with the same score bits, ties included.
/// </summary>
/// <remarks>
/// <para>
/// The two passes sum a document's terms in the same query order — one by folding a term's postings
/// into a buffer, the other by walking cursors over the same arrays and writing on the first touch —
/// so they owe each other bit equality, not a similar ranking. A tolerance here would let the
/// regression the repository's parity tests exist to catch through: <see cref="TopRankedWindow"/>
/// decides a tie on <c>==</c>, so a last-bit difference between two tied documents reorders the page.
/// </para>
/// <para>
/// The cursor pass is forced on the small fixtures, because the density threshold that selects it in
/// production is a measurement at tens of thousands of documents and a unit test cannot reach it.
/// </para>
/// </remarks>
public class CursorPassParityTests
{
    private const int Documents = 400;

    /// <summary>
    /// A corpus full of exact ties: <c>ranking</c> appears once in every document and twice in a third,
    /// so a large block scores identically and the page is decided by the tie-break alone.
    /// </summary>
    private static List<SearchDocument> Corpus() =>
        Enumerable.Range(0, Documents).Select(i => new SearchDocument(
            $"doc-{i:D4}",
            $"term statistics {(i % 3 == 0 ? "ranking ranking" : "ranking")} filler",
            new Dictionary<string, string> { ["category"] = i % 2 == 0 ? "a" : "b" },
            TextFields: new Dictionary<string, string> { ["title"] = "term filler" })).ToList();

    public static TheoryData<string> Scorers =>
        new() { "bm25", "bm25-tuned", "bm25plus", "bm25plus-tuned", "bm25l", "bm25l-tuned", "tfidf" };

    private static ITextScorer Build(string scorerKind) =>
        scorerKind switch
        {
            "bm25" => new Bm25Scorer(),
            "bm25-tuned" => new Bm25Scorer(0.9, 0.4),
            "bm25plus" => new Bm25PlusScorer(),
            "bm25plus-tuned" => new Bm25PlusScorer(1.1, 0.6, 0.9),
            "bm25l" => new Bm25LScorer(),
            "bm25l-tuned" => new Bm25LScorer(1.3, 0.5, 0.4),
            "tfidf" => new TfIdfScorer(),
            _ => throw new ArgumentOutOfRangeException(nameof(scorerKind), scorerKind, "unknown scorer"),
        };

    [Theory]
    [MemberData(nameof(Scorers))]
    public void BothPassesReturnTheSamePage(string scorerKind)
    {
        var index = new InMemoryTextIndex();
        index.Index(Corpus());

        var accumulated = new RankedTextSearchEngine(index, Build(scorerKind));
        var cursor = new RankedTextSearchEngine(index, Build(scorerKind)) { ForcedPass = RankedTextSearchEngine.ScoringPass.Cursor };

        foreach (string query in new[] { "term", "term statistics", "filler", "term ranking", "term term term", "ranking filler statistics" })
        {
            foreach (var options in new[]
            {
                new SearchOptions(Limit: 25),
                new SearchOptions(Limit: 10, Offset: 7),
                new SearchOptions(Limit: 400),
            })
            {
                AssertSamePage(
                    accumulated.Search(query, options),
                    cursor.Search(query, options),
                    $"{scorerKind} / '{query}' / {options.Limit}+{options.Offset}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Scorers))]
    public void BothPassesApplyTheSameFiltersAndExclusions(string scorerKind)
    {
        var index = new InMemoryTextIndex();
        index.Index(Corpus());

        var accumulated = new RankedTextSearchEngine(index, Build(scorerKind));
        var cursor = new RankedTextSearchEngine(index, Build(scorerKind)) { ForcedPass = RankedTextSearchEngine.ScoringPass.Cursor };

        var excluded = new HashSet<string>(StringComparer.Ordinal) { "doc-0000", "doc-0007", "doc-0100" };

        foreach (var options in new[]
        {
            new SearchOptions(Limit: 25, Filters: [new MetadataFilter("category", MetadataFilterOperator.Equal, "a")]),
            new SearchOptions(Limit: 25, ExcludedDocumentIds: excluded),
            new SearchOptions(
                Limit: 25,
                Filters: [new MetadataFilter("category", MetadataFilterOperator.NotEqual, "zzz")],
                ExcludedDocumentIds: excluded),
        })
        {
            AssertSamePage(
                accumulated.Search("term statistics", options),
                cursor.Search("term statistics", options),
                $"{scorerKind} / filtered");
        }
    }

    [Theory]
    [MemberData(nameof(Scorers))]
    public void BothPassesCountTheSameFacets(string scorerKind)
    {
        var index = new InMemoryTextIndex();
        index.Index(Corpus());

        var accumulated = new RankedTextSearchEngine(index, Build(scorerKind));
        var cursor = new RankedTextSearchEngine(index, Build(scorerKind)) { ForcedPass = RankedTextSearchEngine.ScoringPass.Cursor };

        foreach (string query in new[] { "term", "ranking filler statistics" })
        {
            var expected = accumulated.SearchWithFacets(query, new SearchOptions(Limit: 25), ["category"]);
            var actual = cursor.SearchWithFacets(query, new SearchOptions(Limit: 25), ["category"]);

            AssertSamePage(expected.Results, actual.Results, $"{scorerKind} / facets / '{query}'");
            Assert.Equal(FacetLines(expected.Buckets), FacetLines(actual.Buckets));
        }
    }

    private static IEnumerable<string> FacetLines(IEnumerable<FacetBucket> buckets) =>
        buckets.Select(bucket =>
            $"{bucket.Field}={string.Join(",", bucket.Values.Select(value => $"{value.Value}:{value.Count}"))}");

    /// <summary>
    /// A tokenizer that does not offer spans sends the query down the string plan, which reaches the
    /// cursor pass through a different entry point than BM25's span plan does.
    /// </summary>
    [Fact]
    public void BothPassesAgreeWhenTheQueryCannotBeDescribedAsSpans()
    {
        var index = new InMemoryTextIndex();
        index.Index(Corpus());

        var tokenizer = new StringOnlyTokenizer(Tokenizer.Default);
        var accumulated = new RankedTextSearchEngine(index, new Bm25Scorer(), tokenizer);
        var cursor = new RankedTextSearchEngine(index, new Bm25Scorer(), tokenizer) { ForcedPass = RankedTextSearchEngine.ScoringPass.Cursor };

        foreach (string query in new[] { "term", "term statistics", "ranking filler statistics" })
        {
            AssertSamePage(
                accumulated.Search(query, new SearchOptions(Limit: 25)),
                cursor.Search(query, new SearchOptions(Limit: 25)),
                $"string plan / '{query}'");
        }
    }

    /// <summary>
    /// A committed store with the decoded-postings cache lends arrays, and the cursor pass then serves
    /// the same page the in-memory index serves. Without the cache the store declines and the engine
    /// keeps the accumulator — the fallback, not a failure.
    /// </summary>
    [Fact]
    public void ACommittedStoreLendsPostingsAndServesTheSamePage()
    {
        var documents = Corpus();
        string directory = Directory.CreateTempSubdirectory("lexisharp-cursor-").FullName;

        try
        {
            using var committed = WriteAndCommit(directory, documents, decodedCacheEntries: 1_000_000);

            // A commit returns a store over the segment it just wrote in memory, and that segment has
            // no decoded cache until the store is reopened. Reopening is the read path a caller uses.
            using var reopened = new SegmentedTextIndex(directory, decodedCacheEntries: 1_000_000);

            // One segment, no overlay, no tombstones, and the cache can decode every term.
            Assert.True(((IPostingArrayIndex)reopened).TryResolvePostingArrays("ranking", out var postings));
            Assert.Equal(reopened.DocumentFrequency("ranking"), postings.Count);
            Assert.NotNull(((IPostingArrayIndex)reopened).LengthsByOrdinal);

            var memory = new InMemoryTextIndex();
            memory.Index(documents);

            var expected = new RankedTextSearchEngine(memory, new Bm25Scorer());
            var cursor = new RankedTextSearchEngine(reopened, new Bm25Scorer()) { ForcedPass = RankedTextSearchEngine.ScoringPass.Cursor };

            foreach (string query in new[] { "term", "term statistics", "ranking filler statistics" })
            {
                AssertSamePage(
                    expected.Search(query, new SearchOptions(Limit: 25)),
                    cursor.Search(query, new SearchOptions(Limit: 25)),
                    $"store cursor / '{query}'");
            }
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AStoreWithoutTheDecodedCacheFallsBack()
    {
        var documents = Corpus();
        string directory = Directory.CreateTempSubdirectory("lexisharp-cursor-").FullName;

        try
        {
            using var committed = WriteAndCommit(directory, documents, decodedCacheEntries: null);
            using var reopened = new SegmentedTextIndex(directory);

            Assert.Null(((IPostingArrayIndex)reopened).LengthsByOrdinal);
            Assert.False(((IPostingArrayIndex)reopened).TryResolvePostingArrays("ranking", out _));

            var memory = new InMemoryTextIndex();
            memory.Index(documents);
            var expected = new RankedTextSearchEngine(memory, new Bm25Scorer());
            var forced = new RankedTextSearchEngine(reopened, new Bm25Scorer()) { ForcedPass = RankedTextSearchEngine.ScoringPass.Cursor };

            AssertSamePage(
                expected.Search("term statistics", new SearchOptions(Limit: 25)),
                forced.Search("term statistics", new SearchOptions(Limit: 25)),
                "store fallback");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static SegmentedTextIndex WriteAndCommit(
        string directory,
        IReadOnlyList<SearchDocument> documents,
        int? decodedCacheEntries)
    {
        var store = new SegmentedTextIndex(directory, decodedCacheEntries: decodedCacheEntries);

        store.Index(documents);

        // Commit replaces the store with one reopened from the segment it wrote, which is the shape
        // that has one segment, no overlay and no tombstones.
        return store.Commit();
    }

    /// <summary>
    /// Same ids in the same order and the same score bits. The score is compared exactly, not to a
    /// tolerance: a last-bit difference is a different ranking whenever two documents tie.
    /// </summary>
    private static void AssertSamePage(
        IReadOnlyList<SearchResult> expected,
        IReadOnlyList<SearchResult> actual,
        string because)
    {
        Assert.Equal(expected.Count, actual.Count);

        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].DocumentId, actual[i].DocumentId);
            Assert.Equal(expected[i].Score, actual[i].Score);
        }

        Assert.True(
            expected.Select(result => result.DocumentId).SequenceEqual(actual.Select(result => result.DocumentId)),
            $"the pages differ for {because}");
    }

    /// <summary>An <see cref="ITokenizer"/> without the span capability, so the string plan is taken.</summary>
    private sealed class StringOnlyTokenizer(ITokenizer inner) : ITokenizer
    {
        public IReadOnlyList<string> Tokenize(string text) => inner.Tokenize(text);
    }
}
