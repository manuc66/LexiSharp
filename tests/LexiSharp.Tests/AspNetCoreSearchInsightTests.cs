using System.Text.Json;
using LexiSharp;
using LexiSharp.AspNetCore;
using LexiSharp.Core;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The two agent-facing additions to the search endpoint: per-term corpus statistics
/// (<c>stats</c>) and facet counts over the whole match set (<c>facets</c>).
/// </summary>
/// <remarks>
/// Both are opt-in, and the tests pin the opt-in itself — a response to a request that asked for
/// neither must not grow properties, because that would change every existing client's payload to
/// hand it two nulls.
/// </remarks>
public class AspNetCoreSearchInsightTests
{
    private static LexiSharpIndex<SearchDocument> CreateIndex()
    {
        var index = new LexiSharpIndex<SearchDocument>();
        index.AddRange(new[]
        {
            new SearchDocument("d1", "distributed systems consensus", Fields("storage", "article")),
            new SearchDocument("d2", "distributed storage", Fields("storage", "article")),
            new SearchDocument("d3", "web security", Fields("web", "note")),
        });
        return index;
    }

    private static IReadOnlyDictionary<string, string> Fields(string topic, string kind) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["topic"] = topic,
            ["kind"] = kind,
        };

    private static LexiSharpSearchResponse<SearchDocument> Search(LexiSharpSearchParameters parameters) =>
        Assert.IsType<Ok<LexiSharpSearchResponse<SearchDocument>>>(
            LexiSharpSearchEndpoint.Handle(CreateIndex(), parameters)).Value!;

    [Fact]
    public void Handle_OmitsTermsAndFacetsWhenNeitherWasRequested()
    {
        var response = Search(new LexiSharpSearchParameters { Query = "distributed" });

        Assert.Null(response.Terms);
        Assert.Null(response.Facets);

        // Omitted, not null: the payload a caller that asked for neither receives is the one this
        // endpoint returned before the options existed.
        string json = JsonSerializer.Serialize(response, JsonSerializerOptions.Web);
        Assert.DoesNotContain("\"terms\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"facets\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Handle_ReturnsSearchResultsUnderTheLowerCamelNames()
    {
        var response = Search(new LexiSharpSearchParameters { Query = "distributed", Stats = true });

        string json = JsonSerializer.Serialize(response, JsonSerializerOptions.Web);
        Assert.Contains("\"terms\":", json, StringComparison.Ordinal);
        Assert.Contains("\"documentFrequency\":", json, StringComparison.Ordinal);
        Assert.Contains("\"documentShare\":", json, StringComparison.Ordinal);
        Assert.Contains("\"inverseDocumentFrequency\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Handle_ReportsCorpusStatisticsForEachDistinctQueryTerm()
    {
        var response = Search(new LexiSharpSearchParameters { Query = "distributed security", Stats = true });

        Assert.NotNull(response.Terms);
        Assert.Equal(["distributed", "security"], response.Terms.Select(term => term.Term));
        Assert.Equal([2, 1], response.Terms.Select(term => term.DocumentFrequency));
        Assert.All(response.Terms, term => Assert.Equal(3, term.DocumentCount));
        Assert.All(response.Terms, term => Assert.NotNull(term.InverseDocumentFrequency));

        // A query asking for both halves of the corpus still returns its page: the statistics are an
        // observation, not a filter.
        Assert.Equal(3, response.Hits.Count);
    }

    [Fact]
    public void Handle_ReportsAnAbsentTermRatherThanDroppingIt()
    {
        // The case an agent needs most: the query matched nothing because one of its words is not in
        // the corpus at all, which no search result can say.
        var response = Search(new LexiSharpSearchParameters { Query = "distributed quasar", Stats = true });

        Assert.NotNull(response.Terms);
        var quasar = response.Terms.Single(term => term.Term == "quasar");
        Assert.True(quasar.IsAbsent);
        Assert.Equal(0, quasar.DocumentFrequency);
        Assert.Equal(0, quasar.DocumentShare);
        Assert.Equal(2, response.Hits.Count);
    }

    [Fact]
    public void Handle_CountsTheRequestedFacetOverTheWholeMatchSet()
    {
        var response = Search(new LexiSharpSearchParameters { Query = "distributed", Facets = "topic" });

        var bucket = Assert.Single(response.Facets!);
        Assert.Equal("topic", bucket.Field);
        Assert.Equal(
            [("storage", 2)],
            bucket.Values.Select(value => (value.Value, value.Count)));

        // Counts describe the match set; the page is still the page.
        Assert.Equal(2, response.Hits.Count);
    }

    [Fact]
    public void Handle_ReturnsTheSamePageWithAndWithoutFacets()
    {
        var plain = Search(new LexiSharpSearchParameters { Query = "distributed" });
        var faceted = Search(new LexiSharpSearchParameters { Query = "distributed", Facets = "topic,kind" });

        Assert.Equal(
            plain.Hits.Select(hit => (hit.DocumentId, hit.Score)),
            faceted.Hits.Select(hit => (hit.DocumentId, hit.Score)));
    }

    [Fact]
    public void Handle_ReturnsOneBucketPerDistinctRequestedField()
    {
        var response = Search(new LexiSharpSearchParameters { Query = "distributed", Facets = "topic,kind" });

        Assert.Equal(["topic", "kind"], response.Facets!.Select(bucket => bucket.Field));
    }

    [Fact]
    public void Handle_IgnoresBlankAndRepeatedFacetNames()
    {
        // A client that builds the list by joining a set can emit either; neither changes the answer.
        var response = Search(new LexiSharpSearchParameters { Query = "distributed", Facets = " topic ,,topic, " });

        Assert.Single(response.Facets!);
    }

    [Fact]
    public void Handle_AnswersAnEmptyFacetListWhenTheParameterNamesNoField()
    {
        // Present but unusable is still a request: the property is there, holding nothing, so a
        // client can tell "nobody matched a value" from "I never asked".
        var response = Search(new LexiSharpSearchParameters { Query = "distributed", Facets = " , " });

        Assert.NotNull(response.Facets);
        Assert.Empty(response.Facets!);
    }

    [Fact]
    public void Handle_ReturnsNoBucketForAFieldNoDocumentCarries()
    {
        var response = Search(new LexiSharpSearchParameters { Query = "distributed", Facets = "nope" });

        // An unknown field yields no bucket rather than an error, so a client can send a fixed field
        // set on every request.
        Assert.Empty(response.Facets!);
    }

    [Fact]
    public void Handle_RefusesMoreFacetFieldsThanTheBound()
    {
        string facets = string.Join(',', Enumerable.Range(0, LexiSharpSearchEndpoint.MaxFacetFields + 1)
            .Select(i => $"f{i}"));

        var badRequest = Assert.IsType<BadRequest<LexiSharpSearchError>>(
            LexiSharpSearchEndpoint.Handle(
                CreateIndex(),
                new LexiSharpSearchParameters { Query = "distributed", Facets = facets }));

        Assert.Equal("facets", badRequest.Value!.Field);
        Assert.Contains(LexiSharpSearchEndpoint.MaxFacetFields.ToString(), badRequest.Value.Message);

        // The message must not claim a count it did not compute: the parser stops one past the bound.
        Assert.DoesNotContain("9 were requested", badRequest.Value.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Handle_AcceptsExactlyTheFacetBound()
    {
        string facets = string.Join(',', Enumerable.Range(0, LexiSharpSearchEndpoint.MaxFacetFields)
            .Select(i => $"f{i}"));

        var ok = Assert.IsType<Ok<LexiSharpSearchResponse<SearchDocument>>>(
            LexiSharpSearchEndpoint.Handle(
                CreateIndex(),
                new LexiSharpSearchParameters { Query = "distributed", Facets = facets }));

        // Accepted, and the buckets are empty because no document carries those fields — the bound is
        // on the request, not on the corpus.
        Assert.NotNull(ok.Value!.Facets);
    }

    [Fact]
    public void Handle_StatsAndFacetsAreIndependent()
    {
        var response = Search(new LexiSharpSearchParameters { Query = "distributed", Stats = true, Facets = "topic" });

        Assert.NotNull(response.Terms);
        Assert.NotNull(response.Facets);
    }

    [Fact]
    public void Handle_StatsDoesNotChangeThePage()
    {
        var plain = Search(new LexiSharpSearchParameters { Query = "distributed security" });
        var described = Search(new LexiSharpSearchParameters { Query = "distributed security", Stats = true });

        Assert.Equal(
            plain.Hits.Select(hit => (hit.DocumentId, hit.Score)),
            described.Hits.Select(hit => (hit.DocumentId, hit.Score)));
    }
}
