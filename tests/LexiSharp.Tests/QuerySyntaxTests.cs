using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class QuerySyntaxTests
{
    [Theory]
    [InlineData("machine learning", QueryFeature.None)]
    [InlineData("a*", QueryFeature.None)] // base "a" is a dropped single char: plain text
    [InlineData("learn*", QueryFeature.Expansions)]
    [InlineData("catt~2", QueryFeature.Expansions)]
    [InlineData("\"machine learning\"", QueryFeature.Phrases)]
    [InlineData("neural \"machine learning\"", QueryFeature.Phrases)]
    [InlineData("\"machine learning\" learn*", QueryFeature.Phrases | QueryFeature.Expansions)]
    public void Detect_ClassifiesQueryFeatures(string query, QueryFeature expected) =>
        Assert.Equal(expected, QuerySyntax.Detect(query));

    [Fact]
    public void EnsureSupported_ThrowsOnUnsupportedFeature()
    {
        var exception = Assert.Throws<NotSupportedException>(() =>
            QuerySyntax.EnsureSupported("learn*", QueryFeature.Phrases, "FakeEngine"));

        Assert.Contains("FakeEngine", exception.Message);
        Assert.Contains("prefix/fuzzy", exception.Message);
    }

    [Fact]
    public void EnsureSupported_AcceptsAnythingWhenFullySupported()
    {
        QuerySyntax.EnsureSupported("learn*", QueryFeature.Phrases | QueryFeature.Expansions, "FakeEngine");
        QuerySyntax.EnsureSupported("plain words", QueryFeature.None, "FakeEngine");
        QuerySyntax.EnsureSupported("\"a phrase\"", QueryFeature.Phrases, "FakeEngine");
    }

    [Fact]
    public void EnsureSupported_NullQuery_Throws() =>
        Assert.Throws<ArgumentNullException>(() => QuerySyntax.EnsureSupported(null!, QueryFeature.None, "FakeEngine"));

    [Fact]
    public void Detect_NullQuery_Throws() =>
        Assert.Throws<ArgumentNullException>(() => QuerySyntax.Detect(null!));

    [Fact]
    public void RankedEngine_DeclaresPhrasesAndExpansions()
    {
        var engine = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());

        Assert.True(engine is IQuerySyntaxSupport);

        Assert.Equal(
            QueryFeature.Phrases | QueryFeature.Expansions,
            ((IQuerySyntaxSupport)engine).SupportedQueryFeatures);
    }
}
