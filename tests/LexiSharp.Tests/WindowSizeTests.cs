using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The window an engine works in is a size the caller controls, and it is bounded by the corpus
/// it is cut from.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SearchOptions.Window"/> is <c>Offset + Limit</c> — two numbers a caller owns, both
/// plain <c>int</c> — and <see cref="TopRankedWindow"/> allocates its entry array at that size
/// in its constructor, before any document has been considered. So <c>Offset: int.MaxValue</c> asks
/// for a window of 2 147 483 647 entries, which at 16 bytes each is 32 GiB, allocated for a page
/// the caller cannot receive: the page is <c>[Offset, Offset + Limit)</c>, and no corpus is that
/// deep.
/// </para>
/// <para>
/// Measured on this host, one request at <c>Offset: int.MaxValue</c> threw
/// <see cref="OutOfMemoryException"/> in 9 ms. The minimal-API endpoint binds <c>Offset</c> from
/// the query string, so a single <c>?offset=2147483647</c> reached it.
/// </para>
/// <para>
/// The fix bounds the window by the corpus: <c>min(Window, documentCount)</c>. A window past the
/// last document produces the same page it always did — an empty one — so nothing about the answer
/// changes, only the size of the working set that produces it.
/// </para>
/// </remarks>
public class WindowSizeTests
{
    private static (InMemoryTextIndex Index, RankedTextSearchEngine Engine) SmallCorpus()
    {
        var index = new InMemoryTextIndex();

        for (int i = 0; i < 5; i++)
            index.Add(new SearchDocument("doc-" + i, "alpha beta gamma"));

        return (index, new RankedTextSearchEngine(index, new Bm25Scorer()));
    }

    [Fact]
    public void APageStartingPastTheEndOfTheCorpusComesBackEmpty()
    {
        var (_, engine) = SmallCorpus();

        // Window int.MaxValue: the CLR refused the entry array outright before this change.
        Assert.Empty(engine.Search("alpha", new SearchOptions(Offset: int.MaxValue, Limit: 1)));
        Assert.Empty(engine.Search("alpha", new SearchOptions(Offset: int.MaxValue - 1, Limit: 2)));

        // The other half of the clamp, and the reason it is not merely a truncation: a window
        // larger than the corpus is not a shorter page. The same oversized request, with no offset,
        // still returns every document the corpus holds.
        Assert.Equal(
            new[] { "doc-0", "doc-1", "doc-2", "doc-3", "doc-4" },
            engine.Search("alpha", new SearchOptions(Limit: int.MaxValue)).Select(result => result.DocumentId));
    }

    [Fact]
    public void APageThatRunsOffTheEndIsCutFromTheClampedWindow()
    {
        var (index, engine) = SmallCorpus();

        // Offset 3 with a limit larger than what is left: two documents reachable, and the page is
        // the two of them rather than a window sized at Offset + Limit.
        var page = engine.Search("alpha", new SearchOptions(Offset: 3, Limit: 10));

        Assert.Equal(new[] { "doc-3", "doc-4" }, page.Select(result => result.DocumentId));
        Assert.Equal(2, page.Count);

        // The clamp is the corpus size, not the page size, so a page that starts at the last
        // document still returns it.
        Assert.Equal(
            new[] { "doc-4" },
            engine.Search("alpha", new SearchOptions(Offset: 4, Limit: 1)).Select(result => result.DocumentId));
        Assert.Equal(5, index.Count);
    }

    [Fact]
    public void AWindowSizedPastTheCorpusAllocatesNoMoreThanTheCorpus()
    {
        var (_, engine) = SmallCorpus();

        // The allocation the window's entry array is: 16 bytes an entry, and no more entries than
        // the corpus holds. Pinned with an allocation rather than a stopwatch because that is what
        // grew, and because a machine does not move it.
        const int Iterations = 200;
        var options = new SearchOptions(Offset: int.MaxValue, Limit: 1);

        for (int i = 0; i < 50; i++)
            engine.Search("alpha", options);

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < Iterations; i++)
            engine.Search("alpha", options);

        long allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / Iterations;

        // Five documents at 16 bytes is 80; the rest of a search is the page, the query plan and the
        // result records. The pin is a budget over the whole of it and would not hold if the window
        // were still sized at the request.
        Assert.True(
            allocated < 5_000,
            $"a search past the end of the corpus allocated {allocated} bytes, which is the size of a "
            + "window an engine does not need");
    }
}
