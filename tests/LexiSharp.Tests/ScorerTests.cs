using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class ScorerTests
{
    private sealed record Fixture(ITextIndex Index, SearchDocument Doc1, SearchDocument Doc2, SearchDocument Doc3);

    private static Fixture CreateFixture()
    {
        var doc1 = new SearchDocument("1", "the search engine is powerful");
        var doc2 = new SearchDocument("2", "textual search is a classic technique");
        var doc3 = new SearchDocument("3", "italian cuisine");
        var index = new InMemoryTextIndex();
        index.Index(new[] { doc1, doc2, doc3 });
        return new Fixture(index, doc1, doc2, doc3);
    }

    [Fact]
    public void TfIdf_PrefersDocumentsMatchingMoreOrRarerTerms()
    {
        var (index, doc1, doc2, doc3) = CreateFixture();
        var scorer = new TfIdfScorer();
        var query = new[] { "search", "engine" };

        double score1 = scorer.Score(doc1.Id, query, index);
        double score2 = scorer.Score(doc2.Id, query, index);
        double score3 = scorer.Score(doc3.Id, query, index);

        Assert.True(score1 > score2, "document with two matches should rank first");
        Assert.True(score2 > score3, "partial match should rank above no match");
        Assert.Equal(0, score3);
        Assert.Equal("TF-IDF", scorer.Name);
    }

    [Fact]
    public void Bm25_PrefersDocumentsMatchingMoreTermsAndNormalizesLength()
    {
        var (index, doc1, doc2, doc3) = CreateFixture();
        var scorer = new Bm25Scorer();
        var query = new[] { "search", "engine" };

        double score1 = scorer.Score(doc1.Id, query, index);
        double score2 = scorer.Score(doc2.Id, query, index);
        double score3 = scorer.Score(doc3.Id, query, index);

        Assert.True(score1 > score2);
        Assert.True(score2 > score3);
        Assert.Equal(0, score3);
        Assert.Equal("BM25", scorer.Name);
    }

    [Fact]
    public void Bm25_ShortDocumentWithSameTermCountOutranksLongOne()
    {
        var index = new InMemoryTextIndex();
        index.Add(new SearchDocument("short", "term term term"));
        index.Add(new SearchDocument(
            "long",
            "term term term filler filler filler filler filler filler filler filler filler filler filler filler filler filler filler filler filler filler filler filler filler filler filler filler filler filler filler filler filler"));
        var scorer = new Bm25Scorer();
        var query = new[] { "term" };

        Assert.True(scorer.Score("short", query, index) > scorer.Score("long", query, index));
    }

    [Fact]
    public void Boolean_AllTerms_RequiresEveryQueryTerm()
    {
        var (index, doc1, _, doc3) = CreateFixture();
        var scorer = new BooleanScorer(BooleanMatch.AllTerms);
        var query = new[] { "search", "engine" };

        Assert.Equal(1, scorer.Score(doc1.Id, query, index));
        Assert.Equal(0, scorer.Score(doc3.Id, query, index));
    }

    [Fact]
    public void Boolean_AnyTerm_RequiresOneQueryTerm()
    {
        var (index, doc1, _, doc3) = CreateFixture();
        var scorer = new BooleanScorer(BooleanMatch.AnyTerm);
        var query = new[] { "search", "engine" };

        Assert.Equal(1, scorer.Score(doc1.Id, query, index));
        Assert.Equal(0, scorer.Score(doc3.Id, query, index));
        Assert.Equal("Boolean (OR)", scorer.Name);
    }

    [Fact]
    public void QueryLikelihood_ProducesHigherScoreForRelevantDocument()
    {
        var (index, doc1, doc2, doc3) = CreateFixture();
        var scorer = new QueryLikelihoodScorer(lambda: 0.2);
        var query = new[] { "search", "engine" };

        double score1 = scorer.Score(doc1.Id, query, index);
        double score2 = scorer.Score(doc2.Id, query, index);
        double score3 = scorer.Score(doc3.Id, query, index);

        Assert.True(score1 > score2, "document matching both query terms ranks above the one matching one");
        Assert.True(score2 < 0, "query-likelihood scores are negative log-probabilities");
        Assert.Equal(0, score3); // doc3 shares no term with the query
    }

    [Fact]
    public void Scorers_HandleEmptyIndexGracefully()
    {
        var index = new InMemoryTextIndex();
        var query = new[] { "anything" };

        Assert.Equal(0, new TfIdfScorer().Score("1", query, index));
        Assert.Equal(0, new Bm25Scorer().Score("1", query, index));
        Assert.Equal(0, new BooleanScorer().Score("1", query, index));
        Assert.Equal(0, new QueryLikelihoodScorer().Score("1", query, index));
    }

    [Fact]
    public void Bm25_ValidatesParameters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bm25Scorer(k1: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bm25Scorer(k1: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bm25Scorer(k1: double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bm25Scorer(b: 1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bm25Scorer(b: -0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bm25Scorer(b: double.NaN));
    }

    [Fact]
    public void QueryLikelihood_RejectsNonFiniteOrOutOfRangeLambda()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueryLikelihoodScorer(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueryLikelihoodScorer(1.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new QueryLikelihoodScorer(double.NaN));
    }
}