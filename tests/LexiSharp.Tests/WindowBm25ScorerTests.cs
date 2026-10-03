using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Covers the windowed scorer: the identity it exists to satisfy, the mechanism that makes a window
/// different from a document, and the arithmetic of the sweep itself.
/// </summary>
/// <remarks>
/// Two tests carry the weight. <see cref="Score_OnTheWholeDocument_IsBm25WithNoLengthTerm"/> is the
/// property the type's whole-document mode is built for — exact equality, not "close enough" — so
/// that an experiment comparing the two arms through this scorer compares one scorer against itself.
/// <see cref="Score_PrefersTheConcentratedDocument_WhereBm25PrefersTheShortOne"/> is the opposite
/// direction: on a corpus where the two scorers genuinely disagree, the windowed one reverses the
/// order. Without it, a scorer that quietly ignored its windows would pass everything else here.
/// </remarks>
public class WindowBm25ScorerTests
{
    /// <summary>
    /// Documents of different lengths holding a repeated query term, so both a window sweep and the
    /// whole-document arithmetic have something to disagree about.
    /// </summary>
    private static InMemoryTextIndex CreateCorpus()
    {
        var index = new InMemoryTextIndex();

        index.Add(new SearchDocument("short", "alpha beta alpha"));
        index.Add(new SearchDocument("long", "alpha filler filler filler filler alpha beta"));
        index.Add(new SearchDocument("dense", "alpha alpha alpha beta filler filler"));
        index.Add(new SearchDocument("other", "gamma delta epsilon"));

        return index;
    }

    [Fact]
    public void Score_OnTheWholeDocument_IsBm25WithNoLengthTerm()
    {
        var index = CreateCorpus();
        string[] query = ["alpha", "beta"];

        var windowed = new WindowBm25Scorer(includeWholeDocument: true);
        var bm25 = new Bm25Scorer(k1: 1.5, b: 0);

        foreach (string id in new[] { "short", "long", "dense", "other" })
        {
            // Exact, not approximate: with b = 0 the normalization is exactly 1, so the two must
            // agree on the last bit of every score, and a windowed experiment whose two arms drift
            // apart here is measuring the drift rather than the windowing.
            Assert.Equal(bm25.Score(id, query, index), windowed.Score(id, query, index));
        }
    }

    [Fact]
    public void Score_OnTheWholeDocument_IgnoresWidthsBecauseTheyAreNotAskedFor()
    {
        var index = CreateCorpus();
        string[] query = ["alpha"];

        var wholeOnly = new WindowBm25Scorer(includeWholeDocument: true);
        var withWidths = new WindowBm25Scorer([64], includeWholeDocument: true);

        // A width wider than the document is that whole document, so asking for a 64-token window
        // over a 6-token document adds nothing to the maximum.
        Assert.Equal(wholeOnly.Score("long", query, index), withWidths.Score("long", query, index));
    }

    [Fact]
    public void Score_ConcentratingATermInOneWindow_BeatsSpreadingIt()
    {
        var index = new InMemoryTextIndex();

        // Same term, same occurrences, different places: adjacent in one, at the two ends in the
        // other. A window wide enough to hold both together is the whole of the difference.
        index.Add(new SearchDocument("adjacent", "alpha alpha filler filler filler filler filler filler filler filler filler filler filler filler filler filler filler filler"));
        index.Add(new SearchDocument("apart", "alpha filler filler filler filler filler filler filler filler filler filler filler filler filler filler filler filler filler alpha"));
        string[] query = ["alpha"];

        var scorer = new WindowBm25Scorer([4]);
        double adjacent = scorer.Score("adjacent", query, index);
        double apart = scorer.Score("apart", query, index);

        // Saturation is what does it: two occurrences in one window score less than twice what one
        // occurrence scores (2·sat/(2+k1) < 2·sat/(1+k1) for k1 > 0), so the maximum over windows
        // prefers the document where the term is together.
        Assert.True(adjacent > apart);
    }

    [Fact]
    public void Score_PrefersTheConcentratedDocument_WhereBm25PrefersTheShortOne()
    {
        // Two documents holding "alpha" exactly twice: together at the head of a 20-token document,
        // and at the two ends of a 5-token one. The lengths differ, which is what gives BM25 an
        // opinion at all — and its opinion is the short one, since b damps the long document.
        var index = new InMemoryTextIndex();
        index.Add(new SearchDocument("concentrated", "alpha alpha " + string.Join(' ', Enumerable.Repeat("filler", 18))));
        index.Add(new SearchDocument("even", "alpha filler filler filler alpha"));
        string[] query = ["alpha"];

        var bm25 = new RankedTextSearchEngine(index, new Bm25Scorer(1.5, 0.75));
        var windowed = new RankedTextSearchEngine(index, new WindowBm25Scorer([4]));

        // b = 0.75 over an average length of 12.5 damps the concentrated document to 5 / (2 + 1.5·1.45)
        // against the even one's 5 / (2 + 1.5·0.55); the window sweep never sees a length, and a
        // 4-token window over the concentrated head holds both occurrences where no window over the
        // even one can.
        Assert.Equal("even", bm25.Search("alpha", new SearchOptions(Limit: 1))[0].DocumentId);
        Assert.Equal("concentrated", windowed.Search("alpha", new SearchOptions(Limit: 1))[0].DocumentId);
    }

    [Fact]
    public void Score_CoversTheTailOfADocumentTheStrideWouldSkip()
    {
        // Nine tokens, a term only at the last position, and a stride that lands the last full
        // window short of it. The clamped final window is what finds the term at all.
        var index = new InMemoryTextIndex();
        index.Add(new SearchDocument("tail", "filler filler filler filler filler filler filler filler zebra"));

        double score = new WindowBm25Scorer([4]).Score("tail", ["zebra"], index);

        Assert.True(score > 0);
    }

    [Fact]
    public void Score_WithAnAdvanceWiderThanTheWindow_ScoresTheSameAsANonOverlappingSweep()
    {
        // An advance wider than the width would leave text unscored, so it is capped at the width.
        var index = CreateCorpus();
        string[] query = ["alpha"];

        var capped = new WindowBm25Scorer([4], stride: 100);
        var nonOverlapping = new WindowBm25Scorer([4]);

        Assert.Equal(nonOverlapping.Score("long", query, index), capped.Score("long", query, index));
    }

    [Fact]
    public void Score_WithOverlappingWindows_CountsEachOccurrenceOncePerWindow()
    {
        // A term at positions 1 and 2 belongs to every window that spans both, and must contribute
        // its full frequency to each of them rather than accumulating across the sweep.
        var index = new InMemoryTextIndex();
        index.Add(new SearchDocument("doc", "filler alpha alpha filler filler"));

        var overlapping = new WindowBm25Scorer([4], stride: 1);
        double best = overlapping.Score("doc", ["alpha"], index);

        // One window of width 4 holds both occurrences however far it slides, so the best it can
        // reach is the frequency-2 score — never the sum of three overlapping windows.
        var whole = new WindowBm25Scorer(includeWholeDocument: true).Score("doc", ["alpha"], index);

        Assert.Equal(whole, best);
    }

    [Fact]
    public void Score_OnADocumentWithoutAnyQueryTerm_IsZero_SoTheEngineMaySkipIt()
    {
        var index = CreateCorpus();
        var scorer = new WindowBm25Scorer([4]);

        Assert.IsAssignableFrom<ITermOverlapScorer>(scorer);
        Assert.Equal(0, scorer.Score("other", ["alpha"], index));
    }

    [Fact]
    public void Score_OnAnUnknownOrEmptyDocument_IsZero()
    {
        var index = CreateCorpus();
        index.Add(new SearchDocument("empty", ""));

        var scorer = new WindowBm25Scorer([4]);

        Assert.Equal(0, scorer.Score("absent", ["alpha"], index));
        Assert.Equal(0, scorer.Score("empty", ["alpha"], index));
    }

    [Fact]
    public void Constructor_WithoutAWindowToScore_Refuses()
    {
        // No width and no whole-document window is a scorer with nothing to score; returning 0 for
        // everything would be indistinguishable from a corpus with no match.
        Assert.Throws<ArgumentException>(() => new WindowBm25Scorer());
        Assert.Throws<ArgumentException>(() => new WindowBm25Scorer([]));
    }

    [Fact]
    public void Constructor_RefusesAnImpossibleWidthStrideOrK1()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowBm25Scorer([0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowBm25Scorer([-4]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowBm25Scorer([4], stride: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WindowBm25Scorer([4], k1: -0.5));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WindowBm25Scorer([4], k1: double.NaN));
    }

    [Fact]
    public void Constructor_WithNoWidthsAndNoWholeDocument_Refuses()
    {
        // `widths: null` is the same as an empty list — an optional argument with nothing in it —
        // so it only fails when there is no window at all to score.
        Assert.Throws<ArgumentException>(() => new WindowBm25Scorer(null!));
        Assert.Equal("WindowBM25/whole/k1=1.5", new WindowBm25Scorer(null!, includeWholeDocument: true).Name);
    }

    [Fact]
    public void Score_WithoutItsArguments_Throws()
    {
        var index = CreateCorpus();
        var scorer = new WindowBm25Scorer([4]);

        Assert.Throws<ArgumentNullException>(() => scorer.Score("short", ["alpha"], null!));
        Assert.Throws<ArgumentNullException>(() => scorer.Score("short", null!, index));
    }

    [Fact]
    public void Name_TellsTwoConfigurationsApart()
    {
        var narrow = new WindowBm25Scorer([4]).Name;
        var wide = new WindowBm25Scorer([32]).Name;
        var sliding = new WindowBm25Scorer([4], stride: 2).Name;
        var whole = new WindowBm25Scorer(includeWholeDocument: true).Name;
        var tuned = new WindowBm25Scorer([4], k1: 0.9).Name;

        Assert.Equal("WindowBM25/w4/k1=1.5", narrow);
        Assert.NotEqual(narrow, wide);
        Assert.NotEqual(narrow, sliding);
        Assert.NotEqual(narrow, whole);
        Assert.NotEqual(narrow, tuned);
        Assert.Equal([4], new WindowBm25Scorer([4]).Widths);
    }
}