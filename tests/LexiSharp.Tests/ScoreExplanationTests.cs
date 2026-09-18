using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class ScoreExplanationTests
{
    private sealed record Fixture(ITextIndex Index, SearchDocument Doc1, SearchDocument Doc2);

    private static Fixture CreateFixture()
    {
        var doc1 = new SearchDocument("1", "alpha alpha beta");
        var doc2 = new SearchDocument("2", "beta gamma");
        var index = new InMemoryTextIndex();
        index.Index(new[] { doc1, doc2 });
        return new Fixture(index, doc1, doc2);
    }

    [Fact]
    public void Bm25_Explain_ReproducesTheScoreAndBreaksDownTerms()
    {
        var (index, doc1, _) = CreateFixture();
        var scorer = new Bm25Scorer();
        var query = new[] { "alpha", "beta" };

        var explanation = scorer.Explain(doc1.Id, query, index);

        // Hand-computed corpus figures.
        Assert.Equal(3, explanation.DocumentLength);
        Assert.Equal(2.5, explanation.AverageDocumentLength);
        Assert.Equal(1.2, explanation.LengthRatio, 12);
        Assert.Equal(1.0 - 0.75 + 0.75 * 1.2, explanation.LengthNormalization, 12);
        Assert.Equal(1.5, explanation.Parameters["k1"]);
        Assert.Equal(0.75, explanation.Parameters["b"]);
        Assert.Equal("BM25", explanation.Algorithm);

        // "alpha" occurs twice in doc1 and nowhere else: df = 1, idf = ln(1 + (2-1+0.5)/(1+0.5)) = ln(2).
        var alpha = Assert.Single(explanation.Terms, t => t.Term == "alpha");
        Assert.Equal(2, alpha.TermFrequency);
        Assert.Equal(1, alpha.DocumentFrequency);
        Assert.Equal(Math.Log(2.0), alpha.InverseDocumentFrequency, 12);

        // "beta" occurs in both documents: df = 2, idf = ln(1 + 0.5/2.5).
        var beta = Assert.Single(explanation.Terms, t => t.Term == "beta");
        Assert.Equal(1, beta.TermFrequency);
        Assert.Equal(2, beta.DocumentFrequency);
        Assert.Equal(Math.Log(1.0 + 0.5 / 2.5), beta.InverseDocumentFrequency, 12);

        Assert.Equal(alpha.Score + beta.Score, explanation.TotalScore, 12);
        Assert.Equal(scorer.Score(doc1.Id, query, index), explanation.TotalScore, 12);
    }

    [Fact]
    public void Bm25_Explain_OmitsTermsAbsentFromTheDocument()
    {
        var (index, doc1, _) = CreateFixture();
        var scorer = new Bm25Scorer();

        var explanation = scorer.Explain(doc1.Id, new[] { "alpha", "gamma" }, index);

        Assert.Contains(explanation.Terms, t => t.Term == "alpha");
        Assert.DoesNotContain(explanation.Terms, t => t.Term == "gamma");
    }

    [Fact]
    public void Bm25_Explain_EmptyDocumentAndEmptyIndexAreSafe()
    {
        var empty = new InMemoryTextIndex();
        var explanation = new Bm25Scorer().Explain("ghost", new[] { "alpha" }, empty);

        Assert.Equal(0, explanation.TotalScore);
        Assert.Empty(explanation.Terms);
        Assert.Equal(0, explanation.DocumentLength);
        Assert.Equal(0, explanation.AverageDocumentLength);
    }

    [Fact]
    public void Engine_Explain_ReturnsNullForNonExplainingScorers()
    {
        var (index, _, _) = CreateFixture();
        var engine = new RankedTextSearchEngine(index, new BooleanScorer());

        Assert.Null(engine.Explain("1", "alpha"));
    }

    [Fact]
    public void Engine_Explain_ReturnsNullForUnknownDocuments()
    {
        var (index, _, _) = CreateFixture();
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        Assert.Null(engine.Explain("missing", "alpha"));
    }

    [Fact]
    public void Engine_Explain_TokenizesTheQueryWithTheEngineTokenizer()
    {
        var (index, doc1, _) = CreateFixture();
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        var fromRaw = engine.Explain(doc1.Id, "Alpha, BETA!");
        var fromTerms = engine.Explain(doc1.Id, "alpha beta");

        Assert.NotNull(fromRaw);
        Assert.NotNull(fromTerms);
        Assert.Equal(fromTerms!.TotalScore, fromRaw.TotalScore, 12);
        Assert.Equal(
            fromTerms.Terms.Select(t => t.Term),
            fromRaw.Terms.Select(t => t.Term));
    }
}