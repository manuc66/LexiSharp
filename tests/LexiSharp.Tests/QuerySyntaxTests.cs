using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class QuerySyntaxTests
{
    [Theory]
    [InlineData("machine learning", QueryFeatures.None)]
    [InlineData("a*", QueryFeatures.None)] // base "a" is a dropped single char: plain text
    [InlineData("learn*", QueryFeatures.Expansions)]
    [InlineData("catt~2", QueryFeatures.Expansions)]
    [InlineData("\"machine learning\"", QueryFeatures.Phrases)]
    [InlineData("neural \"machine learning\"", QueryFeatures.Phrases)]
    [InlineData("\"machine learning\" learn*", QueryFeatures.Phrases | QueryFeatures.Expansions)]
    [InlineData("+machine", QueryFeatures.RequiredTerms)]
    [InlineData("-machine", QueryFeatures.ProhibitedTerms)]
    [InlineData("+machine -learning", QueryFeatures.RequiredTerms | QueryFeatures.ProhibitedTerms)]
    [InlineData("+\"machine learning\"", QueryFeatures.Phrases | QueryFeatures.RequiredTerms)]
    [InlineData("-\"machine learning\"", QueryFeatures.Phrases | QueryFeatures.ProhibitedTerms)]
    [InlineData("+mach*", QueryFeatures.Expansions | QueryFeatures.RequiredTerms)]
    [InlineData("-mach~2", QueryFeatures.Expansions | QueryFeatures.ProhibitedTerms)]
    // A hyphen inside a word, a bare sign and a sign separated from its term by whitespace are
    // not operators, exactly as QueryParser decides them.
    [InlineData("e-mail well-being foo-bar", QueryFeatures.None)]
    [InlineData("+ -", QueryFeatures.None)]
    [InlineData("+ machine", QueryFeatures.None)]
    public void Detect_ClassifiesQueryFeatures(string query, QueryFeatures expected) =>
        Assert.Equal(expected, QuerySyntax.Detect(query));

    [Fact]
    public void EnsureSupported_ThrowsOnUnsupportedFeature()
    {
        var exception = Assert.Throws<NotSupportedException>(() =>
            QuerySyntax.EnsureSupported("learn*", QueryFeatures.Phrases, "FakeEngine"));

        Assert.Contains("FakeEngine", exception.Message);
        Assert.Contains("prefix/fuzzy", exception.Message);
    }

    [Fact]
    public void EnsureSupported_NamesRequiredAndProhibitedTerms()
    {
        var required = Assert.Throws<NotSupportedException>(() =>
            QuerySyntax.EnsureSupported("+machine", QueryFeatures.None, "FakeEngine"));

        Assert.Contains("required (+) terms", required.Message);

        var prohibited = Assert.Throws<NotSupportedException>(() =>
            QuerySyntax.EnsureSupported("-machine", QueryFeatures.None, "FakeEngine"));

        Assert.Contains("prohibited (-) terms", prohibited.Message);
    }

    [Fact]
    public void EnsureSupported_AcceptsAnythingWhenFullySupported()
    {
        // Nothing is returned and nothing is recorded, so "did not throw" *is* the property under
        // test. Asserted rather than left implicit, because three bare calls read as a test that
        // cannot fail -- and would still pass if EnsureSupported had an empty body.
        static void Accepts(string query, QueryFeatures supported) =>
            Assert.Null(Record.Exception(() => QuerySyntax.EnsureSupported(query, supported, "FakeEngine")));

        Accepts("learn*", QueryFeatures.Phrases | QueryFeatures.Expansions);
        Accepts("plain words", QueryFeatures.None);
        Accepts("\"a phrase\"", QueryFeatures.Phrases);
        Accepts("+machine", QueryFeatures.RequiredTerms);
        Accepts("-machine", QueryFeatures.ProhibitedTerms);
    }

    [Fact]
    public void EnsureSupported_NullQuery_Throws() =>
        Assert.Throws<ArgumentNullException>(() => QuerySyntax.EnsureSupported(null!, QueryFeatures.None, "FakeEngine"));

    [Fact]
    public void Detect_NullQuery_Throws() =>
        Assert.Throws<ArgumentNullException>(() => QuerySyntax.Detect(null!));

    [Fact]
    public void RankedEngine_DeclaresPhrasesExpansionsAndOperators()
    {
        var engine = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());

        Assert.True(engine is IQuerySyntaxSupport);

        Assert.Equal(
            QueryFeatures.Phrases | QueryFeatures.Expansions
                | QueryFeatures.RequiredTerms | QueryFeatures.ProhibitedTerms,
            ((IQuerySyntaxSupport)engine).SupportedQueryFeatures);
    }

    [Fact]
    public void SqlBackends_DeclareOperatorsUnsupportedAndRefuseThem()
    {
        // No connection is opened: the schema is not created and the refusal happens before the
        // data source is used, which is the property under test — an engine that cannot honour an
        // operator must say so rather than forward it as text.
        using var lexical = new LexiSharp.Postgres.PostgresTextSearchEngine(
            "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=lexisharp",
            autoCreateSchema: false);
        using var parade = new LexiSharp.ParadeDB.ParadeDBTextSearchEngine(
            "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=lexisharp",
            autoCreateSchema: false);

        AssertUnsupported(lexical);
        AssertUnsupported(parade);

        static void AssertUnsupported(ITextSearchEngine engine)
        {
            var support = Assert.IsAssignableFrom<IQuerySyntaxSupport>(engine);

            Assert.Equal(QueryFeatures.Phrases, support.SupportedQueryFeatures);

            var exception = Assert.Throws<NotSupportedException>(
                () => engine.Search("+machine learning", new SearchOptions(Limit: 5)));

            Assert.Contains("required (+) terms", exception.Message);

            Assert.Throws<NotSupportedException>(
                () => engine.Search("machine -learning", new SearchOptions(Limit: 5)));

            // A hyphen inside a word is not an operator, so it reaches the engine as ordinary text
            // and the refusal above is the only thing this test proves about it.
            Assert.True(QuerySyntax.Detect("e-mail") == QueryFeatures.None);
        }
    }
}
