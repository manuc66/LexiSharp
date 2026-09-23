using LexiSharp;
using LexiSharp.AspNetCore;
using LexiSharp.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LexiSharp.Tests;

public class AspNetCoreSearchEndpointIntegrationTests
{
    private static WebApplication BuildApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();

        var index = new LexiSharpIndex<SearchDocument>();
        index.AddRange(new[]
        {
            new SearchDocument("d1", "distributed systems consensus replication"),
            new SearchDocument("d2", "web application security performance"),
            new SearchDocument("d3", "distributed storage fault tolerance"),
        });
        builder.Services.AddSingleton(index);

        var app = builder.Build();
        app.MapLexiSharpSearch<SearchDocument>();

        return app;
    }

    [Fact]
    public async Task Endpoint_RoutesThroughBindingAndSerialization()
    {
        var app = BuildApp();
        await app.StartAsync();

        try
        {
            var client = app.GetTestClient();

            var response = await client.GetAsync("/search?q=distributed&highlight=true");
            var rawBody = await response.Content.ReadAsStringAsync();

            Assert.True(response.StatusCode == System.Net.HttpStatusCode.OK, $"unexpected status {response.StatusCode}: {rawBody}");
            Assert.Contains("\"query\":\"distributed\"", rawBody);
            Assert.Contains("\"documentId\":\"d1\"", rawBody);
            Assert.Contains("\"documentId\":\"d3\"", rawBody);
            Assert.True(rawBody.Contains("\"highlightedText\":\"<em>distributed</em>"), rawBody);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Endpoint_HonorsLimitViaQueryString()
    {
        var app = BuildApp();
        await app.StartAsync();

        try
        {
            var client = app.GetTestClient();

            var response = await client.GetAsync("/search?q=distributed&limit=1");

            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("\"documentId\":\"d1\"", body);
            Assert.DoesNotContain("\"documentId\":\"d3\"", body);

            var bad = await client.GetAsync("/search?q=");
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, bad.StatusCode);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }
}