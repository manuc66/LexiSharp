using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class FacetedSearchEngineTests
{
    private static RankedTextSearchEngine CreateEngine()
    {
        var engine = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());

        engine.Index(new[]
        {
            new SearchDocument("1", "fast car review",
                new Dictionary<string, string> { ["kind"] = "review", ["lang"] = "en" }),
            new SearchDocument("2", "fast boat review",
                new Dictionary<string, string> { ["kind"] = "review", ["lang"] = "fr" }),
            new SearchDocument("3", "slow car notes",
                new Dictionary<string, string> { ["kind"] = "notes", ["lang"] = "en" }),
            // Matches "fast" but carries no Fields at all (and Category is never faceted).
            new SearchDocument("4", "fast unrelated content", Category: "editorial"),
            new SearchDocument("5", "car essay draft",
                new Dictionary<string, string> { ["kind"] = "essay", ["lang"] = "en" }),
        });

        return engine;
    }

    private static readonly SearchOptions Page = new(Limit: 10);

    [Fact]
    public void SearchWithFacets_ReturnsTheSameResultsAsSearch_WithOrderedBuckets()
    {
        var engine = CreateEngine();

        var faceted = engine.SearchWithFacets("fast car", Page, ["kind", "lang"]);

        Assert.Equal(
            engine.Search("fast car", Page).Select(r => r.DocumentId),
            faceted.Results.Select(r => r.DocumentId));
        Assert.Equal(5, faceted.Results.Count);

        Assert.Equal(new[] { "kind", "lang" }, faceted.Buckets.Select(b => b.Field));

        // kind: review ×2, then the tied singles in ordinal order (essay < notes).
        // Document 4 carries no kind, so only 4 of the 5 matches count here.
        var kind = faceted.Buckets[0];
        Assert.Equal(
            new[] { ("review", 2), ("essay", 1), ("notes", 1) },
            kind.Values.Select(v => (v.Value, v.Count)));

        // lang: en ×3 (docs 1, 3, 5), fr ×1 — count order, not alphabetical.
        var lang = faceted.Buckets[1];
        Assert.Equal(
            new[] { ("en", 3), ("fr", 1) },
            lang.Values.Select(v => (v.Value, v.Count)));
    }

    [Fact]
    public void SearchWithFacets_CountsTheWholeMatchSet_IgnoringOffsetAndLimit()
    {
        var engine = CreateEngine();

        var faceted = engine.SearchWithFacets(
            "fast car",
            new SearchOptions(Limit: 1, Offset: 3),
            ["kind"]);

        Assert.Single(faceted.Results);

        var kind = Assert.Single(faceted.Buckets);
        Assert.Equal(
            new[] { ("review", 2), ("essay", 1), ("notes", 1) },
            kind.Values.Select(v => (v.Value, v.Count)));
    }

    [Fact]
    public void SearchWithFacets_AppliesMinimumScoreToTheCounts()
    {
        var engine = CreateEngine();

        var faceted = engine.SearchWithFacets(
            "fast car",
            new SearchOptions(Limit: 10, MinimumScore: double.MaxValue),
            ["kind"]);

        Assert.Empty(faceted.Results);
        Assert.Empty(faceted.Buckets);
    }

    [Fact]
    public void SearchWithFacets_OmitsFieldsNoMatchedDocumentCarries()
    {
        var engine = CreateEngine();

        // "missing" exists on no document; "category" is not a Fields key (Category is
        // never faceted) — neither produces a bucket, even though doc 4 matched.
        var faceted = engine.SearchWithFacets("fast car", Page, ["kind", "missing", "category"]);

        Assert.Equal(5, faceted.Results.Count);
        Assert.Equal("kind", Assert.Single(faceted.Buckets).Field);
    }

    [Fact]
    public void SearchWithFacets_WithoutFacetFields_ReturnsResultsOnly()
    {
        var engine = CreateEngine();

        var nullFields = engine.SearchWithFacets("fast car", Page, null);
        Assert.Empty(nullFields.Buckets);
        Assert.Equal(
            engine.Search("fast car", Page).Select(r => r.DocumentId),
            nullFields.Results.Select(r => r.DocumentId));

        var emptyFields = engine.SearchWithFacets("fast car", Page, []);
        Assert.Empty(emptyFields.Buckets);
    }

    [Fact]
    public void SearchWithFacets_DeduplicatesRequestedFields()
    {
        var engine = CreateEngine();

        var faceted = engine.SearchWithFacets("fast car", Page, ["kind", null!, "kind"]);

        Assert.Equal("kind", Assert.Single(faceted.Buckets).Field);
    }

    [Fact]
    public void SearchWithFacets_NullQuery_Throws()
    {
        var engine = CreateEngine();

        Assert.Throws<ArgumentNullException>(() => engine.SearchWithFacets(null!));
        Assert.Throws<ArgumentNullException>(() => engine.SearchWithFacets(null!, null, ["kind"]));
    }
}
