using LexiSharp;
using LexiSharp.AspNetCore;
using LexiSharp.Core;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace LexiSharp.Tests;

public class AspNetCoreSearchEndpointTests
{
    private static LexiSharpIndex<SearchDocument> CreateIndex()
    {
        var index = new LexiSharpIndex<SearchDocument>();
        index.AddRange(new[]
        {
            new SearchDocument("d1", "distributed systems consensus replication"),
            new SearchDocument("d2", "web application security performance"),
            new SearchDocument("d3", "distributed storage fault tolerance"),
        });
        return index;
    }

    [Fact]
    public void Handle_ReturnsRankedHitsWithOriginalDocuments()
    {
        var ok = Assert.IsType<Ok<LexiSharpSearchResponse<SearchDocument>>>(
            LexiSharpSearchEndpoint.Handle(CreateIndex(), new LexiSharpSearchParameters { Query = "distributed" }));

        var response = ok.Value!;

        Assert.Equal("distributed", response.Query);
        Assert.Equal(2, response.Hits.Count);
        Assert.Equal(["d1", "d3"], response.Hits.Select(hit => hit.DocumentId));
        Assert.All(response.Hits, hit => Assert.Null(hit.HighlightedText));
        Assert.All(response.Hits, hit => Assert.NotNull(hit.Document));
    }

    [Fact]
    public void Handle_RejectsBlankQueries()
    {
        var badRequest = Assert.IsType<BadRequest<LexiSharpSearchError>>(
            LexiSharpSearchEndpoint.Handle(CreateIndex(), new LexiSharpSearchParameters()));

        var error = badRequest.Value!;
        Assert.Equal("q", error.Field);
        Assert.NotEmpty(error.Message);
    }

    [Fact]
    public void Handle_HonorsLimit()
    {
        var ok = Assert.IsType<Ok<LexiSharpSearchResponse<SearchDocument>>>(
            LexiSharpSearchEndpoint.Handle(
                CreateIndex(),
                new LexiSharpSearchParameters { Query = "distributed", Limit = 1 }));

        var hit = Assert.Single(ok.Value!.Hits);
        Assert.Equal("d1", hit.DocumentId);
    }

    [Fact]
    public void Handle_HonorsMinimumScore()
    {
        var ok = Assert.IsType<Ok<LexiSharpSearchResponse<SearchDocument>>>(
            LexiSharpSearchEndpoint.Handle(
                CreateIndex(),
                new LexiSharpSearchParameters { Query = "distributed", MinimumScore = 1_000_000 }));

        Assert.Empty(ok.Value!.Hits);
    }

    [Fact]
    public void Handle_HighlightsMatchedTermsWhenRequested()
    {
        var ok = Assert.IsType<Ok<LexiSharpSearchResponse<SearchDocument>>>(
            LexiSharpSearchEndpoint.Handle(
                CreateIndex(),
                new LexiSharpSearchParameters { Query = "distributed", Highlight = true }));

        Assert.All(ok.Value!.Hits, hit => Assert.Contains("<em>", hit.HighlightedText));
    }
}