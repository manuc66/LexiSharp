using LexiSharp.Core;
using LexiSharp.Hybrid;
using Xunit;

namespace LexiSharp.Tests;

public class ReciprocalRankFusionMergerTests
{
    private sealed class StubEngine : ITextSearchEngine
    {
        private readonly IReadOnlyList<SearchResult> _results;

        public StubEngine(params SearchResult[] results) => _results = results;

        public void Index(IEnumerable<SearchDocument> documents) { }
        public void Add(SearchDocument document) { }
        public void Remove(string documentId) { }
        public void Clear() { }

        public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null)
        => _results;
    }

    private static readonly SearchDocument DocX = new("x", "apple");
    private static readonly SearchDocument DocY = new("y", "banana");
    private static readonly SearchDocument DocZ = new("z", "cherry");

    private static SearchResult R(string id, SearchDocument doc) => new(id, 1.0, doc);

    [Fact]
    public void Merge_AccumulatesReciprocalRanks_SharedDocWins()
    {
        // Engine A ranks: x, y. Engine B ranks: y, z. With k = 60:
        //   RRF(x) = 1/61 ≈ 0.0164
        //   RRF(y) = 1/61 + 1/62 ≈ 0.0325   <- echoed by both engines: boosted
        //   RRF(z) = 1/62 ≈ 0.0161
        var engineA = new StubEngine(R("x", DocX), R("y", DocY));
        var engineB = new StubEngine(R("y", DocY), R("z", DocZ));

        var hybrid = new HybridTextSearchEngine(
            new ITextSearchEngine[] { engineA, engineB },
            new ReciprocalRankFusionMerger());

        var results = hybrid.Search("query");

        Assert.Equal(new[] { "y", "x", "z" }, results.Select(r => r.DocumentId).ToArray());
        Assert.Equal(1.0 / 61.0 + 1.0 / 62.0, results[0].Score, 9);
        Assert.Equal(1.0 / 61.0, results[1].Score, 9);
        Assert.Equal(1.0 / 62.0, results[2].Score, 9);
    }

    [Fact]
    public void Merge_ZeroWeightEngineIsIgnored_AndZeroScoreDocsAreDropped()
    {
        var engineA = new StubEngine(R("x", DocX), R("y", DocY));
        var engineB = new StubEngine(R("y", DocY), R("z", DocZ));

        var hybrid = new HybridTextSearchEngine(
            new ITextSearchEngine[] { engineA, engineB },
            new ReciprocalRankFusionMerger(60, 1.0, 0.0));

        var results = hybrid.Search("query").ToList();

        // Only engine A contributes: x at rank 1, y at rank 2; z never appears in A.
        Assert.Equal(new[] { "x", "y" }, results.Select(r => r.DocumentId).ToArray());
        Assert.Equal(1.0 / 61.0, results[0].Score, 9);
        Assert.Equal(1.0 / 62.0, results[1].Score, 9);
    }

    [Fact]
    public void Merge_SingleEngine_PreservesItsOrdering()
    {
        var engine = new StubEngine(R("x", DocX), R("y", DocY), R("z", DocZ));

        var hybrid = new HybridTextSearchEngine(
            new ITextSearchEngine[] { engine },
            new ReciprocalRankFusionMerger());

        var results = hybrid.Search("query");

        Assert.Equal(new[] { "x", "y", "z" }, results.Select(r => r.DocumentId).ToArray());
        Assert.Equal(new[] { 1.0 / 61.0, 1.0 / 62.0, 1.0 / 63.0 },
            results.Select(r => r.Score).ToArray());
    }

    [Fact]
    public void Merge_DuplicateWithinOneEngine_CountsBestRankOnly()
    {
        var engine = new StubEngine(R("x", DocX), R("x", DocX), R("y", DocY));

        var hybrid = new HybridTextSearchEngine(
            new ITextSearchEngine[] { engine },
            new ReciprocalRankFusionMerger());

        var results = hybrid.Search("query").ToList();

        Assert.Equal(2, results.Count);
        Assert.Equal(1.0 / 61.0, results[0].Score, 9);
    }

    [Fact]
    public void Merge_EmptyLists_YieldNothing()
    {
        var hybrid = new HybridTextSearchEngine(
            new ITextSearchEngine[] { new StubEngine() },
            new ReciprocalRankFusionMerger());

        Assert.Empty(hybrid.Search("query"));
    }

    [Fact]
    public void Ctor_RejectsNonPositiveK()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReciprocalRankFusionMerger(k: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReciprocalRankFusionMerger(60, -1.0));
    }
}