using LexiSharp.Core;
using LexiSharp.Hybrid;
using Xunit;

// CA1859 ("use a concrete type instead of the interface") is suppressed on the lines
// below. The interface is the published return type: a List<T> or an array in its place
// would hand callers a mutable collection through a contract that says they cannot have
// one, and what it saves is a single interface dispatch per call, which no measurement in
// docs/benchmarks.md attributes time to.

// CA1861 ("prefer a static readonly field over a constant array argument") is suppressed on
// the lines below. Its premise is a call repeated with the same literal, allocating each
// time. These are one-shot fixtures, and the literal belongs beside the assertion that reads
// it -- hoisting it into a field saves nothing that is measured, and moves the data away
// from the test that fails on it.

namespace LexiSharp.Tests;

public class CombinationResultMergerTests
{
    private static readonly SearchDocument Doc1 = new("1", "alpha");
    private static readonly SearchDocument Doc2 = new("2", "beta");
    private static readonly SearchDocument Doc3 = new("3", "gamma");

    private static SearchResult R(SearchDocument document, double score) => new(document.Id, score, document);

    private static IReadOnlyList<SearchResult> List(params SearchResult[] results) => results; // NOSONAR:CA1859

    // ---------- CombSUM ----------

    [Fact]
    public void CombSum_Name()
    {
        Assert.Equal("CombSum", new CombSumResultMerger().Name);
    }

    [Fact]
    public void CombSum_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(new CombSumResultMerger().Merge(Array.Empty<IReadOnlyList<SearchResult>>(), "q"));
    }

    [Fact]
    public void CombSum_SumsNormalizedScores()
    {
        var merger = new CombSumResultMerger();

        var merged = merger.Merge(new[]
        {
            List(R(Doc1, 10), R(Doc2, 5)),
            List(R(Doc1, 2), R(Doc3, 2)),
        }, "q");

        // engine 1 normalized by 10 → d1=1, d2=0.5 ; engine 2 by 2 → d1=1, d3=1
        Assert.Equal(new[] { "1", "3", "2" }, merged.Select(r => r.DocumentId).ToArray()); // NOSONAR:CA1861
        Assert.Equal(2.0, merged[0].Score, 6);
        Assert.Equal(1.0, merged[1].Score, 6);
        Assert.Equal(0.5, merged[2].Score, 6);
    }

    [Fact]
    public void CombSum_IgnoresNonPositiveAndNonFiniteScores()
    {
        var merger = new CombSumResultMerger();

        var merged = merger.Merge(new[]
        {
            List(R(Doc1, double.NaN), R(Doc2, double.PositiveInfinity), R(Doc3, -4), R(Doc1, 8)),
        }, "q");

        // Only the finite, positive 8 counts; the engine normalizes by 8 → d1 = 1.
        Assert.Single(merged);
        Assert.Equal("1", merged[0].DocumentId);
        Assert.Equal(1.0, merged[0].Score, 6);
    }

    [Fact]
    public void CombSum_AllNonPositive_ReturnsEmpty()
    {
        var merger = new CombSumResultMerger();
        Assert.Empty(merger.Merge(new[] { List(R(Doc1, -1), R(Doc2, 0)) }, "q"));
    }

    [Fact]
    public void CombSum_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CombSumResultMerger().Merge(null!, "q"));
    }

    // ---------- CombMNZ ----------

    [Fact]
    public void CombMNZ_Name()
    {
        Assert.Equal("CombMNZ", new CombMNZResultMerger().Name);
    }

    [Fact]
    public void CombMNZ_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(new CombMNZResultMerger().Merge(Array.Empty<IReadOnlyList<SearchResult>>(), "q"));
    }

    [Fact]
    public void CombMNZ_RewardsAgreementAcrossEngines()
    {
        var merger = new CombMNZResultMerger();

        var merged = merger.Merge(new[]
        {
            List(R(Doc1, 10), R(Doc2, 10)),
            List(R(Doc1, 10)),
        }, "q");

        // CombSUM: d1 = 1 + 1 = 2, d2 = 1. Agreement: d1 seen by 2 engines → 2 × 2 = 4.
        Assert.Equal(new[] { "1", "2" }, merged.Select(r => r.DocumentId).ToArray()); // NOSONAR:CA1861
        Assert.Equal(4.0, merged[0].Score, 6);
        Assert.Equal(1.0, merged[1].Score, 6);
    }

    [Fact]
    public void CombMNZ_IgnoresNonPositiveScoresWhenCountingAgreement()
    {
        var merger = new CombMNZResultMerger();

        var merged = merger.Merge(new[]
        {
            List(R(Doc1, 10)),
            List(R(Doc1, -5)),
        }, "q");

        // The negative return does not count as agreement: hit count stays 1.
        Assert.Single(merged);
        Assert.Equal(1.0, merged[0].Score, 6);
    }

    [Fact]
    public void CombMNZ_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new CombMNZResultMerger().Merge(null!, "q"));
    }
}
