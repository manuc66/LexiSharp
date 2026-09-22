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

    private static readonly string[] QueryAlphaGamma = new[] { "alpha", "gamma" };
    private static readonly string[] QueryAlpha = new[] { "alpha" };

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

        var explanation = scorer.Explain(doc1.Id, QueryAlphaGamma, index);

        Assert.Contains(explanation.Terms, t => t.Term == "alpha");
        Assert.DoesNotContain(explanation.Terms, t => t.Term == "gamma");
    }

    [Fact]
    public void Bm25_Explain_EmptyDocumentAndEmptyIndexAreSafe()
    {
        var empty = new InMemoryTextIndex();
        var explanation = new Bm25Scorer().Explain("ghost", QueryAlpha, empty);

        Assert.Equal(0, explanation.TotalScore);
        Assert.Empty(explanation.Terms);
        Assert.Equal(0, explanation.DocumentLength);
        Assert.Equal(0, explanation.AverageDocumentLength);
    }

    [Fact]
    public void TfIdf_Explain_ReproducesTheScoreAndBreaksDownTerms()
    {
        var (index, doc1, _) = CreateFixture();
        var scorer = new TfIdfScorer();
        var query = new[] { "alpha", "beta" };

        var explanation = scorer.Explain(doc1.Id, query, index);

        Assert.Equal("TF-IDF", explanation.Algorithm);
        Assert.Equal(1.0, explanation.LengthNormalization);
        Assert.Equal(3, explanation.DocumentLength);
        Assert.Equal(2.5, explanation.AverageDocumentLength);

        // df(alpha)=1, idf = ln(3/2) + 1; df(beta)=2, idf = ln(3/3) + 1 = 1.
        var alpha = Assert.Single(explanation.Terms, t => t.Term == "alpha");
        Assert.Equal(2, alpha.TermFrequency);
        Assert.Equal(1, alpha.DocumentFrequency);
        Assert.Equal(Math.Log(1.5) + 1.0, alpha.InverseDocumentFrequency, 12);
        Assert.Equal(2 * (Math.Log(1.5) + 1.0), alpha.Score, 12);

        var beta = Assert.Single(explanation.Terms, t => t.Term == "beta");
        Assert.Equal(1.0, beta.InverseDocumentFrequency, 12);

        Assert.Equal(alpha.Score + beta.Score, explanation.TotalScore, 12);
        Assert.Equal(scorer.Score(doc1.Id, query, index), explanation.TotalScore, 12);
    }

    [Fact]
    public void QueryLikelihood_Explain_ReproducesTheScoreAndBreaksDownTerms()
    {
        var (index, doc1, _) = CreateFixture();
        var scorer = new QueryLikelihoodScorer();
        var query = new[] { "alpha", "beta" };

        var explanation = scorer.Explain(doc1.Id, query, index);

        Assert.Equal("QueryLikelihood", explanation.Algorithm);
        Assert.Equal(1.0, explanation.LengthNormalization);
        Assert.Equal(0.2, explanation.Parameters["lambda"]);
        Assert.Equal(2, explanation.Terms.Count);

        var alpha = Assert.Single(explanation.Terms, t => t.Term == "alpha");
        double expectedAlpha = Math.Log(0.8 * (2.0 / 3.0) + 0.2 * (2.0 / 5.0));
        Assert.Equal(expectedAlpha, alpha.Score, 12);

        double expectedBeta = Math.Log(0.8 * (1.0 / 3.0) + 0.2 * (2.0 / 5.0));
        Assert.Equal(expectedBeta, Assert.Single(explanation.Terms, t => t.Term == "beta").Score, 12);

        Assert.Equal(alpha.Score + expectedBeta, explanation.TotalScore, 12);
        Assert.Equal(scorer.Score(doc1.Id, query, index), explanation.TotalScore, 12);
    }

    [Fact]
    public void QueryLikelihood_Explain_NonMatchingDocumentReportsZeroButKeepsSmoothingTerms()
    {
        var (index, _, doc2) = CreateFixture();
        var scorer = new QueryLikelihoodScorer();

        var explanation = scorer.Explain(doc2.Id, QueryAlpha, index);

        // doc2 has no "alpha": the engine convention makes it a non-match, score 0.
        Assert.Equal(0, explanation.TotalScore);
        Assert.Equal(scorer.Score(doc2.Id, QueryAlpha, index), explanation.TotalScore);

        // The collection-model contribution is still exposed for diagnostics (tf 0).
        var alpha = Assert.Single(explanation.Terms, t => t.Term == "alpha");
        Assert.Equal(0, alpha.TermFrequency);
        Assert.Equal(Math.Log(0.2 * (2.0 / 5.0)), alpha.Score, 12);
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

    [Fact]
    public void Engine_Explain_ParsesQuotedPhrasesLikeSearch()
    {
        var (index, doc1, _) = CreateFixture();
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        var quoted = engine.Explain(doc1.Id, "\"alpha beta\"");
        var plain = engine.Explain(doc1.Id, "alpha beta");

        Assert.NotNull(quoted);
        Assert.NotNull(plain);
        Assert.Equal(plain!.TotalScore, quoted!.TotalScore, 12);
        Assert.Equal(
            plain.Terms.Select(t => t.Term),
            quoted.Terms.Select(t => t.Term));
    }
}