using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The <see cref="SynchronizedTextSearchEngine"/> wrapper: it serializes writers against readers
/// without serializing readers against each other, and forwards the engine's own capabilities
/// unchanged.
/// </summary>
/// <remarks>
/// The stress test asserts the failure-free half of the guarantee, the same way
/// <c>Predict_IsConsistentUnderConcurrentReinforcement</c> does for the classifier: a race is not
/// asserted to exist (that fails on machines with fewer cores), but the wrapped engine must never
/// throw and must never return a page its own serial runs could not have produced.
/// </remarks>
public class SynchronizedTextSearchEngineTests
{
    private static InMemoryTextIndex Index()
    {
        var index = new InMemoryTextIndex();

        index.Index(Enumerable.Range(0, 400).Select(i =>
            new SearchDocument(
                $"doc-{i}",
                $"policy {i} adoption language session token renewal economy health environment " +
                "roosevelt congress budget deficit reform treaty senate vote committee")));

        return index;
    }

    private static SearchDocument ToggleDoc() =>
        new("toggle", "policy adoption language session token renewal");

    [Fact]
    public void Search_ReturnsTheUnwrappedEnginesResultsExactly()
    {
        var index = Index();
        var plain = new RankedTextSearchEngine(index, new Bm25Scorer(0.9, 0.4));
        using var wrapped = new SynchronizedTextSearchEngine(plain);

        string query = "policy adoption language session token renewal economy";

        Assert.Equal(
            plain.Search(query, new SearchOptions(25)),
            wrapped.Search(query, new SearchOptions(25)));
    }

    [Fact]
    public void Remove_ReturnsWhetherTheDocumentWasPresent()
    {
        var index = Index();
        index.Add(ToggleDoc());

        var plain = new RankedTextSearchEngine(index, new Bm25Scorer());
        using var wrapped = new SynchronizedTextSearchEngine(plain);

        Assert.True(wrapped.Remove("toggle"));
        Assert.False(wrapped.Remove("toggle"));
    }

    [Fact]
    public async Task WritesAndReadsOverlap_WithoutThrowingOrCorruptingAPage()
    {
        var index = Index();
        var plain = new RankedTextSearchEngine(index, new Bm25Scorer(0.9, 0.4));
        using var wrapped = new SynchronizedTextSearchEngine(plain);

        var knownIds = index.Documents.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var exceptions = new System.Collections.Concurrent.ConcurrentQueue<Exception>();

        using var writerStop = new CancellationTokenSource();
        var writer = Task.Run(() =>
        {
            int round = 0;

            while (!writerStop.IsCancellationRequested)
            {
                if ((round++ & 1) == 0)
                    wrapped.Add(ToggleDoc());
                else
                    wrapped.Remove("toggle");
            }
        });

        Parallel.For(0, 100_000, _ =>
        {
            try
            {
                var page = wrapped.Search("policy adoption language session token renewal", new SearchOptions(25));

                // A valid page: distinct ids, every id a corpus document or the toggled one — a
                // torn read would surface as a duplicate, an unknown id, or an exception.
                Assert.Equal(page.Count, page.Select(r => r.DocumentId).Distinct().Count());
                Assert.All(page, r => Assert.True(
                    knownIds.Contains(r.DocumentId) || r.DocumentId == "toggle",
                    $"unexpected id '{r.DocumentId}'"));
            }
            catch (Exception exception)
            {
                exceptions.Enqueue(exception);
            }
        });

        writerStop.Cancel();
        await writer;
        Assert.Empty(exceptions);
    }

    [Fact]
    public void Facets_ForwardToTheWrappedEngineUnchanged()
    {
        var index = new InMemoryTextIndex();
        index.Index(new[]
        {
            new SearchDocument("a", "gamma", Fields: new Dictionary<string, string> { ["kind"] = "g" }),
            new SearchDocument("b", "delta", Fields: new Dictionary<string, string> { ["kind"] = "u" }),
        });

        var plain = new RankedTextSearchEngine(index, new Bm25Scorer());
        using var wrapped = new SynchronizedTextSearchEngine(plain);

        var expected = ((IFacetedSearchEngine)plain).SearchWithFacets("gamma", facetFields: ["kind"]);
        var actual = ((IFacetedSearchEngine)wrapped).SearchWithFacets("gamma", facetFields: ["kind"]);

        Assert.Equal(expected.Buckets.Count, actual.Buckets.Count);

        for (int i = 0; i < expected.Buckets.Count; i++)
        {
            // FacetBucket's synthesized equality is reference-based on the Values list, so the
            // buckets are compared field by field and value by value.
            Assert.Equal(expected.Buckets[i].Field, actual.Buckets[i].Field);
            Assert.Equal(expected.Buckets[i].Values, actual.Buckets[i].Values);
        }

        Assert.Equal(expected.Results, actual.Results);
    }

    [Fact]
    public void Facets_InnerWithoutCapability_ThrowsNamingTheWrappedType()
    {
        using var wrapped = new SynchronizedTextSearchEngine(new StubEngine());

        var exception = Assert.Throws<NotSupportedException>(
            () => ((IFacetedSearchEngine)wrapped).SearchWithFacets("gamma", facetFields: ["kind"]));

        Assert.Contains(nameof(StubEngine), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Dispose_MakesSubsequentCallsThrow()
    {
        var wrapped = new SynchronizedTextSearchEngine(new RankedTextSearchEngine(Index(), new Bm25Scorer()));

        wrapped.Dispose();

        Assert.Throws<ObjectDisposedException>(() => wrapped.Search("gamma"));
        Assert.Throws<ObjectDisposedException>(() => wrapped.Clear());
    }

    private sealed class StubEngine : ITextSearchEngine
    {
        public void Index(IEnumerable<SearchDocument> documents) { }

        public void Add(SearchDocument document) { }

        public bool Remove(string documentId) => false;

        public void Clear() { }

        public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null) =>
            Array.Empty<SearchResult>();
    }
}