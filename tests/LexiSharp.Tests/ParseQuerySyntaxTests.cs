using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// <see cref="SearchOptions.ParseQuerySyntax"/>, on its own.
/// </summary>
/// <remarks>
/// The query language reads <c>"a phrase"</c> as positional adjacency, which is what a caller who
/// typed a phrase wants and the opposite of what prose means by a quotation mark. The two cannot be
/// told apart from the string alone, so the caller has to say which one it is — and until this option
/// existed there was no way to say.
/// <para>
/// The fixture is the shape that exposed it: a corpus whose queries are whole documents, one of them
/// containing a quoted passage. Measured on BEIR ArguAna with 1,406 such queries, 138 contain a
/// straight double quote, and every one of those returned 0 or 1 results at any retrieval depth while
/// the reference implementation returned 100 or more for all 1,406.
/// </para>
/// </remarks>
public class ParseQuerySyntaxTests
{
    /// <summary>
    /// A query that quotes a passage, and a document that shares the argument's wording without
    /// quoting it — so the words are in both, and only the phrase gate can tell them apart.
    /// </summary>
    private static InMemoryTextIndex Index()
    {
        var index = new InMemoryTextIndex();

        index.Index(
        [
            // The query's own wording. Excluded in the measured corpus, and here too, since that is
            // the case that matters: with it excluded, the phrase gate leaves nothing at all.
            new SearchDocument("self", "we shall adopt the policy. Roosevelt once said \"we have one language here\" and that was that."),

            // The counter-argument: the same argument, paraphrased, no quotation marks.
            new SearchDocument("counter", "the policy deserves adoption. Roosevelt observed that a single language is spoken here, nothing more than that."),
            new SearchDocument("other", "policy adoption deserves discussion of the language question raised by Roosevelt."),
        ]);

        return index;
    }

    private const string QuotedQuery =
        "the policy deserves adoption. Roosevelt once said \"we have one language here\" and that was that.";

    private static RankedTextSearchEngine Engine(InMemoryTextIndex index) =>
        new(index, new Bm25Scorer(), Tokenizer.Default);

    /// <summary>
    /// The defect, stated as a test: with the query language on, the quotation becomes a phrase that
    /// no document but the excluded one can satisfy, so the search returns nothing at all.
    /// </summary>
    [Fact]
    public void AQuotationInAProseQueryCollapsesTheSearchWhenTheQueryLanguageIsOn()
    {
        var index = Index();
        var engine = Engine(index);

        var results = engine.Search(
            QuotedQuery,
            new SearchOptions(10, ExcludedDocumentIds: new HashSet<string>(StringComparer.Ordinal) { "self" }));

        Assert.True(
            results.Count == 0,
            $"expected the phrase gate to leave nothing, but the search returned {results.Count} results.");
    }

    /// <summary>
    /// And the same query with the query language off, where the quotation mark is a separator like
    /// any other: the counter-argument is found, on the words it shares with the query.
    /// </summary>
    [Fact]
    public void TheSameQueryIsSearchableWhenTheQueryLanguageIsOff()
    {
        var index = Index();
        var engine = Engine(index);

        var results = engine.Search(
            QuotedQuery,
            new SearchOptions(10,
                ExcludedDocumentIds: new HashSet<string>(StringComparer.Ordinal) { "self" },
                ParseQuerySyntax: false));

        Assert.NotEmpty(results);
        Assert.Contains(results, result => result.DocumentId == "counter");
    }

    /// <summary>
    /// Turning the syntax off must be a change of reading, not a change of ranking: the same terms,
    /// scored the same way, whatever the query language did with them.
    /// </summary>
    [Fact]
    public void TurningTheSyntaxOffDoesNotChangeTheScoresOfTheTermsItFinds()
    {
        var index = Index();
        var engine = Engine(index);

        // A query with no quote and no operator in it: both readings must agree exactly, term for
        // term, score for score. This is what makes the option a reading switch and not a second
        // scoring path.
        const string Plain = "policy adoption language roosevelt";

        var withSyntax = engine.Search(Plain, new SearchOptions(10));
        var withoutSyntax = engine.Search(Plain, new SearchOptions(10, ParseQuerySyntax: false));

        Assert.Equal(
            withSyntax.Select(result => (result.DocumentId, result.Score)),
            withoutSyntax.Select(result => (result.DocumentId, result.Score)));
    }

    /// <summary>
    /// Phrase search is the reason the query language exists, so the default has to keep it: a
    /// caller who typed <c>"new york"</c> means the two words next to each other.
    /// </summary>
    [Fact]
    public void TheQueryLanguageIsOnByDefault()
    {
        var index = Index();
        var engine = Engine(index);

        Assert.True(SearchOptions.Default.ParseQuerySyntax);

        // Two documents with the same words in a different order: only the phrase gate separates them.
        var phraseIndex = new InMemoryTextIndex();
        phraseIndex.Index(
        [
            new SearchDocument("adjacent", "the new york office opened"),
            new SearchDocument("apart", "the old york office was new"),
        ]);

        var phraseEngine = new RankedTextSearchEngine(phraseIndex, new Bm25Scorer(), Tokenizer.Default);

        var results = phraseEngine.Search(
            "\"new york\"", new SearchOptions(10, ExcludedDocumentIds: null));

        Assert.Single(results);
        Assert.Equal("adjacent", results[0].DocumentId);
    }

    /// <summary>
    /// With the syntax off the same query is two ordinary terms, and the ordering stops mattering —
    /// which is the whole difference between the two readings, on the smallest example that shows it.
    /// </summary>
    [Fact]
    public void WithTheSyntaxOffAQuotedPhraseBecomesTwoOrdinaryTerms()
    {
        var phraseIndex = new InMemoryTextIndex();
        phraseIndex.Index(
        [
            new SearchDocument("adjacent", "the new york office opened"),
            new SearchDocument("apart", "the old york office was new"),
        ]);

        var engine = new RankedTextSearchEngine(phraseIndex, new Bm25Scorer(), Tokenizer.Default);

        var results = engine.Search("\"new york\"", new SearchOptions(10, ParseQuerySyntax: false));

        Assert.Equal(2, results.Count);
    }
}