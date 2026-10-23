using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class MoreLikeThisTests
{
    [Fact]
    public void FindSimilar_ExcludesSourceAndReturnsRelated()
    {
        var index = new InMemoryTextIndex();
        index.Index(new[]
        {
            new SearchDocument("a", "the search engine uses bm25 to rank results"),
            new SearchDocument("b", "bm25 ranking inverted index search"),
            new SearchDocument("c", "italian cuisine pasta rome"),
        });

        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        var similar = MoreLikeThis.Search(engine, index, "a", new SearchOptions(Limit: 5));

        Assert.DoesNotContain(similar, r => r.DocumentId == "a");
        Assert.Contains(similar, r => r.DocumentId == "b");
    }

    [Fact]
    public void FindSimilar_UnknownDocument_ReturnsEmpty()
    {
        var index = new InMemoryTextIndex();
        index.Index(new[] { new SearchDocument("a", "hello world") });
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        Assert.Empty(MoreLikeThis.Search(engine, index, "nope"));
    }

    [Fact]
    public void FindSimilar_EmptyDocument_ReturnsEmpty()
    {
        var index = new InMemoryTextIndex();
        index.Index(new[] { new SearchDocument("a", "   ") });
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        Assert.Empty(MoreLikeThis.Search(engine, index, "a"));
    }
}