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
}
