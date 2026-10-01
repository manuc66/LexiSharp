using System.Linq;
using LexiSharp.Compression;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class ContextCompressorTests
{
    [Fact]
    public void QueryWindowCompressor_KeepsTheWordsAroundEachOccurrence()
    {
        var compressor = new QueryWindowCompressor(windowRadius: 1);

        Assert.Equal(
            "two three four",
            compressor.Compress("one two three four five six seven", ["three"]));
    }

    [Fact]
    public void AdjacentOrOverlappingWindows_MergeIntoOnePassage()
    {
        var compressor = new QueryWindowCompressor(windowRadius: 1);

        Assert.Equal(
            "a b c d e",
            compressor.Compress("a b c d e f g h", ["b", "d"]));
    }

    [Fact]
    public void SeparatedWindows_JoinWithAnEllipsis()
    {
        var compressor = new QueryWindowCompressor(windowRadius: 1);

        Assert.Equal(
            "a b c … h i j",
            compressor.Compress("a b c d e f g h i j", ["b", "i"]));
    }

    [Fact]
    public void TheRadius_ClampsAtTheDocumentEdges()
    {
        var compressor = new QueryWindowCompressor(windowRadius: 16);

        Assert.Equal(
            "a b c",
            compressor.Compress("a b c", ["a"]));
    }

    [Fact]
    public void NoMatchingTerm_OrEmptyInput_CompressesToNothing()
    {
        var compressor = new QueryWindowCompressor();

        Assert.Equal("", compressor.Compress("the weather in paris is sunny", ["turbine"]));
        Assert.Equal("", compressor.Compress("the weather in paris is sunny", []));
        Assert.Equal("", compressor.Compress("", ["turbine"]));
    }

    [Fact]
    public void Matching_IsCaseInsensitive()
    {
        var compressor = new QueryWindowCompressor(windowRadius: 1);

        Assert.Equal(
            "the PUMPS and",
            compressor.Compress("words before the PUMPS and the rest after", ["pumps"]));
    }

    [Fact]
    public void QueryWindowCompressor_NamesItsRadius()
    {
        Assert.Equal("query-window(3)", new QueryWindowCompressor(3).Name);
        Assert.Throws<System.ArgumentOutOfRangeException>(() => new QueryWindowCompressor(0));
    }

    [Fact]
    public void Search_ReturnsTheFullText_WhileSearchCompressed_ReturnsTheLeanPage()
    {
        var inner = PlainEngine();
        var compressed = new CompressingTextSearchEngine(inner, new QueryWindowCompressor(windowRadius: 2));

        var plainHits = compressed.Search("alpha beta");
        var leanHits = compressed.SearchCompressed("alpha beta");

        // Same ranking, same ids and scores — only the text differs.
        Assert.Equal(plainHits.Select(hit => hit.DocumentId), leanHits.Select(hit => hit.DocumentId));
        Assert.Equal(plainHits.Select(hit => hit.Score), leanHits.Select(hit => hit.Score));

        var lean = Assert.Single(leanHits);
        Assert.True(lean.CompressedText.Length < plainHits.Single().Document.Text.Length);
        Assert.InRange(lean.CompressionRatio, 0.0, 1.0);
    }

    [Fact]
    public void CompressionRatio_MeasuresWhatWasRetained()
    {
        var full = new CompressingTextSearchEngine(PlainEngine(), new QueryWindowCompressor(windowRadius: 64));
        var slim = new CompressingTextSearchEngine(PlainEngine(), new QueryWindowCompressor(windowRadius: 1));

        var fullHit = Assert.Single(full.SearchCompressed("alpha beta"));
        Assert.Equal(1, fullHit.CompressionRatio, 6); // the window covers the whole document

        var slimHit = Assert.Single(slim.SearchCompressed("alpha beta"));
        Assert.InRange(slimHit.CompressionRatio, 0.0, 1.0);
        Assert.True(slimHit.CompressedText.Length < fullHit.CompressedText.Length);
        Assert.StartsWith("alpha beta gamma", slimHit.CompressedText);
    }

    [Fact]
    public void WritesAndRanking_ForwardToTheInnerEngine()
    {
        var inner = PlainEngine();
        var engine = new CompressingTextSearchEngine(inner, new QueryWindowCompressor());

        engine.Add(new SearchDocument("d4", "alpha turbines spin fast"));
        Assert.Single(engine.Search("turbines"));

        engine.Remove("d4");
        Assert.Empty(engine.Search("turbines"));
        Assert.True(engine is ICompressingSearchEngine);
    }

    private static RankedTextSearchEngine PlainEngine()
    {
        var engine = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());
        engine.Index(
        [
            new SearchDocument("d1", "alpha beta gamma delta epsilon zeta eta theta"),
            new SearchDocument("d2", "the weather in paris is sunny this week"),
        ]);
        return engine;
    }
}