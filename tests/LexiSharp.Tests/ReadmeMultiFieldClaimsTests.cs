using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Checks the concrete numbers and examples the README states about multi-field documents. The
/// documentation makes claims a reader will copy into their own code, so they are asserted here
/// rather than left to drift.
/// </summary>
public class ReadmeMultiFieldClaimsTests
{
    private static InMemoryTextIndex ReadmeIndex()
    {
        var index = new InMemoryTextIndex();

        index.Index(new[]
        {
            new SearchDocument(
                Id: "1",
                Text: "the article body goes on at some length about indexing",
                TextFields: new Dictionary<string, string>
                {
                    ["title"] = "a guide to search ranking",
                    ["summary"] = "an introduction",
                }),
        });

        return index;
    }

    [Fact]
    public void TheDocumentedExampleIndexesAsDocumented()
    {
        var index = ReadmeIndex();

        // "Fields is the default field first, then the named ones in ordinal order."
        Assert.Equal([TextFields.Default, "summary", "title"], index.Fields);

        // "A plain Bm25Scorer matches 'ranking' even though it never appears in SearchDocument.Text."
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());
        var hit = Assert.Single(engine.Search("ranking"));
        Assert.Equal("1", hit.DocumentId);

        // The documented per-field reads.
        Assert.Equal(1, index.FieldTermFrequency("1", "title", "guide"));
        Assert.Equal(1, index.FieldTermFrequency("1", "title", "ranking"));
        Assert.Equal(1, index.FieldDocumentFrequency("title", "ranking"));

        // A title-only term is findable through the flat view, and the default field excludes it.
        Assert.Equal(1, index.TermFrequency("1", "ranking"));
        Assert.Equal(0, index.FieldTermFrequency("1", TextFields.Default, "ranking"));

        // "introduction" and "indexing" live in different fields.
        Assert.Equal(1, index.FieldTermFrequency("1", "summary", "introduction"));
        Assert.Equal(1, index.FieldTermFrequency("1", TextFields.Default, "indexing"));
    }

    [Fact]
    public void APhraseMatchesInsideAFieldButNotAcrossFields()
    {
        var index = ReadmeIndex();
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        // "a guide" is inside the title.
        Assert.Equal("1", Assert.Single(engine.Search("\"a guide\"")).DocumentId);

        // The body ends with 'indexing'; the summary starts with 'an'. "indexing an" would be
        // adjacent if the fields shared a position run.
        Assert.Empty(engine.Search("\"indexing an\""));
    }

    [Fact]
    public void AFieldPresentButEmptyCountsInItsAverage()
    {
        var index = new InMemoryTextIndex();

        index.Index(
        [
            new SearchDocument("1", "body one", TextFields: new Dictionary<string, string> { ["title"] = "alpha beta" }),
            // A title of single-character terms, which the default tokenizer drops: declared, empty.
            new SearchDocument("2", "body two", TextFields: new Dictionary<string, string> { ["title"] = "a b" }),
        ]);

        Assert.Equal(0, index.FieldLength("2", "title"));

        // (2 + 0) over the 2 documents declaring the field.
        Assert.Equal(1.0, index.AverageFieldLength("title"), 6);
    }

    // ---- the BM25F section ------------------------------------------------------------------------

    [Fact]
    public void TheDocumentedBm25FExampleBehavesAsWritten()
    {
        var index = new InMemoryTextIndex();

        index.Index(new[]
        {
            new SearchDocument(
                Id: "1",
                Text: "the body is long and rambles on at some considerable length about many things",
                TextFields: new Dictionary<string, string> { ["title"] = "ranking" }),
        });

        // "A field left out keeps the neutral weight of 1, so an unset map means treat every field
        // equally, not search the main text only."
        var weighted = new Bm25FScorer(fieldWeights: new Dictionary<string, double> { ["title"] = 2.0 });
        var engine = new RankedTextSearchEngine(index, weighted);

        Assert.Equal("1", Assert.Single(engine.Search("ranking")).DocumentId);

        // The term is in the title only, so the flat, unweighted default field sees nothing.
        Assert.Equal(0, index.FieldTermFrequency("1", TextFields.Default, "ranking"));
        Assert.True(weighted.Score("1", ["ranking"], index) > 0);

        // "A weight of 0 removes the field from the ranking entirely."
        var titleBlind = new Bm25FScorer(fieldWeights: new Dictionary<string, double> { ["title"] = 0.0 });
        Assert.Equal(0, titleBlind.Score("1", ["ranking"], index));
    }

    [Fact]
    public void TheDocumentedPresetChainBehavesAsWritten()
    {
        var index = new InMemoryTextIndex();

        index.Index(new[]
        {
            new SearchDocument(
                "1", "an unrelated body of text",
                TextFields: new Dictionary<string, string> { ["title"] = "ranking" }),
        });

        var chained = new Bm25FScorer(Bm25FParameters.Balanced.WithWeight("title", 2.0));
        var spelledOut = new Bm25FScorer(
            Bm25FParameters.Balanced.K1,
            Bm25FParameters.Balanced.B,
            new Dictionary<string, double> { ["title"] = 2.0 });

        Assert.Equal(spelledOut.Score("1", ["ranking"], index), chained.Score("1", ["ranking"], index), 12);
    }
}
