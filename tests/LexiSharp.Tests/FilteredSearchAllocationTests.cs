using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// A metadata-filtered search must not allocate per candidate document.
/// </summary>
/// <remarks>
/// <c>SearchOptions.PassesFilters</c> is called once per candidate, which on a filtered full scan
/// is once per document in the corpus. It walked its filter list with a <c>foreach</c> over an
/// <c>IReadOnlyList</c>, so the enumerator was resolved through the interface and heap-allocated on
/// every one of those calls: measured 288,072 bytes per search on a 10,000-document corpus, against
/// about 1,200 bytes for the same search without a filter. The comment next to that loop said the
/// short-circuit shape was chosen to avoid a per-candidate enumerator — the loop it annotated
/// allocated one anyway.
/// <para>
/// This is pinned with an allocation assertion rather than a stopwatch, because that is the
/// property that regressed and the one that is cheap to assert. It is also the reason the test
/// exists at all: a benchmark reports the number, a test refuses to let it come back.
/// </para>
/// </remarks>
public class FilteredSearchAllocationTests
{
    [Fact]
    public void AFilteredSearchDoesNotAllocatePerCandidateDocument()
    {
        const int Documents = 10_000;

        var index = new InMemoryTextIndex();
        var builder = new System.Text.StringBuilder();

        for (int i = 0; i < Documents; i++)
        {
            builder.Clear();

            for (int w = 0; w < 50; w++)
                builder.Append("body").Append(w % 97).Append(' ');

            builder.Append("needleterm ");

            index.Add(new SearchDocument(
                "doc-" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture),
                builder.ToString()));
        }

        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        // Rejects nothing, so every document is filtered and the per-candidate path is the whole
        // cost. A rejecting filter would skip scoring rather than exercise the loop.
        var options = new SearchOptions(
            Limit: 10,
            Filters: new[] { new MetadataFilter("__absent__", MetadataFilterOperator.NotEqual, "__absent__") });

        const int Iterations = 200;

        for (int i = 0; i < 50; i++)
            engine.Search("needleterm", options);

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < Iterations; i++)
            engine.Search("needleterm", options);

        long after = GC.GetAllocatedBytesForCurrentThread();
        long perSearch = (after - before) / Iterations;

        // Generous enough to survive a GC-induced hiccup, two orders of magnitude below the
        // 288 KB the interface enumerator cost. If someone reintroduces a per-candidate
        // allocation this fails by a wide margin rather than marginally.
        Assert.True(
            perSearch < 16 * 1024,
            $"a filtered search allocated {perSearch} bytes per query on {Documents} documents, " +
            "which is the signature of an allocation per candidate document");
    }

    /// <summary>
    /// The filters must still decide the same thing, including short-circuiting on the first
    /// rejection and handling several of them.
    /// </summary>
    [Fact]
    public void FiltersStillShortCircuitAndCombine()
    {
        var index = new InMemoryTextIndex();
        index.Index(new[]
        {
            new SearchDocument("1", "alpha", new Dictionary<string, string> { ["cat"] = "a", ["tag"] = "t1" }),
            new SearchDocument("2", "alpha", new Dictionary<string, string> { ["cat"] = "b", ["tag"] = "t1" }),
            new SearchDocument("3", "alpha", new Dictionary<string, string> { ["cat"] = "a", ["tag"] = "t2" }),
            new SearchDocument("4", "alpha", new Dictionary<string, string> { ["cat"] = "b", ["tag"] = "t2" }),
        });

        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        Assert.Equal(
            new[] { "1", "3" },
            engine.Search("alpha", new SearchOptions(
                Limit: 10,
                Filters: new[] { new MetadataFilter("cat", MetadataFilterOperator.Equal, "a") }))
                .Select(r => r.DocumentId).OrderBy(x => x, StringComparer.Ordinal).ToArray());

        Assert.Equal(
            new[] { "1" },
            engine.Search("alpha", new SearchOptions(
                Limit: 10,
                Filters: new[]
                {
                    new MetadataFilter("cat", MetadataFilterOperator.Equal, "a"),
                    new MetadataFilter("tag", MetadataFilterOperator.Equal, "t1"),
                }))
                .Select(r => r.DocumentId).ToArray());

        // An empty filter list is the other branch of the same method.
        Assert.Equal(
            4,
            engine.Search("alpha", new SearchOptions(Limit: 10, Filters: [])).Count);
    }
}
