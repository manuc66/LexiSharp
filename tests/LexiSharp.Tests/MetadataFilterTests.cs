using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class MetadataFilterTests
{
    private static readonly SearchDocument Article2023 = new(
        "a1", "vector search basics",
        new Dictionary<string, string> { ["kind"] = "article", ["year"] = "2023", ["tags"] = "search,nlp" });

    private static readonly SearchDocument Talk2024 = new(
        "t1", "hybrid retrieval talk",
        new Dictionary<string, string> { ["kind"] = "talk", ["year"] = "2024" });

    private static readonly SearchDocument Bare = new("b1", "no metadata at all");

    [Fact]
    public void Equal_MatchesExactValue()
    {
        var filter = new MetadataFilter("kind", MetadataFilterOperator.Equal, "article");

        Assert.True(filter.Matches(Article2023));
        Assert.False(filter.Matches(Talk2024));
        Assert.False(filter.Matches(Bare));
    }

    [Fact]
    public void NotEqual_PassesDocumentsWithoutTheField()
    {
        var filter = new MetadataFilter("kind", MetadataFilterOperator.NotEqual, "talk");

        Assert.True(filter.Matches(Article2023));
        Assert.False(filter.Matches(Talk2024));
        Assert.True(filter.Matches(Bare));
    }

    [Fact]
    public void Contains_MatchesSubstring()
    {
        var filter = new MetadataFilter("tags", MetadataFilterOperator.Contains, "nlp");

        Assert.True(filter.Matches(Article2023));
        Assert.False(filter.Matches(Talk2024));
    }

    [Fact]
    public void GreaterThan_ComparesNumerically_WhenBothSidesParse()
    {
        var filter = new MetadataFilter("year", MetadataFilterOperator.GreaterThan, "2023");

        Assert.True(filter.Matches(Talk2024));
        Assert.False(filter.Matches(Article2023));
    }

    [Fact]
    public void LessThan_ComparesNumerically_WhenBothSidesParse()
    {
        var filter = new MetadataFilter("year", MetadataFilterOperator.LessThan, "2024");

        Assert.True(filter.Matches(Article2023));
        Assert.False(filter.Matches(Talk2024));
    }

    [Fact]
    public void Comparison_FallsBackToOrdinalString_WhenNotNumeric()
    {
        var document = new SearchDocument("x", "x",
            new Dictionary<string, string> { ["version"] = "beta" });

        Assert.True(new MetadataFilter("version", MetadataFilterOperator.GreaterThan, "alpha").Matches(document));
        Assert.False(new MetadataFilter("version", MetadataFilterOperator.LessThan, "alpha").Matches(document));
    }

    [Fact]
    public void Ctor_RejectsEmptyFieldOrNullValue()
    {
        Assert.Throws<ArgumentException>(() => new MetadataFilter("", MetadataFilterOperator.Equal, "x"));
        Assert.Throws<ArgumentException>(() => new MetadataFilter("  ", MetadataFilterOperator.Equal, "x"));
        Assert.Throws<ArgumentNullException>(() => new MetadataFilter("kind", MetadataFilterOperator.Equal, null!));
    }

    [Fact]
    public void Search_FiltersGateResults()
    {
        var engine = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());
        engine.Index(new[] { Article2023, Talk2023Doc(), Talk2024 });

        static SearchDocument Talk2023Doc() =>
            new("t2", "vector search talk", new Dictionary<string, string> { ["kind"] = "talk", ["year"] = "2023" });

        var options = new SearchOptions(
            Limit: 10,
            Filters: new[] { new MetadataFilter("kind", MetadataFilterOperator.Equal, "article") });

        var results = engine.Search("vector search", options);

        Assert.Equal(new[] { "a1" }, results.Select(r => r.DocumentId).ToArray());
    }

    [Fact]
    public void Search_MultipleFiltersAreAnded()
    {
        var engine = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());
        engine.Index(new[] { Article2023, Talk2024 });

        var options = new SearchOptions(
            Limit: 10,
            Filters: new[]
            {
                new MetadataFilter("kind", MetadataFilterOperator.Equal, "talk"),
                new MetadataFilter("year", MetadataFilterOperator.GreaterThan, "2023"),
            });

        var results = engine.Search("hybrid retrieval", options);

        Assert.Equal(new[] { "t1" }, results.Select(r => r.DocumentId).ToArray());
    }

    [Fact]
    public void Search_WithoutFilters_DefaultsToUnfiltered()
    {
        var engine = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());
        engine.Index(new[] { Article2023, Talk2024 });

        // One matching term per document: without filters, both are returned.
        var results = engine.Search("vector hybrid");

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void Search_FilteredOutDocuments_NeverReachTheScorer()
    {
        var engine = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());
        engine.Index(new[] { Article2023, Talk2024 });

        var options = new SearchOptions(
            Limit: 10,
            Filters: new[] { new MetadataFilter("kind", MetadataFilterOperator.NotEqual, "talk") });

        var results = engine.Search("talk", options);

        // The only talk is filtered out; the article scores 0 and is excluded by the
        // score-0 convention, so nothing matches.
        Assert.Empty(results);
    }
}
