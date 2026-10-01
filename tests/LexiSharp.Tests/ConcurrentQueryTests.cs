using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Concurrent <see cref="ITextSearchEngine.Search"/> over one shared index, which is what the
/// evaluation harness does to score its queries on more than one core.
/// </summary>
/// <remarks>
/// The harness reduces its per-query metrics into one slot per query and adds them in query order, so
/// the totals cannot depend on which worker finished first. That is only half of it: the harness also
/// reads one index from several threads at once, which is sound only if a search neither writes to the
/// index nor shares mutable state between calls. This is the check on that second half — rankings and
/// scores compared element by element, not merely the aggregate.
/// </remarks>
public class ConcurrentQueryTests
{
    private static InMemoryTextIndex Index()
    {
        var index = new InMemoryTextIndex();

        var documents = new List<SearchDocument>();

        for (int i = 0; i < 400; i++)
        {
            documents.Add(new SearchDocument(
                $"doc-{i}",
                $"policy {i} adoption language session token renewal economy health environment " +
                "roosevelt congress budget deficit reform treaty senate vote committee"));
        }

        // Two documents that are near-duplicates of the vocabulary above, so the top of the ranking
        // is decided by small score differences rather than by everything else being zero.
        documents.Add(new SearchDocument("twin-a", "policy adoption language session token renewal"));
        documents.Add(new SearchDocument("twin-b", "policy adoption language session token renewals"));

        index.Index(documents);
        return index;
    }

    private static string[] Queries()
    {
        var queries = new List<string>();

        // Enough distinct queries for the loop to engage, and long enough that the term-at-a-time pass
        // is the path taken rather than the per-document fallback.
        for (int i = 0; i < 64; i++)
        {
            queries.Add(
                $"policy adoption language session token renewal economy health environment " +
                $"roosevelt congress budget deficit reform treaty senate vote committee {i}");
        }

        return queries.ToArray();
    }

    private static List<(string DocumentId, double Score)> Search(
        ITextSearchEngine engine, string query) =>
        engine.Search(query, new SearchOptions(25))
            .Select(result => (result.DocumentId, result.Score))
            .ToList();

    [Fact]
    public void ConcurrentSearchesOverASharedIndexMatchTheSerialOnesExactly()
    {
        var index = Index();
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer(0.9, 0.4));
        string[] queries = Queries();

        var serial = queries.Select(query => Search(engine, query)).ToList();

        var concurrent = new List<(string DocumentId, double Score)>[queries.Length];

        // A statement body, not an assignment expression: the latter returns a value and binds to the
        // Func<int, int> overload, which is a different method with different semantics.
        Parallel.For(0, queries.Length, i => { concurrent[i] = Search(engine, queries[i]); });

        Assert.Equal(serial.Count, concurrent.Length);

        for (int i = 0; i < serial.Count; i++)
        {
            Assert.Equal(serial[i], concurrent[i]);
        }
    }

    /// <summary>
    /// The same query twice in a row, and the same query run concurrently with others, must be the
    /// same run — the failure mode a shared index would have is state left behind by one search and
    /// read by the next, which a single-threaded comparison cannot see.
    /// </summary>
    [Fact]
    public void ARepeatedSearchIsUnaffectedByTheSearchesRunningBesideIt()
    {
        var index = Index();
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer(0.9, 0.4));
        string[] queries = Queries();

        string probe = queries[0];

        var before = Search(engine, probe);

        Parallel.For(0, queries.Length, query => { Search(engine, queries[query]); });

        Assert.Equal(before, Search(engine, probe));
    }

    /// <summary>
    /// Several engines over one index — what the harness does when every row of its table shares the
    /// index instead of each rebuilding it — must rank exactly as each engine would over its own copy.
    /// </summary>
    [Fact]
    public void SharingOneIndexBetweenEnginesChangesNoResult()
    {
        var queries = Queries();

        var shared = Index();
        var sharedEngine = new RankedTextSearchEngine(shared, new Bm25Scorer(0.9, 0.4));
        var fromShared = queries.Select(query => Search(sharedEngine, query)).ToList();

        var privateEngine = new RankedTextSearchEngine(Index(), new Bm25Scorer(0.9, 0.4));
        var fromPrivate = queries.Select(query => Search(privateEngine, query)).ToList();

        Assert.Equal(fromPrivate, fromShared);
    }
}