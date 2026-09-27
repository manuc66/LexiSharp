using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using LexiSharp.Sources;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The safety property behind the BEIR harness moving from a concatenated title+text string to a
/// real title text field: for a scorer that does not weigh fields, the two forms must be
/// indistinguishable. They should be — the flat view is the union of the fields, so the same
/// tokens land at the same frequencies and lengths, and only their order differs.
/// </summary>
/// <remarks>
/// Worth pinning because the harness change silently rewrites every published BM25 number if it
/// ever stops holding. It also pins the phrase behaviour that genuinely does differ, so the
/// difference is documented rather than assumed away.
/// </remarks>
public class FieldSplitInvarianceTests
{
    private static readonly (string Id, string Title, string Text)[] Documents =
    {
        ("1", "A ranking guide", "How to rank documents with term statistics and length normalization."),
        ("2", "Tokenization", "Splitting text into terms is the first step of any ranking pipeline."),
        ("3", "Refresher", "A short note about refresh tokens, expiry and revocation in one line."),
    };

    private static readonly string[] Queries = ["ranking", "term statistics", "refresh", "tokens", "the"];

    private static InMemoryTextIndex Flattened()
    {
        var index = new InMemoryTextIndex();

        index.Index(Documents.Select(document =>
            new SearchDocument(document.Id, document.Title + " " + document.Text)));

        return index;
    }

    private static InMemoryTextIndex Split()
    {
        var index = new InMemoryTextIndex();

        index.Index(Documents.Select(document => new SearchDocument(
            document.Id,
            document.Text,
            TextFields: new Dictionary<string, string> { ["title"] = document.Title })));

        return index;
    }

    [Fact]
    public void TheIndexHoldsTheSameTokensEitherWay()
    {
        var flat = Flattened();
        var split = Split();

        foreach (var (id, title, text) in Documents)
        {
            Assert.Equal(flat.DocumentLength(id), split.DocumentLength(id));
            Assert.Equal(flat.AverageDocumentLength, split.AverageDocumentLength);
            Assert.Equal(flat.VocabularySize, split.VocabularySize);

            // Same token multiset, different order.
            Assert.Equal(
                flat.GetTerms(id).Order(StringComparer.Ordinal),
                split.GetTerms(id).Order(StringComparer.Ordinal));
        }

        Assert.Equal(flat.CorpusTokenCount, split.CorpusTokenCount);
    }

    [Theory]
    [InlineData("bm25-1.5", 1.5, 0.75)]
    [InlineData("bm25-1.2", 1.2, 0.75)]
    [InlineData("bm25-2.0", 2.0, 0.0)]
    public void Bm25ScoresAreIdenticalEitherWay(string _, double k1, double b)
    {
        var flat = Flattened();
        var split = Split();
        var scorer = new Bm25Scorer(k1, b);

        foreach (string query in Queries)
        {
            var flatTerms = flat.Tokenizer.Tokenize(query);
            var splitTerms = split.Tokenizer.Tokenize(query);

            foreach (var (id, _, _) in Documents)
                Assert.Equal(scorer.Score(id, flatTerms, flat), scorer.Score(id, splitTerms, split), 15);
        }
    }

    [Fact]
    public void TheOtherFlatScorersAgreeToo()
    {
        var flat = Flattened();
        var split = Split();

        ITextScorer[] scorers = [new Bm25Scorer(), new TfIdfScorer(), new QueryLikelihoodScorer(0.2)];

        foreach (ITextScorer scorer in scorers)
        {
            foreach (string query in Queries)
            {
                var flatTerms = flat.Tokenizer.Tokenize(query);
                var splitTerms = split.Tokenizer.Tokenize(query);

                foreach (var (id, _, _) in Documents)
                {
                    Assert.Equal(
                        scorer.Score(id, flatTerms, flat),
                        scorer.Score(id, splitTerms, split),
                        15);
                }
            }
        }
    }

    [Fact]
    public void TheSearchOrderIsIdenticalEitherWay()
    {
        var flat = new RankedTextSearchEngine(Flattened(), new Bm25Scorer());
        var split = new RankedTextSearchEngine(Split(), new Bm25Scorer());

        foreach (string query in Queries)
        {
            Assert.Equal(
                flat.Search(query).Select(result => result.DocumentId),
                split.Search(query).Select(result => result.DocumentId));
        }
    }

    [Fact]
    public void WhatActuallyDiffersIsThePhraseBehaviourAcrossTheBoundary()
    {
        var split = new RankedTextSearchEngine(Split(), new Bm25Scorer());

        // "A ranking guide" is contiguous inside the title, so it matches either way.
        Assert.Equal(
            "1",
            Assert.Single(split.Search("\"a ranking guide\"")).DocumentId);

        // "guide How" spans the title/body boundary. In the flattened form those tokens are
        // adjacent, so it matches there; as separate fields it must not. This is the one
        // intentional behavioural difference, and it is the point of having fields.
        var flat = new RankedTextSearchEngine(Flattened(), new Bm25Scorer());

        Assert.Equal("1", Assert.Single(flat.Search("\"guide How\"")).DocumentId);
        Assert.Empty(split.Search("\"guide How\""));
    }

    [Fact]
    public void BM25FIsTheScorerThatSeesADifference()
    {
        var flat = Flattened();
        var split = Split();
        var scorer = new Bm25FScorer(fieldWeights: new Dictionary<string, double> { ["title"] = 3.0 });

        var flatTerms = flat.Tokenizer.Tokenize("ranking");
        var splitTerms = split.Tokenizer.Tokenize("ranking");

        // The flattened corpus has no title field, so the weight cannot apply: BM25F falls back to
        // a neutral weight on the single field. The split one has a title to weight. Same documents,
        // different capability — which is exactly why the split was worth making.
        Assert.True(scorer.Score("1", flatTerms, flat) > 0);
        Assert.True(scorer.Score("1", splitTerms, split) > 0);
        Assert.NotEqual(
            scorer.Score("1", flatTerms, flat),
            scorer.Score("1", splitTerms, split),
            15);
    }
}
