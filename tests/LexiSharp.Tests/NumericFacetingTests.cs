using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class NumericFacetingTests
{
    // Every document carries the same body, so the query matches all of them with the same score
    // and the match set is exactly the corpus. The field under test is the only variable.
    private static RankedTextSearchEngine CreateEngine(params (string Id, string? Price)[] documents)
    {
        var engine = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());

        engine.Index(documents.Select(document => new SearchDocument(
            document.Id,
            "alpha beta",
            document.Price is null
                ? null
                : new Dictionary<string, string> { ["price"] = document.Price })));

        return engine;
    }

    /// <summary>The corpus most tests use: six parseable prices, one not, one absent, one repeat.</summary>
    private static RankedTextSearchEngine CreateMixedEngine() => CreateEngine(
        ("1", "10"),
        ("2", "19.99"),
        ("3", "20"),
        ("4", "25.5"),
        ("5", "-3"),
        ("6", "n/a"),
        ("7", null),
        ("8", "10"));

    private static readonly SearchOptions Page = new(Limit: 10);

    private static void AssertRange(
        NumericFacetRange range,
        double from,
        double to,
        int count,
        bool upperInclusive = false)
    {
        Assert.Equal(from, range.From);
        Assert.Equal(to, range.To);
        Assert.Equal(count, range.Count);
        Assert.Equal(upperInclusive, range.UpperInclusive);
    }

    [Fact]
    public void Ranges_CountHalfOpenBuckets_AndExcludeTheUpperBoundary()
    {
        var engine = CreateMixedEngine();

        var page = engine.SearchWithNumericFacets(
            "alpha",
            Page,
            numericFacets: [NumericFacetRequest.Ranges("price", [10, 20, 30])]);

        Assert.Equal(8, page.Results.Count);

        var bucket = Assert.Single(page.NumericBuckets);
        Assert.Equal("price", bucket.Field);

        // [10, 20): 10 twice (docs 1 and 8) and 19.99. Doc 3's 20 sits exactly on the upper
        // boundary and stays out.
        // [20, 30): 20 — exactly the lower boundary — and 25.5.
        // -3 is below the first boundary, "n/a" is not a number, doc 7 has no price.
        Assert.Equal(2, bucket.Ranges.Count);
        AssertRange(bucket.Ranges[0], 10, 20, 3);
        AssertRange(bucket.Ranges[1], 20, 30, 2);
        Assert.Equal(1, bucket.UnparseableCount);
        Assert.All(bucket.Ranges, range => Assert.False(range.UpperInclusive));
    }

    [Fact]
    public void Ranges_ValueExactlyAtABoundary_JoinsOnlyTheRangeItOpens()
    {
        var engine = CreateEngine(("1", "9.99"), ("2", "10"), ("3", "20"), ("4", "20.01"));

        var page = engine.SearchWithNumericFacets(
            "alpha",
            Page,
            numericFacets: [NumericFacetRequest.Ranges("price", [10, 20, 30])]);

        var ranges = Assert.Single(page.NumericBuckets).Ranges;

        AssertRange(ranges[0], 10, 20, 1);   // 10 is included; 9.99 is outside.
        AssertRange(ranges[1], 20, 30, 2);   // 20 is included, 20.01 follows it.
    }

    [Fact]
    public void Ranges_EmptyRangeIsReportedWithZero()
    {
        var engine = CreateEngine(("1", "10"), ("2", "25"));

        var page = engine.SearchWithNumericFacets(
            "alpha",
            Page,
            numericFacets: [NumericFacetRequest.Ranges("price", [10, 20, 20.5, 30])]);

        var ranges = Assert.Single(page.NumericBuckets).Ranges;

        Assert.Equal(3, ranges.Count);
        AssertRange(ranges[0], 10, 20, 1);
        AssertRange(ranges[1], 20, 20.5, 0);
        AssertRange(ranges[2], 20.5, 30, 1);
    }

    [Fact]
    public void Ranges_ValuesOutsideEveryBoundaryAreInNoBucket()
    {
        var engine = CreateEngine(("1", "-5"), ("2", "5"), ("3", "50"));

        var page = engine.SearchWithNumericFacets(
            "alpha",
            Page,
            numericFacets: [NumericFacetRequest.Ranges("price", [0, 10])]);

        var bucket = Assert.Single(page.NumericBuckets);

        // Choosing the boundaries is how a caller says what the facet covers: 5 counts, -5 and 50
        // are in no range, and neither absence nor a non-number explains them.
        AssertRange(Assert.Single(bucket.Ranges), 0, 10, 1);
        Assert.Equal(0, bucket.UnparseableCount);
    }

    [Fact]
    public void Histogram_DerivesEqualWidthBucketsFromTheMatchSet()
    {
        var engine = CreateMixedEngine();

        var page = engine.SearchWithNumericFacets(
            "alpha",
            Page,
            numericFacets: [NumericFacetRequest.Histogram("price", 3)]);

        var bucket = Assert.Single(page.NumericBuckets);

        // Parseable values: -3, 10, 10, 19.99, 20, 25.5. min = -3, max = 25.5, width = 9.5.
        Assert.Equal(3, bucket.Ranges.Count);
        AssertRange(bucket.Ranges[0], -3, 6.5, 1);
        AssertRange(bucket.Ranges[1], 6.5, 16, 2);
        AssertRange(bucket.Ranges[2], 16, 25.5, 3, upperInclusive: true);
        Assert.Equal(1, bucket.UnparseableCount);

        // The maximum is in the last bucket rather than outside it.
        Assert.Equal(6, bucket.Ranges.Sum(range => range.Count));
    }

    [Fact]
    public void Histogram_NonIntegerWidthSplitsAtTheComputedEdges()
    {
        var engine = CreateEngine(("1", "0"), ("2", "1"), ("3", "2"), ("4", "3"));

        var page = engine.SearchWithNumericFacets(
            "alpha",
            Page,
            numericFacets: [NumericFacetRequest.Histogram("price", 2)]);

        var ranges = Assert.Single(page.NumericBuckets).Ranges;

        AssertRange(ranges[0], 0, 1.5, 2);   // 0 and 1
        AssertRange(ranges[1], 1.5, 3, 2, upperInclusive: true); // 2 and 3
    }

    [Fact]
    public void Histogram_ValueExactlyOnAnInteriorEdge_JoinsTheBucketThatStartsThere()
    {
        var engine = CreateEngine(("1", "0"), ("2", "1"), ("3", "2"), ("4", "3"), ("5", "4"));

        var page = engine.SearchWithNumericFacets(
            "alpha",
            Page,
            numericFacets: [NumericFacetRequest.Histogram("price", 2)]);

        var ranges = Assert.Single(page.NumericBuckets).Ranges;

        // Edges are 0, 2, 4: the 2 sits on the interior edge and opens [2, 4], which is closed at
        // the top for the 4.
        AssertRange(ranges[0], 0, 2, 2);   // 0 and 1
        AssertRange(ranges[1], 2, 4, 3, upperInclusive: true); // 2, 3 and 4
    }

    [Fact]
    public void Histogram_SingleBucketHoldsEveryValueIncludingTheMaximum()
    {
        var engine = CreateEngine(("1", "0"), ("2", "10"));

        var page = engine.SearchWithNumericFacets(
            "alpha",
            Page,
            numericFacets: [NumericFacetRequest.Histogram("price", 1)]);

        var range = Assert.Single(Assert.Single(page.NumericBuckets).Ranges);

        // [0, 10) would drop the maximum; the last bucket of a histogram is closed at the top.
        AssertRange(range, 0, 10, 2, upperInclusive: true);
    }

    [Fact]
    public void Histogram_EveryValueIdentical_ReportsOnePointBucket()
    {
        var engine = CreateEngine(("1", "5"), ("2", "5"), ("3", "5"));

        var page = engine.SearchWithNumericFacets(
            "alpha",
            Page,
            numericFacets: [NumericFacetRequest.Histogram("price", 4)]);

        var bucket = Assert.Single(page.NumericBuckets);
        var range = Assert.Single(bucket.Ranges);

        // Zero width: a bucket at the point itself, not [5, 5) counting nothing and not a division
        // by zero.
        AssertRange(range, 5, 5, 3, upperInclusive: true);
    }

    [Fact]
    public void Histogram_BucketCountLargerThanDistinctValues_ReportsEmptyBuckets()
    {
        var engine = CreateEngine(("1", "1"), ("2", "2"), ("3", "3"));

        var page = engine.SearchWithNumericFacets(
            "alpha",
            Page,
            numericFacets: [NumericFacetRequest.Histogram("price", 10)]);

        var ranges = Assert.Single(page.NumericBuckets).Ranges;

        Assert.Equal(10, ranges.Count);
        Assert.Equal(3, ranges.Sum(range => range.Count));
        Assert.Contains(ranges, range => range.Count == 0);

        // The edges still span the match set, and each bucket ends where the next one starts.
        Assert.Equal(1, ranges[0].From);
        Assert.Equal(3, ranges[^1].To);

        for (int i = 0; i + 1 < ranges.Count; i++)
            Assert.Equal(ranges[i].To, ranges[i + 1].From);
    }

    [Fact]
    public void Histogram_NoMatchingDocuments_ReportsNoBucket()
    {
        var engine = CreateMixedEngine();

        var page = engine.SearchWithNumericFacets(
            "nothing-matches-this",
            Page,
            facetFields: ["price"],
            numericFacets: [NumericFacetRequest.Histogram("price", 5)]);

        Assert.Empty(page.Results);
        Assert.Empty(page.Buckets);
        Assert.Empty(page.NumericBuckets);
    }

    [Fact]
    public void Histogram_FieldCarriedButNeverNumeric_ReportsTheCountAndNoRanges()
    {
        var engine = CreateEngine(("1", "n/a"), ("2", "unknown"));

        var page = engine.SearchWithNumericFacets(
            "alpha",
            Page,
            numericFacets: [NumericFacetRequest.Histogram("price", 5)]);

        var bucket = Assert.Single(page.NumericBuckets);

        // The field is there and nobody has a number for it: reporting an invented range would be a
        // lie, and reporting nothing would hide the documents.
        Assert.Empty(bucket.Ranges);
        Assert.Equal(2, bucket.UnparseableCount);
    }

    [Fact]
    public void Ranges_FieldCarriedButNeverNumeric_StillReportsTheRequestedRanges()
    {
        var engine = CreateEngine(("1", "n/a"), ("2", "unknown"));

        var page = engine.SearchWithNumericFacets(
            "alpha",
            Page,
            numericFacets: [NumericFacetRequest.Ranges("price", [0, 10, 20])]);

        var bucket = Assert.Single(page.NumericBuckets);

        // The edges are the caller's, so they come back whether or not anything fell in them.
        Assert.Equal(2, bucket.Ranges.Count);
        Assert.All(bucket.Ranges, range => Assert.Equal(0, range.Count));
        Assert.Equal(2, bucket.UnparseableCount);
    }

    [Fact]
    public void NumericAndStringFacets_CoverTheSameMatchSet()
    {
        var engine = CreateEngine(("1", "1"), ("2", "2"), ("3", "2"), ("4", "3"), ("5", null));

        var page = engine.SearchWithNumericFacets(
            "alpha",
            Page,
            facetFields: ["price"],
            numericFacets: [NumericFacetRequest.Ranges("price", [0, 10])]);

        var stringTotal = Assert.Single(page.Buckets).Values.Sum(value => value.Count);
        var numericTotal = Assert.Single(page.NumericBuckets).Ranges.Sum(range => range.Count);

        // Doc 5 carries no price, so both kinds count 4 of the 5 matches; the ranges span the values.
        Assert.Equal(4, stringTotal);
        Assert.Equal(stringTotal, numericTotal);
    }

    [Fact]
    public void CountsApplyTheMetadataFiltersLikeTheStringFacets()
    {
        var engine = CreateMixedEngine();

        // LessThan 22 keeps -3, 10, 10, 19.99 and 20, and not 25.5 — nor "n/a", because the filter's
        // numeric parse fails and its ordinal fallback puts "n/a" after "22". Both facet kinds must
        // count that gated set and nothing outside it.
        var options = new SearchOptions(
            Limit: 10,
            Filters: [new MetadataFilter("price", MetadataFilterOperator.LessThan, "22")]);

        var page = engine.SearchWithNumericFacets(
            "alpha",
            options,
            facetFields: ["price"],
            numericFacets: [NumericFacetRequest.Ranges("price", [-100, 0, 40])]);

        Assert.Equal(5, page.Results.Count);
        Assert.Equal(5, Assert.Single(page.Buckets).Values.Sum(value => value.Count));

        var bucket = Assert.Single(page.NumericBuckets);
        Assert.Equal(1, bucket.Ranges[0].Count);   // -3
        Assert.Equal(4, bucket.Ranges[1].Count);   // 10, 10, 19.99, 20
        Assert.Equal(0, bucket.UnparseableCount);
    }

    [Fact]
    public void NonNumericValue_IsCountedApartAndStillVisibleToTheStringFacet()
    {
        var engine = CreateEngine(("1", "5"), ("2", "n/a"));

        var page = engine.SearchWithNumericFacets(
            "alpha",
            Page,
            facetFields: ["price"],
            numericFacets: [NumericFacetRequest.Ranges("price", [0, 100])]);

        var stringBucket = Assert.Single(page.Buckets);
        Assert.Equal(1, stringBucket.Values.Single(v => v.Value == "n/a").Count);

        var numericBucket = Assert.Single(page.NumericBuckets);
        AssertRange(Assert.Single(numericBucket.Ranges), 0, 100, 1);
        Assert.Equal(1, numericBucket.UnparseableCount);
    }

    [Fact]
    public void MissingField_IsNeitherBinnedNorCountedAsUnparseable()
    {
        var engine = CreateEngine(("1", "5"), ("2", null));

        var page = engine.SearchWithNumericFacets(
            "alpha",
            Page,
            numericFacets: [NumericFacetRequest.Histogram("price", 2)]);

        var bucket = Assert.Single(page.NumericBuckets);

        Assert.Equal(1, bucket.Ranges.Sum(range => range.Count));
        Assert.Equal(0, bucket.UnparseableCount);
    }

    [Fact]
    public void NaN_AndInfinities_AreUnparseable()
    {
        var engine = CreateEngine(("1", "NaN"), ("2", "Infinity"), ("3", "-Infinity"), ("4", "1"));

        var page = engine.SearchWithNumericFacets(
            "alpha",
            Page,
            numericFacets: [NumericFacetRequest.Ranges("price", [0, 2])]);

        var bucket = Assert.Single(page.NumericBuckets);

        AssertRange(Assert.Single(bucket.Ranges), 0, 2, 1);
        Assert.Equal(3, bucket.UnparseableCount);
    }

    [Fact]
    public void ThousandsSeparator_ParsesLikeAMetadataFilterCompares()
    {
        var engine = CreateEngine(("1", "1,234"));

        var page = engine.SearchWithNumericFacets(
            "alpha",
            Page,
            numericFacets: [NumericFacetRequest.Ranges("price", [1000, 2000])]);

        AssertRange(Assert.Single(Assert.Single(page.NumericBuckets).Ranges), 1000, 2000, 1);

        // The same value passes the filter's numeric comparison, so the two agree on what a number is.
        var document = new SearchDocument("1", "alpha", new Dictionary<string, string> { ["price"] = "1,234" });
        Assert.True(new MetadataFilter("price", MetadataFilterOperator.GreaterThan, "1000").Matches(document));
    }

    [Fact]
    public void CountsIgnoreOffsetAndLimit()
    {
        var engine = CreateMixedEngine();

        var page = engine.SearchWithNumericFacets(
            "alpha",
            new SearchOptions(Limit: 1, Offset: 2),
            numericFacets: [NumericFacetRequest.Ranges("price", [10, 20, 30])]);

        Assert.Single(page.Results);

        var bucket = Assert.Single(page.NumericBuckets);
        Assert.Equal(2, bucket.Ranges.Count);
        Assert.Equal(3, bucket.Ranges[0].Count);
        Assert.Equal(2, bucket.Ranges[1].Count);
    }

    [Fact]
    public void ResultsAreIdenticalToPlainSearch()
    {
        var engine = CreateMixedEngine();
        var options = new SearchOptions(Limit: 2, Offset: 1);

        var plain = engine.Search("alpha", options).Select(result => result.DocumentId).ToArray();
        var faceted = engine.SearchWithNumericFacets(
            "alpha",
            options,
            numericFacets: [NumericFacetRequest.Histogram("price", 3)]);

        Assert.Equal(plain, faceted.Results.Select(result => result.DocumentId).ToArray());
    }

    [Fact]
    public void StringFacetCall_LeavesNumericBucketsEmpty()
    {
        var engine = CreateMixedEngine();

        var page = engine.SearchWithFacets("alpha", Page, ["price"]);

        Assert.Single(page.Buckets);
        Assert.Empty(page.NumericBuckets);
    }

    [Fact]
    public void NumericFacetCallWithoutRequests_BehavesLikeTheStringFacetCall()
    {
        var engine = CreateMixedEngine();

        var page = engine.SearchWithNumericFacets("alpha", Page, facetFields: ["price"]);

        Assert.Equal(
            engine.SearchWithFacets("alpha", Page, ["price"]).Buckets.Single().Values.Select(v => (v.Value, v.Count)),
            page.Buckets.Single().Values.Select(v => (v.Value, v.Count)));
        Assert.Empty(page.NumericBuckets);
    }

    [Fact]
    public void NullEntriesInTheRequestAreDropped()
    {
        var engine = CreateMixedEngine();

        var page = engine.SearchWithNumericFacets(
            "alpha",
            Page,
            numericFacets: [null!, NumericFacetRequest.Histogram("price", 2)]);

        Assert.Single(page.NumericBuckets);
    }

    [Fact]
    public void SynchronizedEngine_ForwardsNumericFacets()
    {
        using var engine = new SynchronizedTextSearchEngine(CreateMixedEngine());
        IFacetedSearchEngine faceted = engine;

        var page = faceted.SearchWithNumericFacets(
            "alpha",
            Page,
            numericFacets: [NumericFacetRequest.Ranges("price", [10, 20, 30])]);

        Assert.Equal(8, page.Results.Count);
        Assert.Equal(3, Assert.Single(page.NumericBuckets).Ranges[0].Count);
    }

    [Fact]
    public void LexiSharpIndex_ReturnsTypedHitsAndNumericBuckets()
    {
        var index = new LexiSharpIndex<SampleDoc>(options =>
        {
            options.Id = document => document.Id;
            options.Text = document => document.Text;
            options.Fields = document => new Dictionary<string, string> { ["price"] = document.Price };
        });

        index.AddRange(
        [
            new SampleDoc("1", "alpha beta", "10"),
            new SampleDoc("2", "alpha beta", "20"),
            new SampleDoc("3", "alpha beta", "30"),
        ]);

        var page = index.SearchWithNumericFacets(
            "alpha",
            new LexiSharpQueryOptions(Limit: 2),
            numericFacets: [NumericFacetRequest.Ranges("price", [0, 20, 40])]);

        Assert.Equal(2, page.Results.Count);
        Assert.All(page.Results, hit => Assert.Equal("alpha beta", hit.Document.Text));

        var bucket = Assert.Single(page.NumericBuckets);
        Assert.Equal(1, bucket.Ranges[0].Count);   // 10; 20 sits on the upper boundary
        Assert.Equal(2, bucket.Ranges[1].Count);   // 20 and 30
    }

    /// <summary>An engine with the string facets and no numeric ones, so the default member is reached.</summary>
    private sealed class StringFacetsOnlyEngine : IFacetedSearchEngine
    {
        public void Index(IEnumerable<SearchDocument> documents)
        {
        }

        public void Add(SearchDocument document)
        {
        }

        public bool Remove(string documentId) => false;

        public void Clear()
        {
        }

        public IReadOnlyList<SearchResult> Search(
            string query,
            SearchOptions? options = null,
            CancellationToken cancellationToken = default) => [];

        public FacetedSearchResult SearchWithFacets(
            string query,
            SearchOptions? options = null,
            IReadOnlyList<string>? facetFields = null) =>
            new(Array.Empty<SearchResult>(), Array.Empty<FacetBucket>());
    }

    [Fact]
    public void AnEngineWithoutNumericFacets_ThrowsRatherThanReturningEmptyBuckets()
    {
        using var engine = new SynchronizedTextSearchEngine(new StringFacetsOnlyEngine());

        var error = Assert.Throws<NotSupportedException>(() => engine.SearchWithNumericFacets(
            "alpha",
            Page,
            numericFacets: [NumericFacetRequest.Histogram("price", 2)]));

        // An empty bucket list would read as "every value failed to parse", which is not what
        // "this engine cannot answer" means.
        Assert.Contains(nameof(StringFacetsOnlyEngine), error.Message);
    }

    private sealed record SampleDoc(string Id, string Text, string Price);

    [Fact]
    public void Ranges_RejectsFewerThanTwoBoundaries()
    {
        Assert.Throws<ArgumentException>(() => NumericFacetRequest.Ranges("price", []));
        Assert.Throws<ArgumentException>(() => NumericFacetRequest.Ranges("price", [10]));
    }

    [Fact]
    public void Ranges_RejectsNonAscendingBoundaries()
    {
        Assert.Throws<ArgumentException>(() => NumericFacetRequest.Ranges("price", [10, 10]));
        Assert.Throws<ArgumentException>(() => NumericFacetRequest.Ranges("price", [10, 5]));
    }

    [Fact]
    public void Ranges_RejectsNonFiniteBoundaries()
    {
        Assert.Throws<ArgumentException>(() => NumericFacetRequest.Ranges("price", [0, double.NaN]));
        Assert.Throws<ArgumentException>(() => NumericFacetRequest.Ranges("price", [0, double.PositiveInfinity]));
    }

    [Fact]
    public void Ranges_RejectsABlankField()
    {
        Assert.Throws<ArgumentException>(() => NumericFacetRequest.Ranges(" ", [0, 1]));
        Assert.Throws<ArgumentException>(() => NumericFacetRequest.Histogram("", 2));
    }

    [Fact]
    public void Histogram_RejectsANonPositiveBucketCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NumericFacetRequest.Histogram("price", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => NumericFacetRequest.Histogram("price", -1));
    }

    [Fact]
    public void Ranges_CopiesTheBoundaryArray()
    {
        var boundaries = new[] { 0d, 10d };
        var request = NumericFacetRequest.Ranges("price", boundaries);

        boundaries[0] = -100;

        Assert.Equal(0, request.Boundaries![0]);
        Assert.False(request.IsHistogram);
        Assert.True(NumericFacetRequest.Histogram("price", 2).IsHistogram);
    }
}
