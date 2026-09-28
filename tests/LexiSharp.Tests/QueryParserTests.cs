using LexiSharp.Core;
using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

public class QueryParserTests
{
    [Fact]
    public void Parse_WithoutQuotes_MatchesPlainTokenization()
    {
        const string query = "Neural, network!";

        var parsed = QueryParser.Parse(query, Tokenizer.Default);

        Assert.Equal(Tokenizer.Default.Tokenize(query), parsed.FreeTerms);
        Assert.Equal(Tokenizer.Default.Tokenize(query), parsed.AllTerms);
        Assert.Empty(parsed.Phrases);
        Assert.False(parsed.HasPhrases);
    }

    [Fact]
    public void Parse_SimplePhrase_SeparatesPhraseFromFreeTerms()
    {
        var parsed = QueryParser.Parse("neural \"machine learning\"", Tokenizer.Default);

        Assert.Equal(new[] { "neural" }, parsed.FreeTerms);
        var phrase = Assert.Single(parsed.Phrases);
        Assert.Equal(new[] { "machine", "learning" }, phrase);
        Assert.Equal(new[] { "neural", "machine", "learning" }, parsed.AllTerms);
        Assert.True(parsed.HasPhrases);
    }

    [Fact]
    public void Parse_PhraseOnlyQuery_AllTermsAreThePhraseTerms()
    {
        var parsed = QueryParser.Parse("\"machine learning\"", Tokenizer.Default);

        Assert.Empty(parsed.FreeTerms);
        var phrase = Assert.Single(parsed.Phrases);
        Assert.Equal(new[] { "machine", "learning" }, phrase);
        Assert.Equal(new[] { "machine", "learning" }, parsed.AllTerms);
    }

    [Fact]
    public void Parse_MultiplePhrases_AreAllCaptured()
    {
        var parsed = QueryParser.Parse("\"machine learning\" vs \"deep learning\"", Tokenizer.Default);

        Assert.Equal(new[] { "vs" }, parsed.FreeTerms);
        Assert.Equal(2, parsed.Phrases.Count);
        Assert.Equal(new[] { "machine", "learning" }, parsed.Phrases[0]);
        Assert.Equal(new[] { "deep", "learning" }, parsed.Phrases[1]);
    }

    [Fact]
    public void Parse_UnterminatedQuote_ExtendsPhraseToTheEnd()
    {
        var parsed = QueryParser.Parse("find \"machine learning", Tokenizer.Default);

        Assert.Equal(new[] { "find" }, parsed.FreeTerms);
        var phrase = Assert.Single(parsed.Phrases);
        Assert.Equal(new[] { "machine", "learning" }, phrase);
    }

    [Fact]
    public void Parse_VacuousPhrases_AreDropped()
    {
        var emptyQuotes = QueryParser.Parse("\"\" neural", Tokenizer.Default);
        Assert.Empty(emptyQuotes.Phrases);
        Assert.False(emptyQuotes.HasPhrases);
        Assert.Equal(new[] { "neural" }, emptyQuotes.AllTerms);

        var punctuation = QueryParser.Parse("\"!!!\"", Tokenizer.Default);
        Assert.Empty(punctuation.Phrases);
        Assert.Empty(punctuation.AllTerms);

        var whitespace = QueryParser.Parse("\"   \"", Tokenizer.Default);
        Assert.Empty(whitespace.Phrases);
        Assert.Empty(whitespace.AllTerms);
    }

    [Fact]
    public void Parse_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => QueryParser.Parse(null!, Tokenizer.Default));
        Assert.Throws<ArgumentNullException>(() => QueryParser.Parse("q", null!));
    }

    [Fact]
    public void SplitRaw_WithoutQuotes_ReturnsWholeQueryAsFreeText()
    {
        var segments = QueryParser.SplitRaw("plain query");

        Assert.Equal("plain query", segments.FreeText);
        Assert.Empty(segments.Phrases);
    }

    [Fact]
    public void SplitRaw_KeepsPhraseTextUntokenized()
    {
        var segments = QueryParser.SplitRaw("neural \"Machine Learning!\"");

        Assert.Equal("neural", segments.FreeText);
        Assert.Equal(new[] { "Machine Learning!" }, segments.Phrases);
    }

    [Fact]
    public void SplitRaw_DropsVacuousPhrases()
    {
        var segments = QueryParser.SplitRaw("\"\" \"   \" \"!!!\" \"real phrase\"");

        Assert.Equal(new[] { "real phrase" }, segments.Phrases);
    }

    [Fact]
    public void SplitRaw_JoinsFreeSegmentsWithoutStrayWhitespace()
    {
        var segments = QueryParser.SplitRaw("neural \"machine learning\" vs \"deep learning\"");

        Assert.Equal("neural vs", segments.FreeText);
        Assert.Equal(
            new[] { "machine learning", "deep learning" },
            segments.Phrases);
    }

    [Fact]
    public void SplitRaw_NullQuery_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => QueryParser.SplitRaw(null!));
    }

    [Fact]
    public void Parse_PrefixAtom_RecordsExpansionAndNoLiteralTerm()
    {
        var parsed = QueryParser.Parse("neural*", Tokenizer.Default);

        Assert.Empty(parsed.FreeTerms);
        Assert.Empty(parsed.AllTerms); // expansions resolve later, against the vocabulary
        var expansion = Assert.Single(parsed.Expansions);
        Assert.Equal(new QueryExpansion("neural", QueryExpansionKind.Prefix), expansion);
        Assert.True(parsed.HasExpansions);
    }

    [Fact]
    public void Parse_MixedPlainPrefixAndPhrase_SplitsAllThree()
    {
        var parsed = QueryParser.Parse("learn neural* \"machine learning\"", Tokenizer.Default);

        Assert.Equal(new[] { "learn" }, parsed.FreeTerms);
        Assert.Equal(new[] { "learn", "machine", "learning" }, parsed.AllTerms); // free + phrase, expansions later
        var phrase = Assert.Single(parsed.Phrases);
        Assert.Equal(new[] { "machine", "learning" }, phrase);
        var expansion = Assert.Single(parsed.Expansions);
        Assert.Equal("neural", expansion.BaseTerm);
        Assert.Equal(QueryExpansionKind.Prefix, expansion.Kind);
    }

    [Fact]
    public void Parse_FuzzyAtom_DefaultsToOneEditAndClampsTheCount()
    {
        AssertExpansion("catt~", QueryExpansionKind.Fuzzy, 1);
        AssertExpansion("catt~2", QueryExpansionKind.Fuzzy, 2);
        AssertExpansion("catt~9", QueryExpansionKind.Fuzzy, 2);
        AssertExpansion("catt~10", QueryExpansionKind.Fuzzy, 2);
        AssertExpansion("catt~0", QueryExpansionKind.Fuzzy, 0);
        AssertExpansion("neural*", QueryExpansionKind.Prefix, 1);
    }

    [Fact]
    public void Parse_ExpansionBaseThatTokenizesToNothing_IsPlain()
    {
        var parsed = QueryParser.Parse("a* bb", Tokenizer.Default);

        Assert.Empty(parsed.Expansions);
        // Byte-for-byte the plain tokenization: the dropped single char leaves nothing.
        Assert.Equal(Tokenizer.Default.Tokenize("a* bb"), parsed.FreeTerms);
        Assert.Equal(new[] { "bb" }, parsed.FreeTerms);
    }

    [Fact]
    public void Parse_ExpansionMarkerNotAfterAWordChar_IsPlain()
    {
        var parsed = QueryParser.Parse("foo ~bar", Tokenizer.Default);

        Assert.Empty(parsed.Expansions);
        Assert.Equal(new[] { "foo", "bar" }, parsed.FreeTerms);
    }

    [Fact]
    public void Parse_ExpansionInsideQuotes_IsLiteral()
    {
        var parsed = QueryParser.Parse("\"neural*\"", Tokenizer.Default);

        Assert.Empty(parsed.Expansions);
        var phrase = Assert.Single(parsed.Phrases);
        Assert.Equal(new[] { "neural" }, phrase);
    }

    [Fact]
    public void Parse_ExpansionAfterPhrase_OnlyAppliesToFreeText()
    {
        var parsed = QueryParser.Parse("\"machine learning\" net*", Tokenizer.Default);

        Assert.Empty(parsed.FreeTerms);
        var phrase = Assert.Single(parsed.Phrases);
        Assert.Equal(new[] { "machine", "learning" }, phrase);
        var expansion = Assert.Single(parsed.Expansions);
        Assert.Equal("net", expansion.BaseTerm);
        Assert.Equal(QueryExpansionKind.Prefix, expansion.Kind);
    }

    private static void AssertExpansion(string query, QueryExpansionKind kind, int maxEdits)
    {
        var parsed = QueryParser.Parse(query, Tokenizer.Default);

        Assert.Empty(parsed.FreeTerms);
        var expansion = Assert.Single(parsed.Expansions);
        Assert.Equal(kind, expansion.Kind);
        Assert.Equal(maxEdits, expansion.MaxEdits);
    }
}
