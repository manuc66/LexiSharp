using LexiSharp.Core;
using LexiSharp.Embeddings;
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
    public void Detect_FieldScopesOnlyWhenTheNameIsAKnownField()
    {
        var fields = new HashSet<string>(["title"], StringComparer.Ordinal);

        Assert.Equal(QueryFeatures.FieldScopes, QuerySyntax.Detect("title:neural", fields));
        Assert.Equal(
            QueryFeatures.FieldScopes | QueryFeatures.RequiredTerms,
            QuerySyntax.Detect("+title:neural", fields));
        Assert.Equal(QueryFeatures.None, QuerySyntax.Detect("title:neural"));

        // A known field name in a different position is still ordinary text.
        Assert.Equal(QueryFeatures.None, QuerySyntax.Detect("http://x", fields));
    }

    [Fact]
    public void EnsureSupported_NamesFieldScopes()
    {
        var fields = new HashSet<string>(["title"], StringComparer.Ordinal);

        var exception = Assert.Throws<NotSupportedException>(() =>
            QuerySyntax.EnsureSupported("title:neural", QueryFeatures.Phrases, "FakeEngine", fields));

        Assert.Contains("field-scoped", exception.Message);
    }

    [Fact]
    public void RankedEngine_DeclaresFieldScopesOnlyWithAFieldScopedScorer()
    {
        var index = new InMemoryTextIndex();
        index.Index(
        [
            new SearchDocument("1", "body", TextFields: new Dictionary<string, string> { ["title"] = "heading" }),
        ]);

        var fieldAware = new RankedTextSearchEngine(index, new Bm25FScorer());
        var fieldBlind = new RankedTextSearchEngine(index, new Bm25Scorer());

        Assert.Equal(
            QueryFeatures.Phrases | QueryFeatures.Expansions
                | QueryFeatures.RequiredTerms | QueryFeatures.ProhibitedTerms | QueryFeatures.FieldScopes,
            ((IQuerySyntaxSupport)fieldAware).SupportedQueryFeatures);

        Assert.Equal(
            QueryFeatures.Phrases | QueryFeatures.Expansions
                | QueryFeatures.RequiredTerms | QueryFeatures.ProhibitedTerms,
            ((IQuerySyntaxSupport)fieldBlind).SupportedQueryFeatures);

        // The declaration is the truth, not a label: the field-blind engine refuses the query.
        Assert.Throws<NotSupportedException>(() => fieldBlind.Search("title:heading"));
    }

    [Fact]
    public void SqlBackends_DeclareFieldScopesUnsupportedAndRefuseThem()
    {
        // No connection is opened: the refusal happens before the data source is used. The caller
        // has to name the field vocabulary, because a prefix that no one declared is ordinary
        // text by design (the `key:value` rule) and a backend with no fields cannot guess.
        using var lexical = new LexiSharp.Postgres.PostgresTextSearchEngine(
            "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=lexisharp",
            autoCreateSchema: false);
        using var parade = new LexiSharp.ParadeDB.ParadeDBTextSearchEngine(
            "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=lexisharp",
            autoCreateSchema: false);

        var options = new SearchOptions(Limit: 5)
        {
            KnownFields = new HashSet<string>(["title"], StringComparer.Ordinal),
        };

        AssertRefused(lexical, options);
        AssertRefused(parade, options);

        static void AssertRefused(ITextSearchEngine engine, SearchOptions options)
        {
            var support = Assert.IsAssignableFrom<IQuerySyntaxSupport>(engine);

            Assert.Equal(QueryFeatures.Phrases, support.SupportedQueryFeatures);

            var exception = Assert.Throws<NotSupportedException>(
                () => engine.Search("title:machine", options));

            Assert.Contains("field-scoped", exception.Message, StringComparison.Ordinal);
        }
    }

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
    public void LiteralQuerySyntax_DoesNotConsultTheSyntaxCheck()
    {
        var engine = new InMemoryVectorSearchEngine(new HashingEmbeddingProvider(32));
        engine.Add(new SearchDocument("d1", "neural networks"));

        // The engine declares no query syntax, so the operator is refused.
        Assert.Throws<NotSupportedException>(
            () => engine.Search("+neural", new SearchOptions(Limit: 5)));

        // ParseQuerySyntax: false is the caller saying the parser is not consulted at all — so the
        // same operator text is an ordinary query string to embed, and the syntax check must not
        // turn it into a refusal.
        Assert.Null(Record.Exception(
            () => engine.Search("+neural", new SearchOptions(Limit: 5, ParseQuerySyntax: false))));
        Assert.Null(Record.Exception(
            () => engine.Search("-neural", new SearchOptions(Limit: 5, ParseQuerySyntax: false))));
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
