using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

public class SegmentOverlayTests
{
    private static SegmentOverlay Overlay() => new(Tokenizer.Default);

    [Fact]
    public void TryGetPostings_HandsBackNullForATermTheOverlayDoesNotHold()
    {
        var overlay = Overlay();
        overlay.Add(new SearchDocument("1", "alpha beta"));

        Assert.True(overlay.TryGetPostings("alpha", out var held));
        Assert.NotNull(held);
        Assert.Single(held);

        // The out is null, not a defaulted or stale list: the overlay never resolves a term it
        // does not hold, and the signature says so to a caller who checks the return.
        Assert.False(overlay.TryGetPostings("gamma", out var missing));
        Assert.Null(missing);
    }
}
