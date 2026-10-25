using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The <c>+term</c> (required) and <c>-term</c> (prohibited) query operators through the public
/// search surface: the gates they impose, the score they leave, and the compatibility promise that
/// a hyphen inside a word is still ordinary text.
/// </summary>
public class QueryOperatorTests
{
    /// <summary>
    /// Six documents chosen so every operator has a case that distinguishes it from plain text:
    /// <c>apple</c> is in two, <c>banana</c> in one, and the two phrases share a term.
    /// </summary>
    private static InMemoryTextIndex Index()
    {
        var index = new InMemoryTextIndex();

        index.Index(
        [
            new SearchDocument("d1", "red apple pie"),
            new SearchDocument("d2", "green apple tart"),
            new SearchDocument("d3", "red banana bread"),
            new SearchDocument("d4", "yellow pear"),
            new SearchDocument("d5", "contact us by e-mail"),
            new SearchDocument("d6", "spam folder"),
        ]);

        return index;
    }

    private static RankedTextSearchEngine Engine(InMemoryTextIndex index) =>
        new(index, new Bm25Scorer(), Tokenizer.Default);

    private static string[] Ids(IReadOnlyList<SearchResult> results) =>
        results.Select(result => result.DocumentId).ToArray();

    [Fact]
    public void RequiredTerm_GatesDocumentsWithoutIt()
    {
        var engine = Engine(Index());

        // d2 has "apple" and no "pie": it scores on the free term and would come back without the
        // operator. The + on "pie" is what removes it.
        Assert.Equal(new[] { "d1" }, Ids(engine.Search("apple +pie", new SearchOptions(Limit: 10))));
        Assert.Equal(new[] { "d1", "d2" }, Ids(engine.Search("apple pie", new SearchOptions(Limit: 10))));
    }

    [Fact]
    public void ProhibitedTerm_ExcludesEveryDocumentContainingIt()
    {
        var engine = Engine(Index());

        // d2 matches "apple" but is excluded by "-tart"; d1 survives.
        Assert.Equal(new[] { "d1" }, Ids(engine.Search("apple -tart", new SearchOptions(Limit: 10))));
    }

    [Fact]
    public void RequiredAndProhibited_TogetherGateBothWays()
    {
        var engine = Engine(Index());

        Assert.Equal(new[] { "d1" }, Ids(engine.Search("+apple -tart", new SearchOptions(Limit: 10))));
        Assert.Equal(new[] { "d2" }, Ids(engine.Search("+apple -pie", new SearchOptions(Limit: 10))));
    }

    [Fact]
    public void NegationOnly_ReturnsTheComplementAtUnitScore()
    {
        var engine = Engine(Index());

        var results = engine.Search("-banana", new SearchOptions(Limit: 10));

        // Every document except d3, in the default document-id tie-break because they all score 1.
        Assert.Equal(new[] { "d1", "d2", "d4", "d5", "d6" }, Ids(results));
        Assert.All(results, result => Assert.Equal(1.0, result.Score));

        // A term the corpus does not contain excludes nothing, so the complement is the corpus.
        Assert.Equal(6, engine.Search("-nosuchterm", new SearchOptions(Limit: 10)).Count);

        // A minimum score above the unit score asks for a relevance signal a pure-negative query
        // does not have.
        Assert.Empty(engine.Search(
            "-banana", new SearchOptions(Limit: 10, MinimumScore: 1.5)));
    }

    [Fact]
    public void RequiredPhrase_GatesOnConsecutivePositions()
    {
        var engine = Engine(Index());

        Assert.Equal(new[] { "d1" }, Ids(engine.Search("+\"red apple\"", new SearchOptions(Limit: 10))));

        // The same words apart do not satisfy the phrase.
        var apart = new InMemoryTextIndex();
        apart.Index([new SearchDocument("apart", "apple is red")]);
        Assert.Empty(Engine(apart).Search("+\"red apple\"", new SearchOptions(Limit: 10)));
    }

    [Fact]
    public void ProhibitedPhrase_ExcludesOnConsecutivePositions()
    {
        var engine = Engine(Index());

        Assert.Equal(new[] { "d1" }, Ids(engine.Search("apple -\"green apple\"", new SearchOptions(Limit: 10))));

        // Phrase-only negative: the complement of the documents where the phrase occurs.
        Assert.Equal(
            new[] { "d2", "d3", "d4", "d5", "d6" },
            Ids(engine.Search("-\"red apple\"", new SearchOptions(Limit: 10))));
    }

    [Fact]
    public void RequiredPrefix_GatesOnAnyExpandedTerm()
    {
        var engine = Engine(Index());

        Assert.Equal(new[] { "d1", "d2" }, Ids(engine.Search("+app*", new SearchOptions(Limit: 10))));

        // One document has "red" without an "app" term, so the prohibited prefix keeps only it.
        Assert.Equal(new[] { "d3" }, Ids(engine.Search("red -app*", new SearchOptions(Limit: 10))));
    }

    [Fact]
    public void RequiredFuzzy_GatesOnAnyNearVariant()
    {
        var engine = Engine(Index());

        Assert.Equal(new[] { "d1", "d2" }, Ids(engine.Search("+applf~1", new SearchOptions(Limit: 10))));
        Assert.Equal(new[] { "d3" }, Ids(engine.Search("red -applf~1", new SearchOptions(Limit: 10))));
    }

    [Fact]
    public void RequiredTermsAloneStillScoreOnThoseTerms()
    {
        var engine = Engine(Index());

        var required = Assert.Single(engine.Search("+apple +red", new SearchOptions(Limit: 10)));
        Assert.Equal("d1", required.DocumentId);

        // The operator is a gate, not a replacement for the term: the score is exactly the score
        // the same two terms produce without it.
        var unqualified = Assert.Single(engine.Search("apple red", new SearchOptions(Limit: 10)),
            result => result.DocumentId == "d1");

        Assert.Equal(unqualified.Score, required.Score);
    }

    [Fact]
    public void RepeatedOperators_ReadAsTheFirstOne()
    {
        var engine = Engine(Index());
        var options = new SearchOptions(Limit: 10);

        Assert.Equal(
            Ids(engine.Search("+apple", options)),
            Ids(engine.Search("++apple", options)));

        Assert.Equal(
            Ids(engine.Search("+apple", options)),
            Ids(engine.Search("+-apple", options)));

        Assert.Equal(
            Ids(engine.Search("-banana", options)),
            Ids(engine.Search("--banana", options)));
    }

    [Fact]
    public void HyphenInsideAWord_IsUnchanged()
    {
        var engine = Engine(Index());

        // "e-mail" is two ordinary terms with a hyphen separator, exactly as without the query
        // language; "-spam" is the only operator in the query.
        Assert.Equal(new[] { "d5" }, Ids(engine.Search("e-mail -spam", new SearchOptions(Limit: 10))));

        var parsed = QueryParser.Parse("e-mail well-being foo-bar", Tokenizer.Default);
        Assert.Equal(Tokenizer.Default.Tokenize("e-mail well-being foo-bar"), parsed.FreeTerms);
        Assert.Empty(parsed.RequiredTerms);
        Assert.Empty(parsed.ProhibitedTerms);
    }

    [Fact]
    public void UnknownTermAndExcludedDocument_AreHonoured()
    {
        var engine = Engine(Index());

        Assert.Empty(engine.Search("+nosuchterm", new SearchOptions(Limit: 10)));

        var excluded = new HashSet<string>(StringComparer.Ordinal) { "d2" };

        Assert.Equal(
            new[] { "d1" },
            Ids(engine.Search("+apple", new SearchOptions(Limit: 10, ExcludedDocumentIds: excluded))));

        Assert.Equal(
            new[] { "d1", "d4", "d5", "d6" },
            Ids(engine.Search("-banana", new SearchOptions(Limit: 10, ExcludedDocumentIds: excluded))));
    }

    [Fact]
    public void VacuousQualifiedAtom_LeavesAnEmptyQuery()
    {
        var engine = Engine(Index());

        // The parser documents that a qualifier on a segment producing no terms is dropped with
        // that segment. A query whose only content is such a token therefore has no term to match,
        // like any other empty query — it does not become "everything" or "nothing" by accident.
        Assert.Empty(engine.Search("-\"\"", new SearchOptions(Limit: 10)));
        Assert.Empty(engine.Search("+\"!!!\"", new SearchOptions(Limit: 10)));
        Assert.Empty(engine.Search("-a", new SearchOptions(Limit: 10)));
    }

    [Fact]
    public void LiteralQuerySyntax_TreatsTheSignAsASeparator()
    {
        var engine = Engine(Index());

        // The escape hatch for a corpus of prose: with the query language off, "-banana" is the
        // term "banana", not an exclusion.
        Assert.Equal(
            Ids(engine.Search("banana", new SearchOptions(Limit: 10))),
            Ids(engine.Search("-banana", new SearchOptions(Limit: 10, ParseQuerySyntax: false))));

        Assert.Equal(new[] { "d3" }, Ids(engine.Search(
            "-banana", new SearchOptions(Limit: 10, ParseQuerySyntax: false))));
    }
}
