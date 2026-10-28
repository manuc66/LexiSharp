using LexiSharp.Core;
using LexiSharp.Demo;
using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The demo page inserts a hit's <c>Highlighted</c> text as HTML — it is the one field
/// <c>index.html</c> does not pass through <c>esc()</c>, because the highlighter's tags have to
/// survive the trip. A corpus read from disk is data the page did not write, so the encoding has to
/// happen before the markup is built, and this is the test that says where.
/// </summary>
public sealed class DemoHighlightEncodingTests
{
    private static DemoCorpusSet CorpusWith(string text) =>
        new(
            "encoded",
            "Encoded corpus",
            new[] { new SearchDocument("h1", text) },
            new[] { "quick" });

    [Fact]
    public void ACorpusDocumentCarryingHtmlReachesThePageAsText()
    {
        const string hostile = "<img src=x onerror=alert(1)> the quick brown fox";

        var service = new DemoSearchService(
            CorpusWith(hostile), WordSegmentation.UnicodeWordBoundaries);

        var comparison = service.Compare("quick", limit: 5);

        Assert.NotEmpty(comparison.Lanes);

        foreach (var lane in comparison.Lanes)
        {
            foreach (var hit in lane.Hits)
            {
                // Both the highlighted form and the plain snippet travel in the same field
                // position, so both have to be safe.
                Assert.DoesNotContain("<img", hit.Highlighted);
                Assert.Contains("&lt;img", hit.Highlighted);
                Assert.DoesNotContain("<img", hit.Snippet);
            }
        }
    }

    [Fact]
    public void TheMatchIsStillMarkedAfterEncoding()
    {
        var service = new DemoSearchService(
            CorpusWith("an <em>already</em> marked quick brown fox"),
            WordSegmentation.UnicodeWordBoundaries);

        var hit = service.Compare("quick", limit: 5).Lanes
            .SelectMany(lane => lane.Hits)
            .First();

        // The document's own markup is text now; the highlighter's is still markup.
        Assert.Contains("&lt;em&gt;already&lt;/em&gt;", hit.Highlighted);
        Assert.Contains("<em>quick</em>", hit.Highlighted);
    }
}
