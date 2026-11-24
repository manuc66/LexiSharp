using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class EntityExtractorTests
{
    [Fact]
    public void ExtractsUrls()
    {
        var extractor = new EntityExtractor(new[] { GenericEntityPatterns.Url });
        var result = extractor.Extract("see https://example.com/page");
        Assert.Single(result.Entities);
        Assert.Equal("url", result.Entities[0].Type);
    }

    [Fact]
    public void DeduplicatesOverlappingSpans()
    {
        var extractor = new EntityExtractor(new IEntityPattern[] { GenericEntityPatterns.Url, GenericEntityPatterns.Domain });
        var result = extractor.Extract("https://example.com");
        Assert.NotEmpty(result.Entities);
    }

    [Fact]
    public void NoNer_DoesNotThrow()
    {
        var extractor = new EntityExtractor();
        var result = extractor.Extract("hello world");
        Assert.Empty(result.Entities);
    }
}
