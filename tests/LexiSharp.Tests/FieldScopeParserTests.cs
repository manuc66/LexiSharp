using LexiSharp.Core;
using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The field-scope grammar as <see cref="QueryParser"/> reads it: which prefixes are fields, what
/// a scoped atom becomes, and the colon cases that must stay ordinary text.
/// </summary>
public class FieldScopeParserTests
{
    private static readonly HashSet<string> Fields =
        new(["title", "body"], StringComparer.Ordinal);

    private static ParsedQuery Parse(string query) =>
        QueryParser.Parse(query, Tokenizer.Default, Fields);

    [Fact]
    public void KnownField_ScopesTheTermAndLeavesTheUnscopedListEmpty()
    {
        var parsed = Parse("title:neural");

        Assert.Empty(parsed.FreeTerms);
        Assert.Empty(parsed.AllTerms); // a scoped term has a field attached; it is not unscoped input
        Assert.Equal([new FieldScopedTerm("title", "neural")], parsed.ScopedTerms);
        Assert.True(parsed.HasFieldScopes);
    }

    [Fact]
    public void UnknownField_LeavesTheColonToTheTokenizer()
    {
        var parsed = QueryParser.Parse("title:neural", Tokenizer.Default, new HashSet<string>(["body"]));

        Assert.Empty(parsed.ScopedTerms);
        Assert.False(parsed.HasFieldScopes);
        Assert.Equal(Tokenizer.Default.Tokenize("title:neural"), parsed.FreeTerms);
    }

    [Theory]
    [InlineData("http://example.com")]
    [InlineData("12:30")]
    [InlineData("key:value")]
    [InlineData("ratio 3:1 and time 12:30")]
    public void ColonsThatDoNotNameAKnownField_AreOrdinaryText(string query)
    {
        var parsed = Parse(query);

        Assert.Empty(parsed.ScopedTerms);
        Assert.False(parsed.HasFieldScopes);
        Assert.Equal(Tokenizer.Default.Tokenize(query), parsed.FreeTerms);
        Assert.True(QueryParser.IsPlain(query, Fields));
    }

    [Theory]
    // The prefix must begin the token, immediately precede the atom, and name a field.
    [InlineData("xtitle:neural")]
    [InlineData("foo,title:neural")]
    [InlineData("title:")]
    [InlineData("title: neural")]
    [InlineData("title:-neural")]
    [InlineData("\"title:neural\"")]
    public void FormsOutsideTheGrammar_AreNotScopes(string query)
    {
        Assert.False(Parse(query).HasFieldScopes);
    }

    [Fact]
    public void FieldNamesAreOrdinal()
    {
        Assert.False(QueryParser.Parse("Title:neural", Tokenizer.Default, Fields).HasFieldScopes);

        var caseSensitive = new HashSet<string>(["Title"], StringComparer.Ordinal);
        Assert.Single(QueryParser.Parse("Title:neural", Tokenizer.Default, caseSensitive).ScopedTerms);
    }

    [Fact]
    public void RequiredScopedTerm_GatesAndScores()
    {
        var parsed = Parse("+title:neural network");

        Assert.Equal([new FieldScopedTerm("title", "neural")], parsed.RequiredScopedTerms);
        Assert.Equal([new FieldScopedTerm("title", "neural")], parsed.ScopedTerms);
        Assert.Empty(parsed.RequiredTerms);
        Assert.Equal(["network"], parsed.AllTerms);
        Assert.True(parsed.HasRequired);
    }

    [Fact]
    public void ProhibitedScopedTerm_GatesAndNeverScores()
    {
        var parsed = Parse("neural -title:spam");

        Assert.Equal([new FieldScopedTerm("title", "spam")], parsed.ProhibitedScopedTerms);
        Assert.Empty(parsed.ScopedTerms);
        Assert.Equal(["neural"], parsed.AllTerms);
        Assert.True(parsed.HasProhibited);
    }

    [Fact]
    public void ScopedPhrase_RecordsTheFieldAndScoresItsTerms()
    {
        var parsed = Parse("title:\"machine learning\"");

        Assert.Empty(parsed.Phrases);
        var phrase = Assert.Single(parsed.ScopedPhrases);
        Assert.Equal("title", phrase.Field);
        Assert.Equal(["machine", "learning"], phrase.Terms);
        Assert.Equal(QueryTermOperator.None, phrase.Operator);
        Assert.Equal(
            [new FieldScopedTerm("title", "machine"), new FieldScopedTerm("title", "learning")],
            parsed.ScopedTerms);
        Assert.True(parsed.HasPhrases);
        Assert.Empty(parsed.AllTerms);
    }

    [Fact]
    public void QualifiedScopedPhrases_AreSeparatedByOperator()
    {
        var parsed = Parse("+title:\"machine learning\" -body:\"spam mail\"");

        Assert.Equal(QueryTermOperator.Required, Assert.Single(parsed.RequiredScopedPhrases).Operator);
        Assert.Equal(QueryTermOperator.Prohibited, Assert.Single(parsed.ProhibitedScopedPhrases).Operator);
        Assert.Empty(parsed.ScopedPhrases);

        // Only the required phrase scores.
        Assert.Equal(
            [new FieldScopedTerm("title", "machine"), new FieldScopedTerm("title", "learning")],
            parsed.ScopedTerms);
    }

    [Fact]
    public void ScopedExpansion_CarriesItsFieldAndOperator()
    {
        var required = Parse("+title:neural*");

        var expansion = Assert.Single(required.Expansions);
        Assert.Equal("title", expansion.Field);
        Assert.Equal(QueryTermOperator.Required, expansion.Operator);
        Assert.Equal(QueryExpansionKind.Prefix, expansion.Kind);
        Assert.Empty(required.AllTerms);
        Assert.True(required.HasFieldScopes);

        var prohibited = Parse("-body:catt~2");
        Assert.Equal("body", Assert.Single(prohibited.Expansions).Field);
        Assert.Equal(QueryTermOperator.Prohibited, Assert.Single(prohibited.Expansions).Operator);
    }

    [Fact]
    public void ScopedAndUnscopedAtomsKeepTheirSeparateLists()
    {
        var parsed = Parse("free title:scoped +body:gated -title:out");

        Assert.Equal(["free"], parsed.AllTerms);
        Assert.Equal(
            [new FieldScopedTerm("title", "scoped"), new FieldScopedTerm("body", "gated")],
            parsed.ScopedTerms);
        Assert.Equal([new FieldScopedTerm("body", "gated")], parsed.RequiredScopedTerms);
        Assert.Equal([new FieldScopedTerm("title", "out")], parsed.ProhibitedScopedTerms);
    }

    [Fact]
    public void IsPlain_SeesAScopeOnlyWhenTheNameIsKnown()
    {
        Assert.False(QueryParser.IsPlain("title:neural", Fields));
        Assert.True(QueryParser.IsPlain("title:neural")); // no field vocabulary: ordinary text
        Assert.True(QueryParser.IsPlain("title:", Fields));
        Assert.True(QueryParser.IsPlain("http://example.com", Fields));
    }

    [Fact]
    public void NoFieldVocabulary_KeepsTheParserByteForBytePlain()
    {
        const string query = "title:neural http://x 12:30";

        var parsed = QueryParser.Parse(query, Tokenizer.Default);

        Assert.Equal(Tokenizer.Default.Tokenize(query), parsed.FreeTerms);
        Assert.Equal(Tokenizer.Default.Tokenize(query), parsed.AllTerms);
        Assert.Empty(parsed.ScopedTerms);
        Assert.False(parsed.HasFieldScopes);
    }

    [Fact]
    public void AnUnterminatedScopedQuoteExtendsTheScopedPhrase()
    {
        var parsed = Parse("title:\"machine learning");

        Assert.Empty(parsed.Phrases);
        Assert.Equal(["machine", "learning"], Assert.Single(parsed.ScopedPhrases).Terms);
    }
}
