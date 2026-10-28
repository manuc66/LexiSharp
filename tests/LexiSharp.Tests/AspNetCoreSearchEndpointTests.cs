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

    [Theory]
    [InlineData(LexiSharpSearchEndpoint.MaxLimit + 1)]
    [InlineData(int.MaxValue)]
    public void Handle_RefusesALimitAboveTheBound(int limit)
    {
        // The page size is the working window the ranking is cut from, so an unbounded Limit is a
        // request to build the whole corpus' window — which is quadratic in the corpus, not in the
        // page the caller receives. Refused rather than truncated: a truncated page is a different
        // answer, and the caller can ask for 100.
        var badRequest = Assert.IsType<BadRequest<LexiSharpSearchError>>(
            LexiSharpSearchEndpoint.Handle(
                CreateIndex(),
                new LexiSharpSearchParameters { Query = "distributed", Limit = limit }));

        Assert.Equal("limit", badRequest.Value!.Field);
        Assert.Contains(LexiSharpSearchEndpoint.MaxLimit.ToString(), badRequest.Value.Message);
    }

    [Theory]
    [InlineData(LexiSharpSearchEndpoint.MaxOffset + 1)]
    [InlineData(int.MaxValue)]
    public void Handle_RefusesAnOffsetAboveTheBound(int offset)
    {
        // Offset enlarges the window the same way Limit does, so the bound is the same one:
        // [Offset, Offset + Limit) is only cheap while the window it names is bounded.
        var badRequest = Assert.IsType<BadRequest<LexiSharpSearchError>>(
            LexiSharpSearchEndpoint.Handle(
                CreateIndex(),
                new LexiSharpSearchParameters { Query = "distributed", Offset = offset }));

        Assert.Equal("offset", badRequest.Value!.Field);
        Assert.Contains(LexiSharpSearchEndpoint.MaxOffset.ToString(), badRequest.Value.Message);
    }

    [Fact]
    public void Handle_AcceptsTheBoundsThemselves()
    {
        var ok = Assert.IsType<Ok<LexiSharpSearchResponse<SearchDocument>>>(
            LexiSharpSearchEndpoint.Handle(
                CreateIndex(),
                new LexiSharpSearchParameters
                {
                    Query = "distributed",
                    Limit = LexiSharpSearchEndpoint.MaxLimit,
                    Offset = LexiSharpSearchEndpoint.MaxOffset,
                }));

        Assert.Empty(ok.Value!.Hits);
    }

    [Fact]
    public void Handle_RefusesAQueryTheEngineWillNotRun_AsABadRequest()
    {
        // A query that asks for more expansion atoms than the engine resolves is bad input, not a
        // server fault: the same 400 the blank query gets, with the reason in the payload.
        string atoms = string.Join(' ', Enumerable.Range(0, LexiSharp.Ranking.RankedTextSearchEngine.MaxExpansionAtoms + 1)
            .Select(i => $"atom{i}~"));

        var badRequest = Assert.IsType<BadRequest<LexiSharpSearchError>>(
            LexiSharpSearchEndpoint.Handle(CreateIndex(), new LexiSharpSearchParameters { Query = atoms }));

        Assert.Equal("q", badRequest.Value!.Field);
        Assert.Contains(
            LexiSharp.Ranking.RankedTextSearchEngine.MaxExpansionAtoms.ToString(),
            badRequest.Value.Message);
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