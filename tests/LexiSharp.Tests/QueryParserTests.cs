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

    [Fact]
    public void Parse_LeadingPlus_RecordsRequiredTermAndScoresIt()
    {
        var parsed = QueryParser.Parse("+neural network", Tokenizer.Default);

        Assert.Equal(new[] { "network" }, parsed.FreeTerms);
        Assert.Equal(new[] { "neural" }, parsed.RequiredTerms);
        Assert.Empty(parsed.ProhibitedTerms);
        // Required terms are literal scoring input; prohibited ones never are.
        Assert.Equal(new[] { "network", "neural" }, parsed.AllTerms);
        Assert.True(parsed.HasRequired);
        Assert.False(parsed.HasProhibited);
        Assert.True(parsed.HasQualifiers);
    }

    [Fact]
    public void Parse_LeadingMinus_RecordsProhibitedTermAndKeepsItOutOfScoring()
    {
        var parsed = QueryParser.Parse("neural -network", Tokenizer.Default);

        Assert.Equal(new[] { "neural" }, parsed.FreeTerms);
        Assert.Equal(new[] { "network" }, parsed.ProhibitedTerms);
        Assert.Empty(parsed.RequiredTerms);
        Assert.Equal(new[] { "neural" }, parsed.AllTerms);
        Assert.False(parsed.HasRequired);
        Assert.True(parsed.HasProhibited);
        Assert.True(parsed.HasQualifiers);
    }

    [Theory]
    [InlineData("e-mail")]
    [InlineData("well-being")]
    [InlineData("foo-bar")]
    [InlineData("re-enter the room")]
    public void Parse_HyphenInsideAWord_IsLiteralAndNotAnOperator(string query)
    {
        var parsed = QueryParser.Parse(query, Tokenizer.Default);

        Assert.Equal(Tokenizer.Default.Tokenize(query), parsed.FreeTerms);
        Assert.Equal(Tokenizer.Default.Tokenize(query), parsed.AllTerms);
        Assert.Empty(parsed.RequiredTerms);
        Assert.Empty(parsed.ProhibitedTerms);
        Assert.False(parsed.HasQualifiers);
        Assert.True(QueryParser.IsPlain(query));
    }

    [Fact]
    public void Parse_OnlyALeadingOperatorOnAWhitespaceTokenCounts()
    {
        // "foo-bar" starts a token with 'f'; "-bar" is a separate token and does count.
        var parsed = QueryParser.Parse("foo-bar -baz", Tokenizer.Default);

        Assert.Equal(Tokenizer.Default.Tokenize("foo-bar"), parsed.FreeTerms);
        Assert.Equal(new[] { "baz" }, parsed.ProhibitedTerms);
    }

    [Fact]
    public void Parse_BareSignAndSeparatedSign_AreNotOperators()
    {
        var bare = QueryParser.Parse("+ -", Tokenizer.Default);
        Assert.Empty(bare.RequiredTerms);
        Assert.Empty(bare.ProhibitedTerms);

        // The sign has to touch its term; separated by whitespace it is a token of its own and
        // tokenizes to nothing.
        var separated = QueryParser.Parse("+ neural - network", Tokenizer.Default);
        Assert.Equal(new[] { "neural", "network" }, separated.FreeTerms);
        Assert.Empty(separated.RequiredTerms);
        Assert.Empty(separated.ProhibitedTerms);
    }

    [Fact]
    public void Parse_RepeatedOperators_FirstOneDecides()
    {
        var required = QueryParser.Parse("++neural", Tokenizer.Default);
        Assert.Equal(new[] { "neural" }, required.RequiredTerms);
        Assert.Empty(required.ProhibitedTerms);

        var mixed = QueryParser.Parse("+-neural", Tokenizer.Default);
        Assert.Equal(new[] { "neural" }, mixed.RequiredTerms);
        Assert.Empty(mixed.ProhibitedTerms);

        var prohibited = QueryParser.Parse("--neural", Tokenizer.Default);
        Assert.Equal(new[] { "neural" }, prohibited.ProhibitedTerms);

        var reversed = QueryParser.Parse("-+neural", Tokenizer.Default);
        Assert.Equal(new[] { "neural" }, reversed.ProhibitedTerms);
        Assert.Empty(reversed.RequiredTerms);
    }

    [Theory]
    [InlineData("+neural*", QueryTermOperator.Required)]
    [InlineData("-neural*", QueryTermOperator.Prohibited)]
    [InlineData("+neural~2", QueryTermOperator.Required)]
    [InlineData("-neural~2", QueryTermOperator.Prohibited)]
    public void Parse_OperatorOnAnExpansion_IsCarriedByTheAtom(string query, QueryTermOperator expected)
    {
        var parsed = QueryParser.Parse(query, Tokenizer.Default);

        var expansion = Assert.Single(parsed.Expansions);
        Assert.Equal(expected, expansion.Operator);
        // An expansion resolves later against the vocabulary, so the literal list is empty.
        Assert.Empty(parsed.AllTerms);
        Assert.True(parsed.HasQualifiers);
    }

    [Fact]
    public void Parse_QualifiedPhrases_AreSeparatedFromUnqualifiedOnes()
    {
        var parsed = QueryParser.Parse("+\"machine learning\" -\"neural network\" \"deep learning\" free",
            Tokenizer.Default);

        Assert.Equal(new[] { "free" }, parsed.FreeTerms);

        var required = Assert.Single(parsed.RequiredPhrases);
        Assert.Equal(new[] { "machine", "learning" }, required);

        var prohibited = Assert.Single(parsed.ProhibitedPhrases);
        Assert.Equal(new[] { "neural", "network" }, prohibited);

        var plain = Assert.Single(parsed.Phrases);
        Assert.Equal(new[] { "deep", "learning" }, plain);

        // Scoring input is free + required literal terms + plain/required phrase terms; a
        // prohibited phrase's terms never score.
        Assert.Equal(
            new[] { "free", "deep", "learning", "machine", "learning" },
            parsed.AllTerms);
    }

    [Fact]
    public void Parse_VacuousPhraseWithAnOperator_ImposesNothing()
    {
        var parsed = QueryParser.Parse("-\"\" +\"!!!\"", Tokenizer.Default);

        Assert.Empty(parsed.Phrases);
        Assert.Empty(parsed.RequiredPhrases);
        Assert.Empty(parsed.ProhibitedPhrases);
        Assert.False(parsed.HasQualifiers);
        Assert.Empty(parsed.AllTerms);
    }

    [Fact]
    public void IsPlain_SeesLeadingOperatorsButNotHyphensInsideWords()
    {
        Assert.False(QueryParser.IsPlain("-neural"));
        Assert.False(QueryParser.IsPlain("+neural"));
        Assert.False(QueryParser.IsPlain("neural -network"));
        Assert.False(QueryParser.IsPlain("+\"machine learning\""));

        Assert.True(QueryParser.IsPlain("neural network"));
        Assert.True(QueryParser.IsPlain("e-mail well-being"));
        Assert.True(QueryParser.IsPlain("+"));
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
