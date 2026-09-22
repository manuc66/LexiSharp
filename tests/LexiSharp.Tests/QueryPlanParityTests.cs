using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class QueryPlanParityTests
{
    private static readonly string[] Corpus =
    {
        "the quick brown fox jumps over the lazy dog",
        "a pangram is a sentence using every letter of the alphabet at least once",
        "lexical search engines rank documents by term statistics",
        "bm25 blends term saturation with document length normalization",
        "query likelihood uses jelinek-mercer smoothing",
        "tf idf weights rare terms more heavily than frequent ones",
        "the universal answer is forty two",
        "inverted indexes map every term to the documents containing it",
        "candidatemark", // shared by several generated queries below
    };

    [Theory]
    [InlineData("bm25")]
    [InlineData("bm25-tuned")]
    [InlineData("tfidf")]
    [InlineData("ql")]
    [InlineData("ql-tuned")]
    public void Plan_MatchesScore_Bitwise(string scorerKind)
    {
        ITextScorer scorer = scorerKind switch
        {
            "bm25" => new Bm25Scorer(),
            "bm25-tuned" => new Bm25Scorer(0.9, 0.4),
            "tfidf" => new TfIdfScorer(),
            "ql" => new QueryLikelihoodScorer(),
            _ => new QueryLikelihoodScorer(0.7),
        };
        var index = new InMemoryTextIndex();
        index.Index(Corpus.Select((text, i) => new SearchDocument(i.ToString(), text)));

        string[] queries = { "term", "the quick", "every letter scoring", "zulu missing term" };

        foreach (var query in queries)
        {
            var tokens = index.Tokenizer.Tokenize(query);
            var plan = ((IQueryPlannableScorer)scorer).CreatePlan(tokens, index);

            foreach (var documentId in index.Documents.Select(d => d.Id))
            {
                double expected = scorer.Score(documentId, tokens, index);
                double actual = plan.Score(documentId);

                Assert.Equal(expected, actual);
            }
        }
    }
}