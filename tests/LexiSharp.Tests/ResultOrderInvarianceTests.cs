using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Ranking must be a function of the corpus contents, not of the order the documents happened to
/// be inserted in. The order the loader walks a directory is a property of the filesystem — ext4
/// with <c>dir_index</c> returns hashed names — so the same checkout enumerates differently on a
/// developer machine and on a CI runner.
/// </summary>
/// <remarks>
/// This is not hypothetical. The golden master gate compared exact document order, and it was red
/// from the commit that introduced it: two entries moved, and three more were reported as ties
/// whose order was decided by enumeration order. Re-recording the baseline would not have fixed it,
/// only relocated the failure to whichever machine recorded it last. These tests pin the property
/// that makes the gate reproducible, so a future change that reintroduces enumeration-order
/// sensitivity fails here instead of in CI.
/// </remarks>
public class ResultOrderInvarianceTests
{
    // Deliberately includes documents whose scores tie exactly and documents that differ only in
    // the last bits of the score, which is where an order that is not a total order shows itself.
    private static readonly (string Id, string Text)[] Documents =
    {
        ("alpha", "term statistics term statistics ranking"),
        ("bravo", "term statistics"),
        ("charlie", "ranking ranking ranking ranking"),
        ("delta", "tokenization refresh expiry"),
        ("echo", "term statistics"),
        ("foxtrot", "ranking"),
    };

    [Fact]
    public void ResultOrderDoesNotDependOnInsertionOrder()
    {
        var forward = Search(Documents, "ranking");
        var reversed = Search(Documents.Reverse().ToList(), "ranking");

        Assert.Equal(
            forward.Select(result => result.DocumentId),
            reversed.Select(result => result.DocumentId));

        Assert.Equal(
            forward.Select(result => result.Score),
            reversed.Select(result => result.Score));
    }

    [Theory]
    [InlineData("term")]
    [InlineData("ranking")]
    [InlineData("term statistics")]
    [InlineData("tokenization")]
    public void EveryQueryRanksIdenticallyWhicheverOrderTheCorpusIsIndexedIn(string query)
    {
        var forward = Search(Documents, query);
        var reversed = Search(Documents.Reverse().ToList(), query);

        Assert.Equal(
            forward.Select(result => result.DocumentId),
            reversed.Select(result => result.DocumentId));
    }

    [Fact]
    public void TheFixtureActuallyContainsTies()
    {
        // Without an exact tie the invariance above would pass trivially on the old, enumeration-order
        // tie-break, and would be a test that proves nothing. Assert the precondition, so deleting the
        // tie from the fixture breaks this test rather than silently disarming the others.
        var index = new InMemoryTextIndex();
        index.Index(Documents.Select(document => new SearchDocument(document.Id, document.Text)));

        var results = new RankedTextSearchEngine(index, new Bm25Scorer()).Search("term");

        Assert.Contains(
            results.Select(result => result.Score).Zip(results.Skip(1).Select(result => result.Score)),
            pair => pair.First == pair.Second);
    }

    private static IReadOnlyList<SearchResult> Search(
        IReadOnlyList<(string Id, string Text)> documents,
        string query)
    {
        var index = new InMemoryTextIndex();
        index.Index(documents.Select(document => new SearchDocument(document.Id, document.Text)));

        return new RankedTextSearchEngine(index, new Bm25Scorer()).Search(query);
    }
}
