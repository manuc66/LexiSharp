using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Corpus statistics for a query's terms, read off the index without scoring a document.
/// </summary>
/// <remarks>
/// The figures are exact and cheap — one vocabulary walk per term — so the tests can pin values
/// rather than shape: an idf pinned as "greater than zero" would not notice the formula drifting.
/// </remarks>
public class QueryTermStatisticsTests
{
    private static InMemoryTextIndex CreateIndex()
    {
        var index = new InMemoryTextIndex();
        index.Index(new[]
        {
            new SearchDocument("d1", "distributed systems consensus"),
            new SearchDocument("d2", "distributed storage"),
            new SearchDocument("d3", "web security"),
        });
        return index;
    }

    private static RankedTextSearchEngine CreateEngine(ITextScorer scorer) =>
        new(CreateIndex(), scorer);

    [Fact]
    public void DescribeQuery_ReportsFrequencyShareAndIdfInReadingOrder()
    {
        var terms = CreateEngine(new Bm25Scorer()).DescribeQuery("distribution");

        // "distribution" is not a term of the corpus, so nothing carries it: the case that makes a
        // description worth asking for, and the one a page of hits cannot distinguish from a bad
        // ranking.
        var term = Assert.Single(terms);
        Assert.Equal("distribution", term.Term);
        Assert.Equal(0, term.DocumentFrequency);
        Assert.Equal(3, term.DocumentCount);
        Assert.Equal(0, term.DocumentShare);
        Assert.True(term.IsAbsent);
        Assert.NotNull(term.InverseDocumentFrequency);
    }

    [Fact]
    public void DescribeQuery_CountsDocumentsCarryingEachTerm()
    {
        var terms = CreateEngine(new Bm25Scorer()).DescribeQuery("distributed security");

        Assert.Equal(["distributed", "security"], terms.Select(term => term.Term));
        Assert.Equal([2, 1], terms.Select(term => term.DocumentFrequency));
        Assert.All(terms, term => Assert.Equal(3, term.DocumentCount));
        Assert.Equal(2.0 / 3.0, terms[0].DocumentShare, 12);
        Assert.Equal(1.0 / 3.0, terms[1].DocumentShare, 12);
        Assert.All(terms, term => Assert.False(term.IsAbsent));
    }

    [Fact]
    public void DescribeQuery_ReportsBm25IdfOfTheFormula()
    {
        var terms = CreateEngine(new Bm25Scorer()).DescribeQuery("distributed security");

        // ln(1 + (N − df + 0.5) / (df + 0.5)) with N = 3.
        Assert.Equal(Math.Log(1.0 + (1.5 / 2.5)), terms[0].InverseDocumentFrequency!.Value, 12);
        Assert.Equal(Math.Log(1.0 + (2.5 / 1.5)), terms[1].InverseDocumentFrequency!.Value, 12);
    }

    [Fact]
    public void DescribeQuery_AgreesWithExplainOnTheSameTerm()
    {
        // The contract that keeps the two doors into a term's idf from showing different numbers.
        var engine = CreateEngine(new Bm25Scorer());
        var stat = engine.DescribeQuery("distributed").Single();
        var explained = Assert.IsType<ScoreExplanation>(engine.Explain("d1", "distributed"));
        var contribution = Assert.Single(explained.Terms);

        Assert.Equal("distributed", contribution.Term);
        Assert.Equal(contribution.DocumentFrequency, stat.DocumentFrequency);
        Assert.Equal(contribution.InverseDocumentFrequency, stat.InverseDocumentFrequency!.Value, 12);
    }

    [Fact]
    public void DescribeQuery_ReportsTheTfIdfIdfWhenTfIdfIsActive()
    {
        var terms = CreateEngine(new TfIdfScorer()).DescribeQuery("distributed security");

        // log((N + 1) / (df + 1)) + 1, the smoothed variant that keeps a rare term's idf positive.
        Assert.Equal(Math.Log(4.0 / 3.0) + 1.0, terms[0].InverseDocumentFrequency!.Value, 12);
        Assert.Equal(Math.Log(4.0 / 2.0) + 1.0, terms[1].InverseDocumentFrequency!.Value, 12);
    }

    [Fact]
    public void DescribeQuery_LeavesIdfUnreportedForAScorerThatHasNone()
    {
        // A boolean scorer ranks by presence, not by rarity, and inventing an idf here would be
        // attributing a number to a scorer that does not compute one.
        var terms = CreateEngine(new BooleanScorer()).DescribeQuery("distributed security");

        Assert.Equal(2, terms.Count);
        Assert.All(terms, term => Assert.Null(term.InverseDocumentFrequency));
        Assert.All(terms, term => Assert.NotEqual(0, term.DocumentFrequency));
    }

    [Fact]
    public void DescribeQuery_DescribesARepeatedTermOnce()
    {
        var terms = CreateEngine(new Bm25Scorer()).DescribeQuery("distributed distributed distributed");

        Assert.Single(terms);
    }

    [Fact]
    public void DescribeQuery_ReturnsNothingForAQueryWithNoScoringTerm()
    {
        var engine = CreateEngine(new Bm25Scorer());

        Assert.Empty(engine.DescribeQuery(""));
        Assert.Empty(engine.DescribeQuery("   "));
        Assert.Empty(engine.DescribeQuery("-distributed"));
    }

    [Fact]
    public void DescribeQuery_ReturnsNothingForAnEmptyIndex()
    {
        var engine = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());

        Assert.Empty(engine.DescribeQuery("distributed"));
    }

    [Fact]
    public void DescribeQuery_DescribesTheWordsAsWrittenUnderFuzzyIndexing()
    {
        // The facade's fuzzy rewriting is a search-time expansion. The description is of the
        // question, so the caller sees the word they typed rather than the neighbourhood it reaches.
        var index = new LexiSharpIndex<SearchDocument>(options => options.EnableFuzzy = true);
        index.AddRange(new[]
        {
            new SearchDocument("d1", "distributed systems"),
            new SearchDocument("d2", "distributed storage"),
        });

        var term = Assert.Single(index.DescribeQuery("distributd"));

        Assert.Equal("distributd", term.Term);
        Assert.True(term.IsAbsent);
        Assert.Equal(2, index.Search("distributd").Count);
    }

    [Fact]
    public void Facade_DescribesTheQueryLikeTheEngineItWraps()
    {
        var index = new LexiSharpIndex<SearchDocument>();
        index.AddRange(new[]
        {
            new SearchDocument("d1", "distributed systems"),
            new SearchDocument("d2", "distributed storage"),
        });

        var terms = index.DescribeQuery("distributed storage");

        Assert.Equal(["distributed", "storage"], terms.Select(term => term.Term));
        Assert.Equal([2, 1], terms.Select(term => term.DocumentFrequency));
        Assert.All(terms, term => Assert.Equal(2, term.DocumentCount));
    }
}
